using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Screen.Core;

public sealed class OnlineTranslationProvider : ITranslationProvider
{
    private readonly TencentTranslationProvider _tencent;
    private readonly MyMemoryTranslationProvider _myMemory;
    private readonly OpenAiTranslationProvider _openAi;

    public OnlineTranslationProvider(HttpClient httpClient)
    {
        _tencent = new TencentTranslationProvider(httpClient);
        _myMemory = new MyMemoryTranslationProvider(httpClient);
        _openAi = new OpenAiTranslationProvider(httpClient);
    }

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText = null,
        CancellationToken cancellationToken = default)
    {
        if (profile.Provider == TranslationProviderKind.OpenAiCompatible)
        {
            return await TranslateNaturallyWithFallbackAsync(
                text,
                sourceLanguage,
                profile,
                credentials,
                streamingText,
                cancellationToken).ConfigureAwait(false);
        }

        if (profile.Provider == TranslationProviderKind.MyMemory)
        {
            return await _myMemory.TranslateAsync(
                text,
                sourceLanguage,
                profile,
                credentials,
                streamingText,
                cancellationToken).ConfigureAwait(false);
        }

        return await TranslateWithTencentFallbackAsync(
            text,
            sourceLanguage,
            profile,
            credentials,
            streamingText,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TranslationResult> TranslateNaturallyWithFallbackAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText,
        CancellationToken cancellationToken)
    {
        Exception primaryException;
        var tencentAlreadyAttempted = false;
        Exception? tencentException = null;

        try
        {
            if (string.IsNullOrWhiteSpace(credentials.ApiKey))
            {
                throw new InvalidOperationException("尚未配置 AI 自然翻译 API Key");
            }

            var hasAiRacing = profile.EnableRacing
                              && !string.IsNullOrWhiteSpace(profile.RacingBaseAddress)
                              && !string.IsNullOrWhiteSpace(profile.RacingModel)
                              && !string.IsNullOrWhiteSpace(credentials.RacingApiKey);
            var hasTencent = !string.IsNullOrWhiteSpace(credentials.TencentSecretId)
                             && !string.IsNullOrWhiteSpace(credentials.TencentSecretKey);
            var hasFastTmt = profile.EnableFastFallbackRace && hasTencent;

            if (!hasAiRacing && !hasFastTmt)
            {
                return await TranslateNaturallyWithRetryAsync(
                    text,
                    sourceLanguage,
                    profile,
                    credentials.ApiKey,
                    streamingText,
                    cancellationToken).ConfigureAwait(false);
            }

            var outcome = await RaceNaturallyAsync(
                text,
                sourceLanguage,
                profile,
                credentials,
                hasAiRacing,
                hasFastTmt,
                streamingText,
                cancellationToken).ConfigureAwait(false);
            if (outcome.Winner is not null)
            {
                return outcome.Winner;
            }

            tencentAlreadyAttempted = outcome.TencentAttempted;
            tencentException = outcome.TencentError;
            primaryException = outcome.PrimaryError
                               ?? new InvalidOperationException("AI 自然翻译未返回有效译文。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            primaryException = exception;
        }

        if (!tencentAlreadyAttempted
            && !string.IsNullOrWhiteSpace(credentials.TencentSecretId)
            && !string.IsNullOrWhiteSpace(credentials.TencentSecretKey))
        {
            try
            {
                var fallback = await _tencent.TranslateAsync(
                    text,
                    sourceLanguage,
                    profile,
                    credentials,
                    streamingText,
                    cancellationToken).ConfigureAwait(false);
                return fallback with
                {
                    UsedFallback = true,
                    PrimaryFailure = $"AI 自然翻译：{GetFriendlyMessage(primaryException)}"
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedProviderFailure(exception))
            {
                tencentException = exception;
            }
        }

        if (profile.EnableMyMemoryFallback)
        {
            try
            {
                var fallback = await _myMemory.TranslateAsync(
                    text,
                    sourceLanguage,
                    profile,
                    credentials,
                    streamingText,
                    cancellationToken).ConfigureAwait(false);
                var failure = $"AI 自然翻译：{GetFriendlyMessage(primaryException)}";
                if (tencentException is not null)
                {
                    failure += $"；腾讯机器翻译：{GetFriendlyMessage(tencentException)}";
                }
                return fallback with
                {
                    UsedFallback = true,
                    PrimaryFailure = failure
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception fallbackException)
            {
                throw new InvalidOperationException(
                    $"AI 自然翻译失败：{GetFriendlyMessage(primaryException)}；保底翻译也失败：{GetFriendlyMessage(fallbackException)}",
                    fallbackException);
            }
        }

        if (tencentException is not null)
        {
            throw new InvalidOperationException(
                $"AI 自然翻译失败：{GetFriendlyMessage(primaryException)}；腾讯翻译也失败：{GetFriendlyMessage(tencentException)}",
                tencentException);
        }

        throw new InvalidOperationException(
            $"AI 自然翻译失败：{GetFriendlyMessage(primaryException)}",
            primaryException);
    }

    private async Task<TranslationResult> TranslateWithTencentFallbackAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(credentials.TencentSecretId)
                || string.IsNullOrWhiteSpace(credentials.TencentSecretKey))
            {
                throw new InvalidOperationException("尚未配置腾讯云 SecretId 和 SecretKey");
            }

            return await _tencent.TranslateAsync(
                text,
                sourceLanguage,
                profile,
                credentials,
                streamingText,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception primaryException) when (
            profile.EnableMyMemoryFallback && IsExpectedProviderFailure(primaryException))
        {
            try
            {
                var fallback = await _myMemory.TranslateAsync(
                    text,
                    sourceLanguage,
                    profile,
                    credentials,
                    streamingText,
                    cancellationToken).ConfigureAwait(false);
                return fallback with
                {
                    UsedFallback = true,
                    PrimaryFailure = GetFriendlyMessage(primaryException)
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception fallbackException)
            {
                throw new InvalidOperationException(
                    $"腾讯翻译失败：{GetFriendlyMessage(primaryException)}；MyMemory 也失败：{GetFriendlyMessage(fallbackException)}",
                    fallbackException);
            }
        }
    }

    private static string GetFriendlyMessage(Exception exception)
    {
        var message = exception.Message.Trim();
        return message.Length <= 180 ? message : message[..180] + "…";
    }

    private readonly record struct RaceOutcome(
        TranslationResult? Winner,
        Exception? PrimaryError,
        bool TencentAttempted,
        Exception? TencentError);

    private sealed class RelayProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    /// <summary>
    /// Races the primary AI endpoint (streaming live to the UI) against an
    /// optional secondary AI endpoint and/or a delayed Tencent quick fallback.
    /// If every AI candidate fails before the head-start delay expires, the
    /// delay is cancelled immediately so Tencent runs without dead wait and
    /// failed providers are never called a second time.
    /// </summary>
    private async Task<RaceOutcome> RaceNaturallyAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        bool hasAiRacing,
        bool hasFastTmt,
        IProgress<string>? streamingText,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var delayWake = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);

        var winnerLocked = 0;
        var remainingAiCandidates = hasAiRacing ? 2 : 1;
        Exception? primaryError = null;
        var tencentAttempted = false;
        Exception? tencentError = null;

        IProgress<string>? primaryProgress = streamingText is null
            ? null
            : new RelayProgress(chunk =>
            {
                if (Volatile.Read(ref winnerLocked) == 0 && !linked.IsCancellationRequested)
                {
                    streamingText.Report(chunk);
                }
            });

        void MarkAiCandidateFailed()
        {
            if (Interlocked.Decrement(ref remainingAiCandidates) == 0
                && Volatile.Read(ref winnerLocked) == 0)
            {
                try
                {
                    delayWake.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        async Task<TranslationResult?> RunPrimaryAiAsync()
        {
            try
            {
                var result = await TranslateNaturallyWithRetryAsync(
                    text,
                    sourceLanguage,
                    profile,
                    credentials.ApiKey!,
                    primaryProgress,
                    linked.Token).ConfigureAwait(false);
                if (!IsPlausibleTranslation(text, result.Text))
                {
                    throw new InvalidOperationException("AI 自然翻译返回译文过短。");
                }
                Interlocked.Exchange(ref winnerLocked, 1);
                return result with { ProviderName = $"AI 自然翻译（{profile.Model}）" };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                MarkAiCandidateFailed();
                return null;
            }
            catch (Exception exception) when (IsExpectedProviderFailure(exception))
            {
                primaryError = exception;
                MarkAiCandidateFailed();
                return null;
            }
        }

        async Task<TranslationResult?> RunSecondaryAiAsync(TranslationProfile racingProfile, string racingKey)
        {
            try
            {
                var result = await _openAi.TranslateAsync(
                    text,
                    sourceLanguage,
                    racingProfile,
                    new TranslationCredentials(racingKey),
                    streamingText: null,
                    linked.Token).ConfigureAwait(false);
                if (!IsPlausibleTranslation(text, result.Text))
                {
                    throw new InvalidOperationException("AI 竞速翻译返回译文过短。");
                }
                Interlocked.Exchange(ref winnerLocked, 1);
                return result with { ProviderName = $"AI 自然翻译（{racingProfile.Model}）" };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                MarkAiCandidateFailed();
                return null;
            }
            catch (Exception exception) when (IsExpectedProviderFailure(exception))
            {
                Interlocked.CompareExchange(ref primaryError, exception, null);
                MarkAiCandidateFailed();
                return null;
            }
        }

        async Task<TranslationResult?> RunDelayedTencentAsync(int delaySeconds)
        {
            var wokenByAiFailure = false;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), delayWake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
            catch (OperationCanceledException)
            {
                // All AI candidates have already faulted; skip the remaining delay.
                wokenByAiFailure = true;
            }

            tencentAttempted = true;
            try
            {
                var result = await _tencent.TranslateAsync(
                    text,
                    sourceLanguage,
                    profile,
                    credentials,
                    streamingText: null,
                    linked.Token).ConfigureAwait(false);
                var failureReason = wokenByAiFailure && Volatile.Read(ref primaryError) is { } aiFailure
                    ? $"AI 自然翻译：{GetFriendlyMessage(aiFailure)}"
                    : $"AI 自然翻译超过 {delaySeconds} 秒未完成，已自动切换快速翻译";
                return result with
                {
                    UsedFallback = true,
                    PrimaryFailure = failureReason
                };
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception exception) when (IsExpectedProviderFailure(exception))
            {
                tencentError = exception;
                return null;
            }
        }

        var candidates = new List<Task<TranslationResult?>> { RunPrimaryAiAsync() };
        if (hasAiRacing)
        {
            var racingProfile = profile with
            {
                BaseAddress = profile.RacingBaseAddress,
                Model = profile.RacingModel,
                CredentialName = profile.RacingCredentialName
            };
            candidates.Add(RunSecondaryAiAsync(racingProfile, credentials.RacingApiKey!));
        }
        if (hasFastTmt)
        {
            var delaySeconds = Math.Clamp(profile.FastFallbackDelaySeconds, 2, 30);
            candidates.Add(RunDelayedTencentAsync(delaySeconds));
        }

        while (candidates.Count > 0)
        {
            var finished = await Task.WhenAny(candidates).ConfigureAwait(false);
            candidates.Remove(finished);
            var result = await finished.ConfigureAwait(false);
            if (result is not null)
            {
                Interlocked.Exchange(ref winnerLocked, 1);
                linked.Cancel();
                streamingText?.Report(result.Text);
                return new RaceOutcome(result, null, tencentAttempted, tencentError);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new RaceOutcome(null, primaryError, tencentAttempted, tencentError);
    }

    private static bool IsPlausibleTranslation(string sourceText, string translatedText)
    {
        var visible = translatedText.Count(character => !char.IsWhiteSpace(character));
        var sourceVisible = Math.Max(1, sourceText.Count(character => !char.IsWhiteSpace(character)));
        return visible >= Math.Max(1, sourceVisible / 5);
    }

    private async Task<TranslationResult> TranslateNaturallyWithRetryAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        string apiKey,
        IProgress<string>? streamingText,
        CancellationToken cancellationToken)
    {
        var singleCredentials = new TranslationCredentials(apiKey);
        Exception? lastException = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await _openAi.TranslateAsync(
                    text,
                    sourceLanguage,
                    profile,
                    singleCredentials,
                    streamingText,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException exception) when (
                attempt < 2 && IsTransientHttpStatus(exception))
            {
                // Free AI endpoints (e.g. GLM) return 429 while under load. A short
                // wait usually recovers the request; only escalate after the retries
                // are exhausted so the user keeps the high-quality translation.
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        throw lastException ?? new HttpRequestException("AI 自然翻译失败。");
    }

    private static bool IsTransientHttpStatus(HttpRequestException exception) =>
        exception.StatusCode is
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static bool IsExpectedProviderFailure(Exception exception) => exception is
        HttpRequestException or
        TimeoutException or
        JsonException or
        InvalidOperationException or
        ArgumentException;
}

/// <summary>
/// Sends the non-streaming translation requests (Tencent TMT, MyMemory) with
/// a per-request deadline: the shared client's 90 s timeout would otherwise
/// leave the card stuck on "正在翻译…" with no path to the fallback chain.  A
/// deadline expiry surfaces as TimeoutException so providers classify it as
/// an expected, transient failure instead of caller cancellation.
/// </summary>
internal static class TranslationHttp
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<(int Status, string Reason, string Body)> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            using var response = await httpClient.SendAsync(request, deadline.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            return ((int)response.StatusCode, response.ReasonPhrase ?? string.Empty, body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutMessage);
        }
    }
}

public sealed class TencentTranslationProvider(HttpClient httpClient) : ITranslationProvider
{
    private const string Host = "tmt.tencentcloudapi.com";
    private const string Service = "tmt";
    private const string Version = "2018-03-21";
    private const string Action = "TextTranslate";
    private const string ContentType = "application/json; charset=utf-8";

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.TencentSecretId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.TencentSecretKey);

        var normalizedText = TranslationTextProtector.NormalizeLikelyOcrArtifacts(text);
        var protectedText = TranslationTextProtector.Protect(normalizedText);
        var translated = new StringBuilder();
        foreach (var chunk in TranslationTextChunker.SplitByCharacters(protectedText.Text, 1900))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkResult = await TranslateChunkAsync(
                chunk,
                profile,
                credentials.TencentSecretId!,
                credentials.TencentSecretKey!,
                cancellationToken).ConfigureAwait(false);
            translated.Append(chunkResult);
            streamingText?.Report(protectedText.Restore(translated.ToString()));
        }

        return new TranslationResult(protectedText.Restore(translated.ToString()), "腾讯机器翻译");
    }

    private async Task<string> TranslateChunkAsync(
        string text,
        TranslationProfile profile,
        string secretId,
        string secretKey,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            SourceText = text,
            Source = "auto",
            Target = TranslationLanguageCodes.ToTencent(profile.TargetLanguage),
            ProjectId = 0
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var authorization = CreateAuthorization(secretId, secretKey, payload, timestamp, Host, Service, ContentType);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}/")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Host = Host;
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("X-TC-Action", Action);
        request.Headers.TryAddWithoutValidation("X-TC-Version", Version);
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-TC-Region", profile.TencentRegion);

        var (status, reason, responseBody) = await TranslationHttp.SendAsync(
            httpClient, request, "腾讯翻译请求超时（30 秒）。", cancellationToken).ConfigureAwait(false);
        if (status < 200 || status >= 300)
        {
            throw new HttpRequestException($"HTTP {status} {reason}".Trim());
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("Response", out var responseObject))
        {
            throw new InvalidOperationException("腾讯翻译返回了无法识别的数据");
        }
        if (responseObject.TryGetProperty("Error", out var error))
        {
            var code = error.TryGetProperty("Code", out var codeValue) ? codeValue.GetString() : null;
            var message = error.TryGetProperty("Message", out var messageValue) ? messageValue.GetString() : null;
            throw new InvalidOperationException(string.Join("：", new[] { code, message }.Where(value => !string.IsNullOrWhiteSpace(value))));
        }
        if (!responseObject.TryGetProperty("TargetText", out var targetText)
            || targetText.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("腾讯翻译没有返回译文");
        }
        return targetText.GetString() ?? string.Empty;
    }

    internal static string CreateAuthorization(
        string secretId,
        string secretKey,
        string payload,
        long timestamp,
        string host = Host,
        string service = Service,
        string contentType = ContentType) =>
        TencentCloudApiSigner.CreateAuthorization(
            secretId,
            secretKey,
            payload,
            timestamp,
            host,
            service,
            contentType);
}

public sealed class MyMemoryTranslationProvider(HttpClient httpClient) : ITranslationProvider
{
    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var source = TranslationLanguageCodes.ToMyMemorySource(sourceLanguage, text);
        var target = TranslationLanguageCodes.ToMyMemoryTarget(profile.TargetLanguage);
        var normalizedText = TranslationTextProtector.NormalizeLikelyOcrArtifacts(text);
        var protectedText = TranslationTextProtector.Protect(normalizedText);
        var translated = new StringBuilder();

        foreach (var chunk in TranslationTextChunker.SplitByUtf8Bytes(protectedText.Text, 450))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = "https://api.mymemory.translated.net/get?q="
                           + Uri.EscapeDataString(chunk)
                           + "&langpair=" + Uri.EscapeDataString($"{source}|{target}")
                           + "&mt=1"
                           + (string.IsNullOrWhiteSpace(profile.MyMemoryContactEmail)
                               ? string.Empty
                               : "&de=" + Uri.EscapeDataString(profile.MyMemoryContactEmail.Trim()));
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            var (statusCode, reasonPhrase, responseBody) = await TranslationHttp.SendAsync(
                httpClient, request, "MyMemory 请求超时（30 秒）。", cancellationToken).ConfigureAwait(false);
            if (statusCode < 200 || statusCode >= 300)
            {
                var detail = TryReadMyMemoryError(responseBody);
                var httpStatus = $"HTTP {statusCode} {reasonPhrase}".Trim();
                throw new HttpRequestException(string.IsNullOrWhiteSpace(detail) ? httpStatus : $"{httpStatus}：{detail}");
            }

            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("responseStatus", out var status)
                && status.ToString() is { } statusText
                && statusText != "200")
            {
                var detail = document.RootElement.TryGetProperty("responseDetails", out var details)
                    ? details.ToString()
                    : "服务返回错误";
                throw new InvalidOperationException(detail);
            }
            if (!document.RootElement.TryGetProperty("responseData", out var data)
                || !data.TryGetProperty("translatedText", out var translatedText)
                || translatedText.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("MyMemory 没有返回译文");
            }
            var chunkResult = WebUtility.HtmlDecode(translatedText.GetString() ?? string.Empty);
            translated.Append(chunkResult);
            streamingText?.Report(protectedText.Restore(translated.ToString()));
        }

        return new TranslationResult(protectedText.Restore(translated.ToString()), "MyMemory 基础翻译");
    }

    private static string TryReadMyMemoryError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("responseDetails", out var detail))
            {
                return detail.ToString();
            }
        }
        catch (JsonException)
        {
        }
        return string.Empty;
    }
}

internal static class TranslationLanguageCodes
{
    public static string ToTencent(string language) => language switch
    {
        "简体中文" => "zh",
        "繁體中文" => "zh-TW",
        "English" => "en",
        "日本語" => "ja",
        "한국어" => "ko",
        "Français" => "fr",
        "Español" => "es",
        "Português" => "pt",
        "Deutsch" => "de",
        _ => "zh"
    };

    public static string ToMyMemoryTarget(string language) => language switch
    {
        "简体中文" => "zh-CN",
        "繁體中文" => "zh-TW",
        "English" => "en",
        "日本語" => "ja",
        "한국어" => "ko",
        "Français" => "fr",
        "Español" => "es",
        "Português" => "pt",
        "Deutsch" => "de",
        _ => "zh-CN"
    };

    public static string ToMyMemorySource(string sourceLanguage, string text)
    {
        if (sourceLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return "zh-CN";
        if (sourceLanguage.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return "ja";
        if (sourceLanguage.StartsWith("ko", StringComparison.OrdinalIgnoreCase))
            return "ko";
        if (sourceLanguage.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return "en";
        if (sourceLanguage.StartsWith("fr", StringComparison.OrdinalIgnoreCase))
            return "fr";
        if (sourceLanguage.StartsWith("es", StringComparison.OrdinalIgnoreCase))
            return "es";
        if (sourceLanguage.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
            return "pt";
        if (sourceLanguage.StartsWith("de", StringComparison.OrdinalIgnoreCase))
            return "de";
        return text.Any(character => character is >= '\u4e00' and <= '\u9fff') ? "zh-CN" : "en";
    }
}

internal static class TranslationTextChunker
{
    public static IReadOnlyList<string> SplitByCharacters(string text, int maximumCharacters) =>
        Split(text, rune => rune.Utf16SequenceLength, maximumCharacters);

    public static IReadOnlyList<string> SplitByUtf8Bytes(string text, int maximumBytes) =>
        Split(text, rune => rune.Utf8SequenceLength, maximumBytes);

    private static IReadOnlyList<string> Split(string text, Func<Rune, int> measure, int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        if (text.Length == 0)
        {
            return [text];
        }

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var end = start;
            var lastBreak = -1;
            var size = 0;
            while (end < text.Length)
            {
                var rune = Rune.GetRuneAt(text, end);
                var runeSize = measure(rune);
                if (size + runeSize > maximum)
                {
                    if (end == start)
                    {
                        throw new ArgumentOutOfRangeException(nameof(maximum), "The limit must fit a complete Unicode character.");
                    }
                    break;
                }
                var next = end + rune.Utf16SequenceLength;
                if (rune.Value is '\n' or '\r' or '。' or '！' or '？' or '.' or '!' or '?' or ' ')
                {
                    lastBreak = next;
                }
                size += runeSize;
                end = next;
            }
            if (end == text.Length)
            {
                chunks.Add(text[start..]);
                break;
            }
            var splitAt = lastBreak > start ? lastBreak : end;
            splitAt = KeepProtectionMarkerWhole(text, start, splitAt);
            chunks.Add(text[start..splitAt]);
            start = splitAt;
        }
        return chunks;
    }

    private static int KeepProtectionMarkerWhole(string text, int chunkStart, int splitAt)
    {
        const string markerStartText = "QZXKEEP";
        const string markerEndText = "WZXQ";
        var markerStart = -1;
        var candidate = text.IndexOf(markerStartText, chunkStart, StringComparison.Ordinal);
        while (candidate >= chunkStart && candidate < splitAt)
        {
            markerStart = candidate;
            candidate = text.IndexOf(
                markerStartText,
                candidate + markerStartText.Length,
                StringComparison.Ordinal);
        }
        if (markerStart < 0)
        {
            return splitAt;
        }
        var markerEnd = text.IndexOf(markerEndText, markerStart + markerStartText.Length, StringComparison.Ordinal);
        if (markerEnd < 0 || markerEnd + markerEndText.Length <= splitAt)
        {
            return splitAt;
        }
        return markerStart > chunkStart ? markerStart : markerEnd + markerEndText.Length;
    }
}
