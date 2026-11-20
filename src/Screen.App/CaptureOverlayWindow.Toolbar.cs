using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Lucide.NET;

namespace Screen.App;

internal sealed partial class CaptureOverlayWindow
{
    private Button _undoButton = null!, _redoButton = null!, _ocrButton = null!;
    private readonly Border _toolIndicator = new() { HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
    private readonly Grid _drawTrack = new();
    private CaptureTool? _indicatedTool;
    private bool _syncingTool;
    private TextBlock? _nextNumberText;

    private WrapPanel BuildToolbar(out Button translationButton, out Button translationDropDownButton)
    {
        var toolbar = new WrapPanel { Visibility = Visibility.Collapsed };
        var draw = HorizontalRow();
        _toolIndicator.SetResourceReference(StyleProperty, "CaptureToolIndicatorStyle");
        _drawTrack.Children.Add(_toolIndicator);
        _drawTrack.Children.Add(draw);
        toolbar.Children.Add(ToolbarGroup(_drawTrack));

        AddTool(draw, CaptureTool.Select, LucideIconKind.MousePointer2, "选择");
        AddTool(draw, CaptureTool.Rectangle, LucideIconKind.Square, "矩形");
        AddTool(draw, CaptureTool.Ellipse, LucideIconKind.Circle, "椭圆");
        AddTool(draw, CaptureTool.Line, LucideIconKind.Minus, "直线");
        AddTool(draw, CaptureTool.Arrow, LucideIconKind.MoveUpRight, "箭头");
        AddTool(draw, CaptureTool.Pen, LucideIconKind.PencilLine, "画笔");
        AddTool(draw, CaptureTool.Text, LucideIconKind.Type, "文字");
        AddTool(draw, CaptureTool.Number, LucideIconKind.Circle, "序号");
        AddTool(draw, CaptureTool.Mosaic, LucideIconKind.Grid2x2, "马赛克");
        _drawTrack.SizeChanged += (_, _) => UpdateToolIndicator(false);

        var smart = HorizontalRow();
        toolbar.Children.Add(ToolbarGroup(smart));
        smart.Children.Add(HudAction(LucideIconKind.Pin, "贴到桌面", Pin));
        translationButton = HudAction(LucideIconKind.Languages, "翻译", TranslateWithCurrentLanguage);
        smart.Children.Add(translationButton);
        translationDropDownButton = HudAction(LucideIconKind.ChevronDown, "翻译成…", ToggleTranslationMenu);
        translationDropDownButton.Width = 16;
        translationDropDownButton.Margin = new Thickness(-5, 0, 0, 0);
        ((LucideIconElement)translationDropDownButton.Content).Width = 12;
        smart.Children.Add(translationDropDownButton);
        smart.Children.Add(_ocrButton = HudAction(LucideIconKind.ScanText, "提取文字", ShowOcr));
        smart.Children.Add(HudAction(LucideIconKind.ScissorsLineDashed, "长截图", StartLongCapture, 90));
        smart.Children.Add(HudAction(LucideIconKind.Video, "录制此区域", StartRegionRecording));

        var history = HorizontalRow();
        toolbar.Children.Add(ToolbarGroup(history));
        history.Children.Add(_undoButton = HudAction(LucideIconKind.Undo2, "撤销　Ctrl+Z", (_, _) => _surface.Undo()));
        history.Children.Add(_redoButton = HudAction(LucideIconKind.Redo2, "重做　Ctrl+Y", (_, _) => _surface.Redo()));
        history.Children.Add(HudAction(LucideIconKind.Download, "存储　Ctrl+S", Save));
        history.Children.Add(HudAction(LucideIconKind.X, "取消　Esc", (_, _) => Close()));

        var finish = new Button { ToolTip = "拷贝到剪贴板", Padding = new Thickness(12, 0, 12, 0) };
        finish.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        AutomationProperties.SetName(finish, "完成：拷贝到剪贴板");
        var label = HorizontalRow();
        label.Children.Add(new TextBlock { Text = "完成", VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = "↵", FontSize = 11, Margin = new Thickness(5, 0, 0, 0), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        finish.Content = label;
        finish.Click += Finish;
        toolbar.Children.Add(ToolbarGroup(finish, last: true));
        return toolbar;
    }

    private static StackPanel HorizontalRow() => new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

    private static GlassPanel ToolbarGroup(FrameworkElement content, bool last = false)
    {
        var panel = new GlassPanel { Content = content, Padding = new Thickness(4), Margin = new Thickness(0, 0, last ? 0 : 8, 0) };
        panel.SetResourceReference(GlassPanel.CornerRadiusProperty, "Radius.Bar");
        return panel;
    }

    private static LucideIconElement HudIcon(LucideIconKind kind, double rotation = 0)
    {
        var icon = new LucideIconElement { Kind = kind, Width = 18, Height = 18, StrokeThickness = 1.7 };
        icon.SetBinding(LucideIconElement.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Control), 1)
        });
        if (rotation != 0)
        {
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = new RotateTransform(rotation);
        }
        return icon;
    }

    private static Button HudAction(LucideIconKind kind, string label, RoutedEventHandler handler, double rotation = 0)
    {
        var button = new Button { Content = HudIcon(kind, rotation), ToolTip = label };
        AutomationProperties.SetName(button, label);
        button.SetResourceReference(StyleProperty, "HudIconButtonStyle");
        button.Click += handler;
        return button;
    }

    private void AddTool(Panel row, CaptureTool tool, LucideIconKind kind, string label)
    {
        var button = new RadioButton { ToolTip = label, GroupName = "DrawingTools" };
        AutomationProperties.SetName(button, label);
        button.SetResourceReference(StyleProperty, "HudToolButtonStyle");
        var icon = HudIcon(kind, tool == CaptureTool.Line ? -45 : 0);
        if (tool is CaptureTool.Select or CaptureTool.Rectangle or CaptureTool.Ellipse or CaptureTool.Number)
            icon.SetResourceReference(StyleProperty, "CaptureClosedIconStyle");

        if (tool == CaptureTool.Number)
        {
            var number = new Grid();
            number.Children.Add(icon);
            number.Children.Add(new TextBlock { Text = "1", FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            button.Content = number;
        }
        else if (tool == CaptureTool.Mosaic)
        {
            var mosaic = ToolbarIcons.DualIcon(ToolbarIcons.MosaicFrame, ToolbarIcons.MosaicBlocks, 1.4);
            mosaic.Width = mosaic.Height = 18;
            button.Content = mosaic;
        }
        else
            button.Content = icon;

        button.Checked += (_, _) =>
        {
            if (!_syncingTool)
                SelectTool(tool);
        };
        // A second click on the current tool still dismisses cards or deselects an
        // annotation, just as it did when these controls were plain buttons.
        button.Click += (_, _) =>
        {
            if (_surface.SelectedAnnotationKind.HasValue || _ocrCard.Visibility == Visibility.Visible
                || _translationCard.Visibility == Visibility.Visible || _inPlaceStatusBar.Visibility == Visibility.Visible)
                SelectTool(tool);
        };
        _toolButtons.Add(tool, button);
        row.Children.Add(button);
    }

    private void UpdateToolIndicator(bool animate)
    {
        if (!_toolButtons.TryGetValue(_surface.Tool, out var selected))
            return;
        _syncingTool = true;
        try
        { selected.IsChecked = true; }
        finally { _syncingTool = false; }
        if (selected.ActualWidth <= 0 || (!_drawTrack.IsAncestorOf(selected)))
            return;
        if (_indicatedTool == _surface.Tool && animate)
            return;
        var x = selected.TranslatePoint(new Point(), _drawTrack).X;
        Motion.SlideTo(_toolIndicator, x, selected.ActualWidth, animate && _indicatedTool.HasValue);
        _indicatedTool = _surface.Tool;
    }

    private void UpdateToolbarState()
    {
        _undoButton.IsEnabled = _surface.CanUndo;
        _redoButton.IsEnabled = _surface.CanRedo;
        HudActiveButton.SetIsActive(_ocrButton, _ocrCard.Visibility == Visibility.Visible);
        HudActiveButton.SetIsActive(_translationButton, _translationCard.Visibility == Visibility.Visible || _inPlaceStatusBar.Visibility == Visibility.Visible);
        if (_nextNumberText is not null)
            _nextNumberText.Text = _surface.NextNumber.ToString();
    }

    private ContextMenu BuildTranslationPopup()
    {
        var menu = new ContextMenu { PlacementTarget = _translationDropDownButton, Placement = PlacementMode.Top, VerticalOffset = -7 };
        menu.SetResourceReference(StyleProperty, "GlassMenuStyle");
        menu.Items.Add(new MenuItem { Header = "翻译成", IsEnabled = false });
        foreach (var language in new[] { "简体中文", "English", "日本語", "한국어", "繁體中文", "Français", "Español", "Português", "Deutsch" })
        {
            var item = new MenuItem { Header = language };
            item.SetResourceReference(StyleProperty, "CaptureLanguageItemStyle");
            item.Click += async (_, _) =>
            {
                menu.IsOpen = false;
                try
                {
                    _settingsStore.Update(latest => latest.Translation = latest.Translation with { TargetLanguage = language });
                    await TranslateSelectionAsync(language);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"翻译失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) =>
        {
            var current = _settingsStore.Load().Translation.TargetLanguage;
            foreach (var item in menu.Items.OfType<MenuItem>().Skip(1))
                item.IsChecked = Equals(item.Header, current);
            Motion.PopIn(menu, menu: true);
        };
        return menu;
    }
}
