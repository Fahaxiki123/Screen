using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Full-virtual-screen dimming mask with an accent border around the
/// recording region, kept visible for the whole recording session (Feishu
/// behaviour).  Click-through everywhere, and carries
/// WDA_EXCLUDEFROMCAPTURE so neither the mask nor the border is captured
/// into the footage — on screen they stay visible, in the video they never
/// appear.
/// </summary>
internal sealed class RecordingBorderWindow : Window
{
    public RecordingBorderWindow(PixelRect region)
    {
        Title = "Screen 录制区域";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        IsHitTestVisible = false;

        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            region.X + region.Width / 2,
            region.Y + region.Height / 2)) / 96.0;
        Left = screen.Left / dpi;
        Top = screen.Top / dpi;
        Width = screen.Width / dpi;
        Height = screen.Height / dpi;

        // Region rect in this window's DIP space.
        var rx = (region.X - screen.Left) / dpi;
        var ry = (region.Y - screen.Top) / dpi;
        var rw = region.Width / dpi;
        var rh = region.Height / dpi;

        var mask = RecordingTheme.Brush("Brush.WindowBackground", "#15223A").Clone();
        mask.Opacity = 0.5;
        var canvas = new Canvas { ClipToBounds = true };
        // Four dimmed bands around the region; the region itself stays clear.
        canvas.Children.Add(MaskRect(mask, 0, 0, Width, ry));
        canvas.Children.Add(MaskRect(mask, 0, ry + rh, Width, Height - ry - rh));
        canvas.Children.Add(MaskRect(mask, 0, ry, rx, rh));
        canvas.Children.Add(MaskRect(mask, rx + rw, ry, Width - rx - rw, rh));
        var border = new Border
        {
            BorderBrush = RecordingTheme.Brush("Brush.Accent", "#A7CFDF"),
            BorderThickness = new Thickness(2),
            Width = rw,
            Height = rh
        };
        Canvas.SetLeft(border, rx);
        Canvas.SetTop(border, ry);
        canvas.Children.Add(border);
        Content = canvas;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(
                handle,
                NativeMethods.GwlExStyle,
                (nint)(style | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate));
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        };
    }

    private static Border MaskRect(Brush mask, double left, double top, double width, double height)
    {
        var rect = new Border
        {
            Background = mask,
            Width = Math.Max(0, width),
            Height = Math.Max(0, height)
        };
        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, top);
        return rect;
    }
}
