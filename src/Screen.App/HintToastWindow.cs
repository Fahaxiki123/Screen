using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Screen.App;

/// <summary>
/// Small navy toast shown at the top of the screen for flow-guard
/// messages ("截图进行中…" etc.).  Tray balloons are invisible whenever a
/// fullscreen overlay is up, so guards need an on-screen channel.  The toast
/// is click-through and carries WDA_EXCLUDEFROMCAPTURE so it never leaks
/// into an ongoing recording.
/// </summary>
internal sealed class HintToastWindow : Window
{
    private static HintToastWindow? _current;
    private static Brush Token(string key) => (Brush)Application.Current.FindResource(key);

    private HintToastWindow(string message)
    {
        Title = "Screen 提示";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        Content = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = Token("Brush.ChromeGradient"),
            BorderBrush = Token("Brush.Border"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 9, 16, 9),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                Color = Colors.Black,
                Opacity = 0.3,
                ShadowDepth = 2
            },
            Child = new TextBlock
            {
                Text = message,
                FontSize = 13,
                Foreground = Token("Brush.TextPrimary")
            }
        };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(
                handle,
                NativeMethods.GwlExStyle,
                (nint)(style | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate));
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);

            // Top-center of the monitor under the cursor (DPI-aware).
            NativeMethods.TryGetPhysicalCursorPosition(out var cursor);
            var dpi = NativeMethods.GetDpiAtPoint(cursor) / 96.0;
            var monitor = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point(cursor.X, cursor.Y)).Bounds;
            Left = (monitor.X + monitor.Width / 2.0 - ActualWidth * dpi / 2) / dpi;
            Top = (monitor.Y + 24) / dpi;
        };
        Closed += (_, _) =>
        {
            if (_current == this)
            {
                _current = null;
            }
        };
    }

    public static void Show(string message)
    {
        _current?.Close();
        var toast = new HintToastWindow(message);
        _current = toast;
        toast.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            toast.Close();
        };
        timer.Start();
    }
}
