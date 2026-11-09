using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screen.Core;

namespace Screen.App;

internal static class BitmapUtilities
{
    public static BitmapSource ToBitmapSource(this PixelBuffer buffer, double dpiX = 96, double dpiY = 96)
    {
        var bitmap = BitmapSource.Create(
            buffer.Width,
            buffer.Height,
            dpiX,
            dpiY,
            PixelFormats.Pbgra32,
            null,
            buffer.Pixels,
            buffer.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Converts a pixel buffer to a bitmap capped at the given dimensions,
    /// box-filtering DOWNSAMPLE FIRST so a huge source (long screenshots can
    /// reach hundreds of megabytes) never materializes as a full-size
    /// BitmapSource just to be scaled down afterwards. Iterates per TARGET
    /// pixel so every output byte is always written — a source-to-target
    /// mapping can leave the last row/column unreached for certain sizes,
    /// which used to surface uninitialized memory as visible garbage.
    /// </summary>
    public static BitmapSource ToDownscaledBitmapSource(this PixelBuffer buffer, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(1.0, Math.Min((double)maxWidth / buffer.Width, (double)maxHeight / buffer.Height));
        if (scale >= 1.0)
        {
            return buffer.ToBitmapSource();
        }
        var targetWidth = Math.Max(1, (int)Math.Ceiling(buffer.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Ceiling(buffer.Height * scale));
        var stride = targetWidth * 4;
        var pixels = new byte[checked(stride * targetHeight)];
        for (var ty = 0; ty < targetHeight; ty++)
        {
            var y0 = (int)Math.Floor(ty / scale);
            var y1 = Math.Max(y0 + 1, Math.Min(buffer.Height, (int)Math.Ceiling((ty + 1) / scale)));
            for (var tx = 0; tx < targetWidth; tx++)
            {
                var x0 = (int)Math.Floor(tx / scale);
                var x1 = Math.Max(x0 + 1, Math.Min(buffer.Width, (int)Math.Ceiling((tx + 1) / scale)));
                ulong blue = 0, green = 0, red = 0;
                ulong count = 0;
                for (var y = y0; y < y1; y++)
                {
                    var offset = y * buffer.Stride + x0 * 4;
                    for (var x = x0; x < x1; x++)
                    {
                        blue += buffer.Pixels[offset];
                        green += buffer.Pixels[offset + 1];
                        red += buffer.Pixels[offset + 2];
                        offset += 4;
                        count++;
                    }
                }
                var target = (ty * targetWidth + tx) * 4;
                pixels[target] = (byte)(blue / count);
                pixels[target + 1] = (byte)(green / count);
                pixels[target + 2] = (byte)(red / count);
                pixels[target + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(
            targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource ToBitmapSource(this CaptureFrame frame) =>
        frame.ToPixelBuffer().ToBitmapSource(frame.DpiX, frame.DpiY);

    public static PixelBuffer ToPixelBuffer(this BitmapSource bitmap)
    {
        var source = bitmap.Format == PixelFormats.Pbgra32
            ? bitmap
            : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return new PixelBuffer(pixels, source.PixelWidth, source.PixelHeight, stride);
    }

    public static void SavePng(PixelBuffer buffer, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(buffer.ToBitmapSource()));
        encoder.Save(file);
    }

    public static void CopyToClipboard(PixelBuffer buffer)
    {
        var bitmap = buffer.ToBitmapSource();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetImage(bitmap);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
    }

    /// <summary>
    /// Clipboard contention (CLIPBRD_E_CANT_OPEN while another process holds
    /// it) is transient; retry briefly instead of letting the failure escape
    /// to the global handler as an "unhandled error" dialog.
    /// </summary>
    public static void SetClipboardTextWithRetry(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < 4)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }
        throw new InvalidOperationException("剪贴板正被其他程序占用，复制失败，请重试。");
    }
}
