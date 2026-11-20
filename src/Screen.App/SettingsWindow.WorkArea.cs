using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DrawingRect = System.Drawing.Rectangle;
using FormsScreen = System.Windows.Forms.Screen;

namespace Screen.App;

public partial class SettingsWindow
{
    private void TrackWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(handle);
        var minimum = new Size(MinWidth, MinHeight);
        var preferred = new Size(Width, Height);
        var fitted = preferred;
        (DrawingRect Area, uint Dpi, WindowState State)? last = null;
        var dragging = false;
        var reposition = false;
        var queued = false;

        void Refresh()
        {
            queued = false;
            if (source.IsDisposed || WindowState == WindowState.Minimized)
                return;
            var monitor = FormsScreen.FromHandle(handle);
            var area = monitor.WorkingArea;
            var center = new NativeMethods.NativePoint(area.Left + area.Width / 2, area.Top + area.Height / 2);
            var dpi = NativeMethods.GetDpiAtPoint(center);
            var current = (area, dpi, WindowState);
            if (last != current)
            {
                // Preserve an intentional manual resize, but not the reduction we
                // applied on a smaller monitor. Restore that size when space permits.
                if (last.HasValue && WindowState == WindowState.Normal)
                    preferred = new Size(Math.Abs(Width - fitted.Width) > 1 ? Width : preferred.Width,
                        Math.Abs(Height - fitted.Height) > 1 ? Height : preferred.Height);
                last = current;
                var limits = FitToWorkArea(minimum, preferred, area, dpi);
                MinWidth = limits.Minimum.Width;
                MinHeight = limits.Minimum.Height;
                if (WindowState == WindowState.Normal)
                {
                    fitted = limits.Normal;
                    Width = fitted.Width;
                    Height = fitted.Height;
                    reposition = fitted.Width < preferred.Width || fitted.Height < preferred.Height;
                }
            }
            if (IsLoaded && !dragging && reposition && WindowState == WindowState.Normal && NativeMethods.GetWindowRect(handle, out var rect))
            {
                reposition = false;
                var x = Math.Clamp(rect.Left, area.Left, Math.Max(area.Left, area.Right - (rect.Right - rect.Left)));
                var y = Math.Clamp(rect.Top, area.Top, Math.Max(area.Top, area.Bottom - (rect.Bottom - rect.Top)));
                if (x != rect.Left || y != rect.Top)
                    NativeMethods.SetWindowPos(handle, 0, x, y, 0, 0,
                        NativeMethods.SwpNoActivate | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder);
            }
        }

        nint WorkAreaHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            const int enterSizeMove = 0x0231, exitSizeMove = 0x0232, windowPosChanged = 0x0047;
            const int displayChange = 0x007e, settingChange = 0x001a, dpiChanged = 0x02e0;
            if (message == enterSizeMove)
                dragging = true;
            if (message == exitSizeMove)
                dragging = false;
            if (!queued && message is windowPosChanged or exitSizeMove or displayChange or settingChange or dpiChanged)
            {
                queued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Refresh);
            }
            return 0;
        }

        source.AddHook(WorkAreaHook);
        Loaded += (_, _) => Refresh();
        Refresh();
    }

    internal static (Size Minimum, Size Normal) FitToWorkArea(Size minimum, Size preferred, DrawingRect physicalArea, uint dpi)
    {
        var width = Math.Max(1, Math.Floor(physicalArea.Width * 96d / dpi));
        var height = Math.Max(1, Math.Floor(physicalArea.Height * 96d / dpi));
        var fittedMinimum = new Size(Math.Min(minimum.Width, width), Math.Min(minimum.Height, height));
        var normal = new Size(Math.Clamp(preferred.Width, fittedMinimum.Width, width),
            Math.Clamp(preferred.Height, fittedMinimum.Height, height));
        return (fittedMinimum, normal);
    }
}
