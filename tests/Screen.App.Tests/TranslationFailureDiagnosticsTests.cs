using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class TranslationFailureDiagnosticsTests
{
    // A model name and raw response body must be no more visible in logs than a key or screenshot.
    private const string Secret = "SensitiveScreenshotOrKey123";
    private static readonly TranslationCredentials Credentials = new(Secret, Secret);
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AiAndFreeFallbackFailuresRetainBothStatusesWithoutLeakingText(bool batch)
    {
        using var client = new HttpClient(new Handler(host => host switch
        {
            "primary.example.test" => Reply(HttpStatusCode.Unauthorized, Secret),
            "edge.microsoft.com" => Reply(HttpStatusCode.Forbidden, Secret),
            _ => throw new AssertFailedException("Unexpected provider")
        }));
        var route = new OnlineTranslationProvider(client);
        var profile = Profile(TranslationProviderKind.OpenAiCompatible);
        var error = await Assert.ThrowsExactlyAsync<TranslationRouteException>(async () =>
        {
            if (batch)
                await route.TranslateBatchAsync([Secret], "en", profile, Credentials, cancellationToken: TestContext.CancellationToken);
            else
                await route.TranslateAsync(Secret, "en", profile, Credentials, cancellationToken: TestContext.CancellationToken);
        });

        CollectionAssert.AreEqual(new[] { TranslationProviderKind.OpenAiCompatible, TranslationProviderKind.Edge },
            error.Failures.Select(f => f.Provider).ToArray());
        Assert.AreEqual(HttpStatusCode.Unauthorized, error.Failures[0].HttpStatus);
        Assert.AreEqual(HttpStatusCode.Forbidden, error.Failures[1].HttpStatus);
        var log = CaptureOverlayWindow.FormatSafeTranslationErrorLog(profile.Provider, error);
        StringAssert.Contains(log, "HTTP 403");
        StringAssert.Contains(log, "HTTP 401");
        Assert.IsFalse(log.Contains(Secret));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SuccessfulFreeFallbackRetainsAiFailure(bool batch)
    {
        using var client = new HttpClient(new Handler(host => host switch
        {
            "primary.example.test" => Reply(HttpStatusCode.Unauthorized, Secret),
            "edge.microsoft.com" => Reply(HttpStatusCode.OK, """[{"translations":[{"text":"done"}]}]"""),
            _ => throw new InvalidOperationException("Unexpected provider")
        }));
        var route = new OnlineTranslationProvider(client);
        var profile = Profile(TranslationProviderKind.OpenAiCompatible);
        IReadOnlyList<TranslationFailure> failures;
        TranslationProviderKind? destination;
        if (batch)
        {
            var result = await route.TranslateBatchAsync([Secret], "en", profile, Credentials, cancellationToken: TestContext.CancellationToken);
            failures = result.Failures;
            destination = result.FallbackProvider;
        }
        else
        {
            var result = await route.TranslateAsync(Secret, "en", profile, Credentials, cancellationToken: TestContext.CancellationToken);
            failures = result.Failures;
            destination = result.FallbackProvider;
        }

        Assert.AreEqual(TranslationProviderKind.Edge, destination);
        CollectionAssert.AreEqual(new[] { TranslationProviderKind.OpenAiCompatible },
            failures.Select(f => f.Provider).ToArray());
        var log = CaptureOverlayWindow.FormatSafeFallbackLog(profile.Provider, destination, failures);
        StringAssert.Contains(log, "OpenAiCompatible -> Edge");
        StringAssert.Contains(log, "HTTP 401");
        Assert.IsFalse(log.Contains(Secret));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    public async Task FreeServiceReportsItsOwnTransportStatusWithoutUsingAnotherProvider(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler(host =>
        {
            Assert.AreEqual("edge.microsoft.com", host);
            return Reply(status, Secret);
        }));
        const TranslationProviderKind provider = TranslationProviderKind.Edge;
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new OnlineTranslationProvider(client)
            .TranslateAsync("sample", "en", Profile(provider), Credentials,
                cancellationToken: TestContext.CancellationToken));
        Assert.AreEqual(status, error.StatusCode);
        var log = CaptureOverlayWindow.FormatSafeTranslationErrorLog(provider, error);
        StringAssert.Contains(log, $"HTTP {(int)status}");
        Assert.IsFalse(log.Contains(Secret));
    }

    [TestMethod]
    public async Task AiFormatThenAuthenticationFailureIsRecordedBeforeEdgeSucceeds()
    {
        var aiCalls = 0;
        using var client = new HttpClient(new Handler(host => host switch
        {
            "primary.example.test" when aiCalls++ == 0 => Reply(HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"{}"},"finish_reason":"stop"}]}"""),
            "primary.example.test" => Reply(HttpStatusCode.Unauthorized, Secret),
            "edge.microsoft.com" => Reply(HttpStatusCode.OK, """[{"translations":[{"text":"done"}]}]"""),
            _ => throw new InvalidOperationException("Unexpected provider")
        }));
        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(["source"], "en",
            Profile(TranslationProviderKind.OpenAiCompatible), new TranslationCredentials(Secret), cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(TranslationProviderKind.Edge, result.FallbackProvider);
        CollectionAssert.AreEqual(new[] { "InvalidResponse", "HttpRequestException" },
            result.Failures.Select(f => f.Kind).ToArray());
        Assert.AreEqual(HttpStatusCode.Unauthorized, result.Failures[1].HttpStatus);
    }

    [TestMethod]
    public async Task SameStatusFromBothAiCandidatesRetainsTheirRoles()
    {
        using var client = new HttpClient(new Handler(host => host == "edge.microsoft.com"
            ? Reply(HttpStatusCode.OK, """[{"translations":[{"text":"done"}]}]""")
            : Reply(HttpStatusCode.Unauthorized, Secret)));
        var profile = Profile(TranslationProviderKind.OpenAiCompatible) with
        {
            EnableRacing = true,
            RacingBaseAddress = "https://secondary.example.test",
            RacingModel = Secret
        };
        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(["source"], "en", profile,
            new TranslationCredentials(Secret, RacingApiKey: Secret), cancellationToken: TestContext.CancellationToken);
        CollectionAssert.AreEquivalent(new[] { false, true }, result.Failures.Select(f => f.IsSecondaryAi).ToArray());
        Assert.IsTrue(result.Failures.All(f => f.HttpStatus == HttpStatusCode.Unauthorized));
        Assert.AreEqual(TranslationProviderKind.Edge, result.FallbackProvider);
    }

    [TestMethod]
    public async Task RacingWinnerCanRetainAnEarlierFailureWithoutBeingMarkedAsFallback()
    {
        using var client = new HttpClient(new Handler(host => host == "primary.example.test"
            ? Reply(HttpStatusCode.Unauthorized, Secret)
            : Reply(HttpStatusCode.OK, """{"choices":[{"message":{"content":"{\"0\":\"done\"}"},"finish_reason":"stop"}]}""")));
        var profile = Profile(TranslationProviderKind.OpenAiCompatible) with
        {
            EnableRacing = true,
            RacingBaseAddress = "https://secondary.example.test",
            RacingModel = Secret
        };
        var result = await new OnlineTranslationProvider(client).TranslateBatchAsync(["source"], "en", profile,
            new TranslationCredentials(Secret, RacingApiKey: Secret), cancellationToken: TestContext.CancellationToken);
        Assert.IsFalse(result.UsedFallback);
        Assert.IsNull(result.FallbackProvider);
        Assert.AreEqual(HttpStatusCode.Unauthorized, result.Failures.Single().HttpStatus);
    }

    [TestMethod]
    public async Task AlreadyCancelledCallsNeverStartProvidersOrCreateRouteFailures()
    {
        using var client = new HttpClient(new Handler(_ => throw new AssertFailedException("Provider invoked after cancellation.")));
        var route = new OnlineTranslationProvider(client);
        var cancelled = new CancellationToken(canceled: true);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            route.TranslateAsync("source", "en", Profile(TranslationProviderKind.Edge), Credentials, cancellationToken: cancelled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            route.TranslateBatchAsync([], "en", Profile(TranslationProviderKind.Edge), Credentials, cancellationToken: cancelled));
    }

    private static TranslationProfile Profile(TranslationProviderKind provider) =>
        new AppSettings().Translation with
        {
            Provider = provider,
            BaseAddress = "https://primary.example.test",
            Model = Secret
        };

    private static HttpResponseMessage Reply(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request.RequestUri!.Host));
        }
    }
}
