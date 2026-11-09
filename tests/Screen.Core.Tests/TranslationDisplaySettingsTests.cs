using Screen.Core;

namespace Screen.Core.Tests;

[TestClass]
public sealed class TranslationDisplaySettingsTests
{
    private string _directory = null!;
    private string _path = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"Screen-translation-settings-{Guid.NewGuid():N}");
        _path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_directory, recursive: true);
    }

    [TestMethod]
    public void NewAndLegacySettingsKeepFloatingCards()
    {
        Assert.AreEqual(TranslationDisplayMode.Card, new AppSettings().TranslationDisplayMode);
        var store = new JsonSettingsStore(_path);
        Assert.AreEqual(TranslationDisplayMode.Card, store.Load().TranslationDisplayMode);

        File.WriteAllText(_path, """{"SchemaVersion":4,"OcrLanguage":"ja-JP"}""");

        var legacy = store.Load();
        Assert.AreEqual(TranslationDisplayMode.Card, legacy.TranslationDisplayMode);
        Assert.AreEqual("ja-JP", legacy.OcrLanguage);
    }

    [TestMethod]
    public void InPlaceModePersistsAndCanSwitchBackToCards()
    {
        var store = new JsonSettingsStore(_path);
        store.Save(new AppSettings
        {
            TranslationDisplayMode = TranslationDisplayMode.InPlace,
            OcrLanguage = "ja-JP"
        });

        Assert.AreEqual(TranslationDisplayMode.InPlace, new JsonSettingsStore(_path).Load().TranslationDisplayMode);

        store.Update(settings => settings.TranslationDisplayMode = TranslationDisplayMode.Card);

        var loaded = new JsonSettingsStore(_path).Load();
        Assert.AreEqual(TranslationDisplayMode.Card, loaded.TranslationDisplayMode);
        Assert.AreEqual("ja-JP", loaded.OcrLanguage);
    }

    [TestMethod]
    public void UnknownDisplayModeFallsBackWithoutDiscardingOtherSettings()
    {
        File.WriteAllText(_path, """{"TranslationDisplayMode":99,"OcrLanguage":"ja-JP"}""");

        var loaded = new JsonSettingsStore(_path).Load();

        Assert.AreEqual(TranslationDisplayMode.Card, loaded.TranslationDisplayMode);
        Assert.AreEqual("ja-JP", loaded.OcrLanguage);
        Assert.IsTrue(File.Exists(_path));
        Assert.AreEqual(1, Directory.GetFiles(_directory).Length);
    }

    [TestMethod]
    public void RuntimeSettingsUpdatesPreserveInPlacePreference()
    {
        var store = new JsonSettingsStore(_path);
        store.Save(new AppSettings { TranslationDisplayMode = TranslationDisplayMode.InPlace });

        store.Update(settings => settings.SaveDirectory = "D:\\Screenshots");

        var loaded = store.Load();
        Assert.AreEqual(TranslationDisplayMode.InPlace, loaded.TranslationDisplayMode);
        Assert.AreEqual("D:\\Screenshots", loaded.SaveDirectory);
    }
}
