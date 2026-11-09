using System.Diagnostics;
using Screen.Core;

namespace Screen.Core.Tests;

[TestClass]
public sealed class CoreTests
{
    [TestMethod]
    public void ColorTextFormatsArgbAsHexAndRgb()
    {
        Assert.AreEqual("#FF3B30", ColorText.ToHex(0xFFFF3B30u));
        Assert.AreEqual("rgb(255, 59, 48)", ColorText.ToRgb(0xFFFF3B30u));
        Assert.AreEqual("#010203", ColorText.ToHex(0xFF010203u));
        Assert.AreEqual("rgb(1, 2, 3)", ColorText.ToRgb(0xFF010203u));
        Assert.AreEqual("#000000", ColorText.ToHex(0xFF000000u));
        Assert.AreEqual("#FFFFFF", ColorText.ToHex(0xFFFFFFFFu));
    }

    [TestMethod]
    public void CaptureQualityRejectsUniformBlackFrame()
    {
        Assert.IsTrue(CaptureFrameQuality.IsSuspiciouslyBlack(SolidImage(320, 180, 0, 0, 0)));
        Assert.IsTrue(CaptureFrameQuality.IsSuspiciouslyBlack(SolidImage(320, 180, 2, 2, 2)));
    }

    [TestMethod]
    public void CaptureQualityKeepsDarkFrameWithVisibleContent()
    {
        var image = SolidImage(320, 180, 1, 1, 1);
        Paint(image, new PixelRect(80, 45, 160, 90), 20, 60, 180);

        Assert.IsFalse(CaptureFrameQuality.IsSuspiciouslyBlack(image));
    }

    [TestMethod]
    public void CaptureQualityKeepsOrdinaryDocumentFrame()
    {
        var image = SolidImage(320, 180, 245, 245, 245);
        Paint(image, new PixelRect(24, 36, 250, 5), 18, 18, 18);

        Assert.IsFalse(CaptureFrameQuality.IsSuspiciouslyBlack(image));
    }

    [TestMethod]
    [DataRow(1.0)]
    [DataRow(1.25)]
    [DataRow(1.5)]
    [DataRow(2.0)]
    public void CoordinateRoundTripPreservesPhysicalPixels(double scale)
    {
        const int physical = 1379;
        var logical = physical / scale;
        var roundTrip = (int)Math.Round(logical * scale);
        Assert.AreEqual(physical, roundTrip);
    }

    [TestMethod]
    public void SelectionEdgesResizeIndependentlyAndStayInsideCapture()
    {
        var limits = new PixelRect(0, 0, 1920, 1080);
        var original = new PixelRect(100, 120, 800, 600);

        Assert.AreEqual(
            new PixelRect(40, 120, 860, 600),
            SelectionRegionGeometry.Resize(original, SelectionResizeHandle.Left, new PixelPoint(40, 500), limits));
        Assert.AreEqual(
            new PixelRect(100, 120, 850, 600),
            SelectionRegionGeometry.Resize(original, SelectionResizeHandle.Right, new PixelPoint(950, 500), limits));
        Assert.AreEqual(
            new PixelRect(100, 70, 800, 650),
            SelectionRegionGeometry.Resize(original, SelectionResizeHandle.Top, new PixelPoint(500, 70), limits));
        Assert.AreEqual(
            new PixelRect(100, 120, 800, 700),
            SelectionRegionGeometry.Resize(original, SelectionResizeHandle.Bottom, new PixelPoint(500, 820), limits));
    }

    [TestMethod]
    public void SelectionCornerResizeChangesBothEdgesAndAllowsTinySelections()
    {
        var limits = new PixelRect(0, 0, 1000, 800);
        var original = new PixelRect(100, 100, 400, 300);

        Assert.AreEqual(
            new PixelRect(40, 50, 460, 350),
            SelectionRegionGeometry.Resize(original, SelectionResizeHandle.TopLeft, new PixelPoint(40, 50), limits));
        Assert.AreEqual(
            new PixelRect(100, 100, 5, 10),
            SelectionRegionGeometry.Resize(original, SelectionResizeHandle.BottomRight, new PixelPoint(105, 110), limits));
        Assert.AreEqual(
            new PixelRect(0, 0, 500, 400),
            SelectionRegionGeometry.Resize(original, SelectionResizeHandle.TopLeft, new PixelPoint(-100, -100), limits));
    }

    [TestMethod]
    public void TinySelectionAtCaptureEdgeCanResizeWithoutThrowing()
    {
        var limits = new PixelRect(0, 0, 100, 80);

        Assert.AreEqual(
            new PixelRect(0, 0, 1, 1),
            SelectionRegionGeometry.Resize(
                new PixelRect(0, 0, 1, 1),
                SelectionResizeHandle.TopLeft,
                new PixelPoint(-20, -20),
                limits));
        Assert.AreEqual(
            new PixelRect(99, 79, 1, 1),
            SelectionRegionGeometry.Resize(
                new PixelRect(99, 79, 1, 1),
                SelectionResizeHandle.BottomRight,
                new PixelPoint(120, 120),
                limits));
    }

    [TestMethod]
    public void StitcherJoinsScrolledFramesWithoutRepeatingOverlap()
    {
        var source = GradientImage(32, 240);
        var first = source.Crop(new PixelRect(0, 0, 32, 140));
        var second = source.Crop(new PixelRect(0, 80, 32, 140));

        var result = new VerticalScrollStitcher().Stitch([first, second]);

        Assert.IsTrue(result.IsComplete, result.FailureReason);
        Assert.AreEqual(220, result.Image.Height);
        CollectionAssert.AreEqual(
            source.Crop(new PixelRect(0, 0, 32, 220)).Pixels,
            result.Image.Pixels);
    }

    [TestMethod]
    public void StitcherDoesNotChooseMinimumOverlapForSparseDocument()
    {
        var source = SparseDocumentImage(64, 280);
        var first = source.Crop(new PixelRect(0, 0, 64, 160));
        var second = source.Crop(new PixelRect(0, 60, 64, 160));

        var result = new VerticalScrollStitcher().Stitch([first, second]);

        Assert.IsTrue(result.IsComplete, result.FailureReason);
        Assert.AreEqual(220, result.Image.Height);
        CollectionAssert.AreEqual(
            source.Crop(new PixelRect(0, 0, 64, 220)).Pixels,
            result.Image.Pixels);
    }

    [TestMethod]
    public void StitcherRemovesRepeatedFixedHeader()
    {
        var content = GradientImage(32, 220);
        var header = SolidImage(32, 20, 20, 70, 180);
        var first = Join(header, content.Crop(new PixelRect(0, 0, 32, 120)));
        var second = Join(header, content.Crop(new PixelRect(0, 70, 32, 120)));

        var result = new VerticalScrollStitcher().Stitch([first, second]);

        Assert.IsTrue(result.IsComplete, result.FailureReason);
        Assert.AreEqual(210, result.Image.Height);
    }

    [TestMethod]
    public void StitcherKeepsFixedFooterOnlyOnce()
    {
        var content = GradientImage(64, 300);
        var first = AddFixedFooter(content.Crop(new PixelRect(0, 0, 64, 160)), 32);
        var second = AddFixedFooter(content.Crop(new PixelRect(0, 70, 64, 160)), 32);
        var third = AddFixedFooter(content.Crop(new PixelRect(0, 140, 64, 160)), 32);

        var result = new VerticalScrollStitcher().Stitch([first, second, third]);

        Assert.IsTrue(result.IsComplete, result.FailureReason);
        Assert.AreEqual(300, result.Image.Height);
        var expected = AddFixedFooter(content, 32);
        CollectionAssert.AreEqual(expected.Pixels, result.Image.Pixels);
    }

    [TestMethod]
    public void StitcherKeepsFloatingOverlayOnlyOnFinalViewport()
    {
        var content = GradientImage(64, 300);
        var overlay = new PixelRect(48, 72, 12, 20);
        var first = AddOverlay(content.Crop(new PixelRect(0, 0, 64, 160)), overlay);
        var second = AddOverlay(content.Crop(new PixelRect(0, 70, 64, 160)), overlay);
        var third = AddOverlay(content.Crop(new PixelRect(0, 140, 64, 160)), overlay);

        var result = new VerticalScrollStitcher().Stitch([first, second, third]);

        Assert.IsTrue(result.IsComplete, result.FailureReason);
        Assert.AreEqual(300, result.Image.Height);
        var expected = AddOverlay(content, new PixelRect(overlay.X, 140 + overlay.Y, overlay.Width, overlay.Height));
        CollectionAssert.AreEqual(expected.Pixels, result.Image.Pixels);
    }

    [TestMethod]
    public void StitcherHandlesFixedFooterAndFloatingOverlayTogether()
    {
        var content = GradientImage(64, 300);
        var overlay = new PixelRect(48, 104, 12, 20);
        var first = AddOverlay(AddFixedFooter(content.Crop(new PixelRect(0, 0, 64, 160)), 32), overlay);
        var second = AddOverlay(AddFixedFooter(content.Crop(new PixelRect(0, 70, 64, 160)), 32), overlay);
        var third = AddOverlay(AddFixedFooter(content.Crop(new PixelRect(0, 140, 64, 160)), 32), overlay);

        var result = new VerticalScrollStitcher().Stitch([first, second, third]);

        Assert.IsTrue(result.IsComplete, result.FailureReason);
        var expected = AddOverlay(
            AddFixedFooter(content, 32),
            new PixelRect(overlay.X, 140 + overlay.Y, overlay.Width, overlay.Height));
        CollectionAssert.AreEqual(expected.Pixels, result.Image.Pixels);
    }

    [TestMethod]
    public void StitcherStopsBeforeAnUnreliableJoin()
    {
        var first = SolidImage(32, 100, 0, 0, 0);
        var second = SolidImage(32, 100, 255, 255, 255);

        var result = new VerticalScrollStitcher().Stitch([first, second]);

        Assert.IsFalse(result.IsComplete);
        Assert.AreEqual(first.Height, result.Image.Height);
        StringAssert.Contains(result.FailureReason, "reliably");
    }

    [TestMethod]
    public void StitcherRejectsAmbiguousFlatFrames()
    {
        var first = SolidImage(64, 160, 245, 245, 245);
        var second = SolidImage(64, 160, 245, 245, 245);

        var result = new VerticalScrollStitcher().Stitch([first, second]);

        Assert.IsFalse(result.IsComplete);
        Assert.AreEqual(first.Height, result.Image.Height);
        StringAssert.Contains(result.FailureReason, "reliably");
    }

    [TestMethod]
    public void StitcherRejectsUpwardMovement()
    {
        var source = GradientImage(32, 240);
        var first = source.Crop(new PixelRect(0, 80, 32, 140));
        var movedUp = source.Crop(new PixelRect(0, 0, 32, 140));

        var result = new VerticalScrollStitcher().Stitch([first, movedUp]);

        Assert.IsFalse(result.IsComplete);
        Assert.AreEqual(first.Height, result.Image.Height);
        StringAssert.Contains(result.FailureReason, "moved upward");
    }

    [TestMethod]
    public async Task TranslationStreamParserCombinesIncrementalContent()
    {
        const string stream = "event: ping\ndata: heartbeat\n\n"
                              + "data: {\"choices\":[{\"delta\":{\"content\":\"你\"}}]}\n\n"
                              + "data: {\"choices\":[{\"delta\":{\"content\":\"好\"}}]}\n\n"
                              + "data: [DONE]\n\n";
        using var response = new System.Net.Http.HttpResponseMessage
        {
            Content = new System.Net.Http.StringContent(stream)
        };
        var pieces = new List<string>();
        await foreach (var piece in OpenAiTranslationProvider.ReadServerSentEventsAsync(response, CancellationToken.None))
        {
            pieces.Add(piece);
        }

        CollectionAssert.AreEqual(new[] { "你", "好" }, pieces);
    }

    [TestMethod]
    public void TranslationEndpointAcceptsBaseOrFullChatCompletionsUrl()
    {
        Assert.AreEqual(
            "https://example.test/v1/chat/completions",
            OpenAiTranslationProvider.BuildEndpoint("https://example.test/v1"));
        Assert.AreEqual(
            "https://example.test/v1/chat/completions",
            OpenAiTranslationProvider.BuildEndpoint("https://example.test/v1/chat/completions/"));
    }

    [TestMethod]
    public void TranslationErrorDetailReadsCompatibleJsonMessage()
    {
        var detail = OpenAiTranslationProvider.ExtractErrorDetail("{\"error\":{\"message\":\"model is unavailable\"}}");

        Assert.AreEqual("model is unavailable", detail);
    }

    [TestMethod]
    public async Task NaturalTranslationUsesContextAwarePromptAndStreamsResult()
    {
        string? requestBody = null;
        using var client = new HttpClient(new StubHttpHandler(async (request, cancellationToken) =>
        {
            Assert.AreEqual("https://open.bigmodel.cn/api/paas/v4/chat/completions", request.RequestUri?.ToString());
            requestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            const string stream = "data: {\"choices\":[{\"delta\":{\"content\":\"智能体能力迎来重大升级，\"}}]}\n\n"
                                  + "data: {\"choices\":[{\"delta\":{\"content\":\"生产效率显著提升！\"}}]}\n\n"
                                  + "data: [DONE]\n\n";
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(stream)
            };
        }));
        var provider = new OnlineTranslationProvider(client);
        var profile = new AppSettings().Translation with
        {
            Provider = TranslationProviderKind.OpenAiCompatible,
            BaseAddress = "https://open.bigmodel.cn/api/paas/v4",
            Model = "natural-model"
        };

        var result = await provider.TranslateAsync(
            "Major Agent upgrades with strong production gains!",
            "en-US",
            profile,
            new TranslationCredentials(ApiKey: "test-key"));

        Assert.AreEqual("智能体能力迎来重大升级，生产效率显著提升！", result.Text);
        Assert.AreEqual("AI 自然翻译", result.ProviderName);
        Assert.IsFalse(result.UsedFallback);
        using var body = System.Text.Json.JsonDocument.Parse(requestBody!);
        Assert.AreEqual(0.2, body.RootElement.GetProperty("temperature").GetDouble(), 0.001);
        Assert.AreEqual("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        var systemPrompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        StringAssert.Contains(systemPrompt, "Do not translate word by word");
        StringAssert.Contains(systemPrompt, "never \"代理商\"");
    }

    [TestMethod]
    public async Task NaturalTranslationFallsBackToTencentWhenAiServiceFails()
    {
        var requests = 0;
        using var client = new HttpClient(new StubHttpHandler((request, _) =>
        {
            requests++;
            if (request.RequestUri?.Host == "example.test")
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"invalid api key\"}}")
                });
            }
            Assert.AreEqual("tmt.tencentcloudapi.com", request.RequestUri?.Host);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"Response\":{\"TargetText\":\"腾讯保底译文\",\"RequestId\":\"test\"}}")
            });
        }));
        var provider = new OnlineTranslationProvider(client);
        var profile = new AppSettings().Translation with
        {
            Provider = TranslationProviderKind.OpenAiCompatible,
            BaseAddress = "https://example.test/v1",
            Model = "natural-model",
            EnableMyMemoryFallback = false
        };

        var result = await provider.TranslateAsync(
            "Hello world",
            "en-US",
            profile,
            new TranslationCredentials("test-key", "test-id", "test-secret"));

        Assert.AreEqual(2, requests);
        Assert.AreEqual("腾讯保底译文", result.Text);
        Assert.AreEqual("腾讯机器翻译", result.ProviderName);
        Assert.IsTrue(result.UsedFallback);
        StringAssert.Contains(result.PrimaryFailure, "AI 自然翻译");
    }

    [TestMethod]
    public async Task NaturalTranslationRetriesTransientOverloadThenSucceeds()
    {
        var requests = 0;
        using var client = new HttpClient(new StubHttpHandler((request, _) =>
        {
            requests++;
            if (requests < 3)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"该模型当前访问量过大\"}}")
                });
            }
            var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"重试后的译文\"}}]}\n\ndata: [DONE]\n\n"));
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            });
        }));
        var provider = new OnlineTranslationProvider(client);
        var profile = new AppSettings().Translation with
        {
            Provider = TranslationProviderKind.OpenAiCompatible,
            BaseAddress = "https://example.test/v1",
            Model = "natural-model",
            EnableMyMemoryFallback = false,
            EnableFastFallbackRace = false
        };

        var result = await provider.TranslateAsync(
            "Hello world",
            "en-US",
            profile,
            new TranslationCredentials("test-key", "test-id", "test-secret"));

        Assert.AreEqual(3, requests);
        Assert.AreEqual("重试后的译文", result.Text);
        Assert.IsFalse(result.UsedFallback);
    }

    [TestMethod]
    public async Task NaturalTranslationSwitchesToFastTencentWhenAiIsSlow()
    {
        using var client = new HttpClient(new StubHttpHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.Host == "tmt.tencentcloudapi.com")
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"Response\":{\"TargetText\":\"腾讯快速译文\",\"RequestId\":\"test\"}}")
                };
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "data: {\"choices\":[{\"delta\":{\"content\":\"太慢的AI译文\"}}]}\n\ndata: [DONE]\n\n"));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
        }));
        var provider = new OnlineTranslationProvider(client);
        var profile = new AppSettings().Translation with
        {
            Provider = TranslationProviderKind.OpenAiCompatible,
            BaseAddress = "https://slow.test/v1",
            Model = "primary-model",
            EnableFastFallbackRace = true,
            FastFallbackDelaySeconds = 2,
            EnableMyMemoryFallback = false
        };

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await provider.TranslateAsync(
            "Hello world, please translate this sentence.",
            "en-US",
            profile,
            new TranslationCredentials("test-key", "test-id", "test-secret"));

        Assert.AreEqual("腾讯快速译文", result.Text);
        Assert.AreEqual("腾讯机器翻译", result.ProviderName);
        Assert.IsTrue(result.UsedFallback);
        StringAssert.Contains(result.PrimaryFailure, "超过 2 秒");
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(4), $"fast fallback took {timer.Elapsed.TotalSeconds:F1}s");
    }

    [TestMethod]
    public async Task NaturalTranslationRacingReturnsTheFastestPlausibleResult()
    {
        var fastText = string.Concat(Enumerable.Repeat("快速译文", 8));
        var slowText = string.Concat(Enumerable.Repeat("慢速译文", 20));
        using var client = new HttpClient(new StubHttpHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri?.Host == "fast.test")
            {
                var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                    $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{fastText}\"}}}}]}}\n\ndata: [DONE]\n\n"));
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream)
                };
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var slowStream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{slowText}\"}}}}]}}\n\ndata: [DONE]\n\n"));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StreamContent(slowStream)
            };
        }));
        var provider = new OnlineTranslationProvider(client);
        var profile = new AppSettings().Translation with
        {
            Provider = TranslationProviderKind.OpenAiCompatible,
            BaseAddress = "https://fast.test/v1",
            Model = "primary-model",
            EnableRacing = true,
            RacingBaseAddress = "https://slow.test/v1",
            RacingModel = "slow-model",
            EnableMyMemoryFallback = false
        };

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await provider.TranslateAsync(
            "Hello world, please translate this sentence.",
            "en-US",
            profile,
            new TranslationCredentials("primary-key", "test-id", "test-secret", "racing-key"));

        Assert.AreEqual(fastText, result.Text);
        Assert.IsTrue(result.ProviderName.Contains("primary-model", StringComparison.Ordinal));
        Assert.IsFalse(result.UsedFallback);
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(1.5), $"race took {timer.Elapsed.TotalSeconds:F1}s");
    }

    [TestMethod]
    public void TencentTc3SignatureMatchesIndependentReference()
    {
        const string payload = "{\"SourceText\":\"hello\",\"Source\":\"auto\",\"Target\":\"zh\",\"ProjectId\":0}";

        var authorization = TencentTranslationProvider.CreateAuthorization(
            "AKIDEXAMPLE",
            "test-secret-key",
            payload,
            1551113065);

        Assert.AreEqual(
            "TC3-HMAC-SHA256 Credential=AKIDEXAMPLE/2019-02-25/tmt/tc3_request, SignedHeaders=content-type;host, Signature=c319b4a995151594bc771eac854db8104fa942e8d6c554e70c352463ed94c13f",
            authorization);
    }

    [TestMethod]
    public void TranslationChunkerHonorsMyMemoryUtf8ByteLimit()
    {
        var chunks = TranslationTextChunker.SplitByUtf8Bytes(string.Concat(Enumerable.Repeat("你好 world。", 90)), 450);

        Assert.IsTrue(chunks.Count > 1);
        Assert.IsTrue(chunks.All(chunk => System.Text.Encoding.UTF8.GetByteCount(chunk) <= 450));
    }

    [TestMethod]
    public void TranslationChunkerNeverSplitsAProtectionMarker()
    {
        const string marker = "QZXKEEPABCWZXQ";
        var chunks = TranslationTextChunker.SplitByCharacters(new string('a', 20) + marker + new string('b', 20), 25);

        Assert.IsTrue(chunks.All(chunk => !chunk.Contains("QZXKEEP", StringComparison.Ordinal) || chunk.Contains(marker, StringComparison.Ordinal)));
        Assert.AreEqual(
            1,
            chunks.Count(chunk => chunk.Contains(marker, StringComparison.Ordinal)),
            string.Join(" | ", chunks));
    }

    [TestMethod]
    public void TranslationProtectionPreservesPricesHandlesUrlsAndMultipliers()
    {
        const string source = "@LuminaBench: DeepSeek V4 Pro costs $0.435 / $0.87; 3x input and 4.6x output. https://example.test/v4 #AI";

        var protectedText = TranslationTextProtector.Protect(source);
        var simulatedTranslation = protectedText.Text
            .Replace("The ", "这项 ", StringComparison.Ordinal)
            .Replace(" pricing is ", " 定价为 ", StringComparison.Ordinal)
            .Replace(" input and ", " 输入和 ", StringComparison.Ordinal)
            .Replace(" output", " 输出", StringComparison.Ordinal);
        var restored = protectedText.Restore(simulatedTranslation);

        StringAssert.Contains(restored, "@LuminaBench");
        StringAssert.Contains(restored, "DeepSeek V4 Pro");
        StringAssert.Contains(restored, "$0.435 / $0.87");
        StringAssert.Contains(restored, "3x");
        StringAssert.Contains(restored, "4.6x");
        StringAssert.Contains(restored, "https://example.test/v4");
        StringAssert.Contains(restored, "#AI");
        Assert.IsFalse(restored.Contains("QZXKEEP", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TranslationProtectionRestoresMarkersEvenWhenEngineAddsSpaces()
    {
        var protectedText = TranslationTextProtector.Protect("Cost: $1.98");
        var marker = protectedText.Text[(protectedText.Text.IndexOf(':') + 1)..].Trim();
        var spacedMarker = string.Join(' ', marker.ToCharArray());

        Assert.AreEqual("价格：$1.98", protectedText.Restore("价格：" + spacedMarker));
    }

    [TestMethod]
    public void TranslationProtectionRestoresMarkersMutatedByProvider()
    {
        var protectedText = TranslationTextProtector.Protect("Cost $1.98, speed 3x");
        var mutated = protectedText.Text.Replace("KEEP", "KEEPP", StringComparison.Ordinal);

        var restored = protectedText.Restore(mutated);

        Assert.AreEqual("Cost $1.98, speed 3x", restored);
        Assert.IsFalse(restored.Contains("QZX", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TranslationProtectionDroppedMarkerDoesNotStealLaterMarkers()
    {
        var protectedText = TranslationTextProtector.Protect("Cost $1.98, speed 3x");
        // The engine drops the first marker entirely; the second stays intact.
        var dropped = protectedText.Text.Replace(
            "QZXKEEPAWZXQ", string.Empty, StringComparison.OrdinalIgnoreCase);

        var restored = protectedText.Restore(dropped);

        // The surviving marker must restore to ITS own original — the dropped
        // value's original may not be displaced onto it.
        StringAssert.Contains(restored, "3x");
        Assert.IsFalse(restored.Contains("$1.98", StringComparison.Ordinal));
        Assert.IsFalse(restored.Contains("QZX", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TranslationProtectionRestoresMutatedMarkerBeforeIntactLaterMarker()
    {
        var protectedText = TranslationTextProtector.Protect("Cost $1.98, speed 3x");
        // Mutate only the first marker (doubled P); the second stays intact.
        var mutated = protectedText.Text.Replace(
            "QZXKEEPAWZXQ", "QZXKEEPPAWZXQ", StringComparison.OrdinalIgnoreCase);

        var restored = protectedText.Restore(mutated);

        Assert.AreEqual("Cost $1.98, speed 3x", restored);
    }

    [TestMethod]
    public void TranslationBaseAddressMustUseHttps()
    {
        Assert.IsNull(TranslationEndpointRules.ValidateBaseAddress("https://open.bigmodel.cn/api/paas/v4"));
        Assert.IsNotNull(TranslationEndpointRules.ValidateBaseAddress("http://open.bigmodel.cn/api/paas/v4"));
        Assert.IsNotNull(TranslationEndpointRules.ValidateBaseAddress("open.bigmodel.cn"));
        Assert.IsNotNull(TranslationEndpointRules.ValidateBaseAddress(string.Empty));
        Assert.IsNotNull(TranslationEndpointRules.ValidateBaseAddress(null));
    }

    [TestMethod]
    public void TranslationProtectionDoesNotHideOrdinaryEnglishWords()
    {
        const string source = "Old news actually from a bunch of days ago, but crossed that 15M.";

        var protectedText = TranslationTextProtector.Protect(source);

        Assert.AreEqual(source, protectedText.Text);
    }

    [TestMethod]
    public void OcrTextFormatterRemovesCjkSpacingAndJoinsWrappedLines()
    {
        var document = new OcrDocument(
            "zh-CN",
            string.Empty,
            new[]
            {
                new OcrLine("Windows OCR 会 自 动", new PixelRect(0, 0, 180, 20), Array.Empty<OcrWord>()),
                new OcrLine("缩 小 图 像。", new PixelRect(0, 22, 130, 20), Array.Empty<OcrWord>()),
                new OcrLine("• 下 一 项", new PixelRect(0, 48, 100, 20), Array.Empty<OcrWord>())
            });

        Assert.AreEqual(
            "Windows OCR 会自动缩小图像。" + Environment.NewLine + "• 下一项",
            OcrTextFormatter.Format(document));
    }

    [TestMethod]
    public void OcrTextFormatterPreservesHeadingsListsAndIndentation()
    {
        var document = new OcrDocument(
            "zh-CN",
            string.Empty,
            new[]
            {
                new OcrLine("2. 翻译", new PixelRect(10, 0, 88, 20), Array.Empty<OcrWord>()),
                new OcrLine("○ 修复普通英文。", new PixelRect(38, 24, 170, 20), Array.Empty<OcrWord>()),
                new OcrLine("○ 翻译前清理文本。", new PixelRect(38, 48, 190, 20), Array.Empty<OcrWord>()),
                new OcrLine("3. 长截图", new PixelRect(10, 82, 105, 20), Array.Empty<OcrWord>())
            });

        Assert.AreEqual(
            "2. 翻译" + Environment.NewLine
            + "  ○ 修复普通英文。" + Environment.NewLine
            + "  ○ 翻译前清理文本。" + Environment.NewLine + Environment.NewLine
            + "3. 长截图",
            OcrTextFormatter.Format(document));
    }

    [TestMethod]
    public void TranslationNormalizationRepairsLikelyDollarOcrWithoutChangingOrdinarySNumbers()
    {
        Assert.AreEqual(
            "Off peak: $0.66 input / $1.98 output",
            TranslationTextProtector.NormalizeLikelyOcrArtifacts("Off peak: S0.66 input / S1.98 output"));
        Assert.AreEqual(
            "Section S1.98",
            TranslationTextProtector.NormalizeLikelyOcrArtifacts("Section S1.98"));
    }

    [TestMethod]
    public void NewSettingsDefaultToTencentWithMyMemoryFallback()
    {
        var settings = new AppSettings();

        Assert.AreEqual(TranslationProviderKind.Tencent, settings.Translation.Provider);
        Assert.IsTrue(settings.Translation.EnableMyMemoryFallback);
        Assert.AreEqual("ap-guangzhou", settings.Translation.TencentRegion);
    }

    [TestMethod]
    public void NewSettingsDefaultToCloudFirstOcrAndCloudTranslationEnhancement()
    {
        var settings = new AppSettings();

        Assert.AreEqual(OcrProviderMode.CloudFirst, settings.Ocr.Mode);
        Assert.IsTrue(settings.Ocr.UseCloudForTranslation);
        Assert.AreEqual(2500, settings.Ocr.CloudTimeoutMilliseconds);
    }

    [TestMethod]
    public void OcrQualityOnlyEscalatesUncertainLocalResults()
    {
        var image = SolidImage(400, 180, 248, 248, 248);
        Paint(image, new PixelRect(20, 40, 300, 12), 20, 20, 20);
        var confident = new OcrDocument(
            "zh-CN",
            "这是一段清晰的文字",
            [new OcrLine("这是一段清晰的文字", new PixelRect(20, 40, 240, 28), [], 0.97)])
        {
            Confidence = 0.97
        };
        var uncertain = confident with { Confidence = 0.61 };

        Assert.IsFalse(OcrQualityEvaluator.NeedsCloudEnhancement(confident, image, 0.88));
        Assert.IsTrue(OcrQualityEvaluator.NeedsCloudEnhancement(uncertain, image, 0.88));
    }

    [TestMethod]
    public void OcrQualityEscalatesOnlyWhenMeaningfulShareOfLinesIsUncertain()
    {
        var image = SolidImage(400, 400, 248, 248, 248);
        Paint(image, new PixelRect(20, 40, 300, 12), 20, 20, 20);

        static OcrDocument WithLines(int total, int lowConfidence)
        {
            var lines = new List<OcrLine>();
            for (var index = 0; index < total; index++)
            {
                lines.Add(new OcrLine(
                    "文字",
                    new PixelRect(20, 40 + index * 20, 80, 16),
                    [],
                    index < lowConfidence ? 0.5 : 0.95));
            }
            return new OcrDocument(
                "zh-CN",
                string.Join(Environment.NewLine, lines.Select(line => line.Text)),
                lines)
            {
                Confidence = 0.95
            };
        }

        // A single weak line must not trigger the paid cloud service…
        Assert.IsFalse(OcrQualityEvaluator.NeedsCloudEnhancement(WithLines(10, 1), image, 0.88));
        // …but a meaningful share of weak lines must.
        Assert.IsTrue(OcrQualityEvaluator.NeedsCloudEnhancement(WithLines(10, 3), image, 0.88));
    }

    [TestMethod]
    public void TencentOcrParserPreservesCoordinatesConfidenceAndProvider()
    {
        const string json = """
            {
              "Response": {
                "TextDetections": [
                  {
                    "DetectedText": "倾斜文字",
                    "Confidence": 96,
                    "Polygon": [
                      { "X": 20, "Y": 10 },
                      { "X": 120, "Y": 12 },
                      { "X": 118, "Y": 42 },
                      { "X": 18, "Y": 40 }
                    ]
                  }
                ],
                "Angle": 1.2,
                "RequestId": "test"
              }
            }
            """;

        var document = TencentOcrResponseParser.Parse(json, 0.5, 0.5);

        Assert.AreEqual("倾斜文字", document.Text);
        Assert.AreEqual(new PixelRect(9, 5, 51, 16), document.Lines[0].Bounds);
        Assert.AreEqual(0.96, document.Confidence, 0.0001);
        Assert.AreEqual("腾讯云高精度 OCR", document.ProviderName);
        Assert.IsTrue(document.UsedOnlineService);
    }

    [TestMethod]
    public void TencentOcrParserSurfacesApiErrors()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            TencentOcrResponseParser.Parse(
                "{\"Response\":{\"Error\":{\"Code\":\"AuthFailure\",\"Message\":\"denied\"}}}"));

        StringAssert.Contains(exception.Message, "AuthFailure");
        StringAssert.Contains(exception.Message, "denied");
    }

    [TestMethod]
    public void OcrFusionUsesCloudLineAndKeepsConfidentMissingLocalLine()
    {
        var local = new OcrDocument(
            "zh-CN",
            "倾斜文宁\n本地补充",
            [
                new OcrLine("倾斜文宁", new PixelRect(10, 10, 120, 24), [], 0.72),
                new OcrLine("本地补充", new PixelRect(10, 50, 100, 24), [], 0.94)
            ])
        {
            Confidence = 0.83,
            ProviderName = "PP-OCRv6 本地"
        };
        var cloud = new OcrDocument(
            "zh-CN",
            "倾斜文字",
            [new OcrLine("倾斜文字", new PixelRect(9, 9, 122, 26), [], 0.96)])
        {
            Confidence = 0.96,
            ProviderName = "腾讯云高精度 OCR",
            UsedOnlineService = true
        };

        var fused = OcrQualityEvaluator.Fuse(local, cloud);

        Assert.AreEqual("倾斜文字" + Environment.NewLine + "本地补充", fused.Text);
        Assert.AreEqual(2, fused.Lines.Count);
        Assert.IsTrue(fused.UsedOnlineService);
    }

    [TestMethod]
    public async Task MissingTencentCredentialsFallsBackToMyMemory()
    {
        using var client = new HttpClient(new StubHttpHandler((request, _) =>
        {
            Assert.AreEqual("api.mymemory.translated.net", request.RequestUri?.Host);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"responseData\":{\"translatedText\":\"你好 &amp; 欢迎\"},\"responseStatus\":200}")
            });
        }));
        var provider = new OnlineTranslationProvider(client);

        var result = await provider.TranslateAsync(
            "Hello and welcome",
            "en-US",
            new AppSettings().Translation,
            new TranslationCredentials());

        Assert.AreEqual("你好 & 欢迎", result.Text);
        Assert.IsTrue(result.UsedFallback);
        Assert.AreEqual("MyMemory 基础翻译", result.ProviderName);
        StringAssert.Contains(result.PrimaryFailure, "SecretId");
    }

    [TestMethod]
    public void SettingsRoundTripDoesNotNeedImageOrOcrHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Screen-{Guid.NewGuid():N}", "settings.json");
        var store = new JsonSettingsStore(path);
        var settings = new AppSettings
        {
            OcrLanguage = "ja-JP",
            StartWithWindows = true,
            HotKey = "Alt+Shift+S",
            ClickThroughRecoveryHotKey = "F2",
            HasShownClickThroughHint = true
        };
        try
        {
            store.Save(settings);
            var loaded = store.Load();
            Assert.AreEqual("ja-JP", loaded.OcrLanguage);
            Assert.AreEqual(OcrProviderMode.CloudFirst, loaded.Ocr.Mode);
            Assert.IsTrue(loaded.StartWithWindows);
            Assert.AreEqual("Alt+Shift+S", loaded.HotKey);
            Assert.AreEqual("F2", loaded.ClickThroughRecoveryHotKey);
            Assert.IsTrue(loaded.HasShownClickThroughHint);
            var json = File.ReadAllText(path);
            Assert.IsFalse(json.Contains("ocrText", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(json.Contains("image", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path)))
            {
                Directory.Delete(Path.GetDirectoryName(path)!, true);
            }
        }
    }

    [TestMethod]
    public void LegacyZhipuOcrTranslationSettingsMigrateToNaturalTranslation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Screen-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        var settings = new AppSettings
        {
            SchemaVersion = 2,
            Translation = new TranslationProfile(
                "https://open.bigmodel.cn/api/paas/v4/layout_parsing",
                "glm-ocr",
                "Screen.Translation",
                "简体中文")
            {
                Provider = TranslationProviderKind.Tencent
            }
        };
        try
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(settings));

            var loaded = new JsonSettingsStore(path).Load();

            Assert.AreEqual(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.AreEqual(TranslationProviderKind.OpenAiCompatible, loaded.Translation.Provider);
            Assert.AreEqual("https://open.bigmodel.cn/api/paas/v4", loaded.Translation.BaseAddress);
            Assert.AreEqual("glm-4.7-flash", loaded.Translation.Model);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void CorruptSettingsArePreservedBeforeDefaultsAreReturned()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Screen-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, "{not valid json");
        try
        {
            var loaded = new JsonSettingsStore(path).Load();

            Assert.AreEqual(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.IsFalse(File.Exists(path));
            Assert.AreEqual(1, Directory.GetFiles(directory, "settings.json.corrupt-*").Length);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void LockedSettingsFileMakesLoadThrowInsteadOfReturningDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Screen-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { HotKey = "Ctrl+Alt+A" });
        try
        {
            // Simulate a transient antivirus/indexer lock: an exclusive read
            // handle makes File.ReadAllText fail with a sharing violation.
            // Load must not swallow it into defaults — Update()'s
            // read-modify-write would then persist them over the user's file.
            using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.ThrowsExactly<IOException>(() => store.Load());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void LockedSettingsFilePreventsUpdateFromPersistingDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Screen-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        var store = new JsonSettingsStore(path);
        store.Save(new AppSettings { HotKey = "Ctrl+Alt+A" });
        try
        {
            using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.ThrowsExactly<IOException>(() => store.Update(latest => latest.HotKey = "F9"));

            // The failed update must leave the persisted settings untouched.
            lockHandle.Dispose();
            Assert.AreEqual("Ctrl+Alt+A", store.Load().HotKey);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void SettingsUpdateMergesIntoTheLatestOnDiskDocument()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Screen-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        var store = new JsonSettingsStore(path);
        try
        {
            store.Save(new AppSettings { HotKey = "Ctrl+Alt+A", StartWithWindows = false });

            // Simulate a runtime writer (save-directory update, hint flag)
            // persisting changes after the dialog loaded its own snapshot.
            var runtime = store.Load();
            runtime.SaveDirectory = @"D:\Screenshots";
            runtime.HasShownClickThroughHint = true;
            store.Save(runtime);

            store.Update(latest =>
            {
                latest.HotKey = "Ctrl+Alt+S";
                latest.StartWithWindows = true;
            });

            var merged = store.Load();
            Assert.AreEqual("Ctrl+Alt+S", merged.HotKey);
            Assert.IsTrue(merged.StartWithWindows);
            Assert.AreEqual(@"D:\Screenshots", merged.SaveDirectory);
            Assert.IsTrue(merged.HasShownClickThroughHint);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void SettingsSaveDoesNotLeaveTemporaryFilesBehind()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Screen-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "settings.json");
        var store = new JsonSettingsStore(path);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path + ".tmp", "half-written by a crashed save");
            store.Save(new AppSettings { HotKey = "Ctrl+Alt+A" });

            Assert.IsFalse(File.Exists(path + ".tmp"));
            Assert.AreEqual("Ctrl+Alt+A", store.Load().HotKey);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void HotKeyGrammarParsesModifiersKeyNamesAndFlags()
    {
        Assert.IsTrue(HotKeyGrammar.TryParse("Ctrl+Alt+A", out var hotKey));
        Assert.AreEqual(HotKeyGrammar.ModifierFlags.Control | HotKeyGrammar.ModifierFlags.Alt, hotKey.Modifiers);
        Assert.AreEqual("A", hotKey.KeyName);

        Assert.IsTrue(HotKeyGrammar.TryParse(" ctrl + shift + F12 ", out hotKey));
        Assert.AreEqual(HotKeyGrammar.ModifierFlags.Control | HotKeyGrammar.ModifierFlags.Shift, hotKey.Modifiers);
        Assert.AreEqual("F12", hotKey.KeyName);

        Assert.IsTrue(HotKeyGrammar.TryParse("Control+Win+Oem3", out hotKey));
        Assert.AreEqual(HotKeyGrammar.ModifierFlags.Control | HotKeyGrammar.ModifierFlags.Windows, hotKey.Modifiers);
        Assert.AreEqual("Oem3", hotKey.KeyName);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("+")]
    [DataRow("Ctrl")]
    [DataRow("Ctrl+")]
    [DataRow("None")]
    [DataRow("Ctrl+None")]
    [DataRow("Ctrl+LeftCtrl")]
    [DataRow("Ctrl+LWin")]
    [DataRow("Ctrl+Alt")]
    [DataRow("Ctrl+A+A")]
    [DataRow("Ctrl+A+B")]
    [DataRow("F9")]
    public void HotKeyGrammarRejectsInvalidCombinations(string text)
    {
        Assert.IsFalse(HotKeyGrammar.TryParse(text, out _));
    }

    [TestMethod]
    public void HotKeyGrammarAllowsUnmodifiedFunctionKeysOnlyWhenPermitted()
    {
        Assert.IsFalse(HotKeyGrammar.TryParse("F9", out _, allowUnmodifiedFunctionKey: false));
        Assert.IsTrue(HotKeyGrammar.TryParse("F9", out var recovery, allowUnmodifiedFunctionKey: true));
        Assert.AreEqual(HotKeyGrammar.ModifierFlags.None, recovery.Modifiers);
        Assert.AreEqual("F9", recovery.KeyName);
        Assert.IsFalse(HotKeyGrammar.TryParse("A", out _, allowUnmodifiedFunctionKey: true));
    }

    [TestMethod]
    public void PauseClockAdvancesWithRawTimestamps()
    {
        var now = 0L;
        var clock = new PauseClock(() => now);
        now = TicksFromSeconds(2.5);
        Assert.AreEqual(2.5, clock.ElapsedSeconds, 0.001);
        Assert.IsFalse(clock.IsPaused);
    }

    [TestMethod]
    public void PauseClockExcludesPausedInterval()
    {
        var now = 0L;
        var clock = new PauseClock(() => now);
        now = TicksFromSeconds(2);
        clock.Pause();
        now = TicksFromSeconds(5);
        Assert.AreEqual(2.0, clock.ElapsedSeconds, 0.001);
        clock.Resume();
        now = TicksFromSeconds(6);
        Assert.AreEqual(3.0, clock.ElapsedSeconds, 0.001);
    }

    [TestMethod]
    public void PauseClockMapsTimestampsDuringPauseToPauseStart()
    {
        var now = 0L;
        var clock = new PauseClock(() => now);
        now = TicksFromSeconds(1.5);
        Assert.AreEqual(1.5, clock.ToRecordingSeconds(TicksFromSeconds(1.5)), 0.001);
        clock.Pause();
        now = TicksFromSeconds(9);
        // A frame captured mid-pause collapses onto the pause instant.
        Assert.AreEqual(1.5, clock.ToRecordingSeconds(TicksFromSeconds(8.7)), 0.001);
        clock.Resume();
        now = TicksFromSeconds(10);
        Assert.AreEqual(2.5, clock.ToRecordingSeconds(TicksFromSeconds(10)), 0.001);
    }

    [TestMethod]
    public void PauseClockClampsOutOfOrderTimestamps()
    {
        var now = 0L;
        var clock = new PauseClock(() => now);
        now = TicksFromSeconds(3);
        Assert.AreEqual(3.0, clock.ToRecordingSeconds(TicksFromSeconds(3)), 0.001);
        Assert.AreEqual(3.0, clock.ToRecordingSeconds(TicksFromSeconds(2.2)), 0.001);
        Assert.AreEqual(3.5, clock.ToRecordingSeconds(TicksFromSeconds(3.5)), 0.001);
    }

    [TestMethod]
    public void PauseClockIgnoresRedundantPauseAndResumeCalls()
    {
        var now = 0L;
        var clock = new PauseClock(() => now);
        now = TicksFromSeconds(1);
        clock.Pause();
        clock.Pause();
        now = TicksFromSeconds(4);
        clock.Resume();
        clock.Resume();
        now = TicksFromSeconds(5);
        Assert.AreEqual(2.0, clock.ElapsedSeconds, 0.001);
    }

    [TestMethod]
    public void RecordingDefaultsTarget30FpsAndCtrlShiftR()
    {
        var settings = new AppSettings();
        Assert.AreEqual("Ctrl+Shift+R", settings.RecordHotKey);
        Assert.AreEqual(30, settings.Recording.FrameRate);
        Assert.AreEqual(8_000_000, settings.Recording.VideoBitrate);
        Assert.IsTrue(settings.Recording.ShowCursor);
        Assert.IsTrue(settings.Recording.RecordSystemAudio);
        Assert.IsFalse(settings.Recording.RecordMicrophone);
        Assert.IsFalse(settings.Recording.ShowClickEffects);
        Assert.AreEqual(4_000_000, (settings.Recording with { Quality = RecordingQuality.Smooth }).VideoBitrate);
        Assert.AreEqual(12_000_000, (settings.Recording with { Quality = RecordingQuality.High }).VideoBitrate);
    }

    private static long TicksFromSeconds(double seconds) => (long)(seconds * Stopwatch.Frequency);

    private static PixelBuffer GradientImage(int width, int height)
    {
        var stride = width * 4;
        var bytes = new byte[stride * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                bytes[offset] = (byte)((y * 11 + x * 3) % 251);
                bytes[offset + 1] = (byte)((y * 7 + x * 13) % 253);
                bytes[offset + 2] = (byte)((y * 17 + x * 5) % 255);
                bytes[offset + 3] = 255;
            }
        return new PixelBuffer(bytes, width, height, stride);
    }

    private static PixelBuffer SparseDocumentImage(int width, int height)
    {
        var image = SolidImage(width, height, 245, 245, 245);
        var rows = new[] { 13, 39, 74, 121, 147, 193, 219, 267 };
        for (var index = 0; index < rows.Length; index++)
        {
            Paint(
                image,
                new PixelRect(5 + index % 3, rows[index], width - 12 - index % 5, 1),
                (byte)(30 + index * 17),
                (byte)(55 + index * 11),
                (byte)(80 + index * 7));
        }
        return image;
    }

    private static PixelBuffer SolidImage(int width, int height, byte b, byte g, byte r)
    {
        var stride = width * 4;
        var bytes = new byte[stride * height];
        for (var i = 0; i < bytes.Length; i += 4)
        {
            bytes[i] = b;
            bytes[i + 1] = g;
            bytes[i + 2] = r;
            bytes[i + 3] = 255;
        }
        return new PixelBuffer(bytes, width, height, stride);
    }

    private static PixelBuffer AddFixedFooter(PixelBuffer image, int height)
    {
        var result = new PixelBuffer(image.Pixels.ToArray(), image.Width, image.Height, image.Stride);
        var footer = new PixelRect(0, image.Height - height, image.Width, height);
        Paint(result, footer, 48, 52, 60);
        Paint(result, new PixelRect(3, footer.Y + 2, image.Width - 6, 2), 105, 112, 124);
        Paint(result, new PixelRect(8, footer.Y + 12, image.Width / 2, 5), 175, 181, 190);
        return result;
    }

    private static PixelBuffer AddOverlay(PixelBuffer image, PixelRect rectangle)
    {
        var result = new PixelBuffer(image.Pixels.ToArray(), image.Width, image.Height, image.Stride);
        Paint(result, rectangle, 35, 95, 220);
        Paint(result, new PixelRect(rectangle.X + 3, rectangle.Y + 3, rectangle.Width - 6, rectangle.Height - 6), 235, 240, 250);
        return result;
    }

    private static void Paint(PixelBuffer image, PixelRect rectangle, byte b, byte g, byte r)
    {
        var clipped = rectangle.Intersect(new PixelRect(0, 0, image.Width, image.Height));
        for (var y = clipped.Y; y < clipped.Bottom; y++)
        {
            for (var x = clipped.X; x < clipped.Right; x++)
            {
                var offset = y * image.Stride + x * 4;
                image.Pixels[offset] = b;
                image.Pixels[offset + 1] = g;
                image.Pixels[offset + 2] = r;
                image.Pixels[offset + 3] = 255;
            }
        }
    }

    private static PixelBuffer Join(params PixelBuffer[] parts)
    {
        var width = parts[0].Width;
        var stride = width * 4;
        var height = parts.Sum(p => p.Height);
        var bytes = new byte[stride * height];
        var y = 0;
        foreach (var part in parts)
        {
            for (var row = 0; row < part.Height; row++)
            {
                part.Pixels.AsSpan(row * part.Stride, stride).CopyTo(bytes.AsSpan((y + row) * stride, stride));
            }
            y += part.Height;
        }
        return new PixelBuffer(bytes, width, height, stride);
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
}
