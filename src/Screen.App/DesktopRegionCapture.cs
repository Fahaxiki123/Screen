using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Screen.Core;
using FormsScreen = System.Windows.Forms.Screen;

namespace Screen.App;

/// <summary>Copies visible monitor rectangles into a physical-pixel desktop region.</summary>
internal sealed class DesktopRegionCapture : IDisposable
{
    private readonly PixelRect _region;
    private readonly PixelRect[] _areas;
    private readonly Bitmap _bitmap;
    private readonly Graphics _graphics;

    public DesktopRegionCapture(PixelRect region, IReadOnlyList<PixelRect> monitors)
    {
        _region = region;
        _areas = GetVisibleAreas(region, monitors);
        if (_areas.Length == 0)
        {
            throw new InvalidOperationException("选区内没有可采集的显示区域。");
        }
        _bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppPArgb);
        try
        {
            _graphics = Graphics.FromImage(_bitmap);
            _graphics.Clear(Color.Black);
        }
        catch
        {
            _graphics?.Dispose();
            _bitmap.Dispose();
            throw;
        }
    }

    public static PixelRect[] GetMonitorBounds() => FormsScreen.AllScreens
        .Select(screen => new PixelRect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height))
        .ToArray();

    public static bool FitsOnOneDisplay(PixelRect region, IReadOnlyList<PixelRect> monitors) =>
        monitors.Any(monitor => monitor.Intersect(region) == region);

    internal static PixelRect[] GetVisibleAreas(PixelRect region, IReadOnlyList<PixelRect> monitors) =>
        monitors.Select(monitor => monitor.Intersect(region)).Where(area => !area.IsEmpty).ToArray();

    public void ReadInto(byte[] pixels)
    {
        foreach (var area in _areas)
        {
            _graphics.CopyFromScreen(area.X, area.Y, area.X - _region.X, area.Y - _region.Y,
                new System.Drawing.Size(area.Width, area.Height), CopyPixelOperation.SourceCopy);
        }
        var data = _bitmap.LockBits(new Rectangle(0, 0, _bitmap.Width, _bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var stride = checked(_bitmap.Width * 4);
            if (data.Stride == stride)
            {
                Marshal.Copy(data.Scan0, pixels, 0, checked(stride * _bitmap.Height));
            }
            else
            {
                for (var y = 0; y < _bitmap.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
                }
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    public void Dispose()
    {
        _graphics.Dispose();
        _bitmap.Dispose();
    }
}
