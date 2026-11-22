using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureOverlayWindow
{
    private readonly List<Action> _styleRefresh = [];
    private CaptureTool? _styleGroup;
    private long? _toolbarEntryTimestamp;
    private double _styleToolCenterX = double.NaN;

    private SquircleGlassBorder BuildStylePanel()
    {
        var panel = new SquircleGlassBorder { Padding = new Thickness(4), Visibility = Visibility.Collapsed };
        panel.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                panel.LayoutUpdated += AlignStylePointer;
                if (_toolbarEntryTimestamp is long started)
                {
                    int delay = (int)Math.Max(0, 70 - Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    GlassMotion.Enter(panel, 9, 0.94, 280, delay);
                    _toolbarEntryTimestamp = null;
                }
            }
            else
                panel.LayoutUpdated -= AlignStylePointer;
        };
        return panel;
    }

    private void AlignStylePointer(object? sender, EventArgs e) =>
        _stylePanel.SetTopPointer(_styleToolCenterX - Canvas.GetLeft(_stylePanel));

    private void ConfigureStylePanel(CaptureTool tool, bool applyToSelection = false)
    {
        var group = tool switch
        {
            CaptureTool.Text or CaptureTool.Number or CaptureTool.Mosaic => tool,
            _ => CaptureTool.Pen
        };
        if (_styleGroup != group)
        {
            _styleGroup = group;
            _styleRefresh.Clear();
            var row = new WrapPanel();
            switch (group)
            {
                case CaptureTool.Text:
                    AddChoices(row, [("小", 16d), ("中", 21d), ("大", 27d)], () => _fontSize, value => _fontSize = value,
                        "字号（右键可选特大）", [("特大 · 28pt", 37d)]);
                    row.Children.Add(Separator());
                    AddChoices(row, [("常规", AnnotationVariants.TextRegular), ("粗体", AnnotationVariants.TextBold)],
                        () => _textVariant, value => _textVariant = value, "字重（右键可选中等）", [("中等", AnnotationVariants.TextMedium)]);
                    break;
                case CaptureTool.Mosaic:
                    AddChoices(row, [("像素", AnnotationVariants.MosaicPixelate), ("模糊", AnnotationVariants.MosaicBlur)],
                        () => _mosaicVariant, value => _mosaicVariant = value, "马赛克类型");
                    row.Children.Add(Separator());
                    AddMosaicOptions(row);
                    break;
                case CaptureTool.Number:
                    AddDots(row, [24, 32, 44], () => _numberSize, value => _numberSize = value, "序号大小");
                    break;
                default:
                    AddDots(row, [2, 4, 7], () => _thickness, value => _thickness = value, "线条粗细");
                    break;
            }
            if (group != CaptureTool.Mosaic)
            {
                row.Children.Add(Separator());
                AddPalette(row);
            }
            _stylePanel.Child = row;
            GlassMotion.Enter(row, 5, 1, 200);
        }
        foreach (var refresh in _styleRefresh)
        {
            refresh();
        }
        UpdateStyle(applyToSelection);
    }

    private void CommitStyleChoice()
    {
        foreach (var refresh in _styleRefresh)
        {
            refresh();
        }
        UpdateStyle(applyToSelection: true);
    }

    private static Button StyleChoice(object content, string label, double width)
    {
        var button = new Button
        {
            Content = content,
            ToolTip = label,
            Width = width,
            MinWidth = width,
            Height = 28,
            FontSize = 12,
            Padding = new Thickness(0)
        };
        button.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
        AutomationProperties.SetName(button, label);
        return button;
    }

    private void AddChoices<T>(Panel row, (string Label, T Value)[] choices, Func<T> current, Action<T> set,
        string label, (string Label, T Value)[]? extra = null) where T : IEquatable<T>
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var track = new SpringTrack(bar, 4.5);
        row.Children.Add(new SquircleGlassBorder
        {
            CornerRadiusRadius = 6.5,
            ShowRim = false,
            ShadowLevel = 0,
            GlassBackground = FindResourceBrush("Brush.SurfaceSunken"),
            Padding = new Thickness(2),
            Child = track
        });
        var buttons = new Dictionary<T, Button>();
        foreach (var (text, value) in choices)
        {
            var button = StyleChoice(text, label + " · " + text, text.Length == 1 ? 30 : 39);
            button.Click += (_, _) => { set(value); CommitStyleChoice(); };
            if (extra != null)
            {
                var menu = new ContextMenu();
                foreach (var (option, alternate) in extra)
                {
                    var item = new MenuItem { Header = option };
                    item.Click += (_, _) => { set(alternate); CommitStyleChoice(); };
                    menu.Items.Add(item);
                }
                button.ContextMenu = menu;
            }
            bar.Children.Add(button);
            buttons.Add(value, button);
        }
        _styleRefresh.Add(() =>
        {
            var selected = current();
            track.Select(buttons.GetValueOrDefault(selected));
            foreach (var (value, button) in buttons)
            {
                button.Foreground = FindResourceBrush(value.Equals(selected) ? "Brush.Accent" : "Brush.TextSecondary");
            }
        });
    }

    private void AddDots(Panel row, double[] values, Func<double> current, Action<double> set, string label)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var track = new SpringTrack(bar, 6);
        var buttons = new List<Button>();
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            var dot = new Ellipse { Width = 4 + 3 * i, Height = 4 + 3 * i, Fill = FindResourceBrush("Brush.TextPrimary") };
            var button = StyleChoice(dot, $"{label} · {value}", 30);
            button.Click += (_, _) => { set(value); CommitStyleChoice(); GlassMotion.Pulse(dot); };
            buttons.Add(button);
            bar.Children.Add(button);
        }
        row.Children.Add(track);
        _styleRefresh.Add(() => track.Select(buttons.ElementAtOrDefault(Array.IndexOf(values, current()))));
    }

    private void AddPalette(Panel row)
    {
        uint[] palette = [0xFFFF3B30, 0xFFFFCC00, 0xFF34C759, 0xFF007AFF, 0xFF1C1C1E, 0xFF8E8E93, 0xFFFFFFFF];
        string[] names = ["红", "黄", "绿", "蓝", "黑", "灰", "白"];
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var track = new SpringTrack(bar, 12, colorRing: true);
        var buttons = new Dictionary<uint, Button>();
        for (var i = 0; i < palette.Length; i++)
        {
            var value = palette[i];
            var dot = new Ellipse
            {
                Width = 16,
                Height = 16,
                StrokeThickness = 0.6,
                Stroke = FindResourceBrush("Brush.Border"),
                Fill = new SolidColorBrush(Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value))
            };
            var button = StyleChoice(dot, names[i] + "色", 26);
            button.Click += (_, _) => { _color = value; CommitStyleChoice(); };
            buttons.Add(value, button);
            bar.Children.Add(button);
        }
        row.Children.Add(track);
        _styleRefresh.Add(() => track.Select(buttons.GetValueOrDefault(_color)));
    }

    private void AddMosaicOptions(Panel row)
    {
        var settings = StyleChoice("笔刷 ▾", "马赛克绘制方式与尺寸", 58);
        var menu = settings.ContextMenu = new ContextMenu { PlacementTarget = settings };
        foreach (var (text, mode) in new[] { ("涂抹笔刷", MosaicDrawMode.Brush), ("矩形区域", MosaicDrawMode.Rectangle) })
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) =>
            {
                _surface.ClearAnnotationSelection();
                _surface.MosaicMode = mode;
                CommitStyleChoice();
            };
            _styleRefresh.Add(() => item.IsChecked = _surface.MosaicMode == mode);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        foreach (var size in new[] { 20d, 32d, 48d })
        {
            var item = new MenuItem { Header = $"尺寸 · {size}" };
            item.Click += (_, _) => { _mosaicSize = size; CommitStyleChoice(); };
            _styleRefresh.Add(() => item.IsChecked = _mosaicSize == size);
            menu.Items.Add(item);
        }
        _styleRefresh.Add(() => settings.Content = _surface.MosaicMode == MosaicDrawMode.Brush ? "笔刷 ▾" : "矩形 ▾");
        settings.Click += (_, _) => menu.IsOpen = true;
        row.Children.Add(settings);
        var slider = new Slider { Width = 84, Minimum = 0.2, Maximum = 1, Value = _mosaicIntensity, Margin = new Thickness(6, 0, 5, 0), ToolTip = "马赛克强度" };
        AutomationProperties.SetName(slider, "马赛克强度");
        slider.ValueChanged += (_, _) =>
        {
            if (slider.Value != _mosaicIntensity)
            {
                _mosaicIntensity = slider.Value;
                UpdateStyle(applyToSelection: true);
            }
        };
        _styleRefresh.Add(() => slider.Value = _mosaicIntensity);
        row.Children.Add(slider);
    }
}
