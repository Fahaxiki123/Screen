using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

/// <summary>Recording controls remain operable outside the captured image, without taking keyboard focus.</summary>
internal sealed partial class RecordingControlWindow : Window
{
    private readonly DispatcherTimer _meterTimer;
    private readonly Func<float>? _levelProvider;
    private double _smoothedLevel;
    private int _phaseTick;
    private bool _paused;
    private PixelRect? _recordingRegion;

    public event EventHandler? PauseToggled;
    public event EventHandler? StopRequested;

    public RecordingControlWindow(bool hasAudio = false, Func<float>? levelProvider = null, RecordingFormat format = RecordingFormat.Mp4)
    {
        InitializeComponent();
        _levelProvider = levelProvider;
        var showMeter = hasAudio && format != RecordingFormat.Gif;
        _format.Text = format == RecordingFormat.Gif ? "GIF" : "MP4";
        _meterHost.Visibility = showMeter ? Visibility.Visible : Visibility.Collapsed;
        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
        if (showMeter)
        {
            for (var i = 0; i < 16; i++)
            {
                var bar = new Border();
                bar.SetResourceReference(StyleProperty, "Recording.MeterBar");
                _meterHost.Children.Add(bar);
            }
            _meterTimer.Tick += (_, _) => TickAudioMeter();
            _meterTimer.Start();
        }
        StartBeaconPulse();
        Closed += (_, _) =>
        {
            _meterTimer.Stop();
            _beaconHalo.BeginAnimation(OpacityProperty, null);
        };
        SizeChanged += (_, _) =>
        {
            if (_recordingRegion is { } region && IsLoaded)
                Reposition(region);
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, (nint)(style | NativeMethods.WsExNoActivate));
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        };
    }

    private void PauseClick(object sender, RoutedEventArgs e) => PauseToggled?.Invoke(this, EventArgs.Empty);
    private void StopClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke(this, EventArgs.Empty);

    private void StartBeaconPulse()
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;
        _beaconHalo.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.22, TimeSpan.FromMilliseconds(800))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private void TickAudioMeter()
    {
        var raw = _paused ? 0 : Math.Max(0, _levelProvider?.Invoke() ?? 0);
        var target = Math.Clamp(Math.Sqrt(raw) * 1.45, 0, 1);
        _smoothedLevel = _paused ? 0 : target > _smoothedLevel
            ? _smoothedLevel * 0.35 + target * 0.65
            : _smoothedLevel * 0.72 + target * 0.28;
        _phaseTick++;
        for (var i = 0; i < _meterHost.Children.Count; i++)
        {
            var bar = (Border)_meterHost.Children[i];
            var ripple = 0.7 + 0.3 * Math.Sin(_phaseTick * 0.65 + i * 1.15);
            bar.Height = 3 + 12.5 * _smoothedLevel * ripple;
            bar.Opacity = _smoothedLevel > 0.02 ? 0.95 : 0.45;
        }
    }

    public void UpdateTimer(double elapsedSeconds)
    {
        var elapsed = TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds));
        _timer.Text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"hh\:mm\:ss") : elapsed.ToString(@"mm\:ss");
    }

    public void SetStatus(string? status, bool finishing = false)
    {
        _statusLabel.Text = status ?? string.Empty;
        _statusLabel.ToolTip = status;
        _statusLabel.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;
        if (finishing)
        {
            _pauseButton.IsEnabled = false;
            _stopLabel.Visibility = Visibility.Visible;
            _stopButton.ToolTip = "取消正在进行的保存，保留恢复素材 (Esc)";
            AutomationProperties.SetName(_stopButton, "取消保存");
            _meterTimer.Stop();
            _beaconHalo.BeginAnimation(OpacityProperty, null);
        }
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        _pauseIcon.Kind = paused ? LucideIconKind.Play : LucideIconKind.Pause;
        _pauseButton.ToolTip = paused ? "继续录制" : "暂停录制";
        AutomationProperties.SetName(_pauseButton, (string)_pauseButton.ToolTip);
        _beacon.SetResourceReference(Shape.FillProperty, paused ? "Brush.Warning" : "Recording.Beacon");
        _beaconHalo.Visibility = paused ? Visibility.Hidden : Visibility.Visible;
        if (paused)
            _beaconHalo.BeginAnimation(OpacityProperty, null);
        else
            StartBeaconPulse();
    }

    public void Reposition(PixelRect region)
    {
        _recordingRegion = region;
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
