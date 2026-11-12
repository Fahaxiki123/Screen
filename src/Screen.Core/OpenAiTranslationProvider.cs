using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Screen.Core;

public sealed class OpenAiTranslationProvider(HttpClient httpClient) : ITranslationProvider
{
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
            // HttpClient's own deadline cancels its internal token, not the
            // caller's. Treat that as a provider failure so fallback can run.
            throw new TimeoutException("AI 自然翻译请求超时。", exception);
        }
        catch (IOException exception)
        {
            throw new HttpRequestException("AI 自然翻译连接中断，译文不完整。", exception);
        }
    }

    private async Task<TranslationResult> TranslateCoreAsync(
        string text,
        TranslationProfile profile,
        TranslationCredentials credentials,
        IProgress<string>? streamingText,
        CancellationToken cancellationToken)
    {
        var apiKey = credentials.ApiKey;
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.BaseAddress);
        if (TranslationEndpointRules.ValidateBaseAddress(profile.BaseAddress) is { } endpointError)
        {
            throw new InvalidOperationException(endpointError);
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        var normalizedText = TranslationTextProtector.NormalizeLikelyOcrArtifacts(text);
        var protectedText = TranslationTextProtector.Protect(normalizedText);
        var endpoint = BuildEndpoint(profile.BaseAddress);
        var body = new Dictionary<string, object?>
        {
            ["model"] = profile.Model,
            ["stream"] = true,
            ["temperature"] = 0.2,
            ["messages"] = new object[]
            {
                new { role = "system", content = BuildSystemPrompt(profile.TargetLanguage) },
                new { role = "user", content = protectedText.Text }
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
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var detail = ExtractErrorDetail(responseBody);
            var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
            throw new HttpRequestException(
                string.IsNullOrWhiteSpace(detail) ? status : $"{status}：{detail}",
                null,
                response.StatusCode);
        }

        var result = new StringBuilder();
        await foreach (var fragment in ReadServerSentEventsAsync(response, cancellationToken).ConfigureAwait(false))
        {
            result.Append(fragment);
            streamingText?.Report(protectedText.Restore(result.ToString()));
        }

        var translated = protectedText.Restore(result.ToString()).Trim();
        if (translated.Length == 0)
        {
            throw new InvalidOperationException("AI 自然翻译没有返回译文。");
        }
        return new TranslationResult(translated, "AI 自然翻译");
    }

    internal static string BuildSystemPrompt(string targetLanguage) =>
        $$"""
        You are a professional translator and software-product localizer.
        Translate the source text into {{targetLanguage}}.

        Requirements:
        - First infer the meaning, domain, and tone from the whole passage, then write fluent, idiomatic target-language prose. Do not translate word by word.
        - Preserve the original meaning completely: do not omit, invent, explain, summarize, or add headings.
        - Preserve paragraph breaks, bullet structure, emojis, numbers, URLs, placeholders, model names, product names, and API names.
        - Keep established technical terminology consistent. In an AI/software context, "Agent" means an AI agent; when the target is Simplified Chinese, use "智能体" or retain "Agent" as context requires, never "代理商".
        - Keep short promotional copy concise and natural rather than mechanically mirroring the source grammar.
        - Return only the final translation, without notes, quotation marks, or Markdown fences.
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        // With ResponseHeadersRead, HttpClient.Timeout only guards up to the
        // response headers — without an idle deadline here, a half-open
        // connection would hang the translation until the user cancels. Any
        // received line reschedules the deadline; a stall turns into an
        // HttpRequestException so the provider's retry/fallback chain runs.
        const int IdleTimeoutSeconds = 60;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void RescheduleIdleTimeout() => idle.CancelAfter(TimeSpan.FromSeconds(IdleTimeoutSeconds));
        RescheduleIdleTimeout();
        var completed = false;
        var eventName = string.Empty;
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested
                                                     && !cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException(
                    $"The AI translation stream stalled for over {IdleTimeoutSeconds} seconds.");
            }
            RescheduleIdleTimeout();
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
                // Some compatible gateways emit plain-text ping/heartbeat data
                // in an otherwise valid OpenAI SSE stream.
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
                    // Usage-only chunks and heartbeat objects carry no text.
                    continue;
                }
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var finishReason)
                    && finishReason.ValueKind != JsonValueKind.Null)
                {
                    var reason = finishReason.ValueKind == JsonValueKind.String
                        ? finishReason.GetString()
                        : null;
                    if (reason != "stop")
                    {
                        throw new InvalidOperationException(reason switch
                        {
                            "length" => "AI 自然翻译超过输出长度限制，译文不完整。",
                            "content_filter" => "AI 自然翻译被服务内容过滤中断。",
                            _ => $"AI 自然翻译未正常完成（finish_reason: {reason ?? "无效"}）。"
                        });
                    }
                    completed = true;
                }
                if (choice.TryGetProperty("delta", out var delta)
                    && delta.ValueKind == JsonValueKind.Object
                    && delta.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.String)
                {
                    yield return content.GetString()!;
                }
            }
        }
        if (!completed)
        {
            throw new HttpRequestException("AI 自然翻译连接在完成前中断，译文不完整。");
        }
    }
}
