using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureOverlayWindow
{
    private static Brush FindResourceBrush(string key) => (Brush)Application.Current.FindResource(key);

    private SquircleGlassBorder BuildModeBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _modeTrack = new SpringTrack(row, 6.5);
        foreach (var (mode, name) in new[]
        {
            (CaptureEntryMode.Screenshot, "截图"), (CaptureEntryMode.ScrollCapture, "滚动截图"),
            (CaptureEntryMode.Ocr, "提取文字"), (CaptureEntryMode.Translation, "翻译")
        })
        {
            var button = new Button
            {
                Content = name,
                Width = name.Length == 4 ? 80 : 62,
                FontSize = 13,
                Style = (Style)Application.Current.FindResource("ToolbarIconButtonDarkStyle")
            };
            button.Click += (_, _) =>
            {
                _entryMode = mode;
                UpdateModeButtons();
            };
            _modeButtons.Add(mode, button);
            row.Children.Add(button);
        }

        var bar = new SquircleGlassBorder { Padding = new Thickness(4), Child = _modeTrack };
        bar.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue && !_recordingMode)
                GlassMotion.Enter(bar, -12, 0.94, 300);
        };
        return bar;
    }

    private void UpdateModeButtons()
    {
        foreach (var (mode, button) in _modeButtons)
        {
            bool selected = mode == _entryMode;
            button.Foreground = FindResourceBrush(selected ? "Brush.Accent" : "Brush.OverlayTextDim");
            button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }
        _modeTrack.Select(_modeButtons.GetValueOrDefault(_entryMode));
    }

    private SquircleGlassBorder BuildToolbar(out Button translationButton)
    {
        var row = new WrapPanel();
        var drawing = new WrapPanel();
        _drawingTrack = new SpringTrack(drawing, 6.5);
        row.Children.Add(_drawingTrack);
        foreach (var (tool, icon, label) in new[]
        {
            (CaptureTool.Rectangle, "rect", "矩形"), (CaptureTool.Ellipse, "ellipse", "椭圆"),
            (CaptureTool.Arrow, "arrow", "箭头 · 右键选直线"), (CaptureTool.Pen, "pen", "画笔"),
            (CaptureTool.Text, "text", "文字 · 右键选序号"), (CaptureTool.Mosaic, "mosaic", "马赛克")
        })
        {
            var button = ToolbarButton(drawing, icon, label + " · 再次点击可移动选区", (_, _) =>
            {
                if (_surface.Tool == tool)
                {
                    _surface.ClearAnnotationSelection();
                    _surface.Tool = CaptureTool.Select;
                    _stylePanel.Visibility = Visibility.Collapsed;
                    UpdateDrawingSelection();
                    PositionFloatingPanels();
                }
                else
                    SelectTool(tool);
            }, GlassMotion.Pulse);
            _toolButtons.Add(tool, button);
            if (tool == CaptureTool.Arrow)
                AddMenuAction(button, "直线", () => SelectTool(CaptureTool.Line));
            if (tool == CaptureTool.Text)
                AddMenuAction(button, "序号", () => SelectTool(CaptureTool.Number));
        }
        row.Children.Add(Separator());
        ToolbarButton(row, "pin", "贴图到桌面", Pin);
        ToolbarButton(row, "ocr", "提取文字", ShowOcr);
        translationButton = ToolbarButton(row, "translate", "按当前语种翻译 · 右键选择语种", TranslateWithCurrentLanguage);
        translationButton.MouseRightButtonUp += (_, e) =>
        {
            ToggleTranslationMenu(this, new RoutedEventArgs());
            e.Handled = true;
        };
        translationButton.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
            {
                ToggleTranslationMenu(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };
        ToolbarButton(row, "scroll", "长截图", StartLongCapture, GlassMotion.Dip);
        ToolbarButton(row, "video", "录制此区域", StartRegionRecording);
        row.Children.Add(Separator());
        var undo = ToolbarButton(row, "undo", "撤销 · Ctrl+Z · 右键重做", (_, _) => _surface.Undo(), GlassMotion.Rotate);
        AddMenuAction(undo, "重做", () => _surface.Redo(), "Ctrl+Y");
        ToolbarButton(row, "save", "另存为 PNG · Ctrl+S", Save, GlassMotion.Dip);
        ToolbarButton(row, "close", "取消截图 · Esc", (_, _) => Close()).Foreground = FindResourceBrush("Brush.Danger");

        var finishContent = new StackPanel { Orientation = Orientation.Horizontal };
        finishContent.Children.Add(AppleVectorIcons.Create("check", 17));
        finishContent.Children.Add(new TextBlock { Text = "完成", Margin = new Thickness(4, 0, 0, 0) });
        var finish = new Button
        {
            Content = finishContent,
            ToolTip = "完成并复制 · Enter",
            Margin = new Thickness(4, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("CardPrimaryButtonStyle")
        };
        AutomationProperties.SetName(finish, "完成并复制");
        finish.Click += Finish;
        _toolbarButtons.Add(finish);
        row.Children.Add(finish);
        return new SquircleGlassBorder { Padding = new Thickness(4), Child = row, Visibility = Visibility.Collapsed };
    }

    private Button ToolbarButton(Panel group, string icon, string label, RoutedEventHandler action,
        Action<FrameworkElement>? feedback = null)
    {
        var glyph = AppleVectorIcons.Create(icon);
        var button = new Button { Content = glyph, ToolTip = label };
        button.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
        AutomationProperties.SetName(button, label.Split('·')[0].Trim());
        button.Click += (sender, e) =>
        {
            feedback?.Invoke(glyph);
            action(sender, e);
        };
        group.Children.Add(button);
        _toolbarButtons.Add(button);
        return button;
    }

    private static void AddMenuAction(Button button, string label, Action action, string shortcut = "")
    {
        var item = new MenuItem { Header = label, InputGestureText = shortcut };
        item.Click += (_, _) => action();
        button.ContextMenu = new ContextMenu { Items = { item } };
    }

    private Button? ActiveDrawingButton() => _toolButtons.GetValueOrDefault(_surface.Tool switch
    {
        CaptureTool.Line => CaptureTool.Arrow,
        CaptureTool.Number => CaptureTool.Text,
        _ => _surface.Tool
    });

    private void UpdateDrawingSelection()
    {
        var active = ActiveDrawingButton();
        foreach (var button in _toolButtons.Values)
            button.Foreground = FindResourceBrush(button == active ? "Brush.Accent" : "Brush.OverlayIcon");
        _drawingTrack.Select(active);
    }

    private void AnimateToolbarEntry()
    {
        _toolbarEntryTimestamp = Stopwatch.GetTimestamp();
        GlassMotion.Enter(_toolbar, -10, 0.93, 320, overshootY: 1.5, overshootScale: 1.012);
        for (int i = 0; i < _toolbarButtons.Count; i++)
        {
            var button = _toolbarButtons[i];
            button.ApplyTemplate();
            if (VisualTreeHelper.GetChildrenCount(button) > 0 && VisualTreeHelper.GetChild(button, 0) is FrameworkElement chrome)
                GlassMotion.Enter(chrome, 5, 0.84, 240, 14 * i, opacityTarget: button);
        }
    }

    private Popup BuildTranslationPopup()
    {
        var list = new StackPanel();
        foreach (string language in new[] { "简体中文", "English", "日本語", "한국어", "繁體中文", "Français", "Español", "Português", "Deutsch" })
        {
            var button = new Button { Content = language, Width = 170, Height = 32, FontSize = 13 };
            button.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
            button.Click += async (_, _) =>
            {
                _translationPopup.IsOpen = false;
                try
                {
                    _settingsStore.Update(latest => latest.Translation = latest.Translation with { TargetLanguage = language });
                    await TranslateSelectionAsync(language);
                }
                catch (Exception exception)
                {
                    _translationCard.SetError(exception.Message);
                }
            };
            list.Children.Add(button);
        }
        return new Popup
        {
            PlacementTarget = _translationButton,
            Placement = PlacementMode.Top,
            HorizontalOffset = -SquircleGlassBorder.FloatingShadowMargin.Left,
            VerticalOffset = SquircleGlassBorder.FloatingShadowMargin.Bottom - 6,
            AllowsTransparency = true,
            StaysOpen = false,
            Child = new SquircleGlassBorder
            {
                Margin = SquircleGlassBorder.FloatingShadowMargin,
                Padding = new Thickness(4),
                Child = new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            }
        };
    }

    private SquircleGlassBorder BuildRecordingPanel()
    {
        var defaults = _settingsStore.Load().Recording;
        _showCursorChoice = defaults.ShowCursor;
        _showClickEffectsChoice = defaults.ShowClickEffects;
        _recordSystemAudioChoice = defaults.RecordSystemAudio;
        _recordMicrophoneChoice = defaults.RecordMicrophone;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var segments = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        var track = new SpringTrack(segments, 4.5);
        var mp4 = FormatButton("MP4");
        var gif = FormatButton("GIF");
        gif.ToolTip = $"停止录制后自动保存 GIF，{RecordingGifExporter.FramesPerSecond} 帧/秒，长边最多 {RecordingGifExporter.MaxLongEdge} 像素。";
        segments.Children.Add(mp4);
        segments.Children.Add(gif);
        row.Children.Add(new SquircleGlassBorder
        {
            CornerRadiusRadius = 6.5,
            GlassBackground = FindResourceBrush("Brush.SurfaceSunken"),
            ShowRim = false,
            ShadowLevel = 0,
            Child = track,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(Separator());

        var speaker = RecordingOption("speaker", "系统声音", out var speakerLabel);
        var mic = RecordingOption("mic", "麦克风", out _);
        var ripple = RecordingOption("cursor", "点击波纹", out _);
        row.Children.Add(speaker);
        row.Children.Add(mic);
        row.Children.Add(ripple);
        row.Children.Add(Separator());
        var startLabel = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var start = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new Ellipse { Width = 8, Height = 8, Fill = Brushes.White, Margin = new Thickness(0, 0, 6, 0) },
                    startLabel
                }
            },
            Height = 34,
            Width = 88,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Background = FindResourceBrush("Brush.RubySolid"),
            Style = (Style)Application.Current.FindResource("ToolbarIconButtonDarkStyle")
        };
        row.Children.Add(start);
        start.Click += (_, _) => BeginRecordingFromPanel();

        void Refresh(bool instant = false)
        {
            bool audioAvailable = _recordingFormat == RecordingExportFormat.Mp4;
            track.Select(audioAvailable ? mp4 : gif, instant);
            mp4.Foreground = FindResourceBrush(audioAvailable ? "Brush.Accent" : "Brush.TextSecondary");
            gif.Foreground = FindResourceBrush(audioAvailable ? "Brush.TextSecondary" : "Brush.Accent");
            AutomationProperties.SetItemStatus(mp4, audioAvailable ? "已选择" : string.Empty);
            AutomationProperties.SetItemStatus(gif, audioAvailable ? string.Empty : "已选择");
            speakerLabel.Text = audioAvailable ? "系统声音" : "无音轨";
            startLabel.Text = audioAvailable ? "开始录制" : "录制 GIF";
            AutomationProperties.SetName(start, startLabel.Text);
            UpdateOption(speaker, audioAvailable && _recordSystemAudioChoice, audioAvailable, instant);
            UpdateOption(mic, audioAvailable && _recordMicrophoneChoice, audioAvailable, instant);
            UpdateOption(ripple, _showClickEffectsChoice, true, instant);
            speaker.ToolTip = audioAvailable ? "录制系统声音" : "GIF 没有音轨";
            mic.ToolTip = audioAvailable ? "录制麦克风声音" : "GIF 没有音轨";
        }
        mp4.Click += (_, _) => { _recordingFormat = RecordingExportFormat.Mp4; Refresh(); };
        gif.Click += (_, _) => { _recordingFormat = RecordingExportFormat.Gif; Refresh(); };
        segments.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Left or Key.Right))
                return;
            _recordingFormat = e.Key == Key.Left ? RecordingExportFormat.Mp4 : RecordingExportFormat.Gif;
            (_recordingFormat == RecordingExportFormat.Mp4 ? mp4 : gif).Focus();
            Refresh();
            e.Handled = true;
        };
        speaker.Click += (_, _) => { _recordSystemAudioChoice = !_recordSystemAudioChoice; Refresh(); };
        mic.Click += (_, _) => { _recordMicrophoneChoice = !_recordMicrophoneChoice; Refresh(); };
        ripple.Click += (_, _) => { _showClickEffectsChoice = !_showClickEffectsChoice; Refresh(); };
        Refresh(instant: true);
        // Very narrow displays keep the same single-row arrangement.
        var fit = new Viewbox { Child = row, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        var panel = new SquircleGlassBorder { Child = fit, Padding = new Thickness(4), Visibility = Visibility.Collapsed };
        panel.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                GlassMotion.Enter(panel, -10, 0.93, 320, overshootY: 1.5, overshootScale: 1.012);
        };
        return panel;
    }

    private static Button FormatButton(string label) => new()
    {
        Content = label,
        Width = 46,
        Height = 28,
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Style = (Style)Application.Current.FindResource("ToolbarIconButtonDarkStyle")
    };

    private static Button RecordingOption(string iconName, string label, out TextBlock text)
    {
        var glyph = AppleVectorIcons.Create(iconName, 17);
        text = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, text } };
        var button = new Button
        {
            Content = content,
            Width = label.Length > 3 ? 82 : 72,
            Margin = new Thickness(1, 0, 1, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            ToolTip = label,
            Style = (Style)Application.Current.FindResource("ToolbarIconButtonDarkStyle")
        };
        button.Click += (_, _) => GlassMotion.Pulse(glyph);
        AutomationProperties.SetName(button, label);
        ToolTipService.SetShowOnDisabled(button, true);
        return button;
    }

    private static void UpdateOption(Button option, bool active, bool available, bool instant)
    {
        var background = (SolidColorBrush)option.Background;
        var color = active ? ((SolidColorBrush)FindResourceBrush("Brush.AccentSubtle")).Color : Colors.Transparent;
        var duration = TimeSpan.FromMilliseconds(instant ? 0 : 180);
        background.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(background.Color, color, duration));
        option.Foreground = FindResourceBrush(active ? "Brush.Accent" : "Brush.OverlayTextDim");
        option.IsEnabled = available;
        AutomationProperties.SetItemStatus(option, available ? active ? "已开启" : "已关闭" : "无音轨");
    }

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 16,
        Background = FindResourceBrush("Brush.BorderSubtle"),
        Margin = new Thickness(6, 4, 6, 4),
        VerticalAlignment = VerticalAlignment.Center
    };
}
