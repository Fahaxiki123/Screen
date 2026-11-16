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
        settings.RacingBaseAddress = "https://race.example.test/v1";
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
        var scope = TranslationCredentialStore.EndpointScope(_store.Load().Translation.BaseAddress);
        credentials.Write(name, "test-existing-key", scope);
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

    [STATestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TestConnectionOnlyUsesTheKeyForTheEditedAddress(bool racing)
    {
        var profile = SaveAiProfile();
        var credentials = new FakeCredentialStore();
        credentials.Write(profile.CredentialName, "old-main-key");
        credentials.Write(profile.RacingCredentialName, "old-racing-key");
        using var settings = new SettingsViewModel(_store, credentials, _ => { });

        if (racing)
        {
            settings.RacingBaseAddress = "https://new-race.example.test/v1";
            var draft = settings.BuildTranslationCredentials();
            Assert.AreEqual("old-main-key", draft.ApiKey);
            Assert.IsNull(draft.RacingApiKey);
            settings.RacingApiKey = "new-racing-key";
            Assert.AreEqual("new-racing-key", settings.BuildTranslationCredentials().RacingApiKey);
        }
        else
        {
            settings.BaseAddress = "https://new-main.example.test/v1";
            var draft = settings.BuildTranslationCredentials();
            Assert.IsNull(draft.ApiKey);
            Assert.AreEqual("old-racing-key", draft.RacingApiKey);
            settings.ApiKey = "new-main-key";
            Assert.AreEqual("new-main-key", settings.BuildTranslationCredentials().ApiKey);
        }
    }

    [STATestMethod]
    public void FailedAddressSaveRetainsThePreviousUsablePairUntilRetry()
    {
        var profile = SaveAiProfile();
        var credentials = new FakeCredentialStore();
        credentials.Write(profile.CredentialName, "old-key");
        using var settings = new SettingsViewModel(_store, credentials, _ => { });
        settings.BaseAddress = "https://new-main.example.test/v1";
        settings.ApiKey = "new-key";
        var blockedTempPath = Directory.CreateDirectory(_store.Path + ".tmp");
        try
        {
            settings.SaveNow();

            Assert.IsTrue(settings.HasError);
            var saved = _store.Load().Translation;
            Assert.AreEqual(profile.BaseAddress, saved.BaseAddress);
            Assert.AreEqual("old-key", TranslationCredentialStore.ReadSaved(saved, credentials).ApiKey);
            Assert.IsNull(TranslationCredentialStore.ReadSaved(saved with { BaseAddress = settings.BaseAddress }, credentials).ApiKey);
        }
        finally
        {
            blockedTempPath.Delete();
        }

        settings.SaveNow();

        Assert.IsFalse(settings.HasError, settings.StatusText);
        Assert.AreEqual(settings.BaseAddress, _store.Load().Translation.BaseAddress);
        Assert.AreEqual("new-key", TranslationCredentialStore.ReadSaved(_store.Load().Translation, credentials).ApiKey);
        Assert.IsNull(TranslationCredentialStore.ReadSaved(profile, credentials).ApiKey);
    }

    [STATestMethod]
    public void PasswordSaveFailureNeverReusesThePreviousKeyForTheNewAddress()
    {
        var profile = SaveAiProfile();
        var credentials = new FakeCredentialStore();
        credentials.Write(profile.CredentialName, "old-key");
        using var settings = new SettingsViewModel(_store, credentials, _ => { });
        settings.BaseAddress = "https://new-main.example.test/v1";
        settings.ApiKey = "new-key";
        credentials.FailOnceFor = profile.CredentialName;

        settings.SaveNow();

        Assert.IsTrue(settings.HasError);
        Assert.AreEqual(settings.BaseAddress, _store.Load().Translation.BaseAddress);
        Assert.IsNull(TranslationCredentialStore.ReadSaved(_store.Load().Translation, credentials).ApiKey);
        Assert.AreEqual("old-key", TranslationCredentialStore.ReadSaved(profile, credentials).ApiKey);

        settings.SaveNow();

        Assert.IsFalse(settings.HasError, settings.StatusText);
        Assert.AreEqual("new-key", TranslationCredentialStore.ReadSaved(_store.Load().Translation, credentials).ApiKey);
    }

    [STATestMethod]
    public void ConfiguredCredentialNamesCannotSelectOrDeleteAnotherCredential()
    {
        var profile = SaveAiProfile() with
        {
            CredentialName = "AnotherApp",
            RacingCredentialName = TranslationProfile.TencentSecretKeyCredentialName
        };
        _store.Update(latest => latest.Translation = profile);
        var credentials = new FakeCredentialStore();
        credentials.Write("AnotherApp", "unrelated-key");
        credentials.Write(TranslationProfile.TencentSecretKeyCredentialName, "tencent-key");
        credentials.Write(TranslationProfile.ApiKeyCredentialName, "legacy-key");
        using var settings = new SettingsViewModel(_store, credentials, _ => { });

        Assert.IsNull(settings.BuildTranslationCredentials().ApiKey);
        Assert.IsNull(settings.BuildTranslationCredentials().RacingApiKey);
        settings.ApiKey = "fresh-key";
        settings.SaveNow();
        settings.ApiKey = string.Empty;
        settings.SaveNow();

        Assert.IsNull(credentials.Read(TranslationProfile.ApiKeyCredentialName));
        Assert.AreEqual("unrelated-key", credentials.Read("AnotherApp"));
        Assert.AreEqual("tencent-key", credentials.Read(TranslationProfile.TencentSecretKeyCredentialName));
        Assert.IsNull(TranslationCredentialStore.ReadSaved(profile, credentials).ApiKey);
        Assert.IsNull(TranslationCredentialStore.ReadSaved(profile, credentials).RacingApiKey);
    }

    [TestMethod]
    public void WindowsCredentialStoreRejectsNamesOutsideTheScreenSlots()
    {
        var credentials = new CredentialService();
        const string unrelatedName = "Screen.Unrelated-Settings-Test";
        Assert.ThrowsExactly<ArgumentException>(() => credentials.ReadCredential(unrelatedName));
        Assert.ThrowsExactly<ArgumentException>(() => credentials.Write(unrelatedName, "example"));
        Assert.ThrowsExactly<ArgumentException>(() => credentials.Delete(unrelatedName));
    }

    private TranslationProfile SaveAiProfile()
    {
        var settings = new AppSettings();
        settings.Translation = settings.Translation with
        {
            Provider = TranslationProviderKind.OpenAiCompatible,
            BaseAddress = "https://old-main.example.test/v1",
            RacingBaseAddress = "https://old-race.example.test/v1",
            EnableRacing = true,
            RacingModel = "racing-model"
        };
        _store.Save(settings);
        return settings.Translation;
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, StoredCredential> _secrets = new();
        public string? FailOnceFor { get; set; }
        public List<string> Attempts { get; } = [];

        public string? Read(string targetName) => ReadCredential(targetName)?.Secret;

        public StoredCredential? ReadCredential(string targetName) => _secrets.GetValueOrDefault(targetName);

        public void Write(string targetName, string secret, string? endpointScope = null)
        {
            RecordAttempt(targetName);
            _secrets[targetName] = new StoredCredential(secret, endpointScope);
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
