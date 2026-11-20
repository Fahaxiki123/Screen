using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Screen.Core;

namespace Screen.App;

internal static class RecordingRegionWindow
{
    public static Canvas CreateMask(Window window, PixelRect region, string maskBrush, out Rect cutout)
    {
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.Topmost = true;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.AllowsTransparency = true;
        window.SetResourceReference(Window.BackgroundProperty, "Brush.Transparent");
        window.IsHitTestVisible = false;
        window.UseLayoutRounding = true;

        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(region.X + region.Width / 2, region.Y + region.Height / 2)) / 96.0;
        window.Left = screen.Left / dpi;
        window.Top = screen.Top / dpi;
        window.Width = screen.Width / dpi;
        window.Height = screen.Height / dpi;
        cutout = new Rect((region.X - screen.Left) / dpi, (region.Y - screen.Top) / dpi, region.Width / dpi, region.Height / dpi);
        var canvas = new Canvas { ClipToBounds = true };
        var mask = new System.Windows.Shapes.Path
        {
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, window.Width, window.Height)),
                new RectangleGeometry(cutout))
        };
        mask.SetResourceReference(Shape.FillProperty, maskBrush);
        canvas.Children.Add(mask);
        Attach(window, canvas, screen);
        return canvas;
    }

    private static void Attach(Window window, Canvas canvas, System.Drawing.Rectangle bounds)
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
