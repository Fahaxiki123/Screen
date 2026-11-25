using System.Net;
using System.Text;
using System.Text.Json;

namespace Screen.Core.Tests;

[TestClass]
public sealed class AiFreeFallbackTests
{
    [TestMethod]
    public async Task EdgeReceivesOnlyTheRegionsThatTheAiDidNotTranslate()
    {
        var regions = Enumerable.Range(0, 15).Select(i => $"Entry {i}").ToArray();
        var progress = new List<int>();
        var edgeTexts = new List<string>();
        var aiCalls = 0;
        using var client = new HttpClient(new OfflineHandler(async (request, token) =>
        {
            if (request.RequestUri!.Host == "primary.example.test")
            {
                if (++aiCalls > 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
                var map = Enumerable.Range(0, 12).ToDictionary(i => i.ToString(), i => $"AI {regions[i]}");
                return Json(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(map) } } } });
            }
            Assert.AreEqual("edge.microsoft.com", request.RequestUri.Host);
            Assert.IsNull(request.Headers.Authorization, "The AI key must not follow the fallback request.");
            var items = JsonSerializer.Deserialize<string[]>(await request.Content!.ReadAsStringAsync(token))!;
            edgeTexts.AddRange(items);
            return Json(items.Select(item => new { translations = new[] { new { text = $"Edge {item}" } } }));
        }));

        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(
            regions, "en-US", Profile, Credentials, new InlineProgress(progress.Add), TestContext.CancellationToken);

        Assert.AreEqual(2, aiCalls);
        CollectionAssert.AreEqual(regions[12..], edgeTexts);
        CollectionAssert.AreEqual(regions.Select((item, i) => i < 12 ? $"AI {item}" : $"Edge {item}").ToArray(),
            result.Texts.ToArray());
        CollectionAssert.AreEqual(new[] { 12, regions.Length }, progress);
        Assert.IsTrue(result.UsedFallback);
        Assert.AreEqual(TranslationProviderKind.Edge, result.FallbackProvider);
    }

    [TestMethod]
    public async Task EdgeOnlyReceivesFailedRegionAfterMalformedAiBatch()
    {
        var secondRegionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<int>();
        var edgeTexts = new List<string>();
        var batchRequests = 0;
        var individualRequests = 0;
        using var client = new HttpClient(new OfflineHandler(async (request, token) =>
        {
            var body = await request.Content!.ReadAsStringAsync(token);
            if (request.RequestUri!.Host == "edge.microsoft.com")
            {
                edgeTexts.AddRange(JsonSerializer.Deserialize<string[]>(body)!);
                return Json(new[] { new { translations = new[] { new { text = "免费译文" } } } });
            }
            Assert.AreEqual("primary.example.test", request.RequestUri.Host);
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.GetProperty("stream").GetBoolean())
            {
                Interlocked.Increment(ref batchRequests);
                return Json(new { choices = new[] { new { finish_reason = "stop", message = new { content = "not-json" } } } });
            }
            Interlocked.Increment(ref individualRequests);
            var text = json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            if (text == "First region")
            {
                // Keep a successful request in flight when its sibling fails.
                await secondRegionStarted.Task.WaitAsync(token);
                return Json(new { choices = new[] { new { finish_reason = "stop", message = new { content = "AI 译文" } } } });
            }
            Assert.AreEqual("Second region", text);
            secondRegionStarted.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }));

        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(
            ["First region", "Second region"], "en-US", Profile, Credentials,
            new InlineProgress(progress.Add), TestContext.CancellationToken);

        Assert.AreEqual(1, batchRequests);
        Assert.AreEqual(2, individualRequests);
        CollectionAssert.AreEqual(new[] { "Second region" }, edgeTexts);
        CollectionAssert.AreEqual(new[] { "AI 译文", "免费译文" }, result.Texts.ToArray());
        CollectionAssert.AreEqual(new[] { 1, 2 }, progress);
        Assert.AreEqual(TranslationProviderKind.Edge, result.FallbackProvider);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisabledFreeFallbackNeverSendsFailedAiRequestsToEdge(bool batch)
    {
        var hosts = new List<string>();
        using var client = new HttpClient(new OfflineHandler((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }));
        var provider = new OnlineTranslationProvider(client);
        var profile = Profile with { EnableFreeFallback = false };

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            if (batch)
            {
                await provider.TranslateBatchAsync(["Entry 1", "Entry 2"], "en-US", profile, Credentials);
            }
            else
            {
                await provider.TranslateAsync("Entry 1", "en-US", profile, Credentials);
            }
        });
        StringAssert.Contains(error.Message, "401");
        CollectionAssert.AreEqual(new[] { "primary.example.test" }, hosts);
    }

    [TestMethod]
    public async Task CancellingAiBatchNeverSendsTheBatchToEdge()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hosts = new List<string>();
        using var client = new HttpClient(new OfflineHandler(async (request, token) =>
        {
            hosts.Add(request.RequestUri!.Host);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var translation = new OnlineTranslationProvider(client).TranslateBatchAsync(
            ["Entry 1", "Entry 2"], "en-US", Profile, Credentials, cancellationToken: cancelled.Token);
        await started.Task.WaitAsync(TestContext.CancellationToken);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => translation);
        CollectionAssert.AreEqual(new[] { "primary.example.test" }, hosts);
    }

    public TestContext TestContext { get; set; } = null!;
    private static readonly TranslationCredentials Credentials = new(ApiKey: "test-ai");
    private static readonly TranslationProfile Profile = new AppSettings().Translation with
    {
        Provider = TranslationProviderKind.OpenAiCompatible,
        BaseAddress = "https://primary.example.test/v1",
        Model = "test-model",
        EnableFreeFallback = true
    };

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private sealed class OfflineHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            respond(request, token);
    }
}
