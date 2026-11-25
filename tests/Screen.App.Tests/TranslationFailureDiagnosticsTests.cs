using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class TranslationFailureDiagnosticsTests
{
    // Also a syntactically valid provider code and model name. Neither makes this safe to log.
    private const string Secret = "SensitiveScreenshotOrKey123";
    private static readonly TranslationCredentials Credentials = new(Secret, "test-id", Secret, Secret);
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task FreeRoutePreservesBothFailuresAndRedactsUpstreamDetails(bool batch, bool businessError)
    {
        using var client = new HttpClient(new Handler(host => host == "edge.microsoft.com"
            ? Reply(HttpStatusCode.Forbidden, Secret)
            : Reply(businessError ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests,
                JsonSerializer.Serialize(new { responseStatus = 429, responseDetails = Secret }))));
        var route = new OnlineTranslationProvider(client);
        var profile = Profile(TranslationProviderKind.Edge);
        var error = await Assert.ThrowsExactlyAsync<TranslationRouteException>(async () =>
        {
            if (batch)
                await route.TranslateBatchAsync([Secret], "en", profile, Credentials, cancellationToken: TestContext.CancellationToken);
            else
                await route.TranslateAsync(Secret, "en", profile, Credentials, cancellationToken: TestContext.CancellationToken);
        });

        StringAssert.Contains(error.Message, "403");
        StringAssert.Contains(error.Message, "429");
        CollectionAssert.AreEqual(new[] { TranslationProviderKind.Edge, TranslationProviderKind.MyMemory },
            error.Failures.Select(f => f.Provider).ToArray());
        Assert.AreEqual(HttpStatusCode.Forbidden, error.Failures[0].HttpStatus);
        Assert.AreEqual(businessError ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, error.Failures[1].HttpStatus);
        Assert.AreEqual("429", error.Failures[1].ServiceCode);
        var log = CaptureOverlayWindow.FormatSafeTranslationErrorLog(profile.Provider, error);
        StringAssert.Contains(log, "HTTP 403");
        StringAssert.Contains(log, businessError ? "HTTP 200" : "HTTP 429");
        StringAssert.Contains(log, "service 429");
        Assert.IsFalse(log.Contains(Secret));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NestedSuccessfulFallbackRetainsAllProviderReasons(bool batch)
    {
        using var client = new HttpClient(new Handler(host => host switch
        {
            "primary.example.test" => Reply(HttpStatusCode.Unauthorized, Secret),
            "tmt.tencentcloudapi.com" => TencentError("AuthFailure.SignatureFailure"),
            "edge.microsoft.com" => Reply(HttpStatusCode.Forbidden, Secret),
            "api.mymemory.translated.net" => Reply(HttpStatusCode.OK, """{"responseStatus":200,"responseData":{"translatedText":"done"}}"""),
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

        Assert.AreEqual(TranslationProviderKind.MyMemory, destination);
        CollectionAssert.AreEqual(new[] { TranslationProviderKind.OpenAiCompatible, TranslationProviderKind.Tencent, TranslationProviderKind.Edge },
            failures.Select(f => f.Provider).ToArray());
        var log = CaptureOverlayWindow.FormatSafeFallbackLog(profile.Provider, destination, failures);
        foreach (var value in new[] { "MyMemory", "HTTP 401", "HTTP 200", "AuthFailure.SignatureFailure", "HTTP 403" })
            StringAssert.Contains(log, value);
        Assert.IsFalse(log.Contains(Secret));
    }

    [TestMethod]
    [DataRow("SensitiveScreenshotOrKey123", null)]
    [DataRow("AuthFailure.SensitiveScreenshotOrKey123", "AuthFailure")]
    public async Task UnknownBusinessCodeCannotEchoTextOrCredentialsIntoLogs(string rawCode, string? safeCode)
    {
        using var client = new HttpClient(new Handler(host => host == "tmt.tencentcloudapi.com"
            ? TencentError(rawCode)
            : Reply(HttpStatusCode.OK, """[{"translations":[{"text":"done"}]}]""")));
        var profile = Profile(TranslationProviderKind.Tencent);
        var result = await new OnlineTranslationProvider(client).TranslateAsync(Secret, "en", profile, Credentials,
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(safeCode, result.Failures.Single().ServiceCode);
        var log = CaptureOverlayWindow.FormatSafeFallbackLog(profile.Provider, result.FallbackProvider, result.Failures);
        StringAssert.Contains(log, "HTTP 200");
        Assert.IsFalse(log.Contains(Secret));
    }

    [TestMethod]
    [DataRow(TranslationProviderKind.MyMemory, HttpStatusCode.TooManyRequests)]
    [DataRow(TranslationProviderKind.Tencent, HttpStatusCode.ServiceUnavailable)]
    public async Task DirectHttpFailuresRetainTheirTransportStatus(TranslationProviderKind provider, HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler(_ => Reply(status, Secret)));
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new OnlineTranslationProvider(client)
            .TranslateAsync("sample", "en", Profile(provider) with { EnableMyMemoryFallback = false }, Credentials,
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
            Model = Secret,
            EnableFastFallbackRace = false
        };

    private static HttpResponseMessage TencentError(string code) => Reply(HttpStatusCode.OK,
        JsonSerializer.Serialize(new { Response = new { Error = new { Code = code, Message = Secret } } }));

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
