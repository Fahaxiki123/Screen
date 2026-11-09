using System.Net;
using System.Text;

namespace Screen.Core.Tests;

[TestClass]
public sealed class TranslationRegressionTests
{
    private const string TextEvent = "data: {\"choices\":[{\"delta\":{\"content\":\"部分译文\"}}]}\n\n";

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
        CollectionAssert.AreEqual(new[] { "ai.example.test", "api.mymemory.translated.net" }, requests);
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
        CollectionAssert.AreEqual(new[] { "ai.example.test", "api.mymemory.translated.net" }, requests);
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

    private static TranslationProfile NaturalProfile() => new AppSettings().Translation with
    {
        Provider = TranslationProviderKind.OpenAiCompatible,
        BaseAddress = "https://ai.example.test/v1",
        Model = "offline-model",
        EnableRacing = false,
        EnableFastFallbackRace = false,
        EnableMyMemoryFallback = true
    };

    private static HttpResponseMessage SseResponse(string stream) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
    };

    private static HttpResponseMessage MyMemoryResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"responseStatus\":200,\"responseData\":{\"translatedText\":\"完整的保底译文\"}}")
    };

    private sealed class OfflineHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
