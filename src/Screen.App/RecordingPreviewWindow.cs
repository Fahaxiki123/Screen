using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
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
    private string _filePath;
    private readonly RecordingExportFormat _exportFormat;
    private readonly CancellationTokenSource _exportLifetime = new();
    private readonly DispatcherTimer _statusReset = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Image _videoImage;
    private readonly TimelineBar _timeline;
    private readonly TextBlock _timeText;
    private readonly TextBlock _trimRange = new()
    {
        FontSize = 12,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        Foreground = RecordingTheme.Brush("Brush.TextSecondary", "#9E1D1D1F")
    };
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

    private const double TitleBarHeight = 44;
    private readonly Border _videoHost;
    private LucideIconElement _volumeIcon = null!;
    private TextBlock _speedLabel = null!;
    private Popup? _volumePopup;
    private Popup _speedPopup = null!;
    private double _volume = 1.0;
    private bool _muted;
    private double _speed = 1.0;

    private readonly RecordingExportCache _exportCache = new();
    private bool _closed;

    public RecordingPreviewWindow(string filePath, RecordingExportFormat format = RecordingExportFormat.Mp4, string? savedPath = null)
    {
        _filePath = filePath;
        _savedPath = savedPath ?? filePath;
        _originalGifAvailable = format == RecordingExportFormat.Gif && savedPath is not null;
        _exportFormat = format;
        Title = "编辑录屏";
        Width = Math.Min(1040, SystemParameters.WorkArea.Width * 0.8);
        Height = Math.Min(700, SystemParameters.WorkArea.Height * 0.85);
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = RecordingTheme.Brush("Brush.WindowBackdrop", "#F4F6F9");
        FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI");
        UseLayoutRounding = true;

        // Keep native resize, caption dragging and maximize behavior.
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = TitleBarHeight,
            ResizeBorderThickness = new Thickness(5),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(13),
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
            Background = RecordingTheme.Brush("Brush.WindowBackground", "#F4F6F9"),
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
            Background = RecordingTheme.Brush("Brush.ChromeGradient", "#D9FFFFFF"),
            MinHeight = TitleBarHeight
        };
        titleBar.Children.Add(new TextBlock
        {
            Text = "编辑录屏",
            FontSize = 16,
            FontWeight = FontWeights.Medium,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#1D1D1F"),
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
            Height = 18
        };
        var playButton = MakeFlatButton(_playIcon, 36, 36, Brushes.Transparent, HoverBrush(),
            (_, _) => TogglePlayback());
        playButton.ToolTip = "播放 / 暂停";

        _timeText = new TextBlock
        {
            Text = "00:00:00 / 00:00:00",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI Variable Text"),
            FontSize = 12.5,
            Foreground = RecordingTheme.Brush("Brush.TextSecondary", "#9E1D1D1F"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0)
        };
        Typography.SetNumeralAlignment(_timeText, FontNumeralAlignment.Tabular);
        Typography.SetNumeralAlignment(_trimRange, FontNumeralAlignment.Tabular);
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
        _timeline.TrimChanged += (_, _) => UpdateTrimReadout();

        var speedButton = BuildSpeedButton();
        speedButton.VerticalAlignment = VerticalAlignment.Center;

        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#1D1D1F"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };

        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (format == RecordingExportFormat.Mp4)
        {
            var volumeHost = BuildVolumeControls();
            volumeHost.VerticalAlignment = VerticalAlignment.Center;
            rightButtons.Children.Add(volumeHost);
        }
        rightButtons.Children.Add(speedButton);
        var separator = new Border
        {
            Width = 1,
            Height = 18,
            Background = RecordingTheme.Brush("Brush.Border", "#210F172A"),
            Margin = new Thickness(10, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        rightButtons.Children.Add(separator);
        var downloadButton = MakeTextButton(format == RecordingExportFormat.Gif ? "导出 GIF" : "导出 MP4", SaveAs, primary: false);
        var copyButton = MakeTextButton("复制", CopyToClipboard, primary: true);
        copyButton.Margin = new Thickness(6, 0, 0, 0);
        rightButtons.Children.Add(downloadButton);
        rightButtons.Children.Add(copyButton);

        var bottomBar = new DockPanel
        {
            Height = 52,
            Margin = new Thickness(14, 0, 14, 0),
            LastChildFill = true
        };
        playButton.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(rightButtons, Dock.Right);
        bottomBar.Children.Add(rightButtons);
        bottomBar.Children.Add(playButton);
        bottomBar.Children.Add(_timeText);
        bottomBar.Children.Add(_statusText);

        var progressHost = new SquircleGlassBorder
        {
            Height = 30,
            Margin = new Thickness(12, 10, 12, 0),
            CornerRadiusRadius = 9,
            ShowRim = false,
            ShadowLevel = 0,
            GlassBackground = RecordingTheme.Brush("Brush.SurfaceSunken", "#0E0F172A"),
            Child = _timeline
        };
        var rangeHeader = new DockPanel { Margin = new Thickness(14, 9, 14, 0) };
        DockPanel.SetDock(_trimRange, Dock.Right);
        rangeHeader.Children.Add(_trimRange);
        rangeHeader.Children.Add(new TextBlock { Text = "裁切范围", FontSize = 12, FontWeight = FontWeights.SemiBold });
        var controls = new DockPanel();
        DockPanel.SetDock(rangeHeader, Dock.Top);
        controls.Children.Add(rangeHeader);
        DockPanel.SetDock(progressHost, Dock.Top);
        controls.Children.Add(progressHost);
        controls.Children.Add(bottomBar);
        var bottomHost = new SquircleGlassBorder
        {
            CornerRadiusRadius = 13,
            Margin = new Thickness(12),
            GlassBackground = RecordingTheme.Brush("Brush.AuroraCard", "#EBFFFFFF"),
            Child = controls
        };
        var root = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);
        DockPanel.SetDock(bottomHost, Dock.Bottom);
        root.Children.Add(bottomHost);
        root.Children.Add(_videoHost);
        DesktopWindowChrome.SetEditorContent(this, root, RecordingTheme.Brush("Brush.Border", "#210F172A"));
        _statusReset.Tick += (_, _) =>
        {
            _statusReset.Stop();
            _statusText.Text = string.Empty;
        };

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
                if (_speedPopup.IsOpen)
                    _speedPopup.IsOpen = false;
                else if (_volumePopup?.IsOpen == true)
                    _volumePopup.IsOpen = false;
                else
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
            _statusReset.Stop();
            _exportLifetime.Cancel();
            _exportLifetime.Dispose();
            StopDecoder();
            _exportCache.Dispose();
            _gifExportCache.Dispose();
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
    // Playback controls
    // ------------------------------------------------------------------

    private static Brush HoverBrush() => RecordingTheme.Brush("Brush.SurfaceHover", "#0E0F172A");

    /// <summary>
    /// Compact playback controls share one hover and press treatment.
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
            Height = 15
        };
        var danger = RecordingTheme.Brush("Brush.Danger", "#FF3B30");
        var button = MakeFlatButton(icon, 44, 32, Brushes.Transparent,
            close ? danger : HoverBrush(), (_, _) => action());
        if (close)
        {
            button.MouseEnter += (_, _) => button.Foreground = RecordingTheme.Brush("Brush.OnAccent", "#FFFFFF");
            button.MouseLeave += (_, _) => button.Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#1D1D1F");
        }
        button.ToolTip = tip;
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        return button;
    }

    private Button MakeTextButton(string text, RoutedEventHandler handler, bool primary)
    {
        var button = new Button
        {
            Content = text,
            Height = 34,
            MinWidth = 76,
            Margin = new Thickness(0),
            Padding = new Thickness(14, 0, 14, 0),
            Cursor = Cursors.Hand
        };
        button.SetResourceReference(StyleProperty, primary ? "CardPrimaryButtonStyle" : "CardSecondaryButtonStyle");
        button.Click += handler;
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
            Height = 16
        };
        var button = MakeFlatButton(_volumeIcon, 36, 36, Brushes.Transparent, HoverBrush(),
            (_, _) =>
            {
                _muted = !_muted;
                ApplyVolume();
            });
        System.Windows.Automation.AutomationProperties.SetName(button, "音量");
        var bar = new Slider { Width = 96, Height = 24, Minimum = 0, Maximum = 1, Value = _volume, IsMoveToPointEnabled = true };
        System.Windows.Automation.AutomationProperties.SetName(bar, "播放音量");
        bar.ValueChanged += (_, _) =>
        {
            _volume = bar.Value;
            _muted = false;
            ApplyVolume();
        };
        var popupBorder = new SquircleGlassBorder
        {
            CornerRadiusRadius = 10.5,
            Padding = new Thickness(12, 8, 12, 8),
            Child = bar
        };
        var popup = new Popup
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
        _volumePopup = popup;
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
                popup.IsOpen = false;
            }
        };
        void KeepOpen()
        {
            closeTimer.Stop();
            popup.IsOpen = true;
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
        popup.Closed += (_, _) => closeTimer.Stop();
        return host;
    }

    private Button BuildSpeedButton()
    {
        _speedLabel = new TextBlock
        {
            Text = "1x",
            FontSize = 12,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#1D1D1F")
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

    private SquircleGlassBorder BuildSpeedMenu()
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
                    Foreground = RecordingTheme.Brush("Brush.AccentText", "#007AFF")
                });
            }
            row.Children.Add(slot);
            row.Children.Add(new TextBlock
            {
                Text = speed.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "x",
                FontSize = 12,
                Foreground = selected
                    ? RecordingTheme.Brush("Brush.AccentText", "#007AFF")
                    : RecordingTheme.Brush("Brush.TextSecondary", "#9E1D1D1F"),
                Margin = new Thickness(6, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            var item = MakeFlatButton(row, double.NaN, 30,
                selected ? RecordingTheme.Brush("Brush.AccentSubtle", "#1C007AFF") : Brushes.Transparent,
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
        return new SquircleGlassBorder
        {
            CornerRadiusRadius = 10.5,
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

    private void UpdateTrimReadout()
    {
        var start = TimeSpan.FromSeconds(_timeline.TrimStart);
        var end = TimeSpan.FromSeconds(_timeline.TrimEnd);
        const string timeFormat = @"hh\:mm\:ss\.f";
        _trimRange.Text = $"{start.ToString(timeFormat)} – {end.ToString(timeFormat)}  ·  {(end - start).TotalSeconds:F1} s";
    }

    private void SetStatus(string text)
    {
        _statusText.Text = text;
        _statusText.ToolTip = text;
        _statusReset.Stop();
        _statusReset.Start();
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
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#1D1D1F")
        });
        var openFolderButton = MakeTextButton("在资源管理器中显示", (_, _) =>
        {
            try
            {
                Process.Start("explorer.exe", $"/select,\"{_savedPath}\"");
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
