using System.Net;
using System.Text;
using System.Text.Json;

namespace Screen.Core;

/// <summary>
/// 微软 Edge 免费在线翻译通道（免密钥、自动识别源语种、原生支持字符串数组批量翻译）。
/// </summary>
public sealed class EdgeTranslationProvider(HttpClient httpClient) : ITranslationProvider, IBatchTranslationProvider
{
    internal const string EdgeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";
    internal const int MaximumSingleChunkCharacters = 3000;
    internal const int MaximumBatchItems = 10;
    internal const int MaximumBatchCharacters = 3500;
    private const string EndpointBase = "https://edge.microsoft.com/translate/translatetext";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var normalized = TranslationTextProtector.NormalizeLikelyOcrArtifacts(text);
        var target = TranslationLanguageCodes.ToEdge(profile.TargetLanguage);
        var chunks = TranslationTextChunker.SplitByCharacters(normalized, MaximumSingleChunkCharacters);
        var translated = new StringBuilder();
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = await SendBatchWithRetryAsync([chunk], target, cancellationToken).ConfigureAwait(false);
            translated.Append(items[0]);
            streamingText?.Report(translated.ToString());
        }
        return new TranslationResult(translated.ToString().Trim(), "免费在线翻译");
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
        if (texts.Count == 0)
        {
            return new BatchTranslationResult([], "免费在线翻译");
        }

        var target = TranslationLanguageCodes.ToEdge(profile.TargetLanguage);
        var outputs = new string[texts.Count];
        var completed = 0;

        foreach (var slice in BuildSlices(texts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var translated = await TranslateSliceAsync(slice.Texts, target, sourceLanguage, profile, credentials, cancellationToken)
                .ConfigureAwait(false);
            for (var index = 0; index < slice.Indices.Count; index++)
            {
                outputs[slice.Indices[index]] = translated[index];
            }
            completed += slice.Indices.Count;
            completedCount?.Report(completed);
        }

        return new BatchTranslationResult(outputs, "免费在线翻译");
    }

    /// <summary>
    /// 按条数和字符上限划分请求，保留原始区域的索引。
    /// </summary>
    private static IReadOnlyList<(IReadOnlyList<int> Indices, IReadOnlyList<string> Texts)> BuildSlices(
        IReadOnlyList<string> texts)
    {
        var slices = new List<(IReadOnlyList<int> Indices, IReadOnlyList<string> Texts)>();
        var currentIndices = new List<int>(MaximumBatchItems);
        var currentTexts = new List<string>(MaximumBatchItems);
        var currentCharacters = 0;

        void Flush()
        {
            if (currentTexts.Count == 0)
            {
                return;
            }
            slices.Add((currentIndices.ToArray(), currentTexts.ToArray()));
            currentIndices.Clear();
            currentTexts.Clear();
            currentCharacters = 0;
        }

        for (var index = 0; index < texts.Count; index++)
        {
            var text = texts[index] ?? string.Empty;
            if (text.Length > MaximumSingleChunkCharacters)
            {
                Flush();
                slices.Add(([index], [text]));
                continue;
            }

            if (currentTexts.Count >= MaximumBatchItems
                || (currentTexts.Count > 0 && currentCharacters + text.Length > MaximumBatchCharacters))
            {
                Flush();
            }

            currentIndices.Add(index);
            currentTexts.Add(text);
            currentCharacters += text.Length;
        }

        Flush();
        return slices;
    }

    private async Task<string[]> TranslateSliceAsync(
        IReadOnlyList<string> sliceTexts,
        string targetCode,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (sliceTexts.Count == 1 && sliceTexts[0].Length > MaximumSingleChunkCharacters)
        {
            var single = await TranslateAsync(
                sliceTexts[0], sourceLanguage, profile, credentials, streamingText: null, cancellationToken)
                .ConfigureAwait(false);
            return [single.Text];
        }

        var results = new string[sliceTexts.Count];
        var activePositions = new List<int>(sliceTexts.Count);
        var requestTexts = new List<string>(sliceTexts.Count);

        for (var index = 0; index < sliceTexts.Count; index++)
        {
            var normalized = TranslationTextProtector.NormalizeLikelyOcrArtifacts(sliceTexts[index]);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                results[index] = string.Empty;
                continue;
            }
            activePositions.Add(index);
            requestTexts.Add(normalized);
        }

        if (requestTexts.Count == 0)
        {
            return results;
        }

        var translated = await SendBatchWithRetryAsync(requestTexts, targetCode, cancellationToken)
            .ConfigureAwait(false);
        for (var index = 0; index < activePositions.Count; index++)
        {
            results[activePositions[index]] = translated[index].Trim();
        }
        return results;
    }

    private async Task<string[]> SendBatchWithRetryAsync(
        IReadOnlyList<string> texts,
        string targetCode,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await SendBatchCoreAsync(texts, targetCode, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException exception) when (attempt == 0 && IsTransientStatus(exception.StatusCode))
            {
                lastException = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
        throw lastException ?? new HttpRequestException("免费在线翻译请求失败。");
    }

    private async Task<string[]> SendBatchCoreAsync(
        IReadOnlyList<string> texts,
        string targetCode,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{EndpointBase}?from=&to={Uri.EscapeDataString(targetCode)}&isEnterpriseClient=false";
        var payload = JsonSerializer.Serialize(texts);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        // Edge 翻译端点会校验客户端浏览器 User-Agent，缺少该请求头时服务端直接返回 HTTP 400。
        request.Headers.TryAddWithoutValidation("User-Agent", EdgeUserAgent);

        var (status, reason, responseBody) = await TranslationHttp.SendAsync(
            httpClient,
            request,
            "免费在线翻译请求超时（20 秒）。",
            RequestTimeout,
            cancellationToken).ConfigureAwait(false);
        if (status < 200 || status >= 300)
        {
            throw new HttpRequestException(
                $"HTTP {status} {reason}".Trim(),
                null,
                (HttpStatusCode)status);
        }

        return ParseBatchResponse(responseBody, texts.Count);
    }

    internal static string[] ParseBatchResponse(string responseBody, int expectedCount)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("免费在线翻译返回了无法识别的数据格式。");
        }
        var length = document.RootElement.GetArrayLength();
        if (length != expectedCount)
        {
            throw new InvalidOperationException($"免费在线翻译返回条数不匹配（预期 {expectedCount}，实际 {length}）。");
        }
        var results = new string[expectedCount];
        for (var index = 0; index < expectedCount; index++)
        {
            var entry = document.RootElement[index];
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("translations", out var translations)
                || translations.ValueKind != JsonValueKind.Array
                || translations.GetArrayLength() == 0
                || !translations[0].TryGetProperty("text", out var textValue)
                || textValue.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("免费在线翻译未返回完整译文。");
            }
            results[index] = textValue.GetString() ?? string.Empty;
        }
        return results;
    }

    private static bool IsTransientStatus(HttpStatusCode? statusCode) =>
        statusCode is
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;
}
