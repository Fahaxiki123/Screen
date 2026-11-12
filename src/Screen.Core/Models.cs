using System.Buffers;

namespace Screen.Core;

public readonly record struct PixelPoint(double X, double Y);

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelRect Normalize() => new(
        Width < 0 ? X + Width : X,
        Height < 0 ? Y + Height : Y,
        Math.Abs(Width),
        Math.Abs(Height));

    public PixelRect Intersect(PixelRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(0, 0, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }
}

public enum CaptureColorSpace
{
    Srgb,
    ScRgb,
    Hdr10
}

public sealed record CaptureFrame(
    byte[] Pixels,
    int Width,
    int Height,
    int Stride,
    double DpiX,
    double DpiY,
    PixelRect ScreenBounds,
    CaptureColorSpace ColorSpace,
    DateTimeOffset CapturedAt)
{
    public PixelBuffer ToPixelBuffer() => new(Pixels, Width, Height, Stride);

    public PixelBuffer Crop(PixelRect screenRegion)
    {
        var local = new PixelRect(
            screenRegion.X - ScreenBounds.X,
            screenRegion.Y - ScreenBounds.Y,
            screenRegion.Width,
            screenRegion.Height).Intersect(new PixelRect(0, 0, Width, Height));
        return ToPixelBuffer().Crop(local);
    }
}

public sealed record PixelBuffer(byte[] Pixels, int Width, int Height, int Stride)
{
    public PixelBuffer Crop(PixelRect region)
    {
        var clipped = region.Intersect(new PixelRect(0, 0, Width, Height));
        if (clipped.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Crop region is outside the image.");
        }

        var resultStride = checked(clipped.Width * 4);
        var result = GC.AllocateUninitializedArray<byte>(checked(resultStride * clipped.Height));
        for (var y = 0; y < clipped.Height; y++)
        {
            Pixels.AsSpan((clipped.Y + y) * Stride + clipped.X * 4, resultStride)
                .CopyTo(result.AsSpan(y * resultStride, resultStride));
        }

        return new PixelBuffer(result, clipped.Width, clipped.Height, resultStride);
    }
}

public sealed record WindowTarget(nint Handle, string Title, PixelRect Bounds);

public sealed record SelectionRegion(PixelRect Bounds, WindowTarget? TargetWindow);

public enum AnnotationKind
{
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Pen,
    Text,
    Number,
    Mosaic
}

public sealed record AnnotationStyle(
    uint Argb,
    double Thickness,
    double FontSize,
    int Variant = 0,
    double Intensity = 1);

public sealed record AnnotationItem(
    Guid Id,
    AnnotationKind Kind,
    IReadOnlyList<PixelPoint> Points,
    PixelRect Bounds,
    AnnotationStyle Style,
    string? Text,
    int? Number,
    int ZIndex);

public sealed record OcrWord(string Text, PixelRect Bounds, double Confidence = 0);
public sealed record OcrLine(string Text, PixelRect Bounds, IReadOnlyList<OcrWord> Words, double Confidence = 0);
public sealed record OcrDocument(string LanguageTag, string Text, IReadOnlyList<OcrLine> Lines)
{
    public double Confidence { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public bool UsedOnlineService { get; init; }
}

public enum OcrRecognitionPurpose
{
    TextExtraction,
    Translation
}

public enum OcrProviderMode
{
    Smart,
    CloudFirst,
    LocalOnly
}

public sealed record OcrProfile
{
    public OcrProviderMode Mode { get; init; } = OcrProviderMode.CloudFirst;
    public bool UseCloudForTranslation { get; init; } = true;
    public double CloudConfidenceThreshold { get; init; } = 0.88;
    public int CloudTimeoutMilliseconds { get; init; } = 2500;
}

public enum TranslationProviderKind
{
    Tencent,
    OpenAiCompatible,
    MyMemory
}

public enum TranslationDisplayMode
{
    Card,
    InPlace
}

public sealed record TranslationProfile(
    string BaseAddress,
    string Model,
    string CredentialName,
    string TargetLanguage)
{
    public const string TencentSecretIdCredentialName = "Screen.Translation.Tencent.SecretId";
    public const string TencentSecretKeyCredentialName = "Screen.Translation.Tencent.SecretKey";

    public TranslationProviderKind Provider { get; init; } = TranslationProviderKind.Tencent;
    public string TencentRegion { get; init; } = "ap-guangzhou";
    public bool EnableMyMemoryFallback { get; init; } = true;
    public string MyMemoryContactEmail { get; init; } = string.Empty;
    public bool EnableRacing { get; init; }
    public string RacingBaseAddress { get; init; } = string.Empty;
    public string RacingModel { get; init; } = string.Empty;
    public string RacingCredentialName { get; init; } = "Screen.RacingTranslation";
    public bool EnableFastFallbackRace { get; init; } = true;
    public int FastFallbackDelaySeconds { get; init; } = 8;
}

public sealed record TranslationCredentials(
    string? ApiKey = null,
    string? TencentSecretId = null,
    string? TencentSecretKey = null,
    string? RacingApiKey = null);

public sealed record TranslationResult(
    string Text,
    string ProviderName,
    bool UsedFallback = false,
    string? PrimaryFailure = null);

public enum RecordingQuality
{
    Smooth,
    Standard,
    High
}

public sealed record RecordingProfile
{
    public int FrameRate { get; init; } = 30;
    public RecordingQuality Quality { get; init; } = RecordingQuality.Standard;
    public bool RecordSystemAudio { get; init; } = true;
    public bool RecordMicrophone { get; init; }
    public bool ShowCursor { get; init; } = true;
    public bool ShowClickEffects { get; init; }

    /// <summary>Target video bitrate: 4 / 8 / 12 Mbps by quality tier.</summary>
    public int VideoBitrate => Quality switch
    {
        RecordingQuality.Smooth => 4_000_000,
        RecordingQuality.High => 12_000_000,
        _ => 8_000_000
    };
}

public enum ScrollCaptureMode
{
    Manual,
    Automatic
}

public enum ScrollCaptureState
{
    Ready,
    Capturing,
    Stitching,
    Completed,
    Cancelled,
    Failed
}

public sealed class ScrollCaptureSession
{
    public required ScrollCaptureMode Mode { get; init; }
    public required SelectionRegion Selection { get; init; }
    public ScrollCaptureState State { get; set; } = ScrollCaptureState.Ready;
    public List<PixelBuffer> Frames { get; } = [];
    public StitchResult? Result { get; set; }
}

public sealed record StitchSeam(
    int SourceFrame,
    int FixedHeaderHeight,
    int OverlapHeight,
    double Error,
    int OutputY);

public sealed record StitchResult(
    PixelBuffer Image,
    IReadOnlyList<StitchSeam> Seams,
    bool IsComplete,
    string? FailureReason);

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 4;
    public const string DefaultHotKey = "Ctrl+Shift+A";
    public const string DefaultClickThroughRecoveryHotKey = "F1";
    public const string DefaultRecordHotKey = "Ctrl+Shift+R";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string HotKey { get; set; } = DefaultHotKey;
    public bool PreferScreenHotKeys { get; set; }
    public string ClickThroughRecoveryHotKey { get; set; } = DefaultClickThroughRecoveryHotKey;
    public string RecordHotKey { get; set; } = DefaultRecordHotKey;
    public bool HasShownClickThroughHint { get; set; }
    public string OcrLanguage { get; set; } = "zh-CN";
    public OcrProfile Ocr { get; set; } = new();
    public RecordingProfile Recording { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public string SaveDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    public TranslationProfile Translation { get; set; } = new(
        "https://api.openai.com/v1", "gpt-4.1-mini", "Screen.Translation", "简体中文");
    public TranslationDisplayMode TranslationDisplayMode { get; set; } = TranslationDisplayMode.Card;
}
