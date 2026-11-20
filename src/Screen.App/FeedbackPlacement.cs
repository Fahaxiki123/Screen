using System.Windows;
using System.Windows.Interop;

namespace Screen.App;

internal enum FeedbackAnchor { Top, Center, BottomRight }

internal static class FeedbackPlacement
{
    public static void TryShow(Action show)
    {
        try
        { show(); }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Output succeeded, but the visual confirmation could not be shown", exception);
        }
    }

    public static void Attach(Window window, FeedbackAnchor anchor, NativeMethods.NativePoint? location, bool clickThrough)
    {
        NativeMethods.TryGetPhysicalCursorPosition(out var cursor);
        var point = location ?? cursor;
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(point.X, point.Y)).WorkingArea;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Topmost = true;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.SetResourceReference(Window.BackgroundProperty, "Brush.Transparent");
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var flags = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64()
                | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
            if (clickThrough)
                flags |= NativeMethods.WsExTransparent;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GwlExStyle, (nint)flags);
            NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WdaExcludeFromCapture);
            Position();
        };
        window.SizeChanged += (_, _) => Position();

        void Position()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0)
                return;
            var content = (FrameworkElement)window.Content;
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var scale = NativeMethods.GetDpiAtPoint(point) / 96.0;
            var width = (int)Math.Ceiling((double.IsFinite(window.Width) ? window.Width : content.DesiredSize.Width) * scale);
            var height = (int)Math.Ceiling((double.IsFinite(window.Height) ? window.Height : content.DesiredSize.Height) * scale);
            var inset = (int)Math.Round(18 * scale);
            var x = anchor == FeedbackAnchor.BottomRight ? area.Right - width - inset : area.Left + (area.Width - width) / 2;
            var y = anchor switch
            {
                FeedbackAnchor.Top => area.Top + (int)Math.Round(24 * scale),
                FeedbackAnchor.BottomRight => area.Bottom - height - inset,
                _ => area.Top + (area.Height - height) / 2
            };
            NativeMethods.SetWindowPos(hwnd, 0, x, y, 0, 0,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoSize);
        }
    }
}
