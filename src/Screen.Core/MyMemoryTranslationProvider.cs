using System.Net;
using System.Text;
using System.Text.Json;

namespace Screen.Core;

/// <summary>
/// MyMemory 公共免费翻译提供方（作为离线凭据与冷备兜底通道）。
/// </summary>
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
        var target = TranslationLanguageCodes.ToMyMemoryTarget(profile.TargetLanguage);
        var source = TranslationLanguageCodes.ToMyMemorySource(sourceLanguage, text, target);
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
                var (detail, serviceCode) = TryReadMyMemoryError(responseBody);
                var httpStatus = $"HTTP {statusCode} {reasonPhrase}".Trim();
                var message = string.IsNullOrWhiteSpace(detail) ? httpStatus : $"{httpStatus}：{detail}";
                var transportStatus = (HttpStatusCode)statusCode;
                throw new HttpRequestException(message,
                    serviceCode is null ? null : new TranslationServiceException(message, transportStatus, serviceCode), transportStatus);
            }

            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("responseStatus", out var status)
                && status.ToString() is { } statusText
                && statusText != "200")
            {
                var detail = document.RootElement.TryGetProperty("responseDetails", out var details)
                    ? details.ToString()
                    : "服务返回错误";
                var serviceCode = TranslationServiceException.MyMemoryCode(statusText);
                throw new TranslationServiceException(serviceCode is null ? detail : $"MyMemory {serviceCode}：{detail}",
                    (HttpStatusCode)statusCode, serviceCode);
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

    private static (string? Detail, string? Code) TryReadMyMemoryError(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return (document.RootElement.TryGetProperty("responseDetails", out var detail) ? detail.ToString() : null,
                    document.RootElement.TryGetProperty("responseStatus", out var code) ? TranslationServiceException.MyMemoryCode(code.ToString()) : null);
            }
        }
        catch (JsonException)
        {
        }
        return (null, null);
    }
}
