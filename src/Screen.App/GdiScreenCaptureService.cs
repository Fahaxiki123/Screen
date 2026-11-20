using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Screen.Core;
using FormsScreen = System.Windows.Forms.Screen;

namespace Screen.App;

internal sealed class GdiScreenCaptureService : IScreenCaptureService
{
    public Task<CaptureFrame> CaptureAtAsync(PixelPoint screenPoint, CancellationToken cancellationToken = default) =>
        Task.Run(() => Capture(screenPoint, cancellationToken), cancellationToken);

    public static async Task<CaptureFrame> CaptureDesktopAsync(IReadOnlyList<PixelRect> monitors,
        CancellationToken cancellationToken = default)
    {
        var left = monitors.Min(screen => screen.X);
        var top = monitors.Min(screen => screen.Y);
        var bounds = new PixelRect(left, top,
            checked(monitors.Max(screen => screen.Right) - left),
            checked(monitors.Max(screen => screen.Bottom) - top));
        var image = await DesktopRegionCapture.CaptureAsync(bounds, monitors, cancellationToken).ConfigureAwait(false);
        // WPF uses one DPI for the spanning HWND, on the display with the largest overlap.
        var display = FormsScreen.FromRectangle(new Rectangle(left, top, bounds.Width, bounds.Height)).Bounds;
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            display.Left + display.Width / 2, display.Top + display.Height / 2));
        return new CaptureFrame(image.Pixels, image.Width, image.Height, image.Stride, dpi, dpi,
            bounds, CaptureColorSpace.Srgb, DateTimeOffset.UtcNow)
        {
            DisplayBounds = monitors
        };
    }

    private static CaptureFrame Capture(PixelPoint screenPoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var display = FormsScreen.FromPoint(new System.Drawing.Point((int)screenPoint.X, (int)screenPoint.Y));
        var bounds = display.Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            var stride = bitmap.Width * 4;
            var pixels = GC.AllocateUninitializedArray<byte>(stride * bitmap.Height);
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
            }

            var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
                bounds.Left + bounds.Width / 2,
                bounds.Top + bounds.Height / 2));
            var frame = new CaptureFrame(
                pixels,
                bitmap.Width,
                bitmap.Height,
                stride,
                dpi,
                dpi,
                new PixelRect(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
                CaptureColorSpace.Srgb,
                DateTimeOffset.UtcNow);
            return frame;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
    }
}
