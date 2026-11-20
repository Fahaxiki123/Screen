using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureOverlayWindow
{
    private readonly GlassPanel _sizeTag = new();
    private readonly GlassPanel _sampleTag = new();
    private readonly TextBlock _sizeText = TagText();
    private readonly TextBlock _sampleCoordinates = TagText();
    private readonly TextBlock _sampleValue = TagText();
    private readonly TextBlock _sampleZoom = TagText();
    private readonly TextBlock _sampleHint = TagText();
    private readonly Ellipse _sampleChip = new() { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
    private bool _adornmentsQueued;
    private uint? _lastSampleColor;

    private static TextBlock TagText()
    {
        var text = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Label");
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        return text;
    }

    private void InitializeSurfaceAdornments()
    {
        _sizeTag.Content = _sizeText;
        _sizeTag.Padding = new Thickness(8, 3, 8, 3);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(_sampleCoordinates);
        var divider = new Border { Width = 1, Height = 11, Margin = new Thickness(6, 0, 6, 0) };
        divider.SetResourceReference(Border.BackgroundProperty, "Brush.LabelTertiary");
        row.Children.Add(divider);
        _sampleChip.SetResourceReference(Shape.StrokeProperty, "Brush.EdgeHighlight");
        row.Children.Add(_sampleChip);
        _sampleValue.Margin = new Thickness(5, 0, 6, 0);
        row.Children.Add(_sampleValue);
        _sampleZoom.SetResourceReference(TextBlock.ForegroundProperty, "Brush.LabelSecondary");
        row.Children.Add(_sampleZoom);
        var detail = new StackPanel();
        detail.Children.Add(row);
        _sampleHint.HorizontalAlignment = HorizontalAlignment.Center;
        _sampleHint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.LabelSecondary");
        detail.Children.Add(_sampleHint);
        _sampleTag.Content = detail;
        _sampleTag.Padding = new Thickness(8, 3, 8, 3);

        foreach (var tag in new[] { _sampleTag, _sizeTag })
        {
            tag.SetResourceReference(GlassPanel.CornerRadiusProperty, "Radius.Control");
            tag.IsHitTestVisible = false;
            tag.Visibility = Visibility.Collapsed;
            Panel.SetZIndex(tag, 1);
            _root.Children.Add(tag);
        }
        _surface.AdornmentsRendered += QueueSurfaceAdornments;
    }

    // A sibling can't be measured/arranged from the surface's OnRender. Coalesce
    // pointer, wheel, keyboard and selection updates into one post-render layout.
    private void QueueSurfaceAdornments()
    {
        if (_adornmentsQueued || _captureClosed)
        {
            return;
        }
        _adornmentsQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            _adornmentsQueued = false;
            if (!_captureClosed)
            {
                UpdateSurfaceAdornments();
            }
        }));
    }

    private void UpdateSurfaceAdornments()
    {
        var hasSize = _surface.ShowSelectionChrome && !_surface.Selection.IsEmpty;
        _sizeTag.Visibility = hasSize ? Visibility.Visible : Visibility.Collapsed;
        if (hasSize)
        {
            var selection = _surface.Selection;
            _sizeText.Text = $"{selection.Width} × {selection.Height}";
            var rect = _surface.SelectionInDips;
            Place(_sizeTag, rect.Left, rect.Top >= 26 ? rect.Top - 26 : rect.Top + 7, false);
        }
        _sampleTag.Visibility = _surface.IsSelectionCommitted ? Visibility.Collapsed : Visibility.Visible;
        if (_sampleTag.Visibility != Visibility.Visible)
        {
            return;
        }
        var pos = _surface.SampledScreenPoint;
        _sampleCoordinates.Text = $"{pos.X:0}, {pos.Y:0}";
        var argb = _surface.HoverColor;
        _sampleValue.Text = _surface.ColorRgbMode ? ColorText.ToRgb(argb) : ColorText.ToHex(argb);
        _sampleZoom.Text = $"{_surface.Magnification:0.#}×";
        _sampleHint.Text = _surface.ColorStatus;
        if (_lastSampleColor != argb)
        {
            _lastSampleColor = argb;
            _sampleChip.Fill = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        }
        var lens = _surface.LoupeInDips;
        Place(_sampleTag, lens.Left + lens.Width / 2, lens.Bottom + 9, true);
    }

    private void Place(FrameworkElement tag, double x, double y, bool centered)
    {
        tag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = tag.DesiredSize;
        Canvas.SetLeft(tag, Math.Clamp(centered ? x - size.Width / 2 : x, 1, Math.Max(1, _root.ActualWidth - size.Width - 1)));
        Canvas.SetTop(tag, Math.Clamp(y, 1, Math.Max(1, _root.ActualHeight - size.Height - 1)));
    }
}
