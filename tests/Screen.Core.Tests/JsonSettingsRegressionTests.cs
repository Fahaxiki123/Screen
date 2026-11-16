using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.Core.Tests;

[TestClass]
public sealed class JsonSettingsRegressionTests
{
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
