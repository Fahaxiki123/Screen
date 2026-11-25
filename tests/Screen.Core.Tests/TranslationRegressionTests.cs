using System.Net;
using System.Text;
using System.Text.Json;

namespace Screen.Core.Tests;

[TestClass]
public sealed class TranslationRegressionTests
{
    private const string TextEvent = "data: {\"choices\":[{\"delta\":{\"content\":\"部分译文\"}}]}\n\n";

    [TestMethod]
    public async Task AiStreamIgnoresBlankFinishReasonsAndNeverEmitsSplitThinkTagsAsync()
    {
        var stream = AiEvent("译文<TH", "")
                     + AiEvent("INK>private<thi", " ")
                     + AiEvent("nk>nested</TH")
                     + AiEvent("INK></th")
                     + AiEvent("ink>继续<")
                     + AiEvent("br><thi", "stop")
                     + "data: [DONE]\n\n";
        using var response = SseResponse(stream);

        var visible = new List<string>();
        await foreach (var fragment in OpenAiTranslationProvider.ReadServerSentEventsAsync(response, CancellationToken.None))
        {
            visible.Add(fragment);
        }

        CollectionAssert.AreEqual(new[] { "译文", "继续", "<br>" }, visible);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompletedStreamPreservesATrailingLessThanSignAsync(bool doneMarker)
    {
        var stream = AiEvent("译文 <") + (doneMarker
            ? "data: [DONE]\n\n"
            : AiEvent(string.Empty, "stop"));
        using var response = SseResponse(stream);
        var visible = new StringBuilder();
        await foreach (var fragment in OpenAiTranslationProvider.ReadServerSentEventsAsync(response, CancellationToken.None))
        {
            visible.Append(fragment);
        }
        Assert.AreEqual("译文 <", visible.ToString());
    }

    [TestMethod]
    public async Task AiStreamCoalescesProgressDuringABurstAndReportsTheFinalTextAsync()
    {
        const int burstLength = 200;
        var stream = string.Concat(Enumerable.Repeat(AiEvent("a"), burstLength)) + "data: [DONE]\n\n";
        using var client = new HttpClient(new OfflineHandler((_, _) => Task.FromResult(SseResponse(stream))));
        var streamed = new List<string>();

        var result = await new OpenAiTranslationProvider(client).TranslateAsync(
            "Hello world", "en-US", NaturalProfile(), new TranslationCredentials("offline-key"),
            new SynchronousProgress(streamed.Add));

        Assert.AreEqual(new string('a', burstLength), result.Text);
        Assert.AreEqual("a", streamed[0]);
        Assert.AreEqual(result.Text, streamed[^1]);
        Assert.IsTrue(streamed.Count < burstLength, "A burst must not trigger one progress update per SSE fragment.");
    }

    [TestMethod]
    [DataRow(TextEvent)]
    [DataRow("data: [DONE]\n\n")]
    [DataRow("data: {\"choices\":[{\"delta\":{\"content\":\"   \"}}]}\n\ndata: [DONE]\n\n")]
    [DataRow(TextEvent + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\ndata: [DONE]\n\n")]
    [DataRow(TextEvent + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"content_filter\"}]}\n\ndata: [DONE]\n\n")]
    [DataRow(TextEvent + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n")]
    [DataRow(TextEvent + "data: {\"error\":{\"message\":\"upstream failed\"}}\n\ndata: [DONE]\n\n")]
    [DataRow(TextEvent + "event: error\ndata: upstream failed\n\n")]
    public async Task IncompleteOrFailedAiStreamFallsBackAsync(string stream)
    {
        var requests = new List<string>();
        using var client = new HttpClient(new OfflineHandler((request, _) =>
        {
            var host = request.RequestUri!.Host;
            requests.Add(host);
            return Task.FromResult(host == "ai.example.test" ? SseResponse(stream) : MyMemoryResponse());
        }));

        var result = await new OnlineTranslationProvider(client).TranslateAsync(
            "Hello world", "en-US", NaturalProfile(), new TranslationCredentials(ApiKey: "offline-test-key"));

        Assert.AreEqual("完整的保底译文", result.Text);
        Assert.IsTrue(result.UsedFallback);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.PrimaryFailure));
        CollectionAssert.AreEqual(new[] { "ai.example.test", "edge.microsoft.com", "api.mymemory.translated.net" }, requests);
    }

    [TestMethod]
    [DataRow(TextEvent + "data: [DONE]\n\n")]
    [DataRow(TextEvent + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n")]
    [DataRow("event: ping\ndata: heartbeat\n\n" + TextEvent
        + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: {\"choices\":[],\"usage\":{\"total_tokens\":12}}\n\ndata: [DONE]\n\n")]
    public async Task CompletedCompatibleGatewayStreamsRemainSupportedAsync(string stream)
    {
        var requests = 0;
        using var client = new HttpClient(new OfflineHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(SseResponse(stream));
        }));

        var result = await new OnlineTranslationProvider(client).TranslateAsync(
            "Hello world", "en-US", NaturalProfile(), new TranslationCredentials(ApiKey: "offline-test-key"));

        Assert.AreEqual("部分译文", result.Text);
        Assert.IsFalse(result.UsedFallback);
        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public async Task TruncatedRaceCandidateCannotWinAsync()
    {
        using var client = new HttpClient(new OfflineHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "ai.example.test")
            {
                return SseResponse(TextEvent);
            }
            Assert.AreEqual("second.example.test", request.RequestUri.Host);
            await Task.Delay(20, cancellationToken);
            return SseResponse("data: {\"choices\":[{\"delta\":{\"content\":\"完整竞速译文\"}}]}\n\ndata: [DONE]\n\n");
        }));
        var profile = NaturalProfile() with
        {
            EnableRacing = true,
            RacingBaseAddress = "https://second.example.test/v1",
            RacingModel = "second-model"
        };

        var result = await new OnlineTranslationProvider(client).TranslateAsync(
            "Hello", "en-US", profile, new TranslationCredentials(ApiKey: "offline-test-key", RacingApiKey: "offline-racing-key"));

        Assert.AreEqual("完整竞速译文", result.Text);
        StringAssert.Contains(result.ProviderName, "second-model");
        Assert.IsFalse(result.UsedFallback);
    }

    [TestMethod]
    public async Task AiHttpClientTimeoutFallsBackWithoutCallerCancellationAsync()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new OfflineHandler(async (request, cancellationToken) =>
        {
            var host = request.RequestUri!.Host;
            requests.Add(host);
            if (host == "ai.example.test")
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return MyMemoryResponse();
        }))
        {
            Timeout = TimeSpan.FromMilliseconds(50)
        };

        var result = await new OnlineTranslationProvider(client).TranslateAsync(
            "Hello world", "en-US", NaturalProfile(), new TranslationCredentials(ApiKey: "offline-test-key"));

        Assert.IsTrue(result.UsedFallback);
        Assert.AreEqual("完整的保底译文", result.Text);
        StringAssert.Contains(result.PrimaryFailure, "超时");
        CollectionAssert.AreEqual(new[] { "ai.example.test", "edge.microsoft.com", "api.mymemory.translated.net" }, requests);
    }

    [TestMethod]
    public async Task CallerCancellationDoesNotSendTextToFallbackAsync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new OfflineHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref requests);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return MyMemoryResponse();
        }));
        var operation = new OnlineTranslationProvider(client).TranslateAsync(
            "Hello world", "en-US", NaturalProfile(), new TranslationCredentials(ApiKey: "offline-test-key"),
            cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        try
        {
            await operation;
            Assert.Fail("Caller cancellation must propagate.");
        }
        catch (OperationCanceledException)
        {
            Assert.IsTrue(cancellation.IsCancellationRequested);
        }
        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    [DataRow(0x1F600)]
    [DataRow(0x20000)]
    public void Utf8ChunkBoundaryPreservesSupplementaryCharacters(int codePoint)
    {
        var source = new string('a', 447) + char.ConvertFromUtf32(codePoint) + "b";
        var chunks = TranslationTextChunker.SplitByUtf8Bytes(source, 450);

        Assert.AreEqual(2, chunks.Count);
        Assert.AreEqual(source, string.Concat(chunks));
        Assert.IsTrue(chunks.All(chunk => new UTF8Encoding(false, true).GetByteCount(chunk) <= 450));
        Assert.AreEqual(source, string.Concat(chunks.Select(chunk => Uri.UnescapeDataString(Uri.EscapeDataString(chunk)))));
    }

    [TestMethod]
    [DataRow(0x1F600)]
    [DataRow(0x20000)]
    public void CharacterChunkBoundaryPreservesSupplementaryCharacters(int codePoint)
    {
        var source = new string('a', 1899) + char.ConvertFromUtf32(codePoint) + "b";
        var chunks = TranslationTextChunker.SplitByCharacters(source, 1900);

        Assert.AreEqual(2, chunks.Count);
        Assert.AreEqual(source, string.Concat(chunks));
        Assert.IsTrue(chunks.All(chunk => chunk.Length <= 1900));
        foreach (var chunk in chunks)
        {
            new UTF8Encoding(false, true).GetByteCount(chunk);
        }
    }

    [TestMethod]
    public async Task PlainHttpBaseAddressIsRejectedBeforeSendingBearerKeyAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new OfflineHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(SseResponse(TextEvent + "data: [DONE]\n\n"));
        }));
        var profile = NaturalProfile() with
        {
            BaseAddress = "http://ai.example.test/v1",
            EnableMyMemoryFallback = false
        };

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new OpenAiTranslationProvider(client).TranslateAsync(
                "Hello world",
                "en-US",
                profile,
                new TranslationCredentials(ApiKey: "secret-bearer-key")));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new OnlineTranslationProvider(client).TranslateAsync(
                "Hello world",
                "en-US",
                profile,
                new TranslationCredentials(ApiKey: "secret-bearer-key")));
        Assert.AreEqual(0, requests, "Plain-HTTP endpoints must never transmit the Bearer key.");
    }

    [TestMethod]
    public async Task FastFallbackRacePreservesFirstProgressAndFinalTextAsync()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new OfflineHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.Host);
            return Task.FromResult(SseResponse(
                "data: {\"choices\":[{\"delta\":{\"content\":\"第一段、\"}}]}\n\n"
                + "data: {\"choices\":[{\"delta\":{\"content\":\"第二段、\"}}]}\n\n"
                + "data: {\"choices\":[{\"delta\":{\"content\":\"第三段\"}}]}\n\n"
                + "data: [DONE]\n\n"));
        }));
        var streamed = new List<string>();
        var progress = new SynchronousProgress(streamed.Add);
        var profile = new AppSettings().Translation with
        {
            Provider = TranslationProviderKind.OpenAiCompatible,
            BaseAddress = "https://ai.example.test/v1",
            Model = "stream-model",
            EnableFastFallbackRace = true,
            FastFallbackDelaySeconds = 8,
            EnableMyMemoryFallback = false
        };

        var result = await new OnlineTranslationProvider(client).TranslateAsync(
            "First part, second part, third part.",
            "en-US",
            profile,
            new TranslationCredentials("offline-test-key", "tencent-id", "tencent-secret"),
            progress);

        Assert.AreEqual("第一段、第二段、第三段", result.Text);
        Assert.IsFalse(result.UsedFallback);
        CollectionAssert.AreEqual(new[] { "ai.example.test" }, requests);
        Assert.AreEqual("第一段、", streamed[0]);
        Assert.AreEqual(result.Text, streamed[^1]);
    }

    [TestMethod]
    public async Task ImmediateAiAndTencentFailuresDoNotWaitEightSecondsOrDuplicateRequestsAsync()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new OfflineHandler((request, _) =>
        {
            var host = request.RequestUri!.Host;
            lock (requests)
            {
                requests.Add(host);
            }
            return Task.FromResult(host switch
            {
                "ai.example.test" => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"invalid key\"}}")
                },
                "tmt.tencentcloudapi.com" => new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"Response\":{\"Error\":{\"Code\":\"AuthFailure\",\"Message\":\"bad secret\"}}}")
                },
                _ => MyMemoryResponse()
            });
        }));
        var profile = new AppSettings().Translation with
        {
            Provider = TranslationProviderKind.OpenAiCompatible,
            BaseAddress = "https://ai.example.test/v1",
            Model = "primary-model",
            EnableFastFallbackRace = true,
            FastFallbackDelaySeconds = 8,
            EnableMyMemoryFallback = true
        };

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await new OnlineTranslationProvider(client).TranslateAsync(
            "Hello world",
            "en-US",
            profile,
            new TranslationCredentials("bad-ai-key", "bad-tencent-id", "bad-tencent-secret"));

        Assert.AreEqual("完整的保底译文", result.Text);
        Assert.IsTrue(result.UsedFallback);
        StringAssert.Contains(result.PrimaryFailure, "AI 自然翻译");
        StringAssert.Contains(result.PrimaryFailure, "腾讯机器翻译");
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(1.5), $"Expected immediate fallback without 8s sleep, took {timer.Elapsed.TotalSeconds:F2}s");
        CollectionAssert.AreEqual(
            new[] { "ai.example.test", "tmt.tencentcloudapi.com", "edge.microsoft.com", "api.mymemory.translated.net" },
            requests);
    }

    [TestMethod]
    public async Task EdgeProviderTranslatesSingleAndNativeBatchInOneRequestAsync()
    {
        var requests = new List<(string Uri, string Body)>();
        using var client = new HttpClient(new OfflineHandler(async (request, cancellationToken) =>
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            requests.Add((request.RequestUri!.ToString(), body));
            var items = JsonSerializer.Deserialize<string[]>(body)!;
            var payload = items.Select((item, index) => new
            {
                translations = new[] { new { text = $"译文{index}:{item}", to = "zh-Hans" } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }));

        var provider = new OnlineTranslationProvider(client);
        var profile = new AppSettings().Translation with { Provider = TranslationProviderKind.Edge };
        var progressSteps = new List<int>();

        var batch = await provider.TranslateBatchAsync(
            ["First line", "Second line", "Third line"],
            "en-US",
            profile,
            new TranslationCredentials(),
            new SynchronousIntProgress(progressSteps.Add));

        Assert.AreEqual(1, requests.Count, "3 个短区域必须通过单次 Edge 批量数组请求完成。");
        StringAssert.Contains(requests[0].Uri, "to=zh-Hans");
        Assert.AreEqual("免费在线翻译", batch.ProviderName);
        Assert.IsFalse(batch.UsedFallback);
        CollectionAssert.AreEqual(new[] { "译文0:First line", "译文1:Second line", "译文2:Third line" }, batch.Texts.ToArray());
        CollectionAssert.AreEqual(new[] { 3 }, progressSteps);
    }

    [TestMethod]
    public async Task EdgeFailureFallsBackToMyMemoryAsync()
    {
        var hosts = new List<string>();
        using var client = new HttpClient(new OfflineHandler((request, _) =>
        {
            var host = request.RequestUri!.Host;
            hosts.Add(host);
            return Task.FromResult(host == "edge.microsoft.com"
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : MyMemoryResponse());
        }));
        var profile = new AppSettings().Translation with
        {
            Provider = TranslationProviderKind.Edge,
            EnableMyMemoryFallback = true
        };

        var result = await new OnlineTranslationProvider(client).TranslateAsync(
            "Hello world", "en-US", profile, new TranslationCredentials());

        Assert.AreEqual("完整的保底译文", result.Text);
        Assert.IsTrue(result.UsedFallback);
        Assert.AreEqual("MyMemory 基础翻译", result.ProviderName);
        CollectionAssert.AreEqual(new[] { "edge.microsoft.com", "edge.microsoft.com", "api.mymemory.translated.net" }, hosts);
    }

    [TestMethod]
    public async Task DetectLanguageHandlesMixedChineseEnglishAndMyMemoryNeverSendsSameLanguagePairAsync()
    {
        Assert.AreEqual("en-US", OcrQualityEvaluator.DetectLanguage("Click 复制 to save the pricing table from settings."));
        Assert.AreEqual("zh-CN", OcrQualityEvaluator.DetectLanguage("点击 Copy 按钮保存截图配置到当前目录。"));

        string? capturedQuery = null;
        using var client = new HttpClient(new OfflineHandler((request, _) =>
        {
            capturedQuery = request.RequestUri!.Query;
            return Task.FromResult(MyMemoryResponse());
        }));
        var profile = new AppSettings().Translation with
        {
            Provider = TranslationProviderKind.MyMemory,
            TargetLanguage = "简体中文"
        };

        var result = await new OnlineTranslationProvider(client).TranslateAsync(
            "Click 复制 to save settings.",
            "zh-CN",
            profile,
            new TranslationCredentials());

        Assert.AreEqual("完整的保底译文", result.Text);
        Assert.IsNotNull(capturedQuery);
        StringAssert.Contains(Uri.UnescapeDataString(capturedQuery), "langpair=en|zh-CN");
    }

    [TestMethod]
    public void OcrTextFormatterJoinsNarrowEnglishWrappedLinesWithoutMergingShortButtons()
    {
        var document = new OcrDocument(
            "en-US",
            string.Empty,
            [
                new OcrLine("Settings are saved", new PixelRect(20, 10, 150, 20), []),
                new OcrLine("automatically in the background.", new PixelRect(20, 34, 240, 20), []),
                new OcrLine("Apply", new PixelRect(20, 62, 60, 20), []),
                new OcrLine("Cancel", new PixelRect(20, 86, 65, 20), [])
            ]);

        var formatted = OcrTextFormatter.Format(document);

        Assert.AreEqual(
            "Settings are saved automatically in the background." + Environment.NewLine
            + "Apply" + Environment.NewLine
            + "Cancel",
            formatted);
    }

    [TestMethod]
    public async Task AiBatchTranslationPacksRegionsIntoSingleStructuredRequestAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new OfflineHandler((_, _) =>
        {
            requests++;
            var responseJson = JsonSerializer.Serialize(new
            {
                choices = new[]
                {
                    new
                    {
                        finish_reason = "stop",
                        message = new
                        {
                            content = "<think>draft</think>\n```json\n{\"0\":\"保存设置\",\"1\":\"取消\"}\n```"
                        }
                    }
                }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }));

        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(
            ["Save settings", "Cancel"],
            "en-US",
            NaturalProfile() with { EnableMyMemoryFallback = false },
            new TranslationCredentials(ApiKey: "offline-key"));

        Assert.AreEqual(1, requests);
        Assert.IsFalse(result.UsedFallback);
        CollectionAssert.AreEqual(new[] { "保存设置", "取消" }, result.Texts.ToArray());
    }

    [TestMethod]
    public async Task AiBatchFormatMismatchFallsBackToIndividualRegionRequestsAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new OfflineHandler(async (request, cancellationToken) =>
        {
            var call = Interlocked.Increment(ref requests);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (call == 1)
            {
                // 模拟模型漏掉编号 "1"，触发严格的格式校验异常并降级到逐区翻译。
                var malformed = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new { content = "{\"0\":\"第一段\"}" } } }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(malformed, Encoding.UTF8, "application/json")
                };
            }
            var reply = body.Contains("Alpha region", StringComparison.Ordinal) ? "第一段" : "第二段";
            return SseResponse(AiEvent(reply, "stop") + "data: [DONE]\n\n");
        }));

        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(
            ["Alpha region", "Beta region"],
            "en-US",
            NaturalProfile() with { EnableMyMemoryFallback = false },
            new TranslationCredentials(ApiKey: "offline-key"));

        Assert.AreEqual(3, requests, "1 次批量格式错乱 + 2 次逐区降级。");
        CollectionAssert.AreEqual(new[] { "第一段", "第二段" }, result.Texts.ToArray());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, 1)]
    [DataRow(HttpStatusCode.TooManyRequests, 2)]
    public async Task AiBatchAuthOrRateLimitFailureNeverAmplifiesIntoPerRegionAiRequestsAsync(
        HttpStatusCode statusCode,
        int expectedAiCalls)
    {
        var aiCalls = 0;
        var edgeCalls = 0;
        using var client = new HttpClient(new OfflineHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "ai.example.test")
            {
                Interlocked.Increment(ref aiCalls);
                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"blocked\"}}")
                };
            }
            Assert.AreEqual("edge.microsoft.com", request.RequestUri.Host);
            Interlocked.Increment(ref edgeCalls);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var items = JsonSerializer.Deserialize<string[]>(body)!;
            var payload = items.Select((_, index) => new { translations = new[] { new { text = $"保底{index}" } } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }));

        var regions = Enumerable.Range(0, 6).Select(index => $"Region {index}").ToArray();
        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(
            regions,
            "en-US",
            NaturalProfile() with { EnableMyMemoryFallback = true },
            new TranslationCredentials(ApiKey: "offline-key"));

        Assert.AreEqual(expectedAiCalls, aiCalls, "遇到 401/429 时绝不能向同一 AI 端点补发 N 次逐区请求。");
        Assert.AreEqual(1, edgeCalls, "AI 服务级失败应平滑回退至单次 Edge 批量翻译。");
        Assert.IsTrue(result.UsedFallback);
        Assert.AreEqual(6, result.Texts.Count);
    }

    [TestMethod]
    public void LegacyDefaultTencentWithoutCredentialsMigratesToEdgeWhileConfiguredTencentStays()
    {
        var directory = Directory.CreateTempSubdirectory("screen-migrate-");
        try
        {
            var pathWithoutKey = Path.Combine(directory.FullName, "no-key.json");
            File.WriteAllText(pathWithoutKey, """
                {
                  "SchemaVersion": 4,
                  "Translation": { "Provider": 0 }
                }
                """);
            var storeNoKey = new JsonSettingsStore(pathWithoutKey);
            Assert.IsTrue(storeNoKey.TryMigrateLegacyDefaultProvider(() => false));
            var migrated = storeNoKey.Load();
            Assert.AreEqual(TranslationProviderKind.Edge, migrated.Translation.Provider);
            Assert.AreEqual(AppSettings.CurrentSchemaVersion, migrated.SchemaVersion);

            var pathWithKey = Path.Combine(directory.FullName, "with-key.json");
            File.WriteAllText(pathWithKey, """
                {
                  "SchemaVersion": 4,
                  "Translation": { "Provider": 0 }
                }
                """);
            var storeWithKey = new JsonSettingsStore(pathWithKey);
            Assert.IsFalse(storeWithKey.TryMigrateLegacyDefaultProvider(() => true));
            Assert.AreEqual(TranslationProviderKind.Tencent, storeWithKey.Load().Translation.Provider);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void AiTranslationPresetsMatchKnownEndpoints()
    {
        Assert.AreEqual(0, TranslationPresets.FindPresetIndex("https://custom.example.test/v1"));
        Assert.AreEqual(1, TranslationPresets.FindPresetIndex("https://open.bigmodel.cn/api/paas/v4/"));
        Assert.AreEqual(2, TranslationPresets.FindPresetIndex("https://api.deepseek.com/v1"));
        Assert.AreEqual(5, TranslationPresets.FindPresetIndex("https://api.openai.com/v1"));
        Assert.AreEqual("glm-4.7-flash", TranslationPresets.All[1].DefaultModel);
        Assert.AreEqual("deepseek-flash", TranslationPresets.All[2].DefaultModel);
        Assert.AreEqual("qwen-flash", TranslationPresets.All[3].DefaultModel);
        Assert.AreEqual("Qwen/Qwen2.5-7B-Instruct", TranslationPresets.All[4].DefaultModel);
        Assert.AreEqual("gpt-5.4-mini", TranslationPresets.All[5].DefaultModel);
        var defaults = new AppSettings().Translation;
        Assert.AreEqual(TranslationPresets.DefaultOpenAi.BaseAddress, defaults.BaseAddress);
        Assert.AreEqual(TranslationPresets.DefaultOpenAi.DefaultModel, defaults.Model);
    }

    [TestMethod]
    public async Task AiStreamHeartbeatOnlyTriggersFirstTokenTimeoutAndFallbackAsync()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new HeartbeatOnlySseStream(TimeSpan.FromMilliseconds(25)))
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");

        await Assert.ThrowsExactlyAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in OpenAiTranslationProvider.ReadServerSentEventsAsync(
                response,
                CancellationToken.None,
                firstTokenTimeout: TimeSpan.FromMilliseconds(140),
                idleTimeout: TimeSpan.FromSeconds(5)))
            {
            }
        });
    }

    [TestMethod]
    public async Task AiBatchInPlaceHonorsFastTencentFallbackWhenPrimaryAiHangsAsync()
    {
        var tencentCalls = 0;
        using var client = new HttpClient(new OfflineHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "tmt.tencentcloudapi.com")
            {
                Interlocked.Increment(ref tencentCalls);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"Response\":{\"TargetText\":\"腾讯原位快退\",\"RequestId\":\"fast\"}}")
                };
            }
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{\\\"0\\\":\\\"太慢\\\"}\"}}]}", Encoding.UTF8, "application/json")
            };
        }));

        var profile = NaturalProfile() with
        {
            EnableFastFallbackRace = true,
            FastFallbackDelaySeconds = 2,
            EnableMyMemoryFallback = false
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(
            ["First line", "Second line"],
            "en-US",
            profile,
            new TranslationCredentials("ai-key", "tencent-id", "tencent-secret"));
        sw.Stop();

        Assert.IsTrue(result.UsedFallback);
        Assert.AreEqual("腾讯机器翻译", result.ProviderName);
        StringAssert.Contains(result.PrimaryFailure, "超过 2 秒");
        Assert.AreEqual(2, tencentCalls);
        CollectionAssert.AreEqual(new[] { "腾讯原位快退", "腾讯原位快退" }, result.Texts.ToArray());
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(4.5), $"原位快退耗时过长：{sw.Elapsed.TotalSeconds:F1}s");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AiBatchRacingFormatAndAuthErrorsAreSymmetricWithoutPerRegionAmplificationAsync(bool primaryFormatError)
    {
        var aiCalls = 0;
        var edgeCalls = 0;
        using var client = new HttpClient(new OfflineHandler(async (request, cancellationToken) =>
        {
            var host = request.RequestUri!.Host;
            if (host is "ai.example.test" or "race.example.test")
            {
                Interlocked.Increment(ref aiCalls);
                var returnFormatError = (host == "ai.example.test") == primaryFormatError;
                if (returnFormatError)
                {
                    var malformed = JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { finish_reason = "stop", message = new { content = "{\"0\":\"缺项\"}" } } }
                    });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(malformed, Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":{\"message\":\"invalid key\"}}")
                };
            }

            Assert.AreEqual("edge.microsoft.com", host);
            Interlocked.Increment(ref edgeCalls);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var items = JsonSerializer.Deserialize<string[]>(body)!;
            var payload = items.Select((_, i) => new { translations = new[] { new { text = $"保底{i}" } } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }));

        var profile = NaturalProfile() with
        {
            EnableRacing = true,
            RacingBaseAddress = "https://race.example.test/v1",
            RacingModel = "race-model",
            EnableMyMemoryFallback = true
        };
        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(
            ["One", "Two", "Three", "Four"],
            "en-US",
            profile,
            new TranslationCredentials(ApiKey: "primary-key", RacingApiKey: "racing-key"));

        Assert.AreEqual(2, aiCalls, "无论主备谁报格式错、谁报 401，都绝不能向 4 个区域补发 4 次单条 AI 请求。");
        Assert.AreEqual(1, edgeCalls);
        Assert.IsTrue(result.UsedFallback);
        Assert.AreEqual(4, result.Texts.Count);
    }

    private sealed class HeartbeatOnlySseStream(TimeSpan interval) : Stream
    {
        private readonly byte[] _heartbeat = Encoding.UTF8.GetBytes("event: ping\ndata: heartbeat\n\n");
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset == 0)
            {
                await Task.Delay(interval, cancellationToken);
            }
            var toCopy = Math.Min(buffer.Length, _heartbeat.Length - _offset);
            _heartbeat.AsSpan(_offset, toCopy).CopyTo(buffer.Span);
            _offset = (_offset + toCopy) % _heartbeat.Length;
            return toCopy;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [TestMethod]
    public async Task EdgeProviderSendsBrowserUserAgentAndNormalizesOcrArtifactsWithoutPlaceholderNoiseAsync()
    {
        string? capturedUserAgent = null;
        string? capturedBody = null;
        using var client = new HttpClient(new OfflineHandler(async (request, cancellationToken) =>
        {
            capturedUserAgent = request.Headers.UserAgent.ToString();
            capturedBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var items = JsonSerializer.Deserialize<string[]>(capturedBody)!;
            var payload = items.Select(item => new
            {
                translations = new[] { new { text = item.Replace("Price", "价格", StringComparison.Ordinal) } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
        }));

        var result = await new EdgeTranslationProvider(client).TranslateAsync(
            "Price S0.435 at 4.6x on https://github.com/Fahaxiki123/Screen",
            "en-US",
            new AppSettings().Translation with { Provider = TranslationProviderKind.Edge },
            new TranslationCredentials());

        Assert.IsFalse(string.IsNullOrWhiteSpace(capturedUserAgent), "Edge 请求必须附带浏览器 User-Agent。");
        StringAssert.Contains(capturedUserAgent, "Edg/");
        Assert.IsFalse(capturedBody!.Contains("QZXKEEP", StringComparison.Ordinal), "Edge 神经翻译不应插入破坏语序的占位符。");
        StringAssert.Contains(result.Text, "$0.435");
        StringAssert.Contains(result.Text, "https://github.com/Fahaxiki123/Screen");
    }

    [TestMethod]
    public async Task MyMemoryFallbackDetectsJapaneseAndKoreanPerRegionInsteadOfDocumentLanguageAsync()
    {
        var queries = new List<string>();
        using var client = new HttpClient(new OfflineHandler((request, _) =>
        {
            lock (queries)
            {
                queries.Add(Uri.UnescapeDataString(request.RequestUri!.Query));
            }
            return Task.FromResult(MyMemoryResponse());
        }));
        var profile = new AppSettings().Translation with
        {
            Provider = TranslationProviderKind.MyMemory,
            TargetLanguage = "Français"
        };

        await new OnlineTranslationProvider(client).TranslateBatchAsync(
            ["こんにちは、これはテストです。", "안녕하세요, 테스트입니다."],
            "en-US",
            profile,
            new TranslationCredentials());

        Assert.IsTrue(queries.Any(q => q.Contains("langpair=ja|fr", StringComparison.Ordinal)), string.Join(" ; ", queries));
        Assert.IsTrue(queries.Any(q => q.Contains("langpair=ko|fr", StringComparison.Ordinal)), string.Join(" ; ", queries));
    }

    [TestMethod]
    public async Task AiNonStreamingBodyReadTimeoutTriggersFallbackWithoutHangingAsync()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new OfflineHandler((request, _) =>
        {
            var host = request.RequestUri!.Host;
            requests.Add(host);
            if (host == "ai.example.test")
            {
                // 返回 200 application/json 响应头后无限阻塞正文，验证读取正文有独立超时并触发回退。
                var stalledContent = new StreamContent(new HangingReadStream());
                stalledContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = stalledContent });
            }
            var edgeJson = "[{\"translations\":[{\"text\":\"超时回退译文\"}]}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(edgeJson, Encoding.UTF8, "application/json")
            });
        }))
        {
            Timeout = TimeSpan.FromMilliseconds(80)
        };

        var result = await new OnlineTranslationProvider(client).TranslateAsync(
            "Hello world",
            "en-US",
            NaturalProfile(),
            new TranslationCredentials(ApiKey: "offline-key"));

        Assert.AreEqual("超时回退译文", result.Text);
        Assert.IsTrue(result.UsedFallback);
        CollectionAssert.AreEqual(new[] { "ai.example.test", "edge.microsoft.com" }, requests);
    }

    [TestMethod]
    public async Task AiBatchPartialFormatFailureOnlyFallsBackFailedSliceWithoutResendingCompletedSliceAsync()
    {
        var calls = 0;
        using var client = new HttpClient(new OfflineHandler(async (request, cancellationToken) =>
        {
            var call = Interlocked.Increment(ref calls);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (call == 1)
            {
                // 第 1 批（0..11，共 12 区）正常成功。
                var map = Enumerable.Range(0, 12).ToDictionary(i => i.ToString(), i => $"批量{i}");
                var json = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(map) } } }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
            }
            if (call == 2)
            {
                // 第 2 批（仅第 13 区）返回非法 JSON，触发仅针对第 2 批的逐区降级。
                var bad = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new { content = "not-json" } } }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(bad, Encoding.UTF8, "application/json")
                };
            }
            StringAssert.Contains(body, "Region 12", "降级逐区只能重发失败的第 13 区，绝不能重发前 12 区。");
            return SseResponse(AiEvent("单条12", "stop") + "data: [DONE]\n\n");
        }));

        var regions = Enumerable.Range(0, 13).Select(i => $"Region {i}").ToArray();
        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(
            regions,
            "en-US",
            NaturalProfile() with { EnableMyMemoryFallback = false },
            new TranslationCredentials(ApiKey: "offline-key"));

        Assert.AreEqual(3, calls, "13 区应仅发出 2 次批量 + 1 次单条降级。");
        Assert.AreEqual("批量0", result.Texts[0]);
        Assert.AreEqual("批量11", result.Texts[11]);
        Assert.AreEqual("单条12", result.Texts[12]);
    }

    [TestMethod]
    public async Task EdgeBatchPartialFailurePreservesCompletedSliceAndOnlyFallsBackRemainingRegionsToMyMemoryAsync()
    {
        var edgeCalls = 0;
        var myMemoryQueries = new List<string>();
        using var client = new HttpClient(new OfflineHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "edge.microsoft.com")
            {
                var call = Interlocked.Increment(ref edgeCalls);
                if (call == 1)
                {
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    var items = JsonSerializer.Deserialize<string[]>(body)!;
                    var payload = items.Select((_, i) => new { translations = new[] { new { text = $"Edge{i}" } } });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            lock (myMemoryQueries)
            {
                myMemoryQueries.Add(Uri.UnescapeDataString(request.RequestUri.Query));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"兜底10\"}}")
            };
        }));

        var regions = Enumerable.Range(0, 11).Select(i => $"Line {i}").ToArray();
        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(
            regions,
            "en-US",
            new AppSettings().Translation with { Provider = TranslationProviderKind.Edge, EnableMyMemoryFallback = true },
            new TranslationCredentials());

        Assert.AreEqual(2, edgeCalls);
        Assert.AreEqual(1, myMemoryQueries.Count, "前 10 区已由 Edge 成功完成，MyMemory 只能收到第 11 区。");
        StringAssert.Contains(myMemoryQueries[0], "Line 10");
        Assert.IsTrue(result.UsedFallback);
        Assert.AreEqual("Edge0", result.Texts[0]);
        Assert.AreEqual("Edge9", result.Texts[9]);
        Assert.AreEqual("兜底10", result.Texts[10]);
    }

    [TestMethod]
    public async Task AiBatchInPlaceSupportsSecondaryModelRacingAsync()
    {
        using var client = new HttpClient(new OfflineHandler(async (request, cancellationToken) =>
        {
            if (request.RequestUri!.Host == "ai.example.test")
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            Assert.AreEqual("race.example.test", request.RequestUri.Host);
            var json = JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop", message = new { content = "{\"0\":\"竞速零\",\"1\":\"竞速一\"}" } } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }));

        var profile = NaturalProfile() with
        {
            EnableRacing = true,
            RacingBaseAddress = "https://race.example.test/v1",
            RacingModel = "fast-race-model",
            EnableMyMemoryFallback = false
        };

        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(
            ["Zero", "One"],
            "en-US",
            profile,
            new TranslationCredentials(ApiKey: "primary-key", RacingApiKey: "racing-key"));

        StringAssert.Contains(result.ProviderName, "fast-race-model");
        CollectionAssert.AreEqual(new[] { "竞速零", "竞速一" }, result.Texts.ToArray());
    }

    [TestMethod]
    public void StartupMigrationNeverOverridesLegacyZhipuOcrProfile()
    {
        var directory = Directory.CreateTempSubdirectory("screen-zhipu-migrate-");
        try
        {
            var path = Path.Combine(directory.FullName, "settings.json");
            File.WriteAllText(path, """
                {
                  "SchemaVersion": 2,
                  "Translation": {
                    "Provider": 0,
                    "BaseAddress": "https://open.bigmodel.cn/api/paas/v4/layout_parsing",
                    "Model": "glm-ocr",
                    "CredentialName": "Screen.Translation",
                    "TargetLanguage": "简体中文"
                  }
                }
                """);
            var store = new JsonSettingsStore(path);
            // 模拟 AppHost 真实启动顺序：先执行旧默认迁移，再调用 Load()。
            Assert.IsFalse(store.TryMigrateLegacyDefaultProvider(() => false));
            var loaded = store.Load();
            Assert.AreEqual(TranslationProviderKind.OpenAiCompatible, loaded.Translation.Provider);
            Assert.AreEqual("https://open.bigmodel.cn/api/paas/v4", loaded.Translation.BaseAddress);
            Assert.AreEqual("glm-4.7-flash", loaded.Translation.Model);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class HangingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WaitForeverAsync(cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WaitForeverAsync(cancellationToken);
        private static async Task<int> WaitForeverAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SynchronousIntProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private static TranslationProfile NaturalProfile() => new AppSettings().Translation with
    {
        Provider = TranslationProviderKind.OpenAiCompatible,
        BaseAddress = "https://ai.example.test/v1",
        Model = "offline-model",
        EnableRacing = false,
        EnableFastFallbackRace = false,
        EnableMyMemoryFallback = true
    };

    private static string AiEvent(string content, string? finishReason = null) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content }, finish_reason = finishReason } } }) + "\n\n";

    private static HttpResponseMessage SseResponse(string stream) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
    };

    private static HttpResponseMessage MyMemoryResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"完整的保底译文\"}}")
    };

    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class OfflineHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
