using System.Globalization;
using System.Text.RegularExpressions;

namespace Screen.App;

/// <summary>
/// Facts the recording editor needs from <c>ffmpeg -i</c> (printed on stderr):
/// duration, coded size and frame rate.
/// </summary>
internal sealed record RecordingMediaInfo(double DurationSeconds, int Width, int Height, double FramesPerSecond)
{
    private const double DefaultFramesPerSecond = 30;

    public static RecordingMediaInfo? TryParse(string ffmpegStderr)
    {
        var durationMatch = Regex.Match(ffmpegStderr, @"Duration:\s*(\d+):(\d+):([\d.]+)");
        var sizeMatch = Regex.Match(ffmpegStderr, @"Video:.*?(\d{2,5})x(\d{2,5})");
        if (!durationMatch.Success || !sizeMatch.Success)
        {
            return null;
        }
        var duration = int.Parse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                       + int.Parse(durationMatch.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                       + double.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture);
        var width = int.Parse(sizeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var height = int.Parse(sizeMatch.Groups[2].Value, CultureInfo.InvariantCulture);
        var fps = DefaultFramesPerSecond;
        var fpsMatch = Regex.Match(ffmpegStderr, @"([\d.]+)\s*fps");
        if (fpsMatch.Success
            && double.TryParse(fpsMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            fps = parsed;
        }
        return new RecordingMediaInfo(duration, width, height, fps);
    }

    /// <summary>
    /// Size at which preview frames are decoded: the video scaled down to the
    /// on-screen host (physical pixels), never up. Frames within 10% of the
    /// source size are decoded unscaled to skip the scaler entirely.
    /// </summary>
    public static (int Width, int Height) FitDecodeSize(int videoWidth, int videoHeight, double hostWidthPixels, double hostHeightPixels)
    {
        if (videoWidth <= 0 || videoHeight <= 0
            || !(hostWidthPixels > 0) || !(hostHeightPixels > 0)
            || double.IsInfinity(hostWidthPixels) || double.IsInfinity(hostHeightPixels))
        {
            return (videoWidth, videoHeight);
        }
        var scale = Math.Min(hostWidthPixels / videoWidth, hostHeightPixels / videoHeight);
        if (scale >= 0.9)
        {
            return (videoWidth, videoHeight);
        }
        // Even dimensions keep every pixel format the scaler might pick happy;
        // a 160px floor keeps a briefly tiny window from decoding mush.
        var width = Math.Max(160, (int)Math.Round(videoWidth * scale)) & ~1;
        var height = Math.Max(2, (int)Math.Round(videoHeight * ((double)width / videoWidth))) & ~1;
        return width >= videoWidth || height >= videoHeight
            ? (videoWidth, videoHeight)
            : (width, height);
    }

    /// <summary>
    /// Whether a host resize warrants restarting the decoder at a new size:
    /// growing past the decoded size (blurry) or shrinking well below it (waste).
    /// </summary>
    public static bool ShouldRedecode((int Width, int Height) current, (int Width, int Height) desired) =>
        desired.Width > current.Width * 1.1 || desired.Width < current.Width * 0.7;
}
