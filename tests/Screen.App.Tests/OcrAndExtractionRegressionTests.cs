using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class OcrAndExtractionRegressionTests
{
    [TestMethod]
    public async Task DirectTextBudgetExpiryReturnsNullForOcrFallbackWhileCallerCancelPropagatesAsync()
    {
        var fallbackResult = await DirectTextExtractor.RunWithBudgetAsync(
            token =>
            {
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                token.ThrowIfCancellationRequested();
                return null;
            },
            TimeSpan.FromMilliseconds(40),
            CancellationToken.None);
        Assert.IsNull(fallbackResult, "Internal UIA timeout must return null so image OCR can run.");

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DirectTextExtractor.RunWithBudgetAsync(
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return null;
                },
                TimeSpan.FromSeconds(5),
                callerCts.Token));
    }

    [TestMethod]
    public void BoundedDocumentLinesExcludeOffscreenAndFullWindowSpans()
    {
        var selection = new PixelRect(200, 300, 240, 80);
        var candidates = new (string Text, PixelRect ScreenBounds)[]
        {
            ("选区上方的整段正文不应被读取", new PixelRect(200, 120, 240, 30)),
            ("第一行选中文本", new PixelRect(205, 305, 220, 24)),
            ("第二行选中文本", new PixelRect(205, 340, 210, 24)),
            ("选区下方的整段正文也不应被读取", new PixelRect(200, 500, 240, 30)),
            ("整窗虚假包围盒不应通过", new PixelRect(0, 0, 1600, 1000))
        };

        var document = DirectTextExtractor.BuildDocumentFromBoundedLines(candidates, selection);

        Assert.IsNotNull(document);
        Assert.AreEqual(2, document.Lines.Count);
        StringAssert.Contains(document.Text, "第一行选中文本");
        StringAssert.Contains(document.Text, "第二行选中文本");
        Assert.IsFalse(document.Text.Contains("上方", StringComparison.Ordinal));
        Assert.IsFalse(document.Text.Contains("下方", StringComparison.Ordinal));
        Assert.IsFalse(document.Text.Contains("虚假包围盒", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnexpectedLocalOcrExceptionDoesNotDiscardCloudOcrSuccessAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "Screen.Ocr.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var settingsPath = Path.Combine(tempRoot, "settings.json");
            var store = new JsonSettingsStore(settingsPath);
            var settings = new AppSettings
            {
                Ocr = new OcrProfile { Mode = OcrProviderMode.CloudFirst }
            };
            store.Save(settings);

            const string cloudJson = """
                {
                  "Response": {
                    "TextDetections": [
                      {
                        "DetectedText": "云端识别成功文本",
                        "Confidence": 99,
                        "Polygon": [
                          { "X": 4, "Y": 4 },
                          { "X": 120, "Y": 4 },
                          { "X": 120, "Y": 28 },
                          { "X": 4, "Y": 28 }
                        ]
                      }
                    ],
                    "RequestId": "test-req"
                  }
                }
                """;
            using var http = new HttpClient(new StubHandler(cloudJson));
            var cloud = new TencentCloudOcrEngine(http);
            var failingLocal = new ThrowingOcrEngine(new ArithmeticException("Simulated OnnxRuntimeException"));
            using var smart = new SmartOcrEngine(
                failingLocal,
                cloud,
                store,
                new CredentialService(),
                key => key switch
                {
                    TranslationProfile.TencentSecretIdCredentialName => "test-secret-id",
                    TranslationProfile.TencentSecretKeyCredentialName => "test-secret-key",
                    _ => null
                });

            var image = new PixelBuffer(new byte[32 * 32 * 4], 32, 32, 32 * 4);
            var result = await smart.RecognizeAsync(image, "auto");

            StringAssert.Contains(result.Text, "云端识别成功文本");
            Assert.AreEqual(1, failingLocal.Calls);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class ThrowingOcrEngine(Exception error) : IOcrEngine
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> AvailableLanguages { get; } = ["auto"];
        public bool IsLanguageAvailable(string languageTag) => true;
        public Task<OcrDocument> RecognizeAsync(PixelBuffer image, string languageTag, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<OcrDocument>(error);
        }
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
