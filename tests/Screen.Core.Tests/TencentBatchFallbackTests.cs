using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Screen.Core.Tests;

[TestClass]
public sealed class TencentBatchFallbackTests
{
    [TestMethod]
    [DataRow(TranslationProviderKind.Tencent, false, 3)]
    [DataRow(TranslationProviderKind.OpenAiCompatible, false, 3)]
    [DataRow(TranslationProviderKind.OpenAiCompatible, true, 3)]
    [DataRow(TranslationProviderKind.OpenAiCompatible, true, 15)]
    public async Task FreeFallbackReceivesOnlyUnfinishedRegions(
        TranslationProviderKind provider, bool fastFallback, int failingRegion)
    {
        var regions = Enumerable.Range(1, 17).Select(i => $"Entry {i}").ToArray();
        var handler = new PartialTencentHandler(failingRegion, primaryStalls: failingRegion > 12);
        using var client = new HttpClient(handler);
        var progress = new List<int>();

        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(
            regions, "en-US", Profile(provider, fastFallback), Credentials,
            new InlineProgress(progress.Add), TestContext.CancellationToken);

        Assert.IsTrue(result.UsedFallback);
        Assert.AreEqual("免费在线翻译", result.ProviderName);
        StringAssert.Contains(result.PrimaryFailure, "429");
        Assert.Contains($"Entry {failingRegion - 1}", handler.TencentSucceeded);
        Assert.Contains($"Entry {failingRegion}", handler.EdgeTexts);
        CollectionAssert.AreEquivalent(regions.Except(handler.TencentSucceeded).ToArray(), handler.EdgeTexts.ToArray());
        CollectionAssert.AreEqual(
            regions.Select(text => handler.TencentSucceeded.Contains(text) ? $"T {text}" : $"E {text}").ToArray(),
            result.Texts.ToArray());
        Assert.AreEqual(handler.TencentCalls.Count, handler.TencentCalls.Distinct().Count());
        Assert.AreEqual(regions.Length, progress.Last());
        Assert.IsTrue(progress.Zip(progress.Skip(1)).All(pair => pair.First < pair.Second));
    }

    [TestMethod]
    public async Task DisabledFreeFallbackSurfacesTencentError()
    {
        using var client = new HttpClient(new PartialTencentHandler(3, primaryStalls: false));
        var profile = Profile(TranslationProviderKind.Tencent, false) with { EnableMyMemoryFallback = false };
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            new OnlineTranslationProvider(client).TranslateBatchAsync(
                ["Entry 1", "Entry 2", "Entry 3"], "en-US", profile, Credentials,
                cancellationToken: TestContext.CancellationToken));
        StringAssert.Contains(error.Message, "429");
    }

    [TestMethod]
    public async Task CancellationWhileTencentInFlightNeverStartsFreeFallback()
    {
        var handler = new PartialTencentHandler(3, primaryStalls: false);
        using var client = new HttpClient(handler);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var translation = new OnlineTranslationProvider(client).TranslateBatchAsync(
            ["Entry 1", "Entry 2"], "en-US", Profile(TranslationProviderKind.Tencent, false),
            Credentials, cancellationToken: cancelled.Token);
        await handler.WaitingForFailure.Task.WaitAsync(TestContext.CancellationToken);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => translation);
        Assert.IsEmpty(handler.EdgeTexts);
    }

    public TestContext TestContext { get; set; } = null!;

    private static readonly TranslationCredentials Credentials = new("test-ai", "test-id", "test-secret");
    private static TranslationProfile Profile(TranslationProviderKind provider, bool fastFallback) =>
        new("https://primary.example.test/v1", "test-model", TranslationProfile.ApiKeyCredentialName, "简体中文")
        {
            Provider = provider,
            EnableMyMemoryFallback = true,
            EnableFastFallbackRace = fastFallback,
            FastFallbackDelaySeconds = 2
        };

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private sealed class PartialTencentHandler(int failingRegion, bool primaryStalls) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _failureStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WaitingForFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> TencentCalls { get; } = new();
        public ConcurrentQueue<string> TencentSucceeded { get; } = new();
        public ConcurrentQueue<string> EdgeTexts { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            switch (request.RequestUri!.Host)
            {
                case "primary.example.test":
                    if (primaryStalls)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                case "tmt.tencentcloudapi.com":
                    using (var document = JsonDocument.Parse(body))
                    {
                        var text = document.RootElement.GetProperty("SourceText").GetString()!;
                        TencentCalls.Enqueue(text);
                        if (text == $"Entry {failingRegion - 1}")
                        {
                            // This region was already in flight when its sibling failed.
                            WaitingForFailure.TrySetResult();
                            await _failureStarted.Task.WaitAsync(token);
                        }
                        if (text == $"Entry {failingRegion}")
                        {
                            _failureStarted.TrySetResult();
                            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                        }
                        TencentSucceeded.Enqueue(text);
                        return Json(new { Response = new { TargetText = $"T {text}" } });
                    }
                case "edge.microsoft.com":
                    var texts = JsonSerializer.Deserialize<string[]>(body)!;
                    foreach (var text in texts)
                    {
                        EdgeTexts.Enqueue(text);
                    }
                    return Json(texts.Select(text => new { translations = new[] { new { text = $"E {text}" } } }));
                default:
                    throw new AssertFailedException($"Unexpected translation host: {request.RequestUri.Host}");
            }
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}
