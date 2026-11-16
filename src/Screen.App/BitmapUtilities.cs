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
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".screen-image-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(buffer.ToBitmapSource()));
                encoder.Save(file);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static void CopyToClipboard(PixelBuffer buffer)
    {
        var bitmap = buffer.ToBitmapSource();
        Exception? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetImage(bitmap);
                return;
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                lastError = ex;
                if (attempt < 4)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }
        throw new InvalidOperationException("剪贴板正被其他程序占用，复制截图失败，请重试。", lastError);
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
