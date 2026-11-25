using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Screen.Core;

/// <summary>
/// 表示 AI 批量翻译响应未满足严格的 JSON 编号契约，仅该异常允许安全降级为逐区翻译。
/// </summary>
public sealed class BatchTranslationFormatException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public sealed class OpenAiTranslationProvider(HttpClient httpClient) : ITranslationProvider
{
    internal const int MaximumBatchItems = 12;
    internal const int MaximumBatchCharacters = 2400;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan DefaultBodyReadTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultFirstTokenTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BatchRequestTimeout = TimeSpan.FromSeconds(45);

    public async Task<TranslationResult> TranslateAsync(
        string text,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await TranslateCoreAsync(text, profile, credentials, streamingText, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient 超时或读取正文超时取消内部令牌而非调用方令牌，转为 TimeoutException 以便外层回退。
            throw new TimeoutException("AI 自然翻译请求超时。", exception);
        }
        catch (IOException exception)
        {
            throw new HttpRequestException("AI 自然翻译连接中断，译文不完整。", exception);
        }
    }

    /// <summary>
    /// 将多个原位翻译区域按长度分批打包为结构化 JSON 编号请求，严格校验每个编号恰好返回一次且非空。
    /// </summary>
    public async Task<BatchTranslationResult> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<int>? completedCount = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ValidateProfileAndKey(profile, credentials.ApiKey);
        if (texts.Count == 0)
        {
            return new BatchTranslationResult([], "AI 自然翻译");
        }

        var outputs = new string[texts.Count];
        var completed = 0;

        foreach (var slice in BuildBatchSlices(texts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var translated = await TranslateBatchSliceAsync(
                slice.Texts,
                profile,
                credentials.ApiKey!,
                cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < slice.Indices.Count; index++)
            {
                outputs[slice.Indices[index]] = translated[index];
            }
            completed += slice.Indices.Count;
            completedCount?.Report(completed);
        }

        return new BatchTranslationResult(outputs, $"AI 自然翻译（{profile.Model}）");
    }

    internal static IReadOnlyList<(IReadOnlyList<int> Indices, IReadOnlyList<string> Texts)> BuildBatchSlices(
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
            var normalized = TranslationTextProtector.NormalizeLikelyOcrArtifacts(texts[index] ?? string.Empty);
            if (currentTexts.Count >= MaximumBatchItems
                || (currentTexts.Count > 0 && currentCharacters + normalized.Length > MaximumBatchCharacters))
            {
                Flush();
            }
            currentIndices.Add(index);
            currentTexts.Add(normalized);
            currentCharacters += normalized.Length;
        }

        Flush();
        return slices;
    }

    internal async Task<string[]> TranslateBatchSliceAsync(
        IReadOnlyList<string> slice,
        TranslationProfile profile,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ValidateProfileAndKey(profile, apiKey);

        var results = new string[slice.Count];
        var activePositions = new List<int>(slice.Count);
        var inputMap = new Dictionary<string, string>(slice.Count, StringComparer.Ordinal);
        for (var index = 0; index < slice.Count; index++)
        {
            var normalized = TranslationTextProtector.NormalizeLikelyOcrArtifacts(slice[index] ?? string.Empty);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                results[index] = string.Empty;
                continue;
            }
            inputMap[activePositions.Count.ToString(CultureInfo.InvariantCulture)] = normalized;
            activePositions.Add(index);
        }
        if (activePositions.Count == 0)
        {
            return results;
        }

        var endpoint = BuildEndpoint(profile.BaseAddress);
        var body = new Dictionary<string, object?>
        {
            ["model"] = profile.Model,
            ["stream"] = false,
            ["temperature"] = 0.2,
            ["messages"] = new object[]
            {
                new { role = "system", content = BuildBatchSystemPrompt(profile.TargetLanguage) },
                new { role = "user", content = JsonSerializer.Serialize(inputMap) }
            }
        };
        if (IsZhipuEndpoint(endpoint))
        {
            body["thinking"] = new { type = "disabled" };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var effectiveTimeout = ResolveReadTimeout(BatchRequestTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(effectiveTimeout);
        try
        {
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                throw CreateHttpError(response.StatusCode, response.ReasonPhrase, errorBody);
            }

            string rawContent;
            if (IsEventStreamMediaType(response))
            {
                var builder = new StringBuilder();
                await foreach (var fragment in ReadServerSentEventsAsync(response, deadline.Token).ConfigureAwait(false))
                {
                    builder.Append(fragment);
                }
                rawContent = builder.ToString();
            }
            else
            {
                var jsonBody = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                rawContent = ExtractNonStreamingContent(jsonBody);
            }

            var parsed = ParseBatchResponse(rawContent, activePositions.Count);
            for (var index = 0; index < activePositions.Count; index++)
            {
                results[activePositions[index]] = parsed[index];
            }
            return results;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("AI 批量翻译请求超时。", exception);
        }
        catch (IOException exception)
        {
            throw new HttpRequestException("AI 批量翻译连接中断。", exception);
        }
    }

    private async Task<TranslationResult> TranslateCoreAsync(
        string text,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ValidateProfileAndKey(profile, credentials.ApiKey);
        var apiKey = credentials.ApiKey!;

        var normalizedText = TranslationTextProtector.NormalizeLikelyOcrArtifacts(text);
        var endpoint = BuildEndpoint(profile.BaseAddress);
        var body = new Dictionary<string, object?>
        {
            ["model"] = profile.Model,
            ["stream"] = true,
            ["temperature"] = 0.2,
            ["messages"] = new object[]
            {
                new { role = "system", content = BuildSystemPrompt(profile.TargetLanguage) },
                new { role = "user", content = normalizedText }
            }
        };
        if (IsZhipuEndpoint(endpoint))
        {
            body["thinking"] = new { type = "disabled" };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await ReadContentWithTimeoutAsync(response.Content, cancellationToken).ConfigureAwait(false);
            throw CreateHttpError(response.StatusCode, response.ReasonPhrase, responseBody);
        }

        if (IsJsonMediaType(response))
        {
            // ResponseHeadersRead 下 HttpClient.Timeout 只覆盖响应头，此处必须为非流式正文读取施加独立截止时间。
            var jsonBody = await ReadContentWithTimeoutAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var nonStreaming = ExtractNonStreamingContent(jsonBody).Trim();
            if (nonStreaming.Length == 0)
            {
                throw new InvalidOperationException("AI 自然翻译没有返回译文。");
            }
            streamingText?.Report(nonStreaming);
            return new TranslationResult(nonStreaming, "AI 自然翻译");
        }

        var result = new StringBuilder();
        long? lastProgressTimestamp = null;
        string? lastProgressText = null;
        var firstTokenTimeout = ResolveReadTimeout(DefaultFirstTokenTimeout);
        var idleTimeout = ResolveReadTimeout(DefaultBodyReadTimeout);
        await foreach (var fragment in ReadServerSentEventsAsync(
            response, cancellationToken, firstTokenTimeout, idleTimeout).ConfigureAwait(false))
        {
            result.Append(fragment);
            if (streamingText is not null && HasVisibleText(fragment)
                && (lastProgressTimestamp is null || Stopwatch.GetElapsedTime(lastProgressTimestamp.Value) >= ProgressInterval))
            {
                var snapshot = result.ToString();
                lastProgressText = snapshot;
                lastProgressTimestamp = Stopwatch.GetTimestamp();
                streamingText.Report(snapshot);
            }
        }

        var translated = result.ToString().Trim();
        if (!HasVisibleText(translated))
        {
            throw new InvalidOperationException("AI 自然翻译没有返回译文。");
        }
        if (!string.Equals(translated, lastProgressText, StringComparison.Ordinal))
        {
            streamingText?.Report(translated);
        }
        return new TranslationResult(translated, "AI 自然翻译");
    }

    private async Task<string> ReadContentWithTimeoutAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var timeout = ResolveReadTimeout(DefaultBodyReadTimeout);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("AI 自然翻译读取响应超时。", exception);
        }
    }

    private TimeSpan ResolveReadTimeout(TimeSpan ceiling) =>
        httpClient.Timeout > TimeSpan.Zero && httpClient.Timeout < ceiling
            ? httpClient.Timeout
            : ceiling;

    internal static string[] ParseBatchResponse(string rawContent, int expectedCount)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            throw new BatchTranslationFormatException("AI 批量翻译未返回任何内容。");
        }
        var cleaned = StripCodeFence(StripThinkingBlocks(rawContent)).Trim();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(cleaned);
        }
        catch (JsonException exception)
        {
            throw new BatchTranslationFormatException("AI 批量翻译返回的不是有效 JSON 对象。", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new BatchTranslationFormatException("AI 批量翻译返回的根节点不是 JSON 对象。");
            }

            var mapped = new string?[expectedCount];
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seenKeys.Add(property.Name))
                {
                    throw new BatchTranslationFormatException($"AI 批量翻译返回了重复的区域编号：{property.Name}。");
                }
                if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index < 0
                    || index >= expectedCount)
                {
                    throw new BatchTranslationFormatException($"AI 批量翻译包含未预期的区域编号：{property.Name}。");
                }
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new BatchTranslationFormatException($"AI 批量翻译区域 {property.Name} 的译文不是字符串。");
                }
                var translated = property.Value.GetString()?.Trim() ?? string.Empty;
                if (translated.Length == 0)
                {
                    throw new BatchTranslationFormatException($"AI 批量翻译区域 {property.Name} 的译文为空。");
                }
                mapped[index] = translated;
            }

            if (seenKeys.Count != expectedCount)
            {
                throw new BatchTranslationFormatException($"AI 批量翻译返回区域数不完整（预期 {expectedCount}，实际 {seenKeys.Count}）。");
            }

            var results = new string[expectedCount];
            for (var index = 0; index < expectedCount; index++)
            {
                results[index] = mapped[index]
                    ?? throw new BatchTranslationFormatException($"AI 批量翻译缺少区域编号 {index}。");
            }
            return results;
        }
    }

    private static string ExtractNonStreamingContent(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException($"AI 自然翻译服务返回错误：{ExtractErrorDetail(responseBody)}");
        }
        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("AI 自然翻译未返回候选译文。");
        }
        var choice = choices[0];
        if (choice.TryGetProperty("finish_reason", out var finishReason)
            && finishReason.ValueKind == JsonValueKind.String)
        {
            var reason = finishReason.GetString();
            if (!string.IsNullOrWhiteSpace(reason) && reason != "stop")
            {
                throw new InvalidOperationException(reason switch
                {
                    "length" => "AI 自然翻译超过输出长度限制，译文不完整。",
                    "content_filter" => "AI 自然翻译被服务内容过滤中断。",
                    _ => $"AI 自然翻译未正常完成（finish_reason: {reason}）。"
                });
            }
        }
        if (choice.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return StripThinkingBlocks(content.GetString() ?? string.Empty);
        }
        throw new InvalidOperationException("AI 自然翻译返回了无法识别的消息格式。");
    }

    private static void ValidateProfileAndKey(TranslationProfile profile, string? apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.BaseAddress);
        if (TranslationEndpointRules.ValidateBaseAddress(profile.BaseAddress) is { } endpointError)
        {
            throw new InvalidOperationException(endpointError);
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
    }

    private static HttpRequestException CreateHttpError(HttpStatusCode statusCode, string? reasonPhrase, string responseBody)
    {
        var detail = ExtractErrorDetail(responseBody);
        var status = $"HTTP {(int)statusCode} {reasonPhrase}".Trim();
        return new HttpRequestException(
            string.IsNullOrWhiteSpace(detail) ? status : $"{status}：{detail}",
            null,
            statusCode);
    }

    private static bool IsJsonMediaType(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static bool IsEventStreamMediaType(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

    private static string StripThinkingBlocks(string text)
    {
        var filter = new StreamingThinkingFilter();
        return filter.Filter(text) + filter.Complete();
    }

    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
        {
            return trimmed;
        }
        var inner = trimmed[(firstNewline + 1)..];
        var closing = inner.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? inner[..closing].Trim() : trimmed;
    }

    internal static string BuildSystemPrompt(string targetLanguage) =>
        $$"""
        You are a professional translator and software-product localizer.
        Translate the source text into {{targetLanguage}}.

        Requirements:
        - First infer the meaning, domain, and tone from the whole passage, then write fluent, idiomatic target-language prose. Do not translate word by word.
        - The input may come from screen OCR and contain accidental line breaks inside sentences; mentally repair broken lines before translating so sentences read naturally.
        - Preserve the original meaning completely: do not omit, invent, explain, summarize, or add headings.
        - Preserve paragraph breaks, bullet structure, emojis, numbers, prices, URLs, emails, placeholders, model names, product names, and API names accurately.
        - Keep established technical terminology consistent. In an AI/software context, "Agent" means an AI agent; when the target is Simplified Chinese, use "智能体" or retain "Agent" as context requires, never "代理商".
        - Keep short promotional copy and UI labels concise and natural rather than mechanically mirroring the source grammar.
        - Return only the final translation, without notes, quotation marks, or Markdown fences.
        """;

    internal static string BuildBatchSystemPrompt(string targetLanguage) =>
        $$"""
        You are a professional translator and software-product localizer.
        Translate every string value in the input JSON object into {{targetLanguage}}.

        Requirements:
        - Read all entries together to understand the full screen context and keep terminology consistent across regions.
        - Write fluent, concise, idiomatic target-language prose; mentally repair accidental OCR line breaks inside each region. Do not translate word by word.
        - Preserve numbers, prices, URLs, emails, placeholders, model names, product names, and API names accurately.
        - Keep established technical terminology consistent. In an AI/software context, "Agent" means an AI agent; when the target is Simplified Chinese, use "智能体" or retain "Agent" as context requires, never "代理商".
        - Return ONLY a valid JSON object containing the exact same string keys ("0", "1", ...) mapped to their non-empty translations. Never omit, merge, rename, duplicate, or add keys. No commentary or Markdown fences.
        """;

    internal static string BuildEndpoint(string baseAddress)
    {
        var address = baseAddress.Trim().TrimEnd('/');
        return address.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? address
            : address + "/chat/completions";
    }

    internal static bool IsZhipuEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && uri.Host.Equals("open.bigmodel.cn", StringComparison.OrdinalIgnoreCase);

    internal static string ExtractErrorDetail(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return string.Empty;
        }
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var nestedMessage)
                    && nestedMessage.ValueKind == JsonValueKind.String)
                {
                    return nestedMessage.GetString() ?? string.Empty;
                }
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? string.Empty;
                }
            }
            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }
        var compact = string.Join(' ', responseBody.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return compact.Length <= 360 ? compact : compact[..360] + "…";
    }

    internal static async IAsyncEnumerable<string> ReadServerSentEventsAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        TimeSpan? firstTokenTimeout = null,
        TimeSpan? idleTimeout = null)
    {
        var effectiveIdleTimeout = idleTimeout is { } customIdle && customIdle > TimeSpan.Zero
            ? customIdle
            : DefaultBodyReadTimeout;
        var effectiveFirstTokenTimeout = firstTokenTimeout is { } customFirst && customFirst > TimeSpan.Zero
            ? customFirst
            : DefaultFirstTokenTimeout;

        using var progressDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progressDeadline.CancelAfter(effectiveFirstTokenTimeout);
        await using var stream = await response.Content.ReadAsStreamAsync(progressDeadline.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var receivedVisibleText = false;
        var completed = false;
        var eventName = string.Empty;
        var thinking = new StreamingThinkingFilter();
        while (!completed)
        {
            string? line;
            try
            {
                progressDeadline.Token.ThrowIfCancellationRequested();
                line = await reader.ReadLineAsync(progressDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested
                                                               && progressDeadline.IsCancellationRequested)
            {
                var message = receivedVisibleText
                    ? $"AI 自然翻译超过 {effectiveIdleTimeout.TotalSeconds:0.#} 秒未返回新的可见译文。"
                    : $"AI 自然翻译等待首字超过 {effectiveFirstTokenTimeout.TotalSeconds:0.#} 秒未返回内容。";
                throw new TimeoutException(message, exception);
            }
            if (line is null)
            {
                break;
            }
            if (line.Length == 0)
            {
                eventName = string.Empty;
                continue;
            }
            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data.Length == 0)
            {
                continue;
            }
            if (eventName.Equals("error", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"AI 自然翻译服务返回错误：{ExtractErrorDetail(data)}");
            }
            if (data == "[DONE]")
            {
                completed = true;
                break;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(data);
            }
            catch (JsonException) when (data[0] is not '{' and not '[')
            {
                continue;
            }
            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException("AI 自然翻译返回了无法识别的流数据。");
                }
                if (root.TryGetProperty("error", out var error)
                    && error.ValueKind != JsonValueKind.Null)
                {
                    throw new InvalidOperationException($"AI 自然翻译服务返回错误：{ExtractErrorDetail(data)}");
                }
                if (!root.TryGetProperty("choices", out var choices))
                {
                    continue;
                }
                if (choices.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("AI 自然翻译返回了无法识别的候选结果。");
                }
                if (choices.GetArrayLength() == 0)
                {
                    continue;
                }
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finishReason)
                    && finishReason.ValueKind != JsonValueKind.Null)
                {
                    var reason = finishReason.ValueKind == JsonValueKind.String
                        ? finishReason.GetString()
                        : null;
                    if (reason == "stop")
                    {
                        completed = true;
                    }
                    else if (reason is null || !string.IsNullOrWhiteSpace(reason))
                    {
                        throw new InvalidOperationException(reason switch
                        {
                            "length" => "AI 自然翻译超过输出长度限制，译文不完整。",
                            "content_filter" => "AI 自然翻译被服务内容过滤中断。",
                            _ => $"AI 自然翻译未正常完成（finish_reason: {reason ?? "无效"}）。"
                        });
                    }
                }
                if (choice.TryGetProperty("delta", out var delta)
                    && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    var visibleText = thinking.Filter(content.GetString()!);
                    if (visibleText.Length > 0)
                    {
                        if (HasVisibleText(visibleText))
                        {
                            receivedVisibleText = true;
                            progressDeadline.CancelAfter(effectiveIdleTimeout);
                        }
                        yield return visibleText;
                    }
                }
            }
        }
        if (!completed)
        {
            throw new HttpRequestException("AI 自然翻译连接在完成前中断，译文不完整。");
        }
        if (thinking.Complete() is { Length: > 0 } suffix)
        {
            yield return suffix;
        }
    }

    private static bool HasVisibleText(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
            {
                return true;
            }
        }
        return false;
    }

    private sealed class StreamingThinkingFilter
    {
        private const string OpenTag = "<think>";
        private const string CloseTag = "</think>";
        private string _pendingTag = string.Empty;
        private int _depth;

        public string Complete() => _depth == 0 && _pendingTag == "<" ? "<" : string.Empty;

        public string Filter(string fragment)
        {
            if (_pendingTag.Length == 0 && !fragment.Contains('<'))
            {
                return _depth == 0 ? fragment : string.Empty;
            }

            var text = (_pendingTag + fragment).AsSpan();
            _pendingTag = string.Empty;
            var visible = new StringBuilder();
            while (!text.IsEmpty)
            {
                var opening = text.IndexOf('<');
                if (opening < 0)
                {
                    if (_depth == 0)
                    {
                        visible.Append(text);
                    }
                    break;
                }
                if (_depth == 0)
                {
                    visible.Append(text[..opening]);
                }
                text = text[opening..];

                if (text.StartsWith(OpenTag, StringComparison.OrdinalIgnoreCase))
                {
                    _depth++;
                    text = text[OpenTag.Length..];
                }
                else if (text.StartsWith(CloseTag, StringComparison.OrdinalIgnoreCase))
                {
                    _depth = Math.Max(0, _depth - 1);
                    text = text[CloseTag.Length..];
                }
                else if (OpenTag.AsSpan().StartsWith(text, StringComparison.OrdinalIgnoreCase)
                         || CloseTag.AsSpan().StartsWith(text, StringComparison.OrdinalIgnoreCase))
                {
                    _pendingTag = text.ToString();
                    break;
                }
                else
                {
                    if (_depth == 0)
                    {
                        visible.Append('<');
                    }
                    text = text[1..];
                }
            }
            return visible.ToString();
        }
    }
}
