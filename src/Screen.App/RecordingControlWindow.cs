using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Liquid Sapphire Dynamic-Island recording HUD: pulsing ruby beacon, tabular
/// elapsed timer, live 5-band ice-cyan audio meter, circular pause toggle, and
/// a translucent ruby glass stop pill that illuminates on hover.
/// Carries WDA_EXCLUDEFROMCAPTURE so the HUD never leaks into the video.
/// </summary>
internal sealed class RecordingControlWindow : Window
{
    private readonly TextBlock _timer;
    private readonly LucideIconElement _pauseIcon;
    private readonly Ellipse _beacon;
    private readonly Ellipse _beaconHalo;
    private readonly Border[] _meterBars = new Border[5];
    private readonly StackPanel _meterHost;
    private readonly DispatcherTimer _meterTimer;
    private Func<float>? _levelProvider;
    private double _smoothedLevel;
    private int _phaseTick;
    private bool _paused;

    public event EventHandler? PauseToggled;
    public event EventHandler? StopRequested;

    public RecordingControlWindow(bool hasAudio = false, Func<float>? levelProvider = null)
    {
        _levelProvider = levelProvider;
        Title = "Screen 录屏";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;

        // Pulsing ruby recording beacon + soft halo ring
        var beaconHost = new Grid
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 8, 0)
        };
        _beaconHalo = new Ellipse
        {
            Width = 15,
            Height = 15,
            Fill = new SolidColorBrush(Color.FromArgb(0x42, 0xF4, 0x3F, 0x5E)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _beacon = new Ellipse
        {
            Width = 8.5,
            Height = 8.5,
            Fill = RecordingTheme.Brush("Brush.Danger", "#F43F5E"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0xF4, 0x3F, 0x5E),
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.85
            }
        };
        beaconHost.Children.Add(_beaconHalo);
        beaconHost.Children.Add(_beacon);
        StartBeaconPulse();

        // Tabular elapsed readout (clean MM:SS under 1h, HH:MM:SS beyond)
        _timer = new TextBlock
        {
            Text = "00:00",
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#F4F8FF"),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI Variable Text"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };

        // 5-band ice-cyan audio meter (visible when system or mic audio is active)
        _meterHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Height = 16,
            Margin = new Thickness(2, 0, 10, 0),
            Visibility = hasAudio ? Visibility.Visible : Visibility.Collapsed,
            ToolTip = "实时音频电平"
        };
        var barBrush = new LinearGradientBrush(
            Color.FromRgb(0x7D, 0xD3, 0xFC),
            Color.FromRgb(0x0E, 0xA5, 0xE9),
            new Point(0, 0),
            new Point(0, 1));
        barBrush.Freeze();
        for (var i = 0; i < _meterBars.Length; i++)
        {
            var bar = new Border
            {
                Width = 2.6,
                Height = 4,
                CornerRadius = new CornerRadius(1.3),
                Background = barBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(i == 0 ? 0 : 2.4, 0, 0, 0)
            };
            _meterBars[i] = bar;
            _meterHost.Children.Add(bar);
        }

        // Subtle vertical specular divider
        var divider = new Border
        {
            Width = 1,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0x05, 0xBA, 0xE6, 0xFD), 0),
                    new(Color.FromArgb(0x42, 0xBA, 0xE6, 0xFD), 0.5),
                    new(Color.FromArgb(0x05, 0xBA, 0xE6, 0xFD), 1)
                }
            }
        };

        // Circular pause/resume icon button
        _pauseIcon = new LucideIconElement
        {
            Kind = LucideIconKind.Pause,
            Width = 14,
            Height = 14,
            StrokeThickness = 2,
            Stroke = RecordingTheme.Brush("Brush.TextPrimary", "#F4F8FF")
        };
        var pauseButton = BuildCircleButton(_pauseIcon, "暂停 / 继续", () => PauseToggled?.Invoke(this, EventArgs.Empty));
        pauseButton.Margin = new Thickness(0, 0, 6, 0);

        // Translucent ruby glass stop pill (glows crisp crimson on hover, zero muddy pink)
        var stopSquare = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var stopLabel = new TextBlock
        {
            Text = "结束录制",
            Foreground = RecordingTheme.Brush("Brush.RubyText", "#FFE4E8"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var stopRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        stopRow.Children.Add(stopSquare);
        stopRow.Children.Add(stopLabel);

        var rubyRestFill = RecordingTheme.Brush("Brush.RubyGlass", "#38F43F5E");
        var rubyHoverFill = RecordingTheme.Brush("Brush.RubySolid", "#F43F5E");
        var rubyRestBorder = RecordingTheme.Brush("Brush.RubyBorder", "#6BF43F5E");
        var rubyHoverBorder = new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85));
        rubyHoverBorder.Freeze();

        var stopButton = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = rubyRestFill,
            BorderBrush = rubyRestBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(11, 0, 13, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "结束录制并打开预览 (Esc)",
            Child = stopRow
        };
        var stopHoverGlow = new DropShadowEffect
        {
            Color = Color.FromRgb(0xF4, 0x3F, 0x5E),
            BlurRadius = 14,
            ShadowDepth = 0,
            Opacity = 0.55
        };
        stopButton.MouseEnter += (_, _) =>
        {
            stopButton.Background = rubyHoverFill;
            stopButton.BorderBrush = rubyHoverBorder;
            stopButton.Effect = stopHoverGlow;
            stopSquare.Background = Brushes.White;
            stopLabel.Foreground = Brushes.White;
        };
        stopButton.MouseLeave += (_, _) =>
        {
            stopButton.Background = rubyRestFill;
            stopButton.BorderBrush = rubyRestBorder;
            stopButton.Effect = null;
            stopSquare.Background = new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85));
            stopLabel.Foreground = RecordingTheme.Brush("Brush.RubyText", "#FFE4E8");
        };
        stopButton.MouseLeftButtonUp += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(beaconHost);
        panel.Children.Add(_timer);
        panel.Children.Add(_meterHost);
        panel.Children.Add(divider);
        panel.Children.Add(pauseButton);
        panel.Children.Add(stopButton);

        // Outer Liquid Sapphire capsule with top specular rim
        var innerSheen = new Border
        {
            CornerRadius = new CornerRadius(21),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new LinearGradientBrush(
                Color.FromArgb(0x50, 0xE0, 0xF2, 0xFE),
                Color.FromArgb(0x00, 0xE0, 0xF2, 0xFE),
                new Point(0, 0),
                new Point(1, 0)),
            Padding = new Thickness(10, 5, 6, 5),
            Child = panel
        };

        Content = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = RecordingTheme.Brush("Brush.ChromeGradient", "#EB111F38"),
            BorderBrush = RecordingTheme.Brush("Brush.GlassRim", "#4594C5FF"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(18, 12, 18, 22),
            Effect = new DropShadowEffect
            {
                BlurRadius = 28,
                Color = Color.FromRgb(0x02, 0x06, 0x10),
                Opacity = 0.62,
                ShadowDepth = 8
            },
            Child = innerSheen
        };

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
        _meterTimer.Tick += (_, _) => TickAudioMeter();
        if (hasAudio)
        {
            _meterTimer.Start();
        }
        Closed += (_, _) => _meterTimer.Stop();

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

    private static Border BuildCircleButton(FrameworkElement icon, string toolTip, Action onClick)
    {
        var restBg = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        var hoverBg = new SolidColorBrush(Color.FromArgb(0x30, 0x7D, 0xD3, 0xFC));
        restBg.Freeze();
        hoverBg.Freeze();

        var pill = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Background = restBg,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = toolTip,
            Child = new Grid { Children = { icon } }
        };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        pill.MouseEnter += (_, _) => pill.Background = hoverBg;
        pill.MouseLeave += (_, _) => pill.Background = restBg;
        pill.MouseLeftButtonUp += (_, _) => onClick();
        return pill;
    }

    private void StartBeaconPulse()
    {
        var pulse = new DoubleAnimation(1.0, 0.32, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        _beaconHalo.BeginAnimation(OpacityProperty, pulse);
    }

    private void TickAudioMeter()
    {
        if (_paused)
        {
            for (var i = 0; i < _meterBars.Length; i++)
            {
                _meterBars[i].Height = 3;
                _meterBars[i].Opacity = 0.35;
            }
            return;
        }

        _phaseTick++;
        var raw = _levelProvider?.Invoke() ?? 0f;
        // Perceptual boost so ordinary speech/system audio visibly dances
        var target = Math.Clamp(Math.Sqrt(raw) * 1.45, 0.0, 1.0);
        _smoothedLevel = target > _smoothedLevel
            ? _smoothedLevel * 0.35 + target * 0.65
            : _smoothedLevel * 0.72 + target * 0.28;

        double[] weights = [0.58, 0.92, 0.74, 1.0, 0.64];
        for (var i = 0; i < _meterBars.Length; i++)
        {
            var ripple = 0.85 + 0.15 * Math.Sin(_phaseTick * 0.65 + i * 1.15);
            var normalized = _smoothedLevel * weights[i] * ripple;
            _meterBars[i].Height = Math.Clamp(3.2 + normalized * 12.5, 3.2, 15.5);
            _meterBars[i].Opacity = _smoothedLevel > 0.02 ? 0.95 : 0.45;
        }
    }

    public void UpdateTimer(double elapsedSeconds)
    {
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds));
        _timer.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        _pauseIcon.Kind = paused ? LucideIconKind.Play : LucideIconKind.Pause;
        if (paused)
        {
            _beaconHalo.BeginAnimation(OpacityProperty, null);
            _beaconHalo.Fill = new SolidColorBrush(Color.FromArgb(0x38, 0xFB, 0xBF, 0x24));
            _beacon.Fill = RecordingTheme.Brush("Brush.Warning", "#FBBF24");
            _beacon.Effect = null;
        }
        else
        {
            _beaconHalo.Fill = new SolidColorBrush(Color.FromArgb(0x42, 0xF4, 0x3F, 0x5E));
            _beacon.Fill = RecordingTheme.Brush("Brush.Danger", "#F43F5E");
            _beacon.Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0xF4, 0x3F, 0x5E),
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.85
            };
            StartBeaconPulse();
        }
    }

    /// <summary>Centers the capsule above or below the recording region (DPI-aware).</summary>
    public void Reposition(PixelRect region)
    {
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            region.X + region.Width / 2,
            region.Y + region.Height / 2)) / 96.0;
        Left = (region.X + region.Width / 2.0 - ActualWidth * dpi / 2) / dpi;

        // Prefer floating cleanly above the region like a camera viewfinder HUD;
        // fall back to below or inside top when recording near screen edges.
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualBottom = SystemParameters.VirtualScreenHeight + virtualTop;
        var above = region.Y / dpi - ActualHeight + 8;
        var below = (region.Bottom + 4) / dpi;

        if (above >= virtualTop + 4)
        {
            Top = above;
        }
        else if (below + ActualHeight <= virtualBottom - 4)
        {
            Top = below;
        }
        else
        {
            Top = (region.Y + 8) / dpi;
        }
    }
}
