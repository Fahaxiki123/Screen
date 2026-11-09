namespace Screen.Core;

public static class CaptureFrameQuality
{
    public static bool IsSuspiciouslyBlack(CaptureFrame frame) =>
        IsSuspiciouslyBlack(frame.ToPixelBuffer());

    public static bool IsSuspiciouslyBlack(PixelBuffer image)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Pixels.Length == 0)
        {
            return true;
        }

        var stepX = Math.Max(1, image.Width / 64);
        var stepY = Math.Max(1, image.Height / 64);
        long luminanceTotal = 0;
        var samples = 0;
        var nonBlackSamples = 0;
        var maximumLuminance = 0;

        for (var y = stepY / 2; y < image.Height; y += stepY)
        {
            for (var x = stepX / 2; x < image.Width; x += stepX)
            {
                var offset = y * image.Stride + x * 4;
                if (offset < 0 || offset + 2 >= image.Pixels.Length)
                {
                    continue;
                }

                var luminance = (image.Pixels[offset] * 29
                                 + image.Pixels[offset + 1] * 150
                                 + image.Pixels[offset + 2] * 77) >> 8;
                luminanceTotal += luminance;
                maximumLuminance = Math.Max(maximumLuminance, luminance);
                if (luminance > 6)
                {
                    nonBlackSamples++;
                }
                samples++;
            }
        }

        if (samples == 0)
        {
            return true;
        }

        // Keep this deliberately strict: a dark application or wallpaper is valid content.
        // The failure we recover from is a virtually uniform zero-filled DXGI surface.
        return maximumLuminance <= 8
               && luminanceTotal / (double)samples <= 3.0
               && nonBlackSamples <= Math.Max(1, samples / 1000);
    }
}
