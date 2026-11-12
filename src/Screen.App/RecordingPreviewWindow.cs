using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Lucide.NET;

namespace Screen.App;

/// <summary>
/// Post-recording editor (Feishu 编辑录屏): playback with timeline, save-as
/// (下载) and copy-file-to-clipboard (复制到剪贴板).  Playback is driven by
/// the bundled ffmpeg decoding raw frames into a WriteableBitmap — WPF's
/// MediaElement relies on legacy Windows media components that are absent or
/// broken on recent Windows builds (silent black screen, no events).
/// Audio is decoded separately and tied to the same playback generation.
/// </summary>
internal sealed partial class RecordingPreviewWindow : Window
{
    private readonly string _filePath;
    private readonly Image _videoImage;
    private readonly TimelineBar _timeline;
    private readonly TextBlock _timeText;
    private readonly TextBlock _statusText;
    private readonly LucideIconElement _playIcon;
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

    // Compact editor chrome: navy caption bar and moon-blue control strip.
    private const double TitleBarHeight = 44;
    private readonly Border _videoHost;
    private LucideIconElement _volumeIcon = null!;
    private TextBlock _speedLabel = null!;
    private Popup _volumePopup = null!;
    private Popup _speedPopup = null!;
    private double _volume = 1.0;
    private bool _muted;
    private double _speed = 1.0;

    private readonly RecordingExportCache _exportCache = new();
    private bool _closed;

    public RecordingPreviewWindow(string filePath)
    {
        _filePath = filePath;
        Title = "编辑录屏";
        Width = Math.Min(1040, SystemParameters.WorkArea.Width * 0.8);
        Height = Math.Min(700, SystemParameters.WorkArea.Height * 0.85);
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = RecordingTheme.Brush("Brush.WindowBackdrop", "#15223A");
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        UseLayoutRounding = true;

        // Borderless navy chrome: a calm caption bar, full-bleed
        // video, bottom control strip.  WindowChrome keeps native resize
        // borders, caption drag and double-click maximize.
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = TitleBarHeight,
            ResizeBorderThickness = new Thickness(5),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            UseAeroCaptionButtons = false
        });

        _videoImage = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _videoHost = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x06, 0x0B, 0x15)),
            Child = _videoImage
        };
        _videoHost.MouseLeftButtonDown += (_, e) =>
        {
            if (_loaded)
            {
                TogglePlayback();
                e.Handled = true;
            }
        };

        // --- Caption bar ------------------------------------------------
        var captionButtons = new StackPanel { Orientation = Orientation.Horizontal };
        captionButtons.Children.Add(MakeCaptionButton(LucideIconKind.Minus, "最小化",
            () => WindowState = WindowState.Minimized));
        captionButtons.Children.Add(MakeCaptionButton(LucideIconKind.Square, "最大化 / 还原",
            ToggleMaximize));
        captionButtons.Children.Add(MakeCaptionButton(LucideIconKind.X, "关闭",
            Close, close: true));
        var titleBar = new Grid
        {
            Background = RecordingTheme.Brush("Brush.ChromeGradient", "#1B3049"),
            MinHeight = TitleBarHeight
        };
        titleBar.Children.Add(new TextBlock
        {
            Text = "编辑录屏",
            FontSize = 16,
            FontWeight = FontWeights.Medium,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        });
        captionButtons.HorizontalAlignment = HorizontalAlignment.Right;
        titleBar.Children.Add(captionButtons);

        // --- Bottom control strip (Feishu layout) ------------------------
        _playIcon = new LucideIconElement
        {
            Kind = LucideIconKind.Play,
            Width = 18,
            Height = 18,
            StrokeThickness = 2,
            Stroke = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7")
        };
        var playButton = MakeFlatButton(_playIcon, 36, 36, Brushes.Transparent, HoverBrush(),
            (_, _) => TogglePlayback());
        playButton.ToolTip = "播放 / 暂停";

        _timeText = new TextBlock
        {
            Text = "00:00:00 / 00:00:00",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI Variable Text"),
            FontSize = 12.5,
            Foreground = RecordingTheme.Brush("Brush.TextSecondary", "#94AECE"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0)
        };
        _timeline = new TimelineBar();
        WindowChrome.SetIsHitTestVisibleInChrome(_timeline, true);
        _timeline.ToolTip = "拖动进度定位，拖动两侧浅蓝手柄裁剪录屏";
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

        var volumeHost = BuildVolumeControls();
        volumeHost.VerticalAlignment = VerticalAlignment.Center;
        var speedButton = BuildSpeedButton();
        speedButton.VerticalAlignment = VerticalAlignment.Center;

        _statusText = new TextBlock
        {
            FontSize = 12,
            Foreground = RecordingTheme.Brush("Brush.Success", "#83C8B3"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        rightButtons.Children.Add(_statusText);
        rightButtons.Children.Add(volumeHost);
        rightButtons.Children.Add(speedButton);
        var separator = new Border
        {
            Width = 1,
            Height = 18,
            Background = RecordingTheme.Brush("Brush.Border", "#39556F"),
            Margin = new Thickness(10, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        rightButtons.Children.Add(separator);
        var downloadButton = MakeTextButton("下载", SaveAs, primary: false);
        var copyButton = MakeTextButton("复制", CopyToClipboard, primary: true);
        copyButton.Margin = new Thickness(6, 0, 0, 0);
        rightButtons.Children.Add(downloadButton);
        rightButtons.Children.Add(copyButton);

        var bottomBar = new DockPanel
        {
            Height = 52,
            Margin = new Thickness(14, 0, 14, 0),
            LastChildFill = false
        };
        playButton.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(rightButtons, Dock.Right);
        bottomBar.Children.Add(rightButtons);
        bottomBar.Children.Add(playButton);
        bottomBar.Children.Add(_timeText);

        // The reference's trim band is 33 physical pixels at this desktop's
        // 125% scale: 26.4 DIP, edge to edge, with no extra border or padding.
        var progressHost = new Border
        {
            Height = 30,
            Margin = new Thickness(14, 8, 14, 0),
            CornerRadius = new CornerRadius(7),
            ClipToBounds = true,
            Background = RecordingTheme.Brush("Brush.SurfaceSunken", "#080F1D"),
            BorderBrush = RecordingTheme.Brush("Brush.Border", "#2694C5FF"),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Child = _timeline
        };
        DockPanel.SetDock(progressHost, Dock.Top);
        var buttonHost = new Border
        {
            Child = bottomBar
        };
        var bottomHost = new DockPanel
        {
            Background = RecordingTheme.Brush("Brush.ChromeGradient", "#1B3049"),
            LastChildFill = true
        };
        bottomHost.Children.Add(progressHost);
        bottomHost.Children.Add(buttonHost);

        var root = new DockPanel
        {
            LastChildFill = true,
            Background = RecordingTheme.Brush("Brush.WindowBackground", "#15223A")
        };
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        DockPanel.SetDock(bottomHost, Dock.Bottom);
        root.Children.Add(bottomHost);
        root.Children.Add(_videoHost);
        var windowFrame = new Border
        {
            Background = RecordingTheme.Brush("Brush.WindowBackground", "#0A1222"),
            BorderBrush = RecordingTheme.Brush("Brush.GlassRim", "#4594C5FF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            SnapsToDevicePixels = true,
            Child = root
        };
        StateChanged += (_, _) =>
        {
            var maximized = WindowState == WindowState.Maximized;
            windowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(16);
            windowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        };
        Content = windowFrame;

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
    // Chrome + control strip builders (shared moon-blue theme tokens)
    // ------------------------------------------------------------------

    private static Brush HoverBrush() => RecordingTheme.Brush("Brush.SurfaceHover", "#304E69");

    /// <summary>
    /// Flat button with a code-built template (Border + ContentPresenter):
    /// no default chrome, corner radius 8, explicit hover background, so it
    /// reads correctly on the navy control strip.
    /// </summary>
    private static Button MakeFlatButton(
        FrameworkElement content,
        double width,
        double height,
        Brush background,
        Brush hoverBackground,
        RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = content,
            Width = width,
            Height = height,
            Background = background,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Focusable = false,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        chrome.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(BorderBrushProperty));
        chrome.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(BorderThicknessProperty));
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        chrome.SetValue(Border.SnapsToDevicePixelsProperty, true);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, hoverBackground, "Chrome"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.78, "Chrome"));
        template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35, "Chrome"));
        template.Triggers.Add(disabled);
        button.Template = template;
        button.Click += handler;
        return button;
    }

    private Button MakeCaptionButton(LucideIconKind kind, string tip, Action action, bool close = false)
    {
        var icon = new LucideIconElement
        {
            Kind = kind,
            Width = 15,
            Height = 15,
            StrokeThickness = 1.8,
            Stroke = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7")
        };
        var danger = RecordingTheme.Brush("Brush.Danger", "#E9959B");
        var button = MakeFlatButton(icon, 44, 32, Brushes.Transparent,
            close ? danger : HoverBrush(), (_, _) => action());
        if (close)
        {
            button.MouseEnter += (_, _) => icon.Stroke = RecordingTheme.Brush("Brush.OnAccent", "#15223A");
            button.MouseLeave += (_, _) => icon.Stroke = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7");
        }
        button.ToolTip = tip;
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    private Button MakeTextButton(string text, RoutedEventHandler handler, bool primary)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = primary
                ? Brushes.White
                : RecordingTheme.Brush("Brush.TextPrimary", "#F4F8FF")
        };
        Brush background = primary
            ? RecordingTheme.Brush("Brush.PrimaryAction", "#0EA5E9")
            : RecordingTheme.Brush("Brush.SurfaceRaised", "#13223D");
        Brush hoverBackground = primary
            ? RecordingTheme.Brush("Brush.PrimaryActionHover", "#38BDF8")
            : HoverBrush();
        var button = MakeFlatButton(label, double.NaN, 32, background, hoverBackground, handler);
        button.MinWidth = 76;
        button.Padding = new Thickness(16, 0, 16, 0);
        button.BorderThickness = new Thickness(1);
        button.BorderBrush = primary
            ? RecordingTheme.Brush("Brush.GlassRim", "#50FFFFFF")
            : RecordingTheme.Brush("Brush.Border", "#2694C5FF");
        return button;
    }

    /// <summary>
    /// Volume control in the Feishu layout: icon button (click = mute,
    /// hover = slider popup above it).
    /// </summary>
    private Grid BuildVolumeControls()
    {
        _volumeIcon = new LucideIconElement
        {
            Kind = LucideIconKind.Volume2,
            Width = 16,
            Height = 16,
            StrokeThickness = 1.8,
            Stroke = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7")
        };
        var button = MakeFlatButton(_volumeIcon, 36, 36, Brushes.Transparent, HoverBrush(),
            (_, _) =>
            {
                _muted = !_muted;
                ApplyVolume();
            });
        System.Windows.Automation.AutomationProperties.SetName(button, "音量");
        var bar = new VolumeBar { Width = 96, Height = 20, Value = _volume };
        bar.ValueChanged += (_, _) =>
        {
            _volume = bar.Value;
            _muted = false;
            ApplyVolume();
        };
        var popupBorder = new Border
        {
            Background = RecordingTheme.Brush("Brush.SurfaceRaised", "#243D57"),
            BorderBrush = RecordingTheme.Brush("Brush.Border", "#39556F"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Child = bar
        };
        _volumePopup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Top,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            HorizontalOffset = -42,
            VerticalOffset = 2,
            Child = popupBorder
        };
        var host = new Grid();
        host.Children.Add(button);
        var closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            if (!host.IsMouseOver && !popupBorder.IsMouseOver)
            {
                _volumePopup.IsOpen = false;
            }
        };
        void KeepOpen()
        {
            closeTimer.Stop();
            _volumePopup.IsOpen = true;
        }
        void ScheduleClose()
        {
            closeTimer.Stop();
            closeTimer.Start();
        }
        host.MouseEnter += (_, _) => KeepOpen();
        host.MouseLeave += (_, _) => ScheduleClose();
        popupBorder.MouseEnter += (_, _) => KeepOpen();
        popupBorder.MouseLeave += (_, _) => ScheduleClose();
        _volumePopup.Closed += (_, _) => closeTimer.Stop();
        return host;
    }

    private Button BuildSpeedButton()
    {
        _speedLabel = new TextBlock
        {
            Text = "1x",
            FontSize = 12,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7")
        };
        var button = MakeFlatButton(_speedLabel, 38, 36, Brushes.Transparent, HoverBrush(),
            (_, _) => OpenSpeedMenu());
        button.ToolTip = "倍速";
        _speedPopup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            HorizontalOffset = -62,
            VerticalOffset = -4
        };
        return button;
    }

    private void OpenSpeedMenu()
    {
        _speedPopup.Child = BuildSpeedMenu();
        _speedPopup.IsOpen = true;
    }

    private Border BuildSpeedMenu()
    {
        var panel = new StackPanel();
        foreach (var speed in new[] { 0.5, 1.0, 1.5, 2.0 })
        {
            var value = speed;
            var selected = Math.Abs(value - _speed) < 0.01;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var slot = new Grid { Width = 16 };
            if (selected)
            {
                slot.Children.Add(new LucideIconElement
                {
                    Kind = LucideIconKind.Check,
                    Width = 14,
                    Height = 14,
                    StrokeThickness = 2,
                    Stroke = RecordingTheme.Brush("Brush.AccentText", "#C5E5F0")
                });
            }
            row.Children.Add(slot);
            row.Children.Add(new TextBlock
            {
                Text = speed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x",
                FontSize = 12,
                Foreground = selected
                    ? RecordingTheme.Brush("Brush.AccentText", "#C5E5F0")
                    : RecordingTheme.Brush("Brush.TextSecondary", "#B5C9D9"),
                Margin = new Thickness(6, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            var item = MakeFlatButton(row, double.NaN, 30,
                selected ? RecordingTheme.Brush("Brush.AccentSubtle", "#294960") : Brushes.Transparent,
                HoverBrush(),
                (_, _) =>
                {
                    SetSpeed(value);
                    _speedPopup.IsOpen = false;
                });
            item.MinWidth = 100;
            item.Padding = new Thickness(10, 0, 10, 0);
            panel.Children.Add(item);
        }
        return new Border
        {
            Background = RecordingTheme.Brush("Brush.SurfaceRaised", "#243D57"),
            BorderBrush = RecordingTheme.Brush("Brush.Border", "#39556F"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Child = panel
        };
    }

    private void SetSpeed(double speed)
    {
        _speed = speed;
        _speedLabel.Text = speed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x";
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
            if (_waveOut is not null)
            {
                _waveOut.Volume = (float)(_muted ? 0 : _volume);
            }
        }
        catch
        {
        }
        if (_volumeIcon is not null)
        {
            _volumeIcon.Kind = _muted || _volume <= 0.001
                ? LucideIconKind.VolumeX
                : LucideIconKind.Volume2;
        }
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
        var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }
        info.ptMaxPosition.X = monitorInfo.rcWork.Left - monitorInfo.rcMonitor.Left;
        info.ptMaxPosition.Y = monitorInfo.rcWork.Top - monitorInfo.rcMonitor.Top;
        info.ptMaxSize.X = monitorInfo.rcWork.Right - monitorInfo.rcWork.Left;
        info.ptMaxSize.Y = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top;
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
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
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
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
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

    private void SetStatus(string text)
    {
        _statusText.Text = text;
        var clear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
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
            FontSize = 14,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7")
        });
        var openFolderButton = MakeTextButton("在资源管理器中显示", (_, _) =>
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{_filePath}\"");
            }
            catch
            {
            }
        }, primary: true);
        openFolderButton.Margin = new Thickness(0, 12, 0, 0);
        openFolderButton.HorizontalAlignment = HorizontalAlignment.Center;
        panel.Children.Add(openFolderButton);
        _videoHost.Child = panel;
    }

}
