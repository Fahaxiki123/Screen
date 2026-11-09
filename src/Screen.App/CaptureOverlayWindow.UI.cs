using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// UI building methods for CaptureOverlayWindow (toolbars, panels, buttons).
/// </summary>
internal sealed partial class CaptureOverlayWindow
{
    private Border BuildModeBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        AddModeButton(row, CaptureEntryMode.Screenshot, "截图");
        AddModeButton(row, CaptureEntryMode.ScrollCapture, "滚动截图");
        AddModeButton(row, CaptureEntryMode.Ocr, "提取文字");
        AddModeButton(row, CaptureEntryMode.Translation, "翻译");
        return new Border
        {
            Background = FindResourceBrush("Brush.ChromeGradient"),
            BorderBrush = FindResourceBrush("Brush.OverlayBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(5),
            Effect = FindResourceEffect("Shadow.Floating"),
            Child = row
        };
    }

    private static Brush FindResourceBrush(string key) => (Brush)Application.Current.FindResource(key);

    private static System.Windows.Media.Effects.DropShadowEffect FindResourceEffect(string key) =>
        (System.Windows.Media.Effects.DropShadowEffect)Application.Current.FindResource(key);

    private void AddModeButton(Panel row, CaptureEntryMode mode, string text)
    {
        // Rounded text button: the default template paints square-corner
        // backgrounds inside the rounded mode bar.
        var template = new ControlTemplate(typeof(Button));
        var chrome = new FrameworkElementFactory(typeof(Border));
        chrome.Name = "Chrome";
        chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        chrome.AppendChild(presenter);
        template.VisualTree = chrome;
        var hover = new Trigger
        {
            Property = UIElement.IsMouseOverProperty,
            Value = true
        };
        hover.Setters.Add(new Setter(
            Border.BackgroundProperty, FindResourceBrush("Brush.OverlayHover"), "Chrome"));
        template.Triggers.Add(hover);

        var button = new Button
        {
            Content = text,
            MinWidth = mode == CaptureEntryMode.ScrollCapture ? 86 : 70,
            Height = 38,
            Margin = new Thickness(1),
            Padding = new Thickness(12, 0, 12, 0),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindResourceBrush("Brush.TextPrimary"),
            Background = Brushes.Transparent,
            Template = template
        };
        button.Click += (_, _) =>
        {
            _entryMode = mode;
            UpdateModeButtons();
        };
        _modeButtons[mode] = button;
        row.Children.Add(button);
    }

    private void UpdateModeButtons()
    {
        foreach (var (mode, button) in _modeButtons)
        {
            var selected = mode == _entryMode;
            button.Background = selected ? FindResourceBrush("Brush.AccentSubtleDark") : Brushes.Transparent;
            button.Foreground = selected
                ? FindResourceBrush("Brush.AccentText")
                : FindResourceBrush("Brush.OverlayTextDim");
        }
    }

    private Border BuildToolbar(out Button translationButton, out Button translationDropDownButton)
    {
        var tools = new WrapPanel { Orientation = Orientation.Horizontal };
        AddTool(tools, CaptureTool.Rectangle, LucideIconKind.Square, "矩形");
        AddTool(tools, CaptureTool.Ellipse, LucideIconKind.Circle, "圆形");
        AddTool(tools, CaptureTool.Line, LucideIconKind.Minus, "直线");
        AddTool(tools, CaptureTool.Arrow, LucideIconKind.MoveUpRight, "箭头");
        AddTool(tools, CaptureTool.Pen, LucideIconKind.PencilLine, "画笔");
        AddTool(tools, CaptureTool.Text, LucideIconKind.Type, "文字");
        AddTool(tools, CaptureTool.Number, LucideIconKind.ListOrdered, "序号");
        tools.Children.Add(Separator());
        var mosaicButton = ActionButton(
            ToolbarIcons.MosaicFrame,
            ToolbarIcons.MosaicBlocks,
            1.4,
            "马赛克",
            (_, _) => SelectTool(CaptureTool.Mosaic));
        _toolButtons[CaptureTool.Mosaic] = mosaicButton;
        tools.Children.Add(mosaicButton);
        tools.Children.Add(Separator());
        tools.Children.Add(ActionButton(LucideIconKind.Pin, "贴图到桌面", Pin));
        var translationGroup = new StackPanel { Orientation = Orientation.Horizontal };
        translationButton = ActionButton(LucideIconKind.Languages, "按当前语种翻译", TranslateWithCurrentLanguage);
        translationGroup.Children.Add(translationButton);
        translationDropDownButton = ActionButton(LucideIconKind.ChevronDown, "选择翻译语种", ToggleTranslationMenu);
        translationDropDownButton.Width = 22;
        translationDropDownButton.MinWidth = 22;
        translationDropDownButton.Margin = new Thickness(-5, 1, 1, 1);
        translationGroup.Children.Add(translationDropDownButton);
        tools.Children.Add(translationGroup);
        tools.Children.Add(ActionButton(LucideIconKind.ScanText, "提取文字", ShowOcr));
        tools.Children.Add(ActionButton(LucideIconKind.ScissorsLineDashed, "长截图", StartLongCapture, rotationDegrees: 90));
        tools.Children.Add(ActionButton(LucideIconKind.Video, "录制此区域", StartRegionRecording));
        tools.Children.Add(Separator());
        tools.Children.Add(ActionButton(LucideIconKind.Undo2, "撤销 · Ctrl+Z", (_, _) => _surface.Undo()));
        tools.Children.Add(ActionButton(LucideIconKind.Redo2, "重做 · Ctrl+Y", (_, _) => _surface.Redo()));
        tools.Children.Add(ActionButton(LucideIconKind.Download, "另存为 PNG · Ctrl+S", Save));
        tools.Children.Add(ActionButton(LucideIconKind.X, "取消截图 · Esc", (_, _) => Close(), FindResourceBrush("Brush.DangerOnDark")));
        var finishButton = ActionButton(
            LucideIconKind.Check,
            "完成并复制 · Enter",
            Finish);
        finishButton.SetResourceReference(StyleProperty, "CardPrimaryButtonStyle");
        finishButton.Width = finishButton.MinWidth = 36;
        finishButton.Height = 36;
        finishButton.Margin = new Thickness(3, 1, 1, 1);
        finishButton.Padding = new Thickness(0);
        tools.Children.Add(finishButton);
        return new Border
        {
            Background = FindResourceBrush("Brush.ChromeGradient"),
            BorderBrush = FindResourceBrush("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(5),
            Effect = FindResourceEffect("Shadow.Floating"),
            Child = tools,
            Visibility = Visibility.Collapsed
        };
    }

    private Border BuildStylePanel()
    {
        return new Border
        {
            Background = FindResourceBrush("Brush.ChromeGradient"),
            BorderBrush = FindResourceBrush("Brush.OverlayBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 6, 8, 6),
            Effect = FindResourceEffect("Shadow.Floating"),
            Visibility = Visibility.Collapsed
        };
    }

    private void ConfigureStylePanel(CaptureTool tool)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (tool == CaptureTool.Mosaic)
        {
            row.Children.Add(ToggleIcon(LucideIconKind.Grid2x2, "像素马赛克", _mosaicVariant == AnnotationVariants.MosaicPixelate, () =>
            {
                _mosaicVariant = AnnotationVariants.MosaicPixelate;
                ConfigureStylePanel(tool);
            }));
            row.Children.Add(ToggleIcon(LucideIconKind.Droplet, "模糊", _mosaicVariant == AnnotationVariants.MosaicBlur, () =>
            {
                _mosaicVariant = AnnotationVariants.MosaicBlur;
                ConfigureStylePanel(tool);
            }));
            row.Children.Add(Separator());
            row.Children.Add(ToggleIcon(LucideIconKind.Paintbrush, "涂抹笔刷", _surface.MosaicMode == MosaicDrawMode.Brush, () =>
            {
                _surface.MosaicMode = MosaicDrawMode.Brush;
                ConfigureStylePanel(tool);
            }));
            row.Children.Add(ToggleIcon(LucideIconKind.SquareDashed, "矩形区域", _surface.MosaicMode == MosaicDrawMode.Rectangle, () =>
            {
                _surface.MosaicMode = MosaicDrawMode.Rectangle;
                ConfigureStylePanel(tool);
            }));
            row.Children.Add(Separator());
            AddSizeDots(row, new[] { 20d, 32d, 48d }, _mosaicSize, 0.5, size =>
            {
                _mosaicSize = size;
                ConfigureStylePanel(tool);
            });
            row.Children.Add(new TextBlock
            {
                Text = "强度",
                Margin = new Thickness(12, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = FindResourceBrush("Brush.OverlayTextDim")
            });
            var slider = new Slider
            {
                Width = 112,
                Minimum = 0.2,
                Maximum = 1,
                Value = _mosaicIntensity,
                VerticalAlignment = VerticalAlignment.Center
            };
            slider.ValueChanged += (_, _) =>
            {
                _mosaicIntensity = slider.Value;
                UpdateStyle();
            };
            row.Children.Add(slider);
        }
        else if (tool == CaptureTool.Text)
        {
            row.Children.Add(TextWeightButton("T", FontWeights.Normal, AnnotationVariants.TextRegular, tool));
            row.Children.Add(TextWeightButton("T", FontWeights.SemiBold, AnnotationVariants.TextMedium, tool));
            row.Children.Add(TextWeightButton("T", FontWeights.Bold, AnnotationVariants.TextBold, tool));
            row.Children.Add(Separator());
            var fontSize = new ComboBox { Width = 82, Height = 30, Margin = new Thickness(4, 0, 6, 0) };
            foreach (var (label, size) in new[] { ("12pt", 16d), ("16pt", 21d), ("20pt", 27d), ("28pt", 37d) })
            {
                fontSize.Items.Add(new ComboBoxItem { Content = label, Tag = size });
            }
            // Select the current size, falling back to 20pt when the persisted
            // value is not one of the presets — the fallback must not shadow a
            // later preset (it would show 20pt while rendering 28pt text).
            fontSize.SelectedItem = fontSize.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => (double)item.Tag == _fontSize)
                ?? fontSize.Items.Cast<ComboBoxItem>().First(item => (double)item.Tag == 27d);
            fontSize.SelectionChanged += (_, _) =>
            {
                if (fontSize.SelectedItem is ComboBoxItem item)
                {
                    _fontSize = (double)item.Tag;
                    UpdateStyle();
                }
            };
            row.Children.Add(fontSize);
            AddColorSwatches(row);
        }
        else
        {
            AddSizeDots(row, new[] { 2d, 4d, 7d }, _thickness, 2, size =>
            {
                _thickness = size;
                ConfigureStylePanel(tool);
            });
            row.Children.Add(Separator());
            AddColorSwatches(row);
        }
        _stylePanel.Child = row;
        UpdateStyle();
    }

    private Popup BuildTranslationPopup()
    {
        var list = new StackPanel();
        foreach (var language in new[]
                 {
                     "简体中文", "English", "日本語", "한국어", "繁體中文",
                     "Français", "Español", "Português", "Deutsch"
                 })
        {
            var button = new Button
            {
                Content = language,
                Width = 190,
                Height = 40,
                Padding = new Thickness(14, 0, 14, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 14,
                Foreground = FindResourceBrush("Brush.OverlayText"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            // Hover feedback must be a light wash, not the default dark one.
            var template = new ControlTemplate(typeof(Button));
            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.Name = "Chrome";
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            chrome.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(14, 0, 0, 0));
            chrome.AppendChild(presenter);
            template.VisualTree = chrome;
            var hover = new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true
            };
            hover.Setters.Add(new Setter(
                Border.BackgroundProperty, FindResourceBrush("Brush.OverlayHover"), "Chrome"));
            template.Triggers.Add(hover);
            button.Template = template;
            button.Click += async (_, _) =>
            {
                _translationPopup.IsOpen = false;
                _settingsStore.Update(latest =>
                    latest.Translation = latest.Translation with { TargetLanguage = language });
                await TranslateSelectionAsync(language);
            };
            list.Children.Add(button);
        }
        var scroll = new ScrollViewer
        {
            Content = list,
            MaxHeight = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        // Scope the shared dark scrollbar to this menu — the overlay window's
        // default (light) scrollbar would glare against the navy popup.
        scroll.Resources[typeof(ScrollBar)] = Application.Current.FindResource("DarkScrollBarStyle");
        return new Popup
        {
            PlacementTarget = _translationDropDownButton,
            Placement = PlacementMode.Top,
            VerticalOffset = -7,
            AllowsTransparency = true,
            StaysOpen = false,
            Child = new Border
            {
                Background = FindResourceBrush("Brush.OverlayElevated"),
                BorderBrush = FindResourceBrush("Brush.OverlayBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(4, 8, 4, 8),
                Effect = FindResourceEffect("Shadow.Floating"),
                Child = scroll
            }
        };
    }

    private void AddTool(Panel panel, CaptureTool tool, LucideIconKind icon, string tooltip)
    {
        var button = ActionButton(icon, tooltip, (_, _) => SelectTool(tool));
        _toolButtons[tool] = button;
        panel.Children.Add(button);
    }

    private static Button ActionButton(
        LucideIconKind icon,
        string tooltip,
        RoutedEventHandler handler,
        Brush? foreground = null,
        double rotationDegrees = 0)
    {
        var iconElement = new LucideIconElement
        {
            Kind = icon,
            Width = 20,
            Height = 20,
            StrokeThickness = 1.75,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (rotationDegrees != 0)
        {
            iconElement.RenderTransformOrigin = new Point(0.5, 0.5);
            iconElement.RenderTransform = new RotateTransform(rotationDegrees);
        }
        iconElement.SetBinding(
            LucideIconElement.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Button.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor,
                    typeof(Button),
                    1)
            });
        var button = new Button
        {
            Content = iconElement,
            ToolTip = tooltip,
            Style = (Style)Application.Current.FindResource("ToolbarIconButtonDarkStyle")
        };
        if (foreground is not null)
        {
            button.Foreground = foreground;
        }
        button.Click += handler;
        return button;
    }

    private static Button ActionButton(
        Geometry strokeGeometry,
        Geometry fillGeometry,
        double strokeThickness,
        string tooltip,
        RoutedEventHandler handler,
        Brush? foreground = null)
    {
        var button = new Button
        {
            Content = ToolbarIcons.DualIcon(strokeGeometry, fillGeometry, strokeThickness),
            ToolTip = tooltip,
            Style = (Style)Application.Current.FindResource("ToolbarIconButtonDarkStyle")
        };
        if (foreground is not null)
        {
            button.Foreground = foreground;
        }
        button.Click += handler;
        return button;
    }

    private Button TextWeightButton(string text, FontWeight weight, int variant, CaptureTool tool)
    {
        var selected = _textVariant == variant;
        var button = new Button
        {
            Content = new TextBlock { Text = text, FontSize = 20, FontWeight = weight },
            Width = 38,
            MinWidth = 38,
            Height = 34,
            Background = selected ? FindResourceBrush("Brush.AccentSubtleDark") : Brushes.Transparent,
            Foreground = selected ? FindResourceBrush("Brush.AccentOnDark") : FindResourceBrush("Brush.OverlayIcon")
        };
        button.Click += (_, _) =>
        {
            _textVariant = variant;
            ConfigureStylePanel(tool);
        };
        return button;
    }

    private Button ToggleIcon(LucideIconKind icon, string tooltip, bool selected, Action action)
    {
        var button = ActionButton(icon, tooltip, (_, _) => action());
        button.Background = selected ? FindResourceBrush("Brush.AccentSubtleDark") : Brushes.Transparent;
        button.Foreground = selected ? FindResourceBrush("Brush.AccentOnDark") : FindResourceBrush("Brush.OverlayIcon");
        return button;
    }

    private static void AddSizeDots(
        Panel row,
        IReadOnlyList<double> sizes,
        double selected,
        double sampleScale,
        Action<double> changed)
    {
        foreach (var size in sizes)
        {
            var diameter = Math.Clamp(size * sampleScale, 4, 28);
            var isSelected = Math.Abs(size - selected) < 0.1;
            var button = new Button
            {
                Content = new Grid
                {
                    Width = 30,
                    Height = 30,
                    Children =
                    {
                        new System.Windows.Shapes.Ellipse
                        {
                            Width = diameter,
                            Height = diameter,
                            Fill = isSelected
                                ? FindResourceBrush("Brush.AccentOnDark")
                                : FindResourceBrush("Brush.Neutral500"),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                },
                Width = 38,
                MinWidth = 38,
                Height = 38,
                Padding = new Thickness(0),
                Background = isSelected ? FindResourceBrush("Brush.AccentSubtleDark") : Brushes.Transparent,
                ToolTip = $"实际大小 {size:0}px"
            };
            button.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
            button.Click += (_, _) => changed(size);
            row.Children.Add(button);
        }
    }

    private void AddColorSwatches(Panel row)
    {
        foreach (var (color, name) in new[]
                 {
                     (0xFFFF4D4Fu, "红"), (0xFFFFB51Bu, "黄"), (0xFF31C56Au, "绿"),
                     (0xFF2563EBu, "蓝"), (0xFF101216u, "黑"), (0xFF9299A6u, "灰"), (0xFFFFFFFFu, "白")
                 })
        {
            var selected = color == _color;
            var button = new Button { Width = 34, MinWidth = 34, Height = 34, ToolTip = name, Padding = new Thickness(0) };
            button.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
            button.Content = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color)),
                BorderBrush = selected
                    ? FindResourceBrush("Brush.AccentOnDark")
                    : FindResourceBrush(color == 0xFFFFFFFFu ? "Brush.Neutral500" : "Brush.OverlayBorder"),
                BorderThickness = new Thickness(selected ? 2 : 1)
            };
            button.Click += (_, _) =>
            {
                _color = color;
                ConfigureStylePanel(_surface.Tool);
            };
            row.Children.Add(button);
        }
    }

    /// <summary>
    /// Recording setup card uses the shared navy and moon-blue palette,
    /// with a light primary action and quiet format and audio options.
    /// </summary>
    private Border BuildRecordingPanel()
    {
        var recordingDefaults = _settingsStore.Load().Recording;
        _showCursorChoice = recordingDefaults.ShowCursor;
        _recordSystemAudioChoice = recordingDefaults.RecordSystemAudio;
        _recordMicrophoneChoice = recordingDefaults.RecordMicrophone;

        var startButton = new Button
        {
            Content = "开始录制",
            Height = 42,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindResourceBrush("Brush.OnAccent"),
            Padding = new Thickness(0)
        };
        startButton.SetResourceReference(StyleProperty, "CardPrimaryButtonStyle");
        startButton.Click += (_, _) => BeginRecordingFromPanel();

        var formatRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        };
        formatRow.Children.Add(RecordingFormatChip("MP4", isSelected: true, isEnabled: true, "录制为 MP4 视频"));
        formatRow.Children.Add(RecordingFormatChip("GIF", isSelected: false, isEnabled: false, "录后可在编辑窗导出 GIF"));

        var iconRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _speakerToggleIcon = RecordingToggleIcon(
            _recordSystemAudioChoice ? LucideIconKind.Volume2 : LucideIconKind.VolumeX,
            _recordSystemAudioChoice);
        var speakerButton = RecordingOptionButton(
            _speakerToggleIcon,
            "扬声器",
            _recordSystemAudioChoice ? "已开启：录制系统声音" : "已关闭：不录制系统声音",
            enabled: true);
        StyleRecordingToggle(_speakerToggleIcon, _recordSystemAudioChoice, speakerButton);
        speakerButton.Click += (_, _) =>
        {
            _recordSystemAudioChoice = !_recordSystemAudioChoice;
            speakerButton.ToolTip = _recordSystemAudioChoice ? "已开启：录制系统声音" : "已关闭：不录制系统声音";
            _speakerToggleIcon.Kind = _recordSystemAudioChoice ? LucideIconKind.Volume2 : LucideIconKind.VolumeX;
            StyleRecordingToggle(_speakerToggleIcon, _recordSystemAudioChoice, speakerButton);
        };
        iconRow.Children.Add(speakerButton);

        _micToggleIcon = RecordingToggleIcon(LucideIconKind.Mic, _recordMicrophoneChoice);
        var micButton = RecordingOptionButton(
            _micToggleIcon,
            "麦克风",
            _recordMicrophoneChoice ? "已开启：录制麦克风" : "已关闭：不录制麦克风",
            enabled: true);
        StyleRecordingToggle(_micToggleIcon, _recordMicrophoneChoice, micButton);
        micButton.Click += (_, _) =>
        {
            _recordMicrophoneChoice = !_recordMicrophoneChoice;
            micButton.ToolTip = _recordMicrophoneChoice ? "已开启：录制麦克风" : "已关闭：不录制麦克风";
            StyleRecordingToggle(_micToggleIcon, _recordMicrophoneChoice, micButton);
        };
        iconRow.Children.Add(micButton);

        iconRow.Children.Add(RecordingOptionButton(
            RecordingToggleIcon(LucideIconKind.Video, enabled: false),
            "摄像头",
            "摄像头画中画即将推出",
            enabled: false));

        _cursorToggleIcon = RecordingToggleIcon(LucideIconKind.MousePointer2, _showCursorChoice);
        var cursorButton = RecordingOptionButton(
            _cursorToggleIcon,
            "鼠标设置",
            _showCursorChoice ? "已开启：录制鼠标光标与点击波纹" : "已关闭：不录制鼠标光标与点击波纹",
            enabled: true);
        StyleRecordingToggle(_cursorToggleIcon, _showCursorChoice, cursorButton);
        cursorButton.Click += (_, _) =>
        {
            _showCursorChoice = !_showCursorChoice;
            cursorButton.ToolTip = _showCursorChoice ? "已开启：录制鼠标光标与点击波纹" : "已关闭：不录制鼠标光标与点击波纹";
            StyleRecordingToggle(_cursorToggleIcon, _showCursorChoice, cursorButton);
        };
        iconRow.Children.Add(cursorButton);

        var content = new StackPanel { Width = 276, Margin = new Thickness(20, 18, 20, 14) };
        content.Children.Add(startButton);
        content.Children.Add(formatRow);
        content.Children.Add(new Border
        {
            Height = 1,
            Background = FindResourceBrush("Brush.Border"),
            Margin = new Thickness(0, 14, 0, 2)
        });
        content.Children.Add(iconRow);

        return new Border
        {
            Background = FindResourceBrush("Brush.ChromeGradient"),
            CornerRadius = new CornerRadius(12),
            BorderBrush = FindResourceBrush("Brush.Border"),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                Color = Colors.Black,
                Opacity = 0.4,
                ShadowDepth = 6
            },
            Child = content
        };
    }

    private static void StyleRecordingToggle(LucideIconElement icon, bool enabled, Button button)
    {
        icon.Stroke = enabled
            ? FindResourceBrush("Brush.Accent")
            : FindResourceBrush("Brush.TextTertiary");
        icon.Opacity = 1;
        button.Background = enabled ? FindResourceBrush("Brush.AccentSubtleDark") : Brushes.Transparent;
        button.BorderBrush = enabled ? FindResourceBrush("Brush.Accent") : Brushes.Transparent;
        button.BorderThickness = new Thickness(1);
    }

    private static LucideIconElement RecordingToggleIcon(LucideIconKind kind, bool enabled) => new()
    {
        Kind = kind,
        Width = 20,
        Height = 20,
        StrokeThickness = 1.7,
        Stroke = enabled
            ? FindResourceBrush("Brush.Accent")
            : FindResourceBrush("Brush.TextTertiary"),
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private static Button RecordingOptionButton(LucideIconElement icon, string label, string toolTip, bool enabled)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = FindResourceBrush("Brush.TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        };
        var button = new Button
        {
            Content = new StackPanel { Children = { icon, text } },
            Width = 61,
            Height = 54,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            ToolTip = toolTip,
            IsEnabled = enabled,
            Opacity = enabled ? 1 : 0.55
        };
        button.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
        return button;
    }

    private static Border RecordingFormatChip(string label, bool isSelected, bool isEnabled, string toolTip)
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = isSelected
                ? FindResourceBrush("Brush.Accent")
                : Brushes.Transparent,
            Stroke = isSelected
                ? null
                : FindResourceBrush("Brush.TextTertiary"),
            StrokeThickness = 1.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = isSelected
                ? FindResourceBrush("Brush.AccentText")
                : FindResourceBrush("Brush.TextTertiary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 14, 6),
            Margin = new Thickness(0, 0, 10, 0),
            Background = isSelected
                ? FindResourceBrush("Brush.AccentSubtleDark")
                : FindResourceBrush("Brush.SurfaceRaised"),
            BorderBrush = isSelected
                ? FindResourceBrush("Brush.Accent")
                : FindResourceBrush("Brush.Border"),
            BorderThickness = new Thickness(1),
            ToolTip = toolTip,
            Opacity = isEnabled ? 1 : 0.6,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text } }
        };
    }

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 22,
        Background = FindResourceBrush("Brush.OverlayBorder"),
        Margin = new Thickness(7)
    };
}
