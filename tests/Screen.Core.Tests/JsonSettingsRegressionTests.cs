using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.Core.Tests;

[TestClass]
public sealed class JsonSettingsRegressionTests
{
    [TestMethod]
    [DataRow("""{"OcrLanguage":"ja-JP"}""", PinOutlineColor.Subtle, 1.0)]
    [DataRow("""{"OcrLanguage":"ja-JP","PinOutlineColor":99,"PinOutlineWidth":0.5}""", PinOutlineColor.Subtle, 0.5)]
    [DataRow("""{"OcrLanguage":"ja-JP","PinOutlineColor":4,"PinOutlineWidth":0.1}""", PinOutlineColor.Blue, 1.0)]
    [DataRow("""{"OcrLanguage":"ja-JP","PinOutlineColor":"unknown","PinOutlineWidth":null}""", PinOutlineColor.Subtle, 1.0)]
    [DataRow("""{"OcrLanguage":"ja-JP","PinOutlineColor":[0],"PinOutlineWidth":{"value":3}}""", PinOutlineColor.Subtle, 1.0)]
    public void MissingOrInvalidPinPreferencesFallBackIndividually(string json, PinOutlineColor color, double width)
    {
        var directory = Directory.CreateTempSubdirectory("screen-pin-settings-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, json);

            var loaded = new JsonSettingsStore(path).Load();

            Assert.AreEqual(color, loaded.PinOutlineColor);
            Assert.AreEqual(width, loaded.PinOutlineWidth);
            Assert.AreEqual("ja-JP", loaded.OcrLanguage);
            Assert.IsTrue(File.Exists(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void PinMenuAndUnrelatedSettingsUpdatesPreserveEachOthersValues()
    {
        var directory = Directory.CreateTempSubdirectory("screen-pin-settings-");
        try
        {
            var store = new JsonSettingsStore(Path.Combine(directory.FullName, "settings.json"));
            Assert.AreEqual(PinOutlineColor.Subtle, store.Load().PinOutlineColor);
            Assert.AreEqual(1.0, store.Load().PinOutlineWidth);

            store.Update(settings => settings.PinOutlineColor = PinOutlineColor.None);
            foreach (var width in AppSettings.PinOutlineWidths)
            {
                store.Update(settings => settings.PinOutlineWidth = width);
                Assert.AreEqual(width, store.Load().PinOutlineWidth);
            }
            store.Update(settings => settings.OcrLanguage = "ja-JP");

            var loaded = new JsonSettingsStore(store.Path).Load();
            Assert.AreEqual(PinOutlineColor.None, loaded.PinOutlineColor);
            Assert.AreEqual(4.0, loaded.PinOutlineWidth);
            Assert.AreEqual("ja-JP", loaded.OcrLanguage);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void LegacyExplicitNullsLoadWithoutGuessingAnEndpoint()
    {
        var directory = Directory.CreateTempSubdirectory("screen-json-");
        var path = Path.Combine(directory.FullName, "settings.json");
        try
        {
            File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "HotKey": null,
              "ClickThroughRecoveryHotKey": null,
              "RecordHotKey": null,
              "SaveDirectory": null,
              "OcrLanguage": null,
              "Ocr": null,
              "Recording": null,
              "Translation": {
                "BaseAddress": null,
                "Model": null,
                "CredentialName": null,
                "TargetLanguage": null,
                "TencentRegion": null,
                "RacingBaseAddress": null,
                "RacingModel": null,
                "RacingCredentialName": null,
                "MyMemoryContactEmail": null,
                "EnableRacing": true
              }
            }
            """);

            var loaded = new JsonSettingsStore(path).Load();
            var defaults = new AppSettings();

            Assert.AreEqual(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.AreEqual(defaults.HotKey, loaded.HotKey);
            Assert.AreEqual(defaults.ClickThroughRecoveryHotKey, loaded.ClickThroughRecoveryHotKey);
            Assert.AreEqual(defaults.RecordHotKey, loaded.RecordHotKey);
            Assert.AreEqual(defaults.SaveDirectory, loaded.SaveDirectory);
            Assert.AreEqual(defaults.OcrLanguage, loaded.OcrLanguage);
            Assert.AreEqual(defaults.Recording, loaded.Recording);
            Assert.AreEqual(defaults.Ocr, loaded.Ocr);
            Assert.AreEqual(defaults.Translation.Model, loaded.Translation.Model);
            Assert.AreEqual(defaults.Translation.TargetLanguage, loaded.Translation.TargetLanguage);
            Assert.AreEqual(defaults.Translation.TencentRegion, loaded.Translation.TencentRegion);
            Assert.AreEqual(string.Empty, loaded.Translation.BaseAddress);
            Assert.AreEqual(string.Empty, loaded.Translation.RacingBaseAddress);
            Assert.AreEqual(string.Empty, loaded.Translation.CredentialName);
            Assert.AreEqual(string.Empty, loaded.Translation.RacingCredentialName);
            Assert.AreEqual(string.Empty, loaded.Translation.RacingModel);
            Assert.AreEqual(string.Empty, loaded.Translation.MyMemoryContactEmail);
            Assert.IsTrue(loaded.Translation.EnableRacing);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }
}
