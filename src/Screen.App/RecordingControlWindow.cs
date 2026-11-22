using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

internal sealed class RecordingControlWindow : Window
{
    private readonly TextBlock _timer;
    private readonly LucideIconElement _pauseIcon;
    private readonly Button _pauseButton;
    private readonly Button _stopButton;
    private readonly Ellipse _beacon;
    private readonly Ellipse _halo;
    private readonly Border[] _meterBars = new Border[5];
    private readonly DispatcherTimer _meterTimer;
    private readonly Func<float>? _levelProvider;
    private readonly bool _hasAudio;
    private double _smoothedLevel;
    private int _phase;
    private bool _paused;

    public event EventHandler? PauseToggled;
    public event EventHandler? StopRequested;

    public RecordingControlWindow(bool hasAudio = false, Func<float>? levelProvider = null,
        RecordingExportFormat format = RecordingExportFormat.Mp4)
    {
        _levelProvider = levelProvider;
        _hasAudio = hasAudio && format != RecordingExportFormat.Gif;
        Title = "Flick 录屏";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        _halo = new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(Color.FromArgb(48, 255, 59, 48)) };
        _beacon = new Ellipse { Width = 8, Height = 8, Fill = RecordingTheme.Brush("Brush.Danger", "#FF3B30") };
        panel.Children.Add(new Grid { Width = 20, Margin = new Thickness(3, 0, 4, 0), Children = { _halo, _beacon } });
        panel.Children.Add(new SquircleGlassBorder
        {
            CornerRadiusRadius = 4.5,
            ShadowLevel = 0,
            ShowRim = false,
            VerticalAlignment = VerticalAlignment.Center,
            GlassBackground = RecordingTheme.Brush("Brush.SurfaceSunken", "#0E0F172A"),
            Padding = new Thickness(5, 2, 5, 2),
            Child = new TextBlock
            {
                Text = format == RecordingExportFormat.Gif ? "GIF" : "MP4",
                FontWeight = FontWeights.SemiBold,
                FontSize = 10,
                Foreground = RecordingTheme.Brush("Brush.TextSecondary", "#9E1D1D1F")
            }
        });
        _timer = new TextBlock
        {
            Text = "00:00",
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#1D1D1F"),
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Typography.SetNumeralAlignment(_timer, FontNumeralAlignment.Tabular);
        panel.Children.Add(_timer);
        var meter = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = _hasAudio ? "音频电平" : "无音轨",
            Visibility = format == RecordingExportFormat.Gif ? Visibility.Collapsed : Visibility.Visible
        };
        for (int i = 0; i < _meterBars.Length; i++)
        {
            var bar = new Border
            {
                Width = 3,
                Height = 3,
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Center,
                Background = RecordingTheme.Brush("Brush.Accent", "#007AFF")
            };
            _meterBars[i] = bar;
            meter.Children.Add(bar);
        }
        panel.Children.Add(meter);
        _pauseIcon = new LucideIconElement
        {
            Kind = LucideIconKind.Pause,
            Width = 16,
            Height = 16
        };
        _pauseButton = new Button { Content = _pauseIcon, Width = 30, Height = 30, ToolTip = "暂停", MinWidth = 30 };
        _pauseButton.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
        AutomationProperties.SetName(_pauseButton, "暂停");
        _pauseButton.Click += (_, _) => PauseToggled?.Invoke(this, EventArgs.Empty);
        panel.Children.Add(_pauseButton);

        var stopFill = RecordingTheme.Brush("Brush.RubyGlass", "#24FF3B30");
        _stopButton = new Button
        {
            Content = "结束录制",
            FontSize = 12,
            Height = 30,
            Width = 70,
            Margin = new Thickness(4, 0, 0, 0),
            Foreground = RecordingTheme.Brush("Brush.Danger", "#FF3B30"),
            Background = stopFill,
            BorderBrush = RecordingTheme.Brush("Brush.RubyBorder", "#57FF3B30"),
            BorderThickness = new Thickness(1),
            ToolTip = "结束并打开预览"
        };
        _stopButton.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
        _stopButton.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        panel.Children.Add(_stopButton);

        Content = new SquircleGlassBorder
        {
            CornerRadiusRadius = 10.5,
            Margin = new Thickness(12),
            Padding = new Thickness(4),
            GlassBackground = RecordingTheme.Brush("Brush.ChromeGradient", "#D9FFFFFF"),
            Child = panel
        };
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _meterTimer.Tick += (_, _) => TickMeter();
        Loaded += (_, _) =>
        {
            SetPaused(_paused);
        };
        Closed += (_, _) =>
        {
            _meterTimer.Stop();
            _halo.BeginAnimation(OpacityProperty, null);
            _beacon.BeginAnimation(OpacityProperty, null);
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, (nint)(style | NativeMethods.WsExNoActivate));
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        };
    }

    private void TickMeter()
    {
        double level = Math.Clamp(Math.Sqrt(Math.Max(0f, _levelProvider?.Invoke() ?? 0f)) * 1.45, 0, 1);
        _smoothedLevel += (level - _smoothedLevel) * (level > _smoothedLevel ? 0.65 : 0.38);
        _phase++;
        for (int i = 0; i < _meterBars.Length; i++)
        {
            double band = 0.70 + 0.30 * Math.Sin(_phase * 0.9 + i * 1.3);
            AnimateBar(_meterBars[i], 3 + 12 * _smoothedLevel * band);
        }
    }

    private static void AnimateBar(Border bar, double height) =>
        bar.BeginAnimation(HeightProperty, new DoubleAnimation(height, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
        });

    public void ShowSavingGif()
    {
        SetPaused(true);
        _pauseButton.Visibility = Visibility.Collapsed;
        _stopButton.Content = "正在保存 GIF…";
        _stopButton.Width = 88;
        _stopButton.IsEnabled = false;
    }

    public void UpdateTimer(double elapsedSeconds)
    {
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds));
        _timer.Text = elapsed.TotalHours >= 1
            ? $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : elapsed.ToString(@"mm\:ss");
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        _pauseIcon.Kind = paused ? LucideIconKind.Play : LucideIconKind.Pause;
        var label = paused ? "继续" : "暂停";
        _pauseButton.ToolTip = label;
        AutomationProperties.SetName(_pauseButton, label);
        _beacon.Fill = paused ? RecordingTheme.Brush("Brush.Warning", "#FF9F0A") : RecordingTheme.Brush("Brush.Danger", "#FF3B30");
        _halo.Visibility = paused ? Visibility.Hidden : Visibility.Visible;
        _halo.BeginAnimation(OpacityProperty, paused ? null : new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        _beacon.BeginAnimation(OpacityProperty, paused ? null : new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
        if (paused)
        {
            _smoothedLevel = 0;
            _meterTimer.Stop();
            foreach (var bar in _meterBars)
                AnimateBar(bar, 3);
        }
        else if (_hasAudio && IsLoaded)
            _meterTimer.Start();
    }

    /// <summary>Centers the capsule above or below the recording region (DPI-aware).</summary>
    public void Reposition(PixelRect region)
    {
        var center = new System.Drawing.Point(region.X + region.Width / 2, region.Y + region.Height / 2);
        var workArea = System.Windows.Forms.Screen.FromPoint(center).WorkingArea;
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            workArea.X + workArea.Width / 2, workArea.Y + workArea.Height / 2)) / 96.0;
        var width = (int)Math.Ceiling(ActualWidth * dpi);
        var height = (int)Math.Ceiling(ActualHeight * dpi);
        var maxX = Math.Max(workArea.Left, workArea.Right - width);
        var maxY = Math.Max(workArea.Top, workArea.Bottom - height);
        var above = region.Y - height - 4;
        var below = region.Bottom + 4;
        var top = above >= workArea.Top ? above : below <= maxY ? below : region.Y + 8;
        var handle = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(handle, 0,
            Math.Clamp(center.X - width / 2, workArea.Left, maxX), Math.Clamp(top, workArea.Top, maxY),
            0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }
}
