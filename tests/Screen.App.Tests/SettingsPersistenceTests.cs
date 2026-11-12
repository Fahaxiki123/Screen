using System.IO;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class SettingsPersistenceTests
{
    private DirectoryInfo _directory = null!;
    private JsonSettingsStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Directory.CreateTempSubdirectory("Screen.Settings.Tests-");
        _store = new JsonSettingsStore(Path.Combine(_directory.FullName, "settings.json"));
    }

    [TestCleanup]
    public void Cleanup() => _directory.Delete(recursive: true);

    [STATestMethod]
    public void CredentialWriteFailureStaysVisibleAndRetriesOnlyTheFailedCredential()
    {
        var profile = _store.Load().Translation;
        var credentials = new FakeCredentialStore { FailOnceFor = profile.CredentialName };
        var startupUpdates = 0;
        using var settings = new SettingsViewModel(_store, credentials, _ => startupUpdates++);
        settings.ApiKey = "test-main-key";
        settings.RacingApiKey = "test-racing-key";
        settings.RecordingShowCursor = false;

        settings.SaveNow();

        Assert.IsTrue(settings.HasError);
        StringAssert.StartsWith(settings.StatusText, "凭据保存失败");
        Assert.IsFalse(_store.Load().Recording.ShowCursor);
        Assert.IsNull(credentials.Read(profile.CredentialName));
        Assert.AreEqual("test-racing-key", credentials.Read(profile.RacingCredentialName));

        // Let the actual autosave timer retry on the WPF dispatcher.
        var frame = new DispatcherFrame();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(settings.StatusText) && !settings.HasError)
            {
                frame.Continue = false;
            }
        };
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timeout.Tick += (_, _) => frame.Continue = false;
        timeout.Start();
        try
        {
            Dispatcher.PushFrame(frame);
        }
        finally
        {
            timeout.Stop();
        }

        Assert.IsFalse(settings.HasError, settings.StatusText);
        StringAssert.StartsWith(settings.StatusText, "已自动保存");
        Assert.AreEqual("test-main-key", credentials.Read(profile.CredentialName));
        Assert.AreEqual(2, credentials.Attempts.Count(name => name == profile.CredentialName));
        Assert.AreEqual(1, credentials.Attempts.Count(name => name == profile.RacingCredentialName));
        Assert.AreEqual(3, credentials.Attempts.Count);
        Assert.AreEqual(1, startupUpdates);
        Assert.IsFalse(File.ReadAllText(_store.Path).Contains("test-main-key", StringComparison.Ordinal));
    }

    [STATestMethod]
    public void CredentialDeleteFailureRemainsPendingWithoutWritingOrdinarySettings()
    {
        var name = _store.Load().Translation.CredentialName;
        var credentials = new FakeCredentialStore();
        credentials.Write(name, "test-existing-key");
        credentials.FailOnceFor = name;
        using var settings = new SettingsViewModel(_store, credentials, _ => Assert.Fail("Credential-only edits must not update startup settings."));
        settings.ApiKey = string.Empty;

        settings.SaveNow();
        Assert.IsTrue(settings.HasError);
        StringAssert.StartsWith(settings.StatusText, "凭据保存失败");
        Assert.AreEqual("test-existing-key", credentials.Read(name));

        settings.SaveNow();
        Assert.IsFalse(settings.HasError);
        Assert.IsNull(credentials.Read(name));
        Assert.IsFalse(File.Exists(_store.Path));
    }

    [STATestMethod]
    public void InvalidOrdinarySettingsDoNotBlockCredentialSave()
    {
        var name = _store.Load().Translation.CredentialName;
        var credentials = new FakeCredentialStore();
        using var settings = new SettingsViewModel(_store, credentials, _ => Assert.Fail("Invalid settings must not be written."));
        settings.HotKey = "invalid";
        settings.ApiKey = "test-main-key";

        settings.SaveNow();

        Assert.IsTrue(settings.HasError);
        StringAssert.Contains(settings.StatusText, "截图快捷键格式无效");
        Assert.AreEqual("test-main-key", credentials.Read(name));
        Assert.IsFalse(File.Exists(_store.Path));
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new();
        public string? FailOnceFor { get; set; }
        public List<string> Attempts { get; } = [];

        public string? Read(string targetName) => _secrets.GetValueOrDefault(targetName);

        public void Write(string targetName, string secret)
        {
            RecordAttempt(targetName);
            _secrets[targetName] = secret;
        }

        public void Delete(string targetName)
        {
            RecordAttempt(targetName);
            _secrets.Remove(targetName);
        }

        private void RecordAttempt(string targetName)
        {
            Attempts.Add(targetName);
            if (FailOnceFor == targetName)
            {
                FailOnceFor = null;
                throw new IOException("Credential store unavailable.");
            }
        }
    }
}
