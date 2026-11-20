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

    [TestMethod]
    public void PreGifSettingsKeepVideoOptionsAndReceiveGifDefaultsWithoutMigration()
    {
        const string legacy = """{"SchemaVersion":4,"Recording":{"FrameRate":15,"Quality":2,"RecordSystemAudio":false,"RecordMicrophone":true}}""";
        File.WriteAllText(_store.Path, legacy);

        var settings = _store.Load();
        Assert.AreEqual(4, settings.SchemaVersion);
        Assert.AreEqual(RecordingFormat.Mp4, settings.Recording.Format);
        Assert.AreEqual(15, settings.Recording.GifFrameRate);
        Assert.AreEqual(1280, settings.Recording.GifMaxWidth);
        Assert.AreEqual(15, settings.Recording.FrameRate);
        Assert.AreEqual(RecordingQuality.High, settings.Recording.Quality);
        Assert.IsFalse(settings.Recording.RecordSystemAudio);
        Assert.IsTrue(settings.Recording.RecordMicrophone);
        Assert.AreEqual(legacy, File.ReadAllText(_store.Path));
    }

    [TestMethod]
    [DataRow(10, 0)]
    [DataRow(20, 960)]
    [DataRow(30, 1920)]
    public void GifOptionsSurviveRestartAndAnUnrelatedUpdate(int fps, int maxWidth)
    {
        var expected = new RecordingProfile
        {
            Format = RecordingFormat.Gif,
            GifFrameRate = fps,
            GifMaxWidth = maxWidth,
            RecordSystemAudio = true,
            RecordMicrophone = true,
            FrameRate = 60,
            Quality = RecordingQuality.Smooth
        };
        _store.Update(settings => settings.Recording = expected);
        new JsonSettingsStore(_store.Path).Update(settings => settings.RecordHotKey = "F9");
        var reloaded = new JsonSettingsStore(_store.Path).Load();
        Assert.AreEqual(4, reloaded.SchemaVersion);
        Assert.AreEqual(expected, reloaded.Recording);
        Assert.AreEqual("F9", reloaded.RecordHotKey);
    }

    [STATestMethod]
    public void CustomQualityAndGifSettingsRoundTripWithoutOverwritingConcurrentFields()
    {
        _store.Update(s => s.Recording = s.Recording with { FrameRate = 45, Quality = RecordingQuality.High });
        using var view = new SettingsViewModel(_store, new FakeCredentialStore(), _ => { });
        Assert.AreEqual(2, view.RecordingPresetIndex);
        view.RecordingPresetIndex = 2;
        Assert.IsFalse(view.HasUnsavedChanges);
        view.RecordingFormatIndex = 1;
        view.RecordingGifFrameRateIndex = 3;
        view.RecordingGifMaxWidthIndex = 0;
        view.SaveDirectory = _directory.FullName;
        // Another writer changed audio after the window opened.
        _store.Update(s => s.Recording = s.Recording with { RecordMicrophone = true });
        view.SaveNow();

        var restored = _store.Load();
        Assert.AreEqual(_directory.FullName, restored.SaveDirectory);
        Assert.AreEqual(45, restored.Recording.FrameRate);
        Assert.AreEqual(RecordingQuality.High, restored.Recording.Quality);
        Assert.AreEqual(RecordingFormat.Gif, restored.Recording.Format);
        Assert.AreEqual(30, restored.Recording.GifFrameRate);
        Assert.AreEqual(0, restored.Recording.GifMaxWidth);
        Assert.IsTrue(restored.Recording.RecordMicrophone);
        view.RecordingPresetIndex = 0;
        view.SaveNow();
        Assert.AreEqual(30, _store.Load().Recording.FrameRate);
        Assert.AreEqual(RecordingQuality.Standard, _store.Load().Recording.Quality);
        view.RecordingPresetIndex = 1;
        view.SaveNow();
        Assert.AreEqual(60, _store.Load().Recording.FrameRate);
        Assert.AreEqual(RecordingQuality.High, _store.Load().Recording.Quality);
        view.RecordingFrameRateIndex = 0;
        Assert.AreEqual(2, view.RecordingPresetIndex);
    }

    [STATestMethod]
    public void DisplayedServiceOrderUsesExistingProviderAndCredentialSlots()
    {
        using var view = new SettingsViewModel(_store, new FakeCredentialStore(), _ => { });
        foreach (var (index, provider) in new[] { (0, TranslationProviderKind.OpenAiCompatible), (1, TranslationProviderKind.Tencent), (2, TranslationProviderKind.MyMemory) })
        {
            view.TranslationServiceIndex = index;
            view.SaveNow();
            Assert.AreEqual(index, view.TranslationServiceIndex);
            Assert.AreEqual(provider, _store.Load().Translation.Provider);
        }
    }

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
        Assert.AreEqual(0, startupUpdates, "Editing recording or translation options must not rewrite Windows startup registration.");
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
    [DataRow(false)]
    [DataRow(true)]
    public void AKeyDraftSurvivesAddressEditsButRequiresBindingBeforeUse(bool racing)
    {
        var credentials = new FakeCredentialStore();
        using var settings = new SettingsViewModel(_store, credentials, _ => { });
        settings.TranslationProviderIndex = 2;
        if (racing)
        {
            settings.EnableRacing = true;
            settings.RacingApiKey = "new-key";
            settings.SaveNow();
            settings.RacingBaseAddress = "https://example.test/v1";
            Assert.AreEqual("new-key", settings.RacingApiKey);
            Assert.IsTrue(settings.CanBindRacingApiKey);
            Assert.IsNull(settings.BuildTranslationCredentials().RacingApiKey);
        }
        else
        {
            settings.BaseAddress = "https://example.test";
            settings.ApiKey = "new-key";
            settings.BaseAddress += "/v1";
            Assert.AreEqual("new-key", settings.ApiKey);
            Assert.IsTrue(settings.CanBindApiKey);
            Assert.IsNull(settings.BuildTranslationCredentials().ApiKey);
        }

        var slot = racing ? TranslationProfile.RacingApiKeyCredentialName : TranslationProfile.ApiKeyCredentialName;
        settings.SaveNow();
        Assert.IsNull(credentials.Read(slot));
        settings.BindDraftKey(racing);
        settings.SaveNow();

        var saved = TranslationCredentialStore.ReadSaved(_store.Load().Translation, credentials);
        Assert.AreEqual("new-key", racing ? saved.RacingApiKey : saved.ApiKey);
        Assert.IsFalse(settings.HasError, settings.StatusText);
    }

    [STATestMethod]
    public void AddressEditInvalidatesDraftConfirmationWithoutErasingIt()
    {
        var credentials = new FakeCredentialStore();
        using var settings = new SettingsViewModel(_store, credentials, _ => { });
        settings.ApiKey = "draft-key";
        settings.BaseAddress = "https://example.test";
        settings.BindDraftKey(racing: false);
        Assert.AreEqual("draft-key", settings.BuildTranslationCredentials().ApiKey);

        settings.BaseAddress += "/v1";

        Assert.AreEqual("draft-key", settings.ApiKey);
        Assert.IsNull(settings.BuildTranslationCredentials().ApiKey);
        settings.SaveNow();
        Assert.IsNull(credentials.Read(TranslationProfile.ApiKeyCredentialName));
    }

    [STATestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AddressEditKeepsSessionInputAfterAutosaveWithoutRebindingIt(bool racing)
    {
        var credentials = new FakeCredentialStore();
        using var settings = new SettingsViewModel(_store, credentials, _ => { });
        settings.TranslationProviderIndex = 2;
        settings.EnableRacing = true;
        if (racing)
        {
            settings.RacingBaseAddress = "https://example.test";
            settings.RacingApiKey = "session-key";
        }
        else
        {
            settings.BaseAddress = "https://example.test";
            settings.ApiKey = "session-key";
        }
        settings.SaveNow();
        var oldProfile = _store.Load().Translation;
        var oldKeys = TranslationCredentialStore.ReadSaved(oldProfile, credentials);
        Assert.AreEqual("session-key", racing ? oldKeys.RacingApiKey : oldKeys.ApiKey);

        if (racing)
        {
            settings.RacingBaseAddress += "/v1";
            Assert.AreEqual("session-key", settings.RacingApiKey);
            Assert.IsTrue(settings.CanBindRacingApiKey);
            Assert.IsNull(settings.BuildTranslationCredentials().RacingApiKey);
        }
        else
        {
            settings.BaseAddress += "/v1";
            Assert.AreEqual("session-key", settings.ApiKey);
            Assert.IsTrue(settings.CanBindApiKey);
            Assert.IsNull(settings.BuildTranslationCredentials().ApiKey);
        }
        settings.SaveNow();

        var currentProfile = _store.Load().Translation;
        var currentKeys = TranslationCredentialStore.ReadSaved(currentProfile, credentials);
        Assert.IsNull(racing ? currentKeys.RacingApiKey : currentKeys.ApiKey);
        oldKeys = TranslationCredentialStore.ReadSaved(oldProfile, credentials);
        Assert.AreEqual("session-key", racing ? oldKeys.RacingApiKey : oldKeys.ApiKey);

        settings.BindDraftKey(racing);
        settings.SaveNow();
        currentKeys = TranslationCredentialStore.ReadSaved(currentProfile, credentials);
        Assert.AreEqual("session-key", racing ? currentKeys.RacingApiKey : currentKeys.ApiKey);
        oldKeys = TranslationCredentialStore.ReadSaved(oldProfile, credentials);
        Assert.IsNull(racing ? oldKeys.RacingApiKey : oldKeys.ApiKey);
    }

    [STATestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public void InactiveAiOptionsDoNotBlockOtherSettings(int providerIndex)
    {
        var credentials = new FakeCredentialStore();
        using var settings = new SettingsViewModel(_store, credentials, _ => { });
        settings.TranslationProviderIndex = 2;
        settings.FastFallbackDelayText = "31";
        settings.RacingBaseAddress = "invalid";
        settings.TranslationProviderIndex = providerIndex;
        settings.RecordingShowCursor = false;

        settings.SaveNow();

        Assert.IsFalse(settings.HasError, settings.StatusText);
        Assert.IsFalse(_store.Load().Recording.ShowCursor);
        Assert.AreEqual(providerIndex == 0 ? TranslationProviderKind.Tencent : TranslationProviderKind.MyMemory,
            _store.Load().Translation.Provider);
    }

    [STATestMethod]
    public void DisabledAiRaceDoesNotValidateItsStoredAddress()
    {
        var credentials = new FakeCredentialStore();
        using var settings = new SettingsViewModel(_store, credentials, _ => { });
        settings.TranslationProviderIndex = 2;
        settings.EnableRacing = true;
        settings.RacingBaseAddress = "invalid";
        settings.EnableRacing = false;
        settings.SaveNow();
        Assert.IsFalse(settings.HasError, settings.StatusText);
        Assert.IsFalse(_store.Load().Translation.EnableRacing);
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
