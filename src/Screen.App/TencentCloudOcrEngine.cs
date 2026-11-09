using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Screen.Core;
using SkiaSharp;

namespace Screen.App;

internal sealed class TencentCloudOcrEngine(HttpClient httpClient) : IOcrEngine
{
    private const string Host = "ocr.tencentcloudapi.com";
    private const string Service = "ocr";
    private const string Version = "2018-11-19";
    private const string Action = "GeneralAccurateOCR";
    private const string ContentType = "application/json; charset=utf-8";
    private const int MaximumBinaryBytes = 7_300_000;
    private const int MaximumBase64Bytes = 7_000_000;

    public IReadOnlyList<string> AvailableLanguages { get; } = ["auto"];

    public bool IsLanguageAvailable(string languageTag) => true;

    public Task<OcrDocument> RecognizeAsync(
        PixelBuffer image,
        string languageTag,
        CancellationToken cancellationToken = default) =>
        // Faulted task instead of a synchronous throw: Task-returning members
        // that throw before the first await bypass async callers that store
        // the task before awaiting it.
        Task.FromException<OcrDocument>(
            new InvalidOperationException("腾讯云 OCR 缺少访问密钥。"));

    public async Task<OcrDocument> RecognizeAsync(
        PixelBuffer image,
        string secretId,
        string secretKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        var encoded = EncodeImage(image);
        // Tencent limits the Base64-encoded image to 7 MB; check the final
        // encoding (PNG or JPEG) instead of the raw pixel bytes.
        var base64Length = checked(((encoded.Bytes.Length + 2) / 3) * 4);
        if (base64Length > MaximumBase64Bytes)
        {
            throw new InvalidOperationException("选区图片过大，无法发送到腾讯云 OCR；请缩小选区后重试。");
        }

        var payload = JsonSerializer.Serialize(new
        {
            ImageBase64 = Convert.ToBase64String(encoded.Bytes)
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var authorization = TencentCloudApiSigner.CreateAuthorization(
            secretId,
            secretKey,
            payload,
            timestamp,
            Host,
            Service,
            ContentType);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}/")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Host = Host;
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("X-TC-Action", Action);
        request.Headers.TryAddWithoutValidation("X-TC-Version", Version);
        request.Headers.TryAddWithoutValidation(
            "X-TC-Timestamp",
            timestamp.ToString(CultureInfo.InvariantCulture));

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
        }
        return TencentOcrResponseParser.Parse(responseBody, encoded.ScaleX, encoded.ScaleY);
    }

    private static EncodedImage EncodeImage(PixelBuffer source)
    {
        using var bitmap = ToBitmap(source);
        var longestSide = Math.Max(source.Width, source.Height);
        var scale = longestSide < 1400
            ? Math.Min(3.0, 1400.0 / Math.Max(1, longestSide))
            : longestSide > 5000
                ? 5000.0 / longestSide
                : 1.0;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = width == source.Width && height == source.Height
            ? null
            : bitmap.Resize(
                new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
        var target = resized ?? bitmap;
        using var image = SKImage.FromBitmap(target);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();
        if (bytes.Length > MaximumBinaryBytes)
        {
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 92);
            bytes = jpeg.ToArray();
        }
        return new EncodedImage(
            bytes,
            source.Width / (double)target.Width,
            source.Height / (double)target.Height);
    }

    private static SKBitmap ToBitmap(PixelBuffer image)
    {
        var bitmap = new SKBitmap(new SKImageInfo(
            image.Width,
            image.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul));
        var bytesPerRow = checked(image.Width * 4);
        for (var y = 0; y < image.Height; y++)
        {
            Marshal.Copy(
                image.Pixels,
                y * image.Stride,
                bitmap.GetAddress(0, y),
                bytesPerRow);
        }
        return bitmap;
    }

    private sealed record EncodedImage(byte[] Bytes, double ScaleX, double ScaleY);
}
