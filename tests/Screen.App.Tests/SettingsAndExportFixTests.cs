using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class SettingsAndExportFixTests
{
    private DirectoryInfo _root = null!;
    private JsonSettingsStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Directory.CreateTempSubdirectory("Screen.SettingsExport.Fixes-");
        _store = new JsonSettingsStore(Path.Combine(_root.FullName, "settings.json"));
    }

    [TestCleanup]
    public void Cleanup() => _root.Delete(recursive: true);

    [STATestMethod]
    public void InvalidTranslationDoesNotDiscardValidHotKeysOrRecordingChanges()
    {
        using var model = new SettingsViewModel(_store, new FakeCredentials(), _ => Assert.Fail("Startup is unchanged"));
        model.TranslationProviderIndex = 2;
        model.BaseAddress = "https://";
        model.HotKey = "Ctrl+Shift+B";
        model.RecordingShowCursor = false;
        model.SaveNow();

        Assert.IsTrue(model.HasError);
        Assert.IsTrue(model.HasUnsavedChanges);
        Assert.AreEqual("Ctrl+Shift+B", _store.Load().HotKey);
        Assert.IsFalse(_store.Load().Recording.ShowCursor);
        Assert.AreEqual(TranslationProviderKind.Tencent, _store.Load().Translation.Provider);

        model.BaseAddress = "https://offline.example.test/v1";
        model.SaveNow();
        Assert.IsFalse(model.HasError, model.StatusText);
        Assert.IsFalse(model.HasUnsavedChanges);
        Assert.AreEqual(TranslationProviderKind.OpenAiCompatible, _store.Load().Translation.Provider);
    }

    [STATestMethod]
    public void SavingOneTranslationFieldPreservesNewerRuntimeLanguage()
    {
        using var model = new SettingsViewModel(_store, new FakeCredentials(), _ => { });
        _store.Update(settings => settings.Translation = settings.Translation with { TargetLanguage = "日本語" });
        model.Model = "new-offline-model";
        model.SourceLanguageIndex = 3;
        model.SaveNow();
        Assert.AreEqual("日本語", _store.Load().Translation.TargetLanguage);
        Assert.AreEqual("new-offline-model", _store.Load().Translation.Model);
        Assert.AreEqual("en", _store.Load().Translation.SourceLanguage);
    }

    [STATestMethod]
    public void InvalidStorageOnlyBlocksStorageAndFailedStartupRemainsPending()
    {
        using var model = new SettingsViewModel(_store, new FakeCredentials(), _ => throw new IOException("offline registry failure"));
        model.StartWithWindows = true;
        model.RecordingTempDirectory = "relative-path";
        model.RecordingFrameRateIndex = 2;
        model.SaveNow();
        Assert.IsTrue(model.HasUnsavedChanges);
        Assert.IsTrue(model.HasError);
        Assert.AreEqual(60, _store.Load().Recording.FrameRate);
        Assert.AreEqual(string.Empty, _store.Load().Recording.TempDirectory);
        StringAssert.Contains(model.StatusText, "offline registry failure");
    }

    [TestMethod]
    public void FailedPriorityTransitionRollsBackOnlyNewReservations()
    {
        var keys = new HashSet<uint>();
        using var first = new HotKeyRegistration(1, 2, (_, _, key) => keys.Add(key), _ => keys.Remove(65));
        using var conflict = new HotKeyRegistration(3, 4, (_, _, _) => false, _ => Assert.Fail("Failed reservation was never owned"));
        Assert.IsFalse(HotKeyModeTransition.TryReserveAll([
            new("Ctrl+A", first, 2, 65), new("Ctrl+B", conflict, 2, 66)
        ], out var name));
        Assert.AreEqual("Ctrl+B", name);
        Assert.IsFalse(first.IsRegistered);
        Assert.AreEqual(0, keys.Count);

        Assert.IsTrue(HotKeyModeTransition.TryReserveAll([new("Ctrl+A", first, 2, 65)], out _));
        Assert.IsTrue(first.IsRegistered);
    }

    [STATestMethod]
    public void CredentialFailureDoesNotBlockIndependentFieldsOrRetryDiscardedDrafts()
    {
        var credentials = new FakeCredentials();
        using var model = new SettingsViewModel(_store, credentials, _ => { });
        credentials.FailReads = true;
        model.SourceLanguageIndex = 3;
        model.RecordingShowCursor = false;
        model.SaveNow();
        Assert.IsFalse(model.HasError, model.StatusText);
        Assert.AreEqual("en", _store.Load().Translation.SourceLanguage);
        Assert.IsFalse(_store.Load().Recording.ShowCursor);

        model.BaseAddress = "https://changed.example.test/v1";
        model.RecordingFrameRateIndex = 2;
        model.SaveNow();
        Assert.IsTrue(model.HasError);
        Assert.AreEqual(60, _store.Load().Recording.FrameRate);
        Assert.AreNotEqual(model.BaseAddress, _store.Load().Translation.BaseAddress);
        model.DiscardPendingChanges();
        credentials.FailReads = false;
        credentials.FailWrites = true;
        model.TencentSecretKey = "offline-discarded-key";
        model.SaveNow();
        Assert.IsTrue(model.HasUnsavedChanges);
        var attempts = credentials.WriteAttempts;
        model.PauseAutoSave();
        model.DiscardPendingChanges();
        credentials.FailWrites = false;
        model.Dispose();
        Assert.AreEqual(attempts, credentials.WriteAttempts, "Closing after discard must not retry the discarded key.");
    }

    [TestMethod]
    public async Task CancellingCopyPreservesBothOriginalAndExistingDestinationAsync()
    {
        var source = Path.Combine(_root.FullName, "source.mp4");
        var target = Path.Combine(_root.FullName, "target.mp4");
        var bytes = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(bytes);
        await File.WriteAllBytesAsync(source, bytes);
        await File.WriteAllBytesAsync(target, [1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => RecordingFileStore.CopyAtomicallyAsync(
            source, target, cancellation.Token, new InlineProgress(_ => cancellation.Cancel())));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(target));
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(source));
        Assert.AreEqual(0, Directory.GetFiles(_root.FullName, ".screen-export-*.tmp").Length);

        await RecordingFileStore.CopyAtomicallyAsync(source, target);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(target));
    }

    [TestMethod]
    public void RecoveryMarkersAndUnmarkedLegacySessionsSurviveCleanup()
    {
        var marked = Directory.CreateDirectory(Path.Combine(_root.FullName, Guid.NewGuid().ToString("N"))).FullName;
        RecordingTempCleaner.PreserveRecovery(marked);
        File.SetLastWriteTimeUtc(Path.Combine(marked, "RECOVERY.txt"), DateTime.UtcNow.AddYears(-1));
        Directory.SetLastWriteTimeUtc(marked, DateTime.UtcNow.AddYears(-1));
        Assert.AreEqual(0, RecordingTempCleaner.Clean(_root.FullName, DateTime.UtcNow));
        Assert.IsTrue(File.Exists(Path.Combine(marked, "RECOVERY.txt")));
    }

    [TestMethod]
    public void CustomTempRootCannotBeARelativePathOrAnExistingFile()
    {
        Assert.IsNotNull(RecordingStorage.ValidateDirectory("relative"));
        var file = Path.Combine(_root.FullName, "not-a-folder");
        File.WriteAllText(file, "fixture");
        Assert.IsNotNull(RecordingStorage.ValidateDirectory(file));
        Assert.AreEqual(Path.Combine(_root.FullName, "Screen.Recording"),
            RecordingStorage.GetRoot(new RecordingProfile { TempDirectory = _root.FullName }));
    }

    [TestMethod]
    public void WritableLoopbackShareSupportsRecordingSessionsAndSpaceChecks()
    {
        var localRoot = _root.FullName;
        var share = $@"\\localhost\{localRoot[0]}$\";
        var directory = Path.Combine(share, Path.GetRelativePath(_root.Root.FullName, localRoot));
        if (!Directory.Exists(directory))
            Assert.Inconclusive("This integration test needs access to the local drive's SMB share.");

        Assert.IsNull(RecordingStorage.ValidateDirectory(directory));
        var session = RecordingStorage.CreateSession(new RecordingProfile { TempDirectory = directory });
        Assert.IsTrue(Directory.Exists(Path.Combine(localRoot, "Screen.Recording", Path.GetFileName(session))));
        RecordingStorage.EnsureAvailableSpace(session);
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(session)!, ".write-check-*"));
        var insufficient = Assert.ThrowsExactly<IOException>(() => RecordingStorage.EnsureAvailableSpace(session, long.MaxValue));
        StringAssert.Contains(insufficient.Message, "空间不足");
    }

    [TestMethod]
    public void SpaceCheckReportsAnUnavailableShareAndEnforcesTheLocalThreshold()
    {
        var missing = $@"\\localhost\Screen-Tests-{Guid.NewGuid():N}\recordings";
        var unavailable = Assert.ThrowsExactly<IOException>(() => RecordingStorage.EnsureAvailableSpace(missing));
        StringAssert.Contains(unavailable.Message, missing);
        Assert.IsInstanceOfType<System.ComponentModel.Win32Exception>(unavailable.InnerException);
        RecordingStorage.EnsureAvailableSpace(_root.FullName, 1);
        var insufficient = Assert.ThrowsExactly<IOException>(() => RecordingStorage.EnsureAvailableSpace(_root.FullName, long.MaxValue));
        StringAssert.Contains(insufficient.Message, "空间不足");
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class FakeCredentials : ICredentialStore
    {
        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public int WriteAttempts { get; private set; }
        public StoredCredential? ReadCredential(string targetName) => FailReads
            ? throw new System.ComponentModel.Win32Exception("offline credential failure") : null;
        public void Write(string targetName, string secret, string? endpointScope = null)
        {
            WriteAttempts++;
            if (FailWrites)
                throw new System.ComponentModel.Win32Exception("offline write failure");
        }
        public void Delete(string targetName) { }
    }
}
