using System.Net;
using System.Text.Json;

namespace Screen.Core;

/// <summary>
/// Routes translations to the built-in free service or to the user's AI endpoints.
/// Only an AI failure with free fallback enabled may switch services.
/// </summary>
public sealed class OnlineTranslationProvider(HttpClient httpClient) : ITranslationProvider, IBatchTranslationProvider
{
    private readonly EdgeTranslationProvider _edge = new(httpClient);
    private readonly OpenAiTranslationProvider _openAi = new(httpClient);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (profile.Provider != TranslationProviderKind.OpenAiCompatible)
        {
            var free = await _edge.TranslateAsync(
                text, sourceLanguage, profile, credentials, streamingText, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return free;
        }

        var trace = new TranslationFailureTrace();
        TranslationResult result;
        try
        {
            RequirePrimaryKey(credentials);
            result = HasAiRacing(profile, credentials)
                ? await RaceTextAsync(text, sourceLanguage, profile, credentials, streamingText, trace, cancellationToken).ConfigureAwait(false)
                : await TranslateNaturallyWithRetryAsync(text, sourceLanguage, profile, credentials.ApiKey!, streamingText, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsExpectedProviderFailure(error))
        {
            result = await RecoverAiFailureAsync(error, profile, trace, async reason =>
            {
                var free = await _edge.TranslateAsync(
                    text, sourceLanguage, profile, credentials, streamingText, cancellationToken).ConfigureAwait(false);
                return free with { UsedFallback = true, FallbackProvider = TranslationProviderKind.Edge, PrimaryFailure = reason };
            }, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result with { Failures = trace.Snapshot() };
    }

    public async Task<BatchTranslationResult> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<int>? completedCount = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        cancellationToken.ThrowIfCancellationRequested();
        if (texts.Count == 0)
        {
            return new BatchTranslationResult([], string.Empty);
        }
        if (profile.Provider != TranslationProviderKind.OpenAiCompatible)
        {
            var free = await _edge.TranslateBatchAsync(
                texts, sourceLanguage, profile, credentials, completedCount, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return free;
        }

        var trace = new TranslationFailureTrace();
        var outputs = new string[texts.Count];
        var completed = 0;
        var providerName = $"AI 自然翻译（{profile.Model}）";
        BatchTranslationResult result;
        try
        {
            RequirePrimaryKey(credentials);
            foreach (var slice in OpenAiTranslationProvider.BuildBatchSlices(texts))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var translated = HasAiRacing(profile, credentials)
                        ? await RaceAiCandidatesAsync(profile, credentials, (candidate, key, secondary, token) =>
                            TranslateAiSliceAsync(slice.Texts, candidate, key, secondary, token), trace, cancellationToken).ConfigureAwait(false)
                        : await TranslateAiSliceAsync(slice.Texts, profile, credentials.ApiKey!, false, cancellationToken).ConfigureAwait(false);
                    MergeIntoOutputs(outputs, slice.Indices, translated.Texts);
                    providerName = translated.ProviderName;
                    completed += slice.Indices.Count;
                    completedCount?.Report(completed);
                }
                catch (BatchTranslationFormatException error)
                {
                    // Only malformed structured output may be retried as individual AI requests.
                    trace.Record(TranslationProviderKind.OpenAiCompatible, error);
                    await TranslateIndividualRegionsAsync(
                        texts, slice.Indices, outputs, sourceLanguage, profile, credentials,
                        OffsetProgress(completedCount, completed), cancellationToken).ConfigureAwait(false);
                    providerName = $"AI 自然翻译（{profile.Model}）";
                    completed += slice.Indices.Count;
                }
            }
            result = new BatchTranslationResult(outputs, providerName);
        }
        catch (Exception error) when (IsExpectedProviderFailure(error))
        {
            result = await RecoverAiFailureAsync(error, profile, trace, async reason =>
            {
                // Null is unfinished; an empty string is a completed blank OCR region.
                var missing = Enumerable.Range(0, outputs.Length).Where(i => outputs[i] is null).ToArray();
                var free = await _edge.TranslateBatchAsync(
                    missing.Select(i => texts[i]).ToArray(), sourceLanguage, profile, credentials,
                    OffsetProgress(completedCount, outputs.Length - missing.Length), cancellationToken).ConfigureAwait(false);
                MergeIntoOutputs(outputs, missing, free.Texts);
                return free with
                {
                    Texts = outputs,
                    UsedFallback = true,
                    FallbackProvider = TranslationProviderKind.Edge,
                    PrimaryFailure = reason
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result with { Failures = trace.Snapshot() };
    }

    private static async Task<TResult> RecoverAiFailureAsync<TResult>(
        Exception error,
        TranslationProfile profile,
        TranslationFailureTrace trace,
        Func<string, Task<TResult>> translateWithEdge,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        trace.Record(TranslationProviderKind.OpenAiCompatible, error);
        var reason = $"AI 自然翻译失败：{GetFriendlyMessage(error)}";
        if (!profile.EnableFreeFallback)
        {
            var failures = trace.Snapshot();
            if (failures.Length > 1)
            {
                throw new TranslationRouteException(reason, failures, error);
            }
            throw new InvalidOperationException(reason, error);
        }

        try
        {
            return await translateWithEdge(reason).ConfigureAwait(false);
        }
        catch (Exception edgeError) when (IsExpectedProviderFailure(edgeError))
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Record(TranslationProviderKind.Edge, edgeError);
            throw new TranslationRouteException($"{reason}；免费在线翻译也失败：{GetFriendlyMessage(edgeError)}",
                trace.Snapshot(), edgeError);
        }
    }

    private async Task<TranslationResult> RaceTextAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText,
        TranslationFailureTrace trace,
        CancellationToken cancellationToken)
    {
        var progressGate = new object();
        var acceptingPrimaryProgress = true;
        var primaryProgress = streamingText is null ? null : new RelayProgress(chunk =>
        {
            lock (progressGate)
            {
                if (acceptingPrimaryProgress)
                {
                    streamingText.Report(chunk);
                }
            }
        });

        async Task<TranslationResult> TranslateCandidateAsync(
            TranslationProfile candidate, string key, bool secondary, CancellationToken token)
        {
            var result = secondary
                ? await _openAi.TranslateAsync(text, sourceLanguage, candidate, new TranslationCredentials(key), null, token).ConfigureAwait(false)
                : await TranslateNaturallyWithRetryAsync(text, sourceLanguage, candidate, key, primaryProgress, token).ConfigureAwait(false);
            if (!IsPlausibleTranslation(text, result.Text))
            {
                throw new InvalidOperationException("AI 自然翻译返回译文过短。");
            }
            return result with { ProviderName = $"AI 自然翻译（{candidate.Model}）" };
        }

        TranslationResult winner;
        try
        {
            winner = await RaceAiCandidatesAsync(profile, credentials, TranslateCandidateAsync, trace, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (progressGate)
            {
                acceptingPrimaryProgress = false;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        streamingText?.Report(winner.Text);
        return winner;
    }

    private async Task<BatchTranslationResult> TranslateAiSliceAsync(
        IReadOnlyList<string> texts, TranslationProfile profile, string key, bool secondary, CancellationToken token)
    {
        var translated = secondary
            ? await _openAi.TranslateBatchSliceAsync(texts, profile, key, token).ConfigureAwait(false)
            : await TranslateBatchSliceWithRetryAsync(texts, profile, key, token).ConfigureAwait(false);
        return new BatchTranslationResult(translated, $"AI 自然翻译（{profile.Model}）");
    }

    private static async Task<TResult> RaceAiCandidatesAsync<TResult>(
        TranslationProfile profile,
        TranslationCredentials credentials,
        Func<TranslationProfile, string, bool, CancellationToken, Task<TResult>> translate,
        TranslationFailureTrace trace,
        CancellationToken cancellationToken) where TResult : class
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var secondaryProfile = profile with
        {
            BaseAddress = profile.RacingBaseAddress,
            Model = profile.RacingModel,
            CredentialName = profile.RacingCredentialName
        };

        async Task<(TResult? Result, Exception? Error)> RunCandidateAsync(
            TranslationProfile candidate, string key, bool secondary)
        {
            try
            {
                var result = await translate(candidate, key, secondary, linked.Token).ConfigureAwait(false);
                return (result, null);
            }
            catch (Exception error) when (IsExpectedProviderFailure(error))
            {
                trace.Record(TranslationProviderKind.OpenAiCompatible, error, secondaryAi: secondary);
                return (null, error);
            }
        }

        try
        {
            var primary = RunCandidateAsync(profile, credentials.ApiKey!, false);
            var secondary = RunCandidateAsync(secondaryProfile, credentials.RacingApiKey!, true);
            var first = await Task.WhenAny(primary, secondary).ConfigureAwait(false);
            var firstOutcome = await first.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (firstOutcome.Result is not null)
            {
                return firstOutcome.Result;
            }
            var secondOutcome = await (first == primary ? secondary : primary).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (secondOutcome.Result is not null)
            {
                return secondOutcome.Result;
            }

            var primaryError = first == primary ? firstOutcome.Error! : secondOutcome.Error!;
            var secondaryError = first == primary ? secondOutcome.Error! : firstOutcome.Error!;
            // A format error may trigger per-region AI requests only if BOTH candidates failed on formatting.
            if (primaryError is BatchTranslationFormatException && secondaryError is not BatchTranslationFormatException)
            {
                throw secondaryError;
            }
            throw primaryError;
        }
        finally
        {
            linked.Cancel();
        }
    }

    private async Task TranslateIndividualRegionsAsync(
        IReadOnlyList<string> texts,
        IReadOnlyList<int> indices,
        string[] outputs,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<int>? completedCount,
        CancellationToken cancellationToken)
    {
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(3);
        var completed = 0;
        var tasks = indices.Select(async index =>
        {
            await slots.WaitAsync(batch.Token).ConfigureAwait(false);
            try
            {
                var translated = string.Empty;
                if (!string.IsNullOrWhiteSpace(texts[index]))
                {
                    var result = await _openAi.TranslateAsync(
                        texts[index], sourceLanguage, profile, credentials, null, cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(result.Text))
                    {
                        throw new InvalidOperationException("翻译服务返回了空内容，请重试。");
                    }
                    translated = result.Text;
                }
                cancellationToken.ThrowIfCancellationRequested();
                lock (outputs)
                {
                    outputs[index] = translated;
                    completedCount?.Report(++completed);
                }
            }
            catch
            {
                // Stop queuing work; let in-flight AI requests finish so the free service only sees missing regions.
                batch.Cancel();
                throw;
            }
            finally
            {
                slots.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static void RequirePrimaryKey(TranslationCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            throw new InvalidOperationException("尚未配置 AI 自然翻译 API Key");
        }
    }

    private async Task<string[]> TranslateBatchSliceWithRetryAsync(
        IReadOnlyList<string> sliceTexts,
        TranslationProfile profile,
        string apiKey,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await _openAi.TranslateBatchSliceAsync(
                    sliceTexts, profile, apiKey, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException exception) when (attempt == 0 && IsTransientHttpStatus(exception))
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(1.2), cancellationToken).ConfigureAwait(false);
            }
        }
        throw lastException ?? new HttpRequestException("AI 批量翻译失败。");
    }

    private static void MergeIntoOutputs(string[] outputs, IReadOnlyList<int> indices, IReadOnlyList<string> values)
    {
        for (var index = 0; index < indices.Count; index++)
        {
            outputs[indices[index]] = values[index];
        }
    }

    private static IProgress<int>? OffsetProgress(IProgress<int>? progress, int completed) =>
        progress is null ? null : new RelayIntProgress(done => progress.Report(completed + done));

    private static bool HasAiRacing(TranslationProfile profile, TranslationCredentials credentials) =>
        profile.EnableRacing
        && !string.IsNullOrWhiteSpace(profile.RacingBaseAddress)
        && !string.IsNullOrWhiteSpace(profile.RacingModel)
        && !string.IsNullOrWhiteSpace(credentials.RacingApiKey);

    private static string GetFriendlyMessage(Exception exception)
    {
        var message = exception.Message.Trim();
        return message.Length <= 180 ? message : message[..180] + "…";
    }

    private sealed class RelayProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class RelayIntProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
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
                    text, sourceLanguage, profile, singleCredentials, streamingText, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException exception) when (attempt < 2 && IsTransientHttpStatus(exception))
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
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
