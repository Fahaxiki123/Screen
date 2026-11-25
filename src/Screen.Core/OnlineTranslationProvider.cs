using System.Net;
using System.Text.Json;

namespace Screen.Core;

/// <summary>
/// 在线翻译路由与编排器：支持免费在线翻译（Edge 优先、MyMemory 冷备）、AI 自然翻译（含可选竞速与分片增量批量）、腾讯机器翻译与回退链。
/// </summary>
public sealed class OnlineTranslationProvider : ITranslationProvider, IBatchTranslationProvider
{
    private readonly EdgeTranslationProvider _edge;
    private readonly TencentTranslationProvider _tencent;
    private readonly MyMemoryTranslationProvider _myMemory;
    private readonly OpenAiTranslationProvider _openAi;

    public OnlineTranslationProvider(HttpClient httpClient)
    {
        _edge = new EdgeTranslationProvider(httpClient);
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
        cancellationToken.ThrowIfCancellationRequested();
        var trace = new TranslationFailureTrace();
        try
        {
            var task = profile.Provider switch
            {
                TranslationProviderKind.Edge => FallbackToFreeProvidersAsync(
                    text, sourceLanguage, profile, credentials, streamingText, null, cancellationToken, trace),
                TranslationProviderKind.OpenAiCompatible => TranslateNaturallyWithFallbackAsync(
                    text, sourceLanguage, profile, credentials, streamingText, cancellationToken, trace),
                TranslationProviderKind.MyMemory => _myMemory.TranslateAsync(
                    text, sourceLanguage, profile, credentials, streamingText, cancellationToken),
                _ => TranslateWithTencentFallbackAsync(
                    text, sourceLanguage, profile, credentials, streamingText, cancellationToken, trace)
            };
            var result = await task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var attempts = trace.Snapshot();
            return attempts.Length == 0 ? result : result with { Failures = attempts };
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failures = trace.Snapshot();
            if (failures.Length > 1)
            {
                throw new TranslationRouteException(exception.Message, failures, exception);
            }
            throw;
        }
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

        var trace = new TranslationFailureTrace();
        try
        {
            var task = profile.Provider switch
            {
                TranslationProviderKind.Edge => FallbackBatchToFreeProvidersAsync(
                    texts, sourceLanguage, profile, credentials, completedCount, null, cancellationToken, trace),
                TranslationProviderKind.OpenAiCompatible => TranslateBatchNaturallyWithFallbackAsync(
                    texts, sourceLanguage, profile, credentials, completedCount, cancellationToken, trace),
                TranslationProviderKind.MyMemory => TranslateRegionsIndividuallyAsync(
                    texts, sourceLanguage, profile, credentials, _myMemory, "MyMemory 基础翻译", maxConcurrency: 2, completedCount, cancellationToken),
                _ => TranslateBatchWithTencentFallbackAsync(
                    texts, sourceLanguage, profile, credentials, completedCount, cancellationToken, trace)
            };
            var result = await task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var attempts = trace.Snapshot();
            return attempts.Length == 0 ? result : result with { Failures = attempts };
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failures = trace.Snapshot();
            if (failures.Length > 1)
            {
                throw new TranslationRouteException(exception.Message, failures, exception);
            }
            throw;
        }
    }

    private async Task<TranslationResult> TranslateNaturallyWithFallbackAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText,
        CancellationToken cancellationToken,
        TranslationFailureTrace trace)
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

            var hasAiRacing = HasAiRacing(profile, credentials);
            var hasTencent = HasTencentCredentials(credentials);
            var hasFastTmt = profile.EnableFastFallbackRace && hasTencent;

            if (!hasAiRacing && !hasFastTmt)
            {
                return await TranslateNaturallyWithRetryAsync(
                    text, sourceLanguage, profile, credentials.ApiKey, streamingText, cancellationToken).ConfigureAwait(false);
            }

            var outcome = await RaceNaturallyAsync(
                text, sourceLanguage, profile, credentials, hasAiRacing, hasFastTmt, streamingText, cancellationToken, trace).ConfigureAwait(false);
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

        trace.Record(TranslationProviderKind.OpenAiCompatible, primaryException);
        if (!tencentAlreadyAttempted && HasTencentCredentials(credentials))
        {
            try
            {
                var fallback = await _tencent.TranslateAsync(
                    text, sourceLanguage, profile, credentials, streamingText, cancellationToken).ConfigureAwait(false);
                return fallback with
                {
                    UsedFallback = true,
                    FallbackProvider = TranslationProviderKind.Tencent,
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
                trace.Record(TranslationProviderKind.Tencent, exception);
            }
        }

        if (profile.EnableMyMemoryFallback)
        {
            var failure = $"AI 自然翻译：{GetFriendlyMessage(primaryException)}";
            if (tencentException is not null)
            {
                failure += $"；腾讯机器翻译：{GetFriendlyMessage(tencentException)}";
            }
            return await FallbackToFreeProvidersAsync(
                text, sourceLanguage, profile, credentials, streamingText, failure, cancellationToken, trace).ConfigureAwait(false);
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
        CancellationToken cancellationToken,
        TranslationFailureTrace trace)
    {
        try
        {
            if (!HasTencentCredentials(credentials))
            {
                throw new InvalidOperationException("尚未配置腾讯云 SecretId 和 SecretKey");
            }

            return await _tencent.TranslateAsync(
                text, sourceLanguage, profile, credentials, streamingText, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception primaryException) when (
            profile.EnableMyMemoryFallback && IsExpectedProviderFailure(primaryException))
        {
            trace.Record(TranslationProviderKind.Tencent, primaryException);
            return await FallbackToFreeProvidersAsync(
                text, sourceLanguage, profile, credentials, streamingText,
                GetFriendlyMessage(primaryException), cancellationToken, trace).ConfigureAwait(false);
        }
    }

    private async Task<TranslationResult> FallbackToFreeProvidersAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText,
        string? primaryFailureSummary,
        CancellationToken cancellationToken,
        TranslationFailureTrace trace)
    {
        Exception edgeException;
        try
        {
            var edgeFallback = await _edge.TranslateAsync(
                text, sourceLanguage, profile, credentials, streamingText, cancellationToken).ConfigureAwait(false);
            return edgeFallback with
            {
                UsedFallback = primaryFailureSummary is not null,
                FallbackProvider = primaryFailureSummary is null ? null : TranslationProviderKind.Edge,
                PrimaryFailure = primaryFailureSummary
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (profile.EnableMyMemoryFallback && IsExpectedProviderFailure(exception))
        {
            trace.Record(TranslationProviderKind.Edge, exception);
            edgeException = exception;
        }

        var failureSummary = AppendEdgeFailure(primaryFailureSummary, edgeException);
        try
        {
            var myMemoryFallback = await _myMemory.TranslateAsync(
                text, sourceLanguage, profile, credentials, streamingText, cancellationToken).ConfigureAwait(false);
            return myMemoryFallback with
            {
                UsedFallback = true,
                FallbackProvider = TranslationProviderKind.MyMemory,
                PrimaryFailure = failureSummary
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception fallbackException)
        {
            trace.Record(TranslationProviderKind.MyMemory, fallbackException);
            throw new InvalidOperationException(
                $"{failureSummary}；MyMemory 也失败：{GetFriendlyMessage(fallbackException)}",
                fallbackException);
        }
    }

    private static string AppendEdgeFailure(string? previousFailure, Exception edgeException) =>
        (previousFailure is null ? "" : previousFailure + "；") + $"免费在线翻译：{GetFriendlyMessage(edgeException)}";

    /// <summary>
    /// 分片执行 AI 原位批量翻译（支持已开启的第二 AI 竞速与超时快速回退腾讯）：
    /// 格式错乱时仅对当前失败分片降级逐区；401/403/429 等服务错误绝不放大为逐区请求，且保留已完成分片再回退剩余区域。
    /// </summary>
    private async Task<BatchTranslationResult> TranslateBatchNaturallyWithFallbackAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<int>? completedCount,
        CancellationToken cancellationToken,
        TranslationFailureTrace trace)
    {
        var outputs = new string[texts.Count];
        var completed = 0;
        var providerName = $"AI 自然翻译（{profile.Model}）";
        var slices = OpenAiTranslationProvider.BuildBatchSlices(texts);
        var failedSliceIndex = -1;
        Exception? primaryException = null;
        var tencentAlreadyAttempted = false;
        Exception? tencentException = null;
        var usedFastFallback = false;
        var usedPartialTencent = false;
        string? fastFallbackReason = null;

        if (string.IsNullOrWhiteSpace(credentials.ApiKey))
        {
            failedSliceIndex = 0;
            primaryException = new InvalidOperationException("尚未配置 AI 自然翻译 API Key");
        }
        else
        {
            var hasAiRacing = HasAiRacing(profile, credentials);
            var hasFastTmt = profile.EnableFastFallbackRace && HasTencentCredentials(credentials);

            for (var sliceIndex = 0; sliceIndex < slices.Count; sliceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = slices[sliceIndex];
                if (usedFastFallback)
                {
                    // 前序分片已触发超时快退腾讯，剩余分片直接继续走腾讯避免再次死等超时。
                    var remainingIndices = CollectRemainingIndices(slices, sliceIndex);
                    var remainingSliceTexts = remainingIndices.Select(index => texts[index]).ToArray();
                    var remainder = await TranslateBatchWithTencentFallbackAsync(
                        remainingSliceTexts, sourceLanguage, profile, credentials,
                        OffsetProgress(completedCount, completed), cancellationToken, trace, fastFallbackReason).ConfigureAwait(false);
                    MergeIntoOutputs(outputs, remainingIndices, remainder.Texts);
                    return remainder with { Texts = outputs };
                }

                tencentAlreadyAttempted = false;
                tencentException = null;
                try
                {
                    if (hasAiRacing || hasFastTmt)
                    {
                        var outcome = await RaceBatchSliceAsync(
                            slice.Texts,
                            sourceLanguage,
                            profile,
                            credentials,
                            hasAiRacing,
                            hasFastTmt,
                            cancellationToken, trace).ConfigureAwait(false);
                        if (outcome.Texts is not null)
                        {
                            providerName = outcome.ProviderName;
                            usedFastFallback |= outcome.UsedFastFallback;
                            fastFallbackReason ??= outcome.FastFallbackReason;
                            for (var index = 0; index < slice.Indices.Count; index++)
                            {
                                outputs[slice.Indices[index]] = outcome.Texts[index];
                            }
                            completed += slice.Indices.Count;
                            completedCount?.Report(completed);
                            continue;
                        }

                        tencentAlreadyAttempted = outcome.TencentAttempted;
                        tencentException = outcome.TencentError;
                        if (outcome.PartialTencentTexts is { } partial)
                        {
                            var added = MergeCompletedRegions(outputs, slice.Indices, partial);
                            if (added > 0)
                            {
                                completed += added;
                                usedPartialTencent = true;
                                completedCount?.Report(completed);
                            }
                        }
                        if (outcome.FormatFailedOnly)
                        {
                            throw outcome.PrimaryError ?? new BatchTranslationFormatException("AI 竞速批量翻译格式校验失败。");
                        }
                        failedSliceIndex = sliceIndex;
                        primaryException = outcome.PrimaryError ?? new HttpRequestException("AI 批量翻译失败。");
                        break;
                    }

                    var translated = await TranslateBatchSliceWithRetryAsync(
                        slice.Texts, profile, credentials.ApiKey!, cancellationToken).ConfigureAwait(false);
                    providerName = $"AI 自然翻译（{profile.Model}）";
                    for (var index = 0; index < slice.Indices.Count; index++)
                    {
                        outputs[slice.Indices[index]] = translated[index];
                    }
                    completed += slice.Indices.Count;
                    completedCount?.Report(completed);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (BatchTranslationFormatException exception)
                {
                    trace.Record(TranslationProviderKind.OpenAiCompatible, exception);
                    // 仅对当前格式错乱的分片降级逐区，绝不重发前面已经成功的分片。
                    var missing = slice.Indices.Where(index => outputs[index] is null).ToArray();
                    var individualOutputs = new string[missing.Length];
                    try
                    {
                        await TranslateRegionsIndividuallyAsync(
                            missing.Select(index => texts[index]).ToArray(), sourceLanguage, profile, credentials,
                            _openAi, providerName, maxConcurrency: 3, OffsetProgress(completedCount, completed),
                            cancellationToken, individualOutputs).ConfigureAwait(false);
                    }
                    catch (Exception individualError) when (IsExpectedProviderFailure(individualError))
                    {
                        failedSliceIndex = sliceIndex;
                        primaryException = individualError;
                        break;
                    }
                    finally
                    {
                        completed += MergeCompletedRegions(outputs, missing, individualOutputs);
                    }
                }
                catch (Exception exception) when (IsExpectedProviderFailure(exception))
                {
                    failedSliceIndex = sliceIndex;
                    primaryException = exception;
                    break;
                }
            }
        }

        if (primaryException is null)
        {
            return new BatchTranslationResult(
                outputs,
                providerName,
                UsedFallback: usedFastFallback || usedPartialTencent,
                PrimaryFailure: fastFallbackReason ?? (usedPartialTencent ? "部分区域已回退腾讯机器翻译" : null))
            {
                FallbackProvider = usedFastFallback || usedPartialTencent ? TranslationProviderKind.Tencent : null
            };
        }

        trace.Record(TranslationProviderKind.OpenAiCompatible, primaryException);
        var remainingIndicesAfterFailure = CollectRemainingIndices(slices, Math.Max(0, failedSliceIndex))
            .Where(index => outputs[index] is null).ToArray();
        var remainingTexts = remainingIndicesAfterFailure.Select(index => texts[index]).ToArray();
        var remainingProgress = OffsetProgress(completedCount, completed);

        if (!tencentAlreadyAttempted && HasTencentCredentials(credentials))
        {
            var result = await TranslateBatchWithTencentFallbackAsync(
                remainingTexts, sourceLanguage, profile, credentials, remainingProgress, cancellationToken, trace,
                $"AI 自然翻译：{GetFriendlyMessage(primaryException)}").ConfigureAwait(false);
            MergeIntoOutputs(outputs, remainingIndicesAfterFailure, result.Texts);
            return result with { Texts = outputs };
        }

        if (profile.EnableMyMemoryFallback)
        {
            var summary = $"AI 自然翻译：{GetFriendlyMessage(primaryException)}";
            if (tencentException is not null)
            {
                summary += $"；腾讯机器翻译：{GetFriendlyMessage(tencentException)}";
            }
            var freeFallback = await FallbackBatchToFreeProvidersAsync(
                remainingTexts, sourceLanguage, profile, credentials, remainingProgress, summary, cancellationToken, trace).ConfigureAwait(false);
            MergeIntoOutputs(outputs, remainingIndicesAfterFailure, freeFallback.Texts);
            return freeFallback with { Texts = outputs };
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

    private readonly record struct BatchSliceOutcome(
        string[]? Texts,
        string ProviderName,
        bool UsedFastFallback,
        string? FastFallbackReason,
        bool FormatFailedOnly,
        Exception? PrimaryError,
        bool TencentAttempted,
        Exception? TencentError,
        string[]? PartialTencentTexts = null);

    private async Task<BatchSliceOutcome> RaceBatchSliceAsync(
        IReadOnlyList<string> sliceTexts,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        bool hasAiRacing,
        bool hasFastTmt,
        CancellationToken cancellationToken,
        TranslationFailureTrace trace)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var delayWake = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var racingProfile = profile with
        {
            BaseAddress = profile.RacingBaseAddress,
            Model = profile.RacingModel,
            CredentialName = profile.RacingCredentialName
        };

        var winnerLocked = 0;
        var remainingAiCandidates = hasAiRacing ? 2 : 1;
        Exception? primaryError = null;
        Exception? racingError = null;
        var primaryFormatFailed = false;
        var racingFormatFailed = false;
        var anyNonFormatFailure = 0;
        string[]? partialTencentTexts = null;
        var tencentAttempted = false;
        Exception? tencentError = null;

        void MarkAiCandidateFailed(bool isFormatError)
        {
            if (!isFormatError)
            {
                Interlocked.Exchange(ref anyNonFormatFailure, 1);
            }
            if (Interlocked.Decrement(ref remainingAiCandidates) == 0
                && Volatile.Read(ref winnerLocked) == 0)
            {
                try
                {
                    if (Volatile.Read(ref anyNonFormatFailure) == 0)
                    {
                        // 所有 AI 候选均仅因 JSON 格式错乱失败，取消延迟快退并交由外层降级逐区 AI。
                        linked.Cancel();
                    }
                    else
                    {
                        delayWake.Cancel();
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        async Task<BatchSliceOutcome?> RunCandidateAsync(
            TranslationProfile candidateProfile,
            string apiKey,
            bool isPrimary)
        {
            try
            {
                var translated = isPrimary
                    ? await TranslateBatchSliceWithRetryAsync(sliceTexts, candidateProfile, apiKey, linked.Token).ConfigureAwait(false)
                    : await _openAi.TranslateBatchSliceAsync(sliceTexts, candidateProfile, apiKey, linked.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref winnerLocked, 1);
                return new BatchSliceOutcome(
                    translated,
                    $"AI 自然翻译（{candidateProfile.Model}）",
                    UsedFastFallback: false,
                    FastFallbackReason: null,
                    FormatFailedOnly: false,
                    PrimaryError: null,
                    TencentAttempted: false,
                    TencentError: null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                MarkAiCandidateFailed(isFormatError: false);
                return null;
            }
            catch (BatchTranslationFormatException exception)
            {
                trace.Record(TranslationProviderKind.OpenAiCompatible, exception, secondaryAi: !isPrimary);
                if (isPrimary)
                {
                    primaryFormatFailed = true;
                    primaryError = exception;
                }
                else
                {
                    racingFormatFailed = true;
                    racingError = exception;
                }
                MarkAiCandidateFailed(isFormatError: true);
                return null;
            }
            catch (Exception exception) when (IsExpectedProviderFailure(exception))
            {
                trace.Record(TranslationProviderKind.OpenAiCompatible, exception, secondaryAi: !isPrimary);
                if (isPrimary)
                {
                    primaryError = exception;
                }
                else
                {
                    racingError = exception;
                }
                MarkAiCandidateFailed(isFormatError: false);
                return null;
            }
        }

        async Task<BatchSliceOutcome?> RunDelayedTencentSliceAsync(int delaySeconds)
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
                wokenByAiFailure = true;
            }

            if (linked.IsCancellationRequested || Volatile.Read(ref winnerLocked) != 0)
            {
                return null;
            }

            tencentAttempted = true;
            partialTencentTexts = new string[sliceTexts.Count];
            try
            {
                var tencentResult = await TranslateRegionsIndividuallyAsync(
                    sliceTexts,
                    sourceLanguage,
                    profile,
                    credentials,
                    _tencent,
                    "腾讯机器翻译",
                    maxConcurrency: 3,
                    completedCount: null,
                    linked.Token, partialTencentTexts).ConfigureAwait(false);
                Interlocked.Exchange(ref winnerLocked, 1);
                var nonFormat = PickNonFormatError(primaryError, primaryFormatFailed, racingError, racingFormatFailed);
                if (!wokenByAiFailure)
                {
                    trace.Record(TranslationProviderKind.OpenAiCompatible, new TimeoutException());
                }
                var reason = wokenByAiFailure && nonFormat is not null
                    ? $"AI 自然翻译：{GetFriendlyMessage(nonFormat)}"
                    : $"AI 自然翻译超过 {delaySeconds} 秒未完成，已自动切换快速翻译";
                return new BatchSliceOutcome(
                    tencentResult.Texts.ToArray(),
                    tencentResult.ProviderName,
                    UsedFastFallback: true,
                    FastFallbackReason: reason,
                    FormatFailedOnly: false,
                    PrimaryError: null,
                    TencentAttempted: true,
                    TencentError: null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception exception) when (IsExpectedProviderFailure(exception))
            {
                tencentError = exception;
                trace.Record(TranslationProviderKind.Tencent, exception);
                return null;
            }
        }

        var candidates = new List<Task<BatchSliceOutcome?>>
        {
            RunCandidateAsync(profile, credentials.ApiKey!, isPrimary: true)
        };
        if (hasAiRacing)
        {
            candidates.Add(RunCandidateAsync(racingProfile, credentials.RacingApiKey!, isPrimary: false));
        }
        if (hasFastTmt)
        {
            var delaySeconds = Math.Clamp(profile.FastFallbackDelaySeconds, 2, 30);
            candidates.Add(RunDelayedTencentSliceAsync(delaySeconds));
        }

        while (candidates.Count > 0)
        {
            var finished = await Task.WhenAny(candidates).ConfigureAwait(false);
            candidates.Remove(finished);
            var winner = await finished.ConfigureAwait(false);
            if (winner is not null)
            {
                linked.Cancel();
                return winner.Value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var allFormatFailed = hasAiRacing
            ? primaryFormatFailed && racingFormatFailed
            : primaryFormatFailed;
        if (allFormatFailed)
        {
            return new BatchSliceOutcome(
                Texts: null,
                ProviderName: string.Empty,
                UsedFastFallback: false,
                FastFallbackReason: null,
                FormatFailedOnly: true,
                PrimaryError: primaryError ?? new BatchTranslationFormatException("AI 批量翻译格式校验失败。"),
                TencentAttempted: tencentAttempted,
                TencentError: tencentError,
                PartialTencentTexts: partialTencentTexts);
        }

        // 主备异常对称处理：只要任一路报 401/429 等非格式错误，优先返回该非格式异常，绝不漏抛另一路的格式异常触发逐区放大。
        var nonFormatError = PickNonFormatError(primaryError, primaryFormatFailed, racingError, racingFormatFailed)
                             ?? new InvalidOperationException(primaryError?.Message ?? racingError?.Message ?? "AI 批量翻译失败。");
        return new BatchSliceOutcome(
            Texts: null,
            ProviderName: string.Empty,
            UsedFastFallback: false,
            FastFallbackReason: null,
            FormatFailedOnly: false,
            PrimaryError: nonFormatError,
            TencentAttempted: tencentAttempted,
            TencentError: tencentError,
            PartialTencentTexts: partialTencentTexts);
    }

    private static Exception? PickNonFormatError(
        Exception? primaryError,
        bool primaryFormatFailed,
        Exception? racingError,
        bool racingFormatFailed)
    {
        if (!primaryFormatFailed && primaryError is not null)
        {
            return primaryError;
        }
        if (!racingFormatFailed && racingError is not null)
        {
            return racingError;
        }
        return null;
    }

    private async Task<BatchTranslationResult> TranslateBatchWithTencentFallbackAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<int>? completedCount,
        CancellationToken cancellationToken,
        TranslationFailureTrace trace,
        string? aiFailure = null)
    {
        var outputs = new string[texts.Count];
        try
        {
            if (!HasTencentCredentials(credentials))
            {
                throw new InvalidOperationException("尚未配置腾讯云 SecretId 和 SecretKey");
            }
            var result = await TranslateRegionsIndividuallyAsync(
                texts, sourceLanguage, profile, credentials, _tencent, "腾讯机器翻译",
                maxConcurrency: 3, completedCount, cancellationToken, outputs).ConfigureAwait(false);
            return result with
            {
                UsedFallback = aiFailure is not null,
                PrimaryFailure = aiFailure,
                FallbackProvider = aiFailure is null ? null : TranslationProviderKind.Tencent
            };
        }
        catch (Exception exception) when (IsExpectedProviderFailure(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Record(TranslationProviderKind.Tencent, exception);
            var reason = $"腾讯机器翻译：{GetFriendlyMessage(exception)}";
            if (aiFailure is not null)
            {
                reason = $"{aiFailure}；{reason}";
            }
            if (!profile.EnableMyMemoryFallback)
            {
                if (aiFailure is null)
                {
                    throw;
                }
                throw new InvalidOperationException(reason, exception);
            }

            var missing = Enumerable.Range(0, texts.Count).Where(index => outputs[index] is null).ToArray();
            var fallback = await FallbackBatchToFreeProvidersAsync(
                missing.Select(index => texts[index]).ToArray(), sourceLanguage, profile, credentials,
                OffsetProgress(completedCount, texts.Count - missing.Length), reason, cancellationToken, trace).ConfigureAwait(false);
            MergeIntoOutputs(outputs, missing, fallback.Texts);
            return fallback with { Texts = outputs };
        }
    }

    private async Task<BatchTranslationResult> FallbackBatchToFreeProvidersAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<int>? completedCount,
        string? primaryFailureSummary,
        CancellationToken cancellationToken,
        TranslationFailureTrace trace)
    {
        var outputs = new string[texts.Count];
        var completed = 0;
        var targetCode = TranslationLanguageCodes.ToEdge(profile.TargetLanguage);
        var slices = EdgeTranslationProvider.BuildSlices(texts);

        for (var sliceIndex = 0; sliceIndex < slices.Count; sliceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slice = slices[sliceIndex];
            try
            {
                var translated = await _edge.TranslateSliceAsync(
                    slice.Texts, targetCode, sourceLanguage, profile, credentials, cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < slice.Indices.Count; index++)
                {
                    outputs[slice.Indices[index]] = translated[index];
                }
                completed += slice.Indices.Count;
                completedCount?.Report(completed);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception edgeException) when (profile.EnableMyMemoryFallback && IsExpectedProviderFailure(edgeException))
            {
                trace.Record(TranslationProviderKind.Edge, edgeException);
                var failureSummary = AppendEdgeFailure(primaryFailureSummary, edgeException);
                var remainingIndices = CollectRemainingIndices(slices, sliceIndex);
                var remainingTexts = remainingIndices.Select(index => texts[index]).ToArray();
                var baseCompleted = completed;
                try
                {
                    var myMemoryResult = await TranslateRegionsIndividuallyAsync(
                        remainingTexts,
                        sourceLanguage,
                        profile,
                        credentials,
                        _myMemory,
                        "MyMemory 基础翻译",
                        maxConcurrency: 2,
                        completedCount is null ? null : new RelayIntProgress(done => completedCount.Report(baseCompleted + done)),
                        cancellationToken).ConfigureAwait(false);
                    MergeIntoOutputs(outputs, remainingIndices, myMemoryResult.Texts);
                    return new BatchTranslationResult(
                        outputs,
                        myMemoryResult.ProviderName,
                        UsedFallback: true,
                        PrimaryFailure: failureSummary)
                    { FallbackProvider = TranslationProviderKind.MyMemory };
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception fallbackException)
                {
                    trace.Record(TranslationProviderKind.MyMemory, fallbackException);
                    throw new InvalidOperationException(
                        $"{failureSummary}；MyMemory 也失败：{GetFriendlyMessage(fallbackException)}",
                        fallbackException);
                }
            }
        }

        return new BatchTranslationResult(
            outputs,
            "免费在线翻译",
            UsedFallback: primaryFailureSummary is not null,
            PrimaryFailure: primaryFailureSummary)
        {
            FallbackProvider = primaryFailureSummary is null ? null : TranslationProviderKind.Edge
        };
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

    private static List<int> CollectRemainingIndices(
        IReadOnlyList<(IReadOnlyList<int> Indices, IReadOnlyList<string> Texts)> slices,
        int fromSliceIndex)
    {
        var remaining = new List<int>();
        for (var sliceIndex = fromSliceIndex; sliceIndex < slices.Count; sliceIndex++)
        {
            remaining.AddRange(slices[sliceIndex].Indices);
        }
        return remaining;
    }

    private static void MergeIntoOutputs(string[] outputs, IReadOnlyList<int> indices, IReadOnlyList<string> values)
    {
        for (var index = 0; index < indices.Count; index++)
        {
            outputs[indices[index]] = values[index];
        }
    }

    private static int MergeCompletedRegions(string[] outputs, IReadOnlyList<int> indices, IReadOnlyList<string> partial)
    {
        var count = 0;
        for (var i = 0; i < indices.Count; i++)
        {
            // Null means unfinished. An empty string is the valid result of a blank OCR region.
            if (partial[i] is { } translated)
            {
                outputs[indices[i]] = translated;
                count++;
            }
        }
        return count;
    }

    private static IProgress<int>? OffsetProgress(IProgress<int>? progress, int completed) =>
        progress is null ? null : new RelayIntProgress(done => progress.Report(completed + done));

    private static bool HasAiRacing(TranslationProfile profile, TranslationCredentials credentials) =>
        profile.EnableRacing
        && !string.IsNullOrWhiteSpace(profile.RacingBaseAddress)
        && !string.IsNullOrWhiteSpace(profile.RacingModel)
        && !string.IsNullOrWhiteSpace(credentials.RacingApiKey);

    private static bool HasTencentCredentials(TranslationCredentials credentials) =>
        !string.IsNullOrWhiteSpace(credentials.TencentSecretId)
        && !string.IsNullOrWhiteSpace(credentials.TencentSecretKey);

    private static async Task<BatchTranslationResult> TranslateRegionsIndividuallyAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        ITranslationProvider provider,
        string defaultProviderName,
        int maxConcurrency,
        IProgress<int>? completedCount,
        CancellationToken cancellationToken,
        string[]? retainedOutputs = null)
    {
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(maxConcurrency);
        var outputs = retainedOutputs ?? new string[texts.Count];
        // After a service failure, stop starting regions. Let requests already on the wire
        // finish when the caller can preserve their results for fallback.
        var requestToken = retainedOutputs is null ? batch.Token : cancellationToken;
        var completed = 0;
        var providerName = defaultProviderName;

        var tasks = texts.Select(async (text, index) =>
        {
            await slots.WaitAsync(batch.Token).ConfigureAwait(false);
            try
            {
                var translated = string.Empty;
                string? service = null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var result = await provider.TranslateAsync(
                        text, sourceLanguage, profile, credentials, streamingText: null, requestToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(result.Text))
                    {
                        throw new InvalidOperationException("翻译服务返回了空内容，请重试。");
                    }
                    translated = result.Text;
                    service = result.ProviderName;
                }
                requestToken.ThrowIfCancellationRequested();
                lock (outputs)
                {
                    outputs[index] = translated;
                    if (!string.IsNullOrWhiteSpace(service))
                    {
                        providerName = service;
                    }
                    completedCount?.Report(++completed);
                }
            }
            catch
            {
                batch.Cancel();
                throw;
            }
            finally
            {
                slots.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new BatchTranslationResult(outputs, providerName);
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

    private sealed class RelayIntProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private async Task<RaceOutcome> RaceNaturallyAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        bool hasAiRacing,
        bool hasFastTmt,
        IProgress<string>? streamingText,
        CancellationToken cancellationToken,
        TranslationFailureTrace trace)
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
                    text, sourceLanguage, profile, credentials.ApiKey!, primaryProgress, linked.Token).ConfigureAwait(false);
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
                trace.Record(TranslationProviderKind.OpenAiCompatible, exception);
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
                    text, sourceLanguage, racingProfile, new TranslationCredentials(racingKey), streamingText: null, linked.Token).ConfigureAwait(false);
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
                trace.Record(TranslationProviderKind.OpenAiCompatible, exception, secondaryAi: true);
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
                wokenByAiFailure = true;
            }

            tencentAttempted = true;
            try
            {
                var result = await _tencent.TranslateAsync(
                    text, sourceLanguage, profile, credentials, streamingText: null, linked.Token).ConfigureAwait(false);
                if (!wokenByAiFailure)
                {
                    trace.Record(TranslationProviderKind.OpenAiCompatible, new TimeoutException());
                }
                var failureReason = wokenByAiFailure && Volatile.Read(ref primaryError) is { } aiFailure
                    ? $"AI 自然翻译：{GetFriendlyMessage(aiFailure)}"
                    : $"AI 自然翻译超过 {delaySeconds} 秒未完成，已自动切换快速翻译";
                return result with
                {
                    UsedFallback = true,
                    FallbackProvider = TranslationProviderKind.Tencent,
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
                trace.Record(TranslationProviderKind.Tencent, exception);
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
