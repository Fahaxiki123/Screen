using System.Text.Json;

namespace Screen.Core;

public static class TencentOcrResponseParser
{
    public static OcrDocument Parse(string responseBody, double scaleX = 1, double scaleY = 1)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("Response", out var response))
        {
            throw new InvalidOperationException("腾讯云 OCR 返回了无法识别的数据");
        }
        if (response.TryGetProperty("Error", out var error))
        {
            var code = error.TryGetProperty("Code", out var codeValue) ? codeValue.GetString() : null;
            var message = error.TryGetProperty("Message", out var messageValue) ? messageValue.GetString() : null;
            throw new InvalidOperationException(string.Join(
                "：",
                new[] { code, message }.Where(value => !string.IsNullOrWhiteSpace(value))));
        }

        if (!response.TryGetProperty("TextDetections", out var detections)
            || detections.ValueKind != JsonValueKind.Array)
        {
            return new OcrDocument("auto", string.Empty, [])
            {
                ProviderName = "腾讯云高精度 OCR",
                UsedOnlineService = true
            };
        }

        var lines = new List<OcrLine>();
        foreach (var detection in detections.EnumerateArray())
        {
            var text = detection.TryGetProperty("DetectedText", out var textValue)
                ? textValue.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            if (text.Length == 0)
            {
                continue;
            }
            var confidence = detection.TryGetProperty("Confidence", out var confidenceValue)
                && confidenceValue.TryGetDouble(out var rawConfidence)
                    ? NormalizeConfidence(rawConfidence)
                    : 0;
            var bounds = ReadBounds(detection, scaleX, scaleY);
            var words = new[] { new OcrWord(text, bounds, confidence) };
            lines.Add(new OcrLine(text, bounds, words, confidence));
        }

        var ordered = lines.OrderBy(line => line.Bounds.Y).ThenBy(line => line.Bounds.X).ToArray();
        var combined = string.Join(Environment.NewLine, ordered.Select(line => line.Text));
        return new OcrDocument(OcrQualityEvaluator.DetectLanguage(combined), combined, ordered)
        {
            Confidence = ordered.Length == 0 ? 0 : ordered.Average(line => line.Confidence),
            ProviderName = "腾讯云高精度 OCR",
            UsedOnlineService = true
        };
    }

    private static PixelRect ReadBounds(JsonElement detection, double scaleX, double scaleY)
    {
        if (detection.TryGetProperty("Polygon", out var polygon)
            && polygon.ValueKind == JsonValueKind.Array)
        {
            var points = polygon.EnumerateArray()
                .Select(point => (
                    X: ReadNumber(point, "X"),
                    Y: ReadNumber(point, "Y")))
                .ToArray();
            if (points.Length > 0)
            {
                return ScaleBounds(
                    points.Min(point => point.X),
                    points.Min(point => point.Y),
                    points.Max(point => point.X) - points.Min(point => point.X),
                    points.Max(point => point.Y) - points.Min(point => point.Y),
                    scaleX,
                    scaleY);
            }
        }

        if (detection.TryGetProperty("ItemPolygon", out var item))
        {
            return ScaleBounds(
                ReadNumber(item, "X"),
                ReadNumber(item, "Y"),
                ReadNumber(item, "Width"),
                ReadNumber(item, "Height"),
                scaleX,
                scaleY);
        }
        return new PixelRect(0, 0, 1, 1);
    }

    private static PixelRect ScaleBounds(
        double x,
        double y,
        double width,
        double height,
        double scaleX,
        double scaleY)
    {
        var left = (int)Math.Floor(x * scaleX);
        var top = (int)Math.Floor(y * scaleY);
        var right = (int)Math.Ceiling((x + width) * scaleX);
        var bottom = (int)Math.Ceiling((y + height) * scaleY);
        return new PixelRect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static double ReadNumber(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var number)
            ? number
            : 0;

    private static double NormalizeConfidence(double confidence) => confidence > 1
        ? Math.Clamp(confidence / 100, 0, 1)
        : Math.Clamp(confidence, 0, 1);
}
