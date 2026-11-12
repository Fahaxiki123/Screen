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

    [TestMethod]
    public async Task PaddleOcrEngineReleasesModelsAfterIdleTimeoutAndReloadsOnNextUseAsync()
    {
        var modelRoot = FindBuiltAppModelsDirectory();
        using var engine = new PaddleOcrEngine(TimeSpan.FromMilliseconds(140), modelRoot);
        var blank = new PixelBuffer(new byte[48 * 24 * 4], 48, 24, 48 * 4);
        Array.Fill<byte>(blank.Pixels, 255);

        Assert.IsFalse(engine.IsEngineLoaded);
        var first = await engine.RecognizeAsync(blank, "auto");
        Assert.IsNotNull(first);
        Assert.IsTrue(engine.IsEngineLoaded, "Local OCR engine must be loaded right after recognition.");

        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (engine.IsEngineLoaded && DateTime.UtcNow < deadline)
        {
            await Task.Delay(30);
        }
        Assert.IsFalse(engine.IsEngineLoaded, "Local OCR engine must release ONNX sessions once idle.");

        var second = await engine.RecognizeAsync(blank, "auto");
        Assert.IsNotNull(second);
        Assert.IsTrue(engine.IsEngineLoaded, "Local OCR engine must lazily reload on subsequent recognition.");
    }

    private static string FindBuiltAppModelsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Screen.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.IsNotNull(dir, "Could not locate repository root.");
        var appBin = Path.Combine(dir.FullName, "src", "Screen.App", "bin");
        var clsFiles = Directory.GetFiles(appBin, "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx", SearchOption.AllDirectories);
        Assert.IsTrue(clsFiles.Length > 0, "Screen.App output models were not found under src/Screen.App/bin.");
        return Directory.GetParent(Path.GetDirectoryName(clsFiles[0])!)!.FullName;
    }

    [TestMethod]
    public async Task HybridScreenCaptureServiceReleasesPrimarySessionAfterIdleTimeoutAsync()
    {
        var resets = 0;
        var primary = new StubScreenCaptureService();
        var fallback = new StubScreenCaptureService();
        using var hybrid = new HybridScreenCaptureService(
            primary,
            fallback,
            () => Interlocked.Increment(ref resets),
            TimeSpan.FromMilliseconds(120));

        var frame = await hybrid.CaptureAtAsync(new PixelPoint(10, 10));
        Assert.AreEqual(32, frame.Width);
        Assert.AreEqual(0, Volatile.Read(ref resets));

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Volatile.Read(ref resets) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
        Assert.AreEqual(1, Volatile.Read(ref resets), "Idle timer must release cached DXGI duplication session.");

        var nextFrame = await hybrid.CaptureAtAsync(new PixelPoint(10, 10));
        Assert.AreEqual(32, nextFrame.Width);
        Assert.AreEqual(2, primary.Calls);
    }

    private sealed class StubScreenCaptureService : IScreenCaptureService
    {
        public int Calls { get; private set; }

        public Task<CaptureFrame> CaptureAtAsync(PixelPoint screenPoint, CancellationToken cancellationToken = default)
        {
            Calls++;
            var pixels = new byte[32 * 32 * 4];
            Array.Fill<byte>(pixels, 200);
            return Task.FromResult(new CaptureFrame(
                pixels,
                32,
                32,
                32 * 4,
                96,
                96,
                new PixelRect(0, 0, 32, 32),
                CaptureColorSpace.Srgb,
                DateTimeOffset.UtcNow));
        }

        public void Dispose()
        {
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
