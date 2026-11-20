using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureOverlayWindow
{
    private static GlassPanel BuildStylePanel()
    {
        var panel = new GlassPanel { Visibility = Visibility.Collapsed, Padding = new Thickness(6, 4, 6, 4) };
        panel.SetResourceReference(GlassPanel.CornerRadiusProperty, "Radius.Bar");
        return panel;
    }

    private void ConfigureStylePanel(CaptureTool tool, bool applyToSelection = false)
    {
        _nextNumberText = null;
        if (tool == CaptureTool.Select)
        {
            _stylePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var row = HorizontalRow();
        if (tool == CaptureTool.Mosaic)
            AddMosaicOptions(row);
        else
        {
            if (tool == CaptureTool.Text)
                AddTextOptions(row);
            else if (tool == CaptureTool.Number)
            {
                _nextNumberText = new TextBlock { Text = _surface.NextNumber.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 12, FontWeight = FontWeights.SemiBold };
                var badge = new Grid { Width = 24, Height = 24 };
                var marker = new Ellipse { Fill = CurrentAnnotationBrush() };
                badge.Children.Add(marker);
                _nextNumberText.SetResourceReference(TextBlock.ForegroundProperty, _color == AnnotationArgb("White") || _color == AnnotationArgb("Yellow") ? "Brush.Black" : "Brush.White");
                badge.Children.Add(_nextNumberText);
                row.Children.Add(badge);
                row.Children.Add(Caption("下一个"));
                AddSizes(row, [24, 32, 44], _numberSize, showLine: false, size =>
                {
                    _numberSize = size;
                    ConfigureStylePanel(tool, true);
                });
            }
            else
                AddSizes(row, [2, 4, 7], _thickness, showLine: true, size =>
                {
                    _thickness = size;
                    ConfigureStylePanel(tool, true);
                });
            row.Children.Add(StyleDivider());
            AddColorSwatches(row, tool);
        }
        _stylePanel.Content = row;
        UpdateStyle(applyToSelection);
    }

    private static TextBlock Caption(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.LabelSecondary");
        return label;
    }

    private static Border StyleDivider() => new()
    {
        Width = 1,
        Height = 18,
        Background = FindResourceBrush("Brush.Separator"),
        Margin = new Thickness(4, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private void AddMosaicOptions(Panel row)
    {
        FrameworkElement Sample(string key, string name)
        {
            var label = HorizontalRow();
            var preview = new Border { Width = 20, Height = 14, Margin = new Thickness(0, 0, 4, 0), Background = FindResourceBrush(key) };
            preview.SetResourceReference(Border.CornerRadiusProperty, "Radius.Key");
            label.Children.Add(preview);
            label.Children.Add(new TextBlock { Text = name });
            return label;
        }

        var variant = new SegmentedControl { ItemsSource = new[] { Sample("Capture.PixelSample", "像素"), Sample("Capture.BlurSample", "模糊") }, SelectedIndex = _mosaicVariant == AnnotationVariants.MosaicBlur ? 1 : 0 };
        AutomationProperties.SetName(variant, "马赛克效果");
        variant.SelectionChanged += (_, _) =>
        {
            _mosaicVariant = variant.SelectedIndex == 0 ? AnnotationVariants.MosaicPixelate : AnnotationVariants.MosaicBlur;
            UpdateStyle(true);
        };
        row.Children.Add(variant);
        row.Children.Add(StyleDivider());
        var mode = new SegmentedControl { ItemsSource = new[] { "涂抹", "矩形" }, SelectedIndex = _surface.MosaicMode == MosaicDrawMode.Brush ? 0 : 1 };
        AutomationProperties.SetName(mode, "马赛克绘制方式");
        mode.SelectionChanged += (_, _) =>
        {
            _surface.ClearAnnotationSelection();
            _surface.MosaicMode = mode.SelectedIndex == 0 ? MosaicDrawMode.Brush : MosaicDrawMode.Rectangle;
            UpdateStyle();
        };
        row.Children.Add(mode);
        row.Children.Add(StyleDivider());
        AddSizes(row, [20, 32, 48], _mosaicSize, showLine: false, size =>
        {
            _mosaicSize = size;
            ConfigureStylePanel(CaptureTool.Mosaic, true);
        });
        row.Children.Add(Caption("强度"));
        var slider = new Slider { Minimum = 0.2, Maximum = 1, Value = _mosaicIntensity, VerticalAlignment = VerticalAlignment.Center };
        slider.SetResourceReference(StyleProperty, "CaptureIntensitySlider");
        AutomationProperties.SetName(slider, "马赛克强度");
        slider.ValueChanged += (_, _) =>
        {
            _mosaicIntensity = slider.Value;
            UpdateStyle(true);
        };
        row.Children.Add(slider);
    }

    private void AddTextOptions(Panel row)
    {
        foreach (var (label, weight, variant) in new[]
        {
            ("常规", FontWeights.Normal, AnnotationVariants.TextRegular),
            ("中等", FontWeights.SemiBold, AnnotationVariants.TextMedium),
            ("粗体", FontWeights.Bold, AnnotationVariants.TextBold)
        })
        {
            row.Children.Add(StyleOption(new TextBlock { Text = "T", FontWeight = weight, FontSize = 15 }, label, _textVariant == variant, () =>
            {
                _textVariant = variant;
                ConfigureStylePanel(CaptureTool.Text, true);
            }));
        }
        row.Children.Add(StyleDivider());
        foreach (var (pt, size, preview) in new[] { (12, 16d, 11), (16, 21d, 14), (20, 27d, 16), (28, 37d, 18) })
        {
            row.Children.Add(StyleOption(new TextBlock { Text = "A", FontSize = preview }, $"{pt} pt", Math.Abs(_fontSize - size) < 0.1, () =>
            {
                _fontSize = size;
                ConfigureStylePanel(CaptureTool.Text, true);
            }));
        }
    }

    private static Button StyleOption(FrameworkElement content, string title, bool selected, Action action)
    {
        var button = new Button { Content = content, ToolTip = title };
        button.SetResourceReference(StyleProperty, "HudIconButtonStyle");
        if (selected)
            button.SetResourceReference(BackgroundProperty, "Brush.SelectedGradient");
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => action();
        return button;
    }

    private void AddSizes(Panel row, double[] sizes, double current, bool showLine, Action<double> change)
    {
        var brush = showLine ? CurrentAnnotationBrush() : FindResourceBrush("Brush.Label");
        for (var i = 0; i < sizes.Length; i++)
        {
            var size = sizes[i];
            FrameworkElement preview = showLine
                ? new Line { X1 = 1, X2 = 16, Y1 = 4, Y2 = 4, Stroke = brush, StrokeThickness = i switch { 0 => 1.5, 1 => 3, _ => 5 }, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }
                : new Ellipse { Width = 4 + 4 * i, Height = 4 + 4 * i, Fill = brush };
            row.Children.Add(StyleOption(preview, $"{size:0} px", Math.Abs(current - size) < 0.1, () => change(size)));
        }
    }

    private Brush CurrentAnnotationBrush()
    {
        var argb = _color;
        return new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
    }

    private static uint AnnotationArgb(string token)
    {
        var c = ((SolidColorBrush)FindResourceBrush($"Annotation.{token}")).Color;
        return ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    }

    private void AddColorSwatches(Panel row, CaptureTool tool)
    {
        foreach (var (token, name) in new[] { ("Red", "红"), ("Yellow", "黄"), ("Green", "绿"), ("Blue", "蓝"), ("Black", "黑"), ("Gray", "灰"), ("White", "白") })
        {
            var argb = AnnotationArgb(token);
            var swatch = new Grid { Width = 32, Height = 32 };
            swatch.Children.Add(new Ellipse { Width = 20, Height = 20, Fill = FindResourceBrush($"Annotation.{token}"), Stroke = FindResourceBrush("Brush.GlassRim"), StrokeThickness = 1 });
            if (_color == argb)
                swatch.Children.Add(new Ellipse { Width = 30, Height = 30, Stroke = FindResourceBrush("Brush.White"), StrokeThickness = 2 });
            var button = StyleOption(swatch, name, false, () =>
            {
                _color = argb;
                ConfigureStylePanel(tool, true);
                if (_stylePanel.Content is StackPanel updated)
                {
                    var selected = updated.Children.OfType<Button>().FirstOrDefault(b => Equals(b.ToolTip, name));
                    if (selected?.Content is FrameworkElement element)
                        Motion.PopSwatch(element);
                }
            });
            row.Children.Add(button);
        }
    }
}
