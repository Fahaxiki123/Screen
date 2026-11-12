using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Screen.App;

internal static class RecordingRegionWindow
{
    public static void Attach(Window window, Canvas canvas, System.Drawing.Rectangle bounds)
    {
        // The canvas keeps the same proportions as the virtual desktop even
        // when the window's WPF DPI differs from the region's monitor DPI.
        canvas.Width = window.Width;
        canvas.Height = window.Height;
        window.Content = new Viewbox { Stretch = Stretch.Fill, Child = canvas };
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle,
                (nint)(style | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate));
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
            NativeMethods.SetWindowPos(handle, new nint(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                NativeMethods.SwpNoActivate);
        };
    }
}
