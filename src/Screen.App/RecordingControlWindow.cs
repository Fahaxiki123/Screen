using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Feishu-style capsule bar shown while recording: pause/resume, elapsed vs
/// maximum duration, and a muted coral 结束录制 button.  Carries
/// WDA_EXCLUDEFROMCAPTURE so the desktop duplication capture never includes
/// the bar itself.
/// </summary>
internal sealed class RecordingControlWindow : Window
{
    private const string MaximumDurationLabel = "01:00:00";

    private readonly TextBlock _timer;
    private readonly LucideIconElement _pauseIcon;

    public event EventHandler? PauseToggled;
    public event EventHandler? StopRequested;

    public RecordingControlWindow()
    {
        Title = "Screen 录屏";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;

        _timer = new TextBlock
        {
            Text = $"00:00:00 / {MaximumDurationLabel}",
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0)
        };
        _pauseIcon = new LucideIconElement
        {
            Kind = LucideIconKind.Pause,
            Width = 17,
            Height = 17,
            StrokeThickness = 2,
            Stroke = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7")
        };
        var pauseButton = new Button
        {
            Content = _pauseIcon,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            ToolTip = "暂停 / 继续"
        };
        pauseButton.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
        pauseButton.Click += (_, _) => PauseToggled?.Invoke(this, EventArgs.Empty);

        var stopBackground = RecordingTheme.Brush("Brush.Danger", "#E9959B");
        var stopButton = new Border
        {
            CornerRadius = new CornerRadius(17),
            Background = stopBackground,
            Padding = new Thickness(18, 8, 18, 8),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "结束并保存",
            Child = new TextBlock
            {
                Text = "结束录制",
                Foreground = RecordingTheme.Brush("Brush.OnAccent", "#15223A"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        stopButton.MouseEnter += (_, _) => stopButton.Opacity = 0.88;
        stopButton.MouseLeave += (_, _) => stopButton.Opacity = 1;
        stopButton.MouseLeftButtonUp += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(pauseButton);
        panel.Children.Add(_timer);
        panel.Children.Add(stopButton);

        Content = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = RecordingTheme.Brush("Brush.ChromeGradient", "#1B3049"),
            BorderBrush = RecordingTheme.Brush("Brush.Border", "#39556F"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 8, 10, 8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                Color = Colors.Black,
                Opacity = 0.3,
                ShadowDepth = 3
            },
            Child = panel
        };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(
                handle,
                NativeMethods.GwlExStyle,
                (nint)(style | NativeMethods.WsExNoActivate));
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        };
    }

    public void UpdateTimer(double elapsedSeconds)
    {
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds));
        _timer.Text = $"{elapsed:hh\\:mm\\:ss} / {MaximumDurationLabel}";
    }

    public void SetPaused(bool paused) =>
        _pauseIcon.Kind = paused ? LucideIconKind.Play : LucideIconKind.Pause;

    /// <summary>Centers the capsule below the recording region (DPI-aware).</summary>
    public void Reposition(PixelRect region)
    {
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            region.X + region.Width / 2,
            region.Y + region.Height / 2)) / 96.0;
        Left = (region.X + region.Width / 2.0 - ActualWidth * dpi / 2) / dpi;
        var below = (region.Bottom + 12) / dpi;
        var virtualBottom = SystemParameters.VirtualScreenHeight
            + SystemParameters.VirtualScreenTop;
        Top = below + ActualHeight > virtualBottom
            ? (region.Y + 12) / dpi
            : below;
    }
}
