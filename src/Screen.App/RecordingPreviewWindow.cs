using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Lucide.NET;

namespace Screen.App;

/// <summary>
/// Post-recording editor: playback with trim timeline, save-as
/// and copy-file-to-clipboard.  Playback is driven by
/// the bundled ffmpeg decoding raw frames into a WriteableBitmap — WPF's
/// MediaElement relies on legacy Windows media components that are absent or
/// broken on recent Windows builds (silent black screen, no events).
/// Audio is decoded separately and tied to the same playback generation.
/// </summary>
internal sealed partial class RecordingPreviewWindow : Window
{
    private readonly string _filePath;
    private readonly Screen.Core.RecordingProfile _exportProfile;
    private readonly string _exportTempRoot;
    private readonly int _preferredPlaybackFps;
    private readonly DispatcherTimer _ticker;

    private double _durationSeconds;
    private int _videoWidth;
    private int _videoHeight;
    private double _fps = 30;
    private double _positionSeconds;
    private bool _playing;
    private bool _scrubbing;
    private bool _tickerUpdating;
    private bool _loaded;

    private static readonly double[] _playbackSpeeds = [0.5, 1.0, 1.5, 2.0];
    private double _volume = 1.0;
    private bool _muted;
    private double _speed = 1.0;

    private readonly RecordingExportCache _exportCache = new();
    private bool _closed;
    private CancellationTokenSource? _exportOperation;
    private DispatcherTimer? _statusClearTimer;

    public RecordingPreviewWindow(string filePath, Screen.Core.RecordingProfile? recordingProfile = null)
    {
        _filePath = filePath;
        _exportProfile = recordingProfile ?? new Screen.Core.RecordingProfile();
        _exportTempRoot = RecordingStorage.GetRoot(_exportProfile);
        _preferredPlaybackFps = recordingProfile is null ? 0 : Math.Clamp(recordingProfile.FrameRate, 1, 60);
        InitializeComponent();
        Width = Math.Min(1040, SystemParameters.WorkArea.Width * 0.8);
        Height = Math.Min(700, SystemParameters.WorkArea.Height * 0.85);
        _speedChoices.ItemsSource = _playbackSpeeds.Select(speed => speed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "×").ToArray();
        _speedChoices.SelectedIndex = 1;
        _speedChoices.SelectionChanged += (_, _) =>
        {
            if (_speedChoices.SelectedIndex >= 0)
                SetSpeed(_playbackSpeeds[_speedChoices.SelectedIndex]);
        };
        _volumeSlider.ValueChanged += (_, _) =>
        {
            _volume = _volumeSlider.Value;
            _muted = false;
            ApplyVolume();
        };
        _options.Opened += (_, _) =>
        {
            if (_options.Child is FrameworkElement panel)
                Motion.PopIn(panel, menu: true);
            _speedChoices.Focus();
        };
        _options.Closed += (_, _) =>
        {
            if (!_closed && IsActive)
                _moreButton.Focus();
        };
        _options.Child.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _options.IsOpen = false;
                e.Handled = true;
            }
        };
        _videoHost.MouseLeftButtonDown += (_, e) =>
        {
            if (_loaded)
            {
                TogglePlayback();
                e.Handled = true;
            }
        };

        _timeline.ScrubStarted += (_, _) =>
        {
            _scrubbing = true;
            _pendingScrubPreviewSeconds = null;
        };
        _timeline.ScrubCompleted += (_, _) =>
        {
            _scrubbing = false;
            _pendingScrubPreviewSeconds = null;
            if (_loaded)
            {
                Seek(_timeline.Value);
            }
        };
        _timeline.ValueChanged += (_, _) =>
        {
            if (_tickerUpdating || !_loaded)
            {
                return;
            }
            if (_scrubbing)
            {
                _positionSeconds = _timeline.Value;
                UpdateTimeText();
                if (!_playing)
                {
                    QueuePausedScrubPreview(_timeline.Value);
                }
                return;
            }
            Seek(_timeline.Value);
        };
        _timeline.TrimChanged += (_, _) => OnTrimChanged();

        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _ticker.Tick += (_, _) =>
        {
            if (!_loaded || _scrubbing)
            {
                return;
            }
            if (_playing && _positionSeconds >= _timeline.TrimEnd - 0.05)
            {
                // Feishu-style: loop inside the trimmed section.
                Seek(Math.Max(0, _timeline.TrimStart));
                return;
            }
            _tickerUpdating = true;
            try
            {
                _timeline.Value = Math.Min(_timeline.TrimEnd, _positionSeconds);
            }
            finally
            {
                _tickerUpdating = false;
            }
            UpdateTimeText();
        };
        _ticker.Start();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Space)
            {
                TogglePlayback();
                e.Handled = true;
            }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _options.IsOpen = false;
            _exportOperation?.Cancel();
            _statusClearTimer?.Stop();
            _ticker.Stop();
            StopDecoder();
            _exportCache.Dispose();
        };
        Loaded += async (_, _) => await LoadMediaInfoAsync();
        _videoHost.SizeChanged += (_, _) => OnVideoHostSizeChanged();
    }

    // ------------------------------------------------------------------
    // UI helpers
    // ------------------------------------------------------------------

    private void SetPlayingVisual(bool playing) =>
        _playIcon.Kind = playing ? LucideIconKind.Pause : LucideIconKind.Play;

    private void UpdateTimeText()
    {
        _timeText.Text = $"{FormatTime(_positionSeconds)} / {FormatTime(_durationSeconds)}";
    }

    private static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"hh\:mm\:ss");

    // ------------------------------------------------------------------
    private void PlayClick(object sender, RoutedEventArgs e) => TogglePlayback();
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void CancelExportClick(object sender, RoutedEventArgs e) => _exportOperation?.Cancel();
    private void PlaybackOptionsClick(object sender, RoutedEventArgs e) => _options.IsOpen = !_options.IsOpen;

    private void MuteClick(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        ApplyVolume();
    }

    private void SetSpeed(double speed)
    {
        _speed = speed;

        if (_loaded)
        {
            if (_playing)
            {
                StartDecoderFrom(_positionSeconds);
            }
            else
            {
                StartDecoderFrom(_positionSeconds, autoPlay: false);
            }
        }
    }

    private void ApplyVolume()
    {
        try
        {
            _waveOut?.Volume = (float)(_muted ? 0 : _volume);
        }
        catch
        {
        }
        _muteButton.ToolTip = _muted ? "恢复声音" : "静音";
        _volumeIcon.Kind = _muted || _volume <= 0.001 ? LucideIconKind.VolumeX : LucideIconKind.Volume2;
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    // ------------------------------------------------------------------
    // Borderless maximize: clamp to the monitor work area (the taskbar
    // would otherwise be covered).
    // ------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            // Ask DWM to clip the custom chrome to real Windows 11 rounded
            // corners. The WPF border supplies the visible one-pixel outline.
            var darkMode = 1;
            _ = NativeMethods.DwmSetWindowAttribute(source.Handle, 20, ref darkMode, sizeof(int));
            var cornerPreference = 2; // DWMWCP_ROUND
            _ = NativeMethods.DwmSetWindowAttribute(source.Handle, 33, ref cornerPreference, sizeof(int));
            source.AddHook(WndProc);
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            ClampMaximizeToWorkArea(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ClampMaximizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        var info = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }
        var monitorInfo = new MONITORINFO { CbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }
        info.PtMaxPosition.X = monitorInfo.RcWork.Left - monitorInfo.RcMonitor.Left;
        info.PtMaxPosition.Y = monitorInfo.RcWork.Top - monitorInfo.RcMonitor.Top;
        info.PtMaxSize.X = monitorInfo.RcWork.Right - monitorInfo.RcWork.Left;
        info.PtMaxSize.Y = monitorInfo.RcWork.Bottom - monitorInfo.RcWork.Top;
        Marshal.StructureToPtr(info, lParam, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT PtReserved;
        public POINT PtMaxSize;
        public POINT PtMaxPosition;
        public POINT PtMinTrackSize;
        public POINT PtMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public int DwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private void OnTrimChanged()
    {
        // Each range owns its cache entry. An export already underway keeps
        // the selection it started with, even while the handles are moved.
        if (!_playing && (_positionSeconds < _timeline.TrimStart || _positionSeconds > _timeline.TrimEnd))
        {
            _positionSeconds = _timeline.TrimStart;
            _timeline.Value = _positionSeconds;
            UpdateTimeText();
        }
    }

    private void SetStatus(string text, bool persistent = false)
    {
        _statusClearTimer?.Stop();
        _statusText.Text = text;
        if (persistent)
            return;
        var clear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusClearTimer = clear;
        clear.Tick += (_, _) =>
        {
            _statusText.Text = string.Empty;
            clear.Stop();
        };
        clear.Start();
    }

    private void ShowPlaybackError(string message)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = GlassTheme.Brush("Brush.Label")
        });
        var openFolderButton = new Button { Content = "在资源管理器中显示", Width = 142 };
        openFolderButton.SetResourceReference(StyleProperty, "PreviewAction");
        openFolderButton.Click += (_, _) =>
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{_filePath}\"");
            }
            catch
            {
            }
        };
        openFolderButton.Margin = new Thickness(0, 12, 0, 0);
        openFolderButton.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(openFolderButton);
        _videoHost.Child = panel;
    }

}
