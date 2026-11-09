using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Screen.App;

/// <summary>
/// Feishu-style click ripple shown while recording: an accent ring expands
/// and fades at the click point over ~450 ms, then closes itself.  Deliberately
/// WITHOUT WDA_EXCLUDEFROMCAPTURE — the effect is meant to be recorded.
/// </summary>
internal sealed class ClickRippleWindow : Window
{
    private const double SizeDip = 96;

    public ClickRippleWindow(int physicalX, int physicalY)
    {
        Title = string.Empty;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        Width = SizeDip;
        Height = SizeDip;

        var ring = new Ellipse
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            StrokeThickness = 3,
            Opacity = 0.9,
            Margin = new Thickness(10),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.25, 0.25)
        };
        Content = new Grid { Children = { ring } };

        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(physicalX, physicalY)) / 96.0;
        Left = (physicalX - SizeDip * dpi / 2) / dpi;
        Top = (physicalY - SizeDip * dpi / 2) / dpi;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(
                handle,
                NativeMethods.GwlExStyle,
                (nint)(style | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate));
        };

        Loaded += (_, _) =>
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var storyboard = new Storyboard();
            var scaleX = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease };
            var scaleY = new DoubleAnimation(0.25, 1.0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease };
            var fade = new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease };
            Storyboard.SetTarget(scaleX, ring);
            Storyboard.SetTargetProperty(scaleX, new PropertyPath("RenderTransform.ScaleX"));
            Storyboard.SetTarget(scaleY, ring);
            Storyboard.SetTargetProperty(scaleY, new PropertyPath("RenderTransform.ScaleY"));
            Storyboard.SetTarget(fade, ring);
            Storyboard.SetTargetProperty(fade, new PropertyPath(Ellipse.OpacityProperty));
            storyboard.Children.Add(scaleX);
            storyboard.Children.Add(scaleY);
            storyboard.Children.Add(fade);
            storyboard.Completed += (_, _) => Close();
            storyboard.Begin();
        };
    }
}
