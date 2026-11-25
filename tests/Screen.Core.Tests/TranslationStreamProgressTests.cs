using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Screen.Core.Tests;

[TestClass]
public sealed class TranslationStreamProgressTests
{
    private static readonly string Heartbeat = ": keep-alive\n\n" + ContentEvent(" \n") + ContentEvent("<think>reasoning</think>");

    [TestMethod]
    [DataRow(" ", "等待首字")]
    [DataRow("\u200B", "等待首字")]
    [DataRow("first", "新的可见译文")]
    public async Task StalledTranslationFallsBackDespiteContinuingSseTraffic(string initialText, string expectedFailure)
    {
        var handler = new TranslationHandler(index => index == 0 ? ContentEvent(initialText) : Heartbeat);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(300) };
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await TranslateAsync(client, limit.Token);

        Assert.IsTrue(result.UsedFallback);
        Assert.AreEqual("完整译文", result.Text);
        StringAssert.Contains(result.PrimaryFailure, expectedFailure);
        CollectionAssert.AreEqual(new[] { "ai.example.test", "edge.microsoft.com" }, handler.Hosts);
    }

    [TestMethod]
    public async Task StopFrameCompletesTranslationBeforeNextHeartbeatAndKeepsWordBreaks()
    {
        var handler = new TranslationHandler(index => index == 0
            ? ContentEvent("first") + ContentEvent(" \n") + ContentEvent("second <", "stop")
            : Heartbeat);
        using var client = new HttpClient(handler);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await TranslateAsync(client, limit.Token);

        Assert.IsFalse(result.UsedFallback);
        Assert.AreEqual("first \nsecond <", result.Text);
        Assert.AreEqual(1, handler.Hosts.Count);
    }

    [TestMethod]
    public async Task VisibleDeltasExtendTheIdleDeadline()
    {
        const int numberOfDeltas = 12;
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new FrameStream(
                index => ContentEvent("字", index == numberOfDeltas - 1 ? "stop" : null),
                TimeSpan.FromMilliseconds(40)))
        };
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var text = new StringBuilder();
        await foreach (var delta in OpenAiTranslationProvider.ReadServerSentEventsAsync(
            response, limit.Token, firstTokenTimeout: TimeSpan.FromSeconds(2), idleTimeout: TimeSpan.FromMilliseconds(300)))
        {
            text.Append(delta);
        }
        Assert.AreEqual(new string('字', numberOfDeltas), text.ToString());
    }

    [TestMethod]
    public async Task CallerCancellationDuringHeartbeatsDoesNotContactFallback()
    {
        var heartbeatsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TranslationHandler(index =>
        {
            if (index == 0)
            {
                return ContentEvent("partial");
            }
            heartbeatsStarted.TrySetResult();
            return Heartbeat;
        });
        using var client = new HttpClient(handler);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var translation = TranslateAsync(client, caller.Token);
        await heartbeatsStarted.Task.WaitAsync(caller.Token);
        await caller.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => translation);
        CollectionAssert.AreEqual(new[] { "ai.example.test" }, handler.Hosts);
    }

    private static Task<TranslationResult> TranslateAsync(HttpClient client, CancellationToken cancellationToken) =>
        new OnlineTranslationProvider(client).TranslateAsync("source", "en-US", new AppSettings().Translation with
        {
            Provider = TranslationProviderKind.OpenAiCompatible,
            BaseAddress = "https://ai.example.test/v1",
            Model = "test-model"
        }, new TranslationCredentials(ApiKey: "test-key"), cancellationToken: cancellationToken);

    private static string ContentEvent(string content, string? finishReason = null) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content }, finish_reason = finishReason } } }) + "\n\n";

    private sealed class TranslationHandler(Func<int, string> nextFrame) : HttpMessageHandler
    {
        internal List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            Hosts.Add(host);
            HttpContent content;
            if (host == "ai.example.test")
            {
                content = new StreamContent(new FrameStream(nextFrame, TimeSpan.FromMilliseconds(15)));
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            }
            else
            {
                Assert.AreEqual("edge.microsoft.com", host);
                content = new StringContent("""[{"translations":[{"text":"完整译文"}]}]""", Encoding.UTF8, "application/json");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class FrameStream(Func<int, string> nextFrame, TimeSpan interval) : MemoryStream
    {
        private int _index;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length)
            {
                if (_index > 0)
                {
                    await Task.Delay(interval, cancellationToken);
                }
                SetLength(0);
                Write(Encoding.UTF8.GetBytes(nextFrame(_index++)));
                Position = 0;
            }
            return Read(buffer.Span);
        }
    }
}
