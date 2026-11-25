using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Screen.Core;

/// <summary>
/// 腾讯云机器翻译（TMT）提供方。
/// </summary>
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
            TranslationServiceException? serviceError = null;
            try
            {
                using var errorDocument = JsonDocument.Parse(responseBody);
                serviceError = ReadServiceError(errorDocument.RootElement, (HttpStatusCode)status);
            }
            catch (JsonException)
            {
                // Proxies may return a plain-text or HTML error instead of a Tencent envelope.
            }
            throw new HttpRequestException($"HTTP {status} {reason}".Trim(), serviceError, (HttpStatusCode)status);
        }

        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("Response", out var responseObject))
        {
            throw new InvalidOperationException("腾讯翻译返回了无法识别的数据");
        }
        if (ReadServiceError(document.RootElement, (HttpStatusCode)status) is { } businessError)
        {
            throw businessError;
        }
        if (!responseObject.TryGetProperty("TargetText", out var targetText)
            || targetText.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("腾讯翻译没有返回译文");
        }
        return targetText.GetString() ?? string.Empty;
    }

    private static TranslationServiceException? ReadServiceError(JsonElement root, HttpStatusCode httpStatus)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Response", out var response)
            || response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("Error", out var error)
            || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var code = error.TryGetProperty("Code", out var rawCode) && rawCode.ValueKind == JsonValueKind.String ? rawCode.GetString() : null;
        var message = error.TryGetProperty("Message", out var rawMessage) && rawMessage.ValueKind == JsonValueKind.String ? rawMessage.GetString() : null;
        return new TranslationServiceException(string.Join("：", new[] { code, message }.Where(value => !string.IsNullOrWhiteSpace(value))),
            httpStatus, TranslationServiceException.TencentCode(code));
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
