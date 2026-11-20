using System.Windows;
using System.Windows.Controls;

namespace Screen.App;

/// <summary>A native single-selection list with a sliding, equal-width segment indicator.</summary>
[TemplatePart(Name = "PART_Indicator", Type = typeof(FrameworkElement))]
[TemplatePart(Name = "PART_Track", Type = typeof(FrameworkElement))]
public sealed class SegmentedControl : ListBox
{
    private FrameworkElement? _indicator;
    private FrameworkElement? _track;
    private double _previousX = double.NaN;
    private double _previousWidth = double.NaN;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _indicator = GetTemplateChild("PART_Indicator") as FrameworkElement;
        _track = GetTemplateChild("PART_Track") as FrameworkElement;
        _previousX = _previousWidth = double.NaN;
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        var arranged = base.ArrangeOverride(arrangeBounds);
        PositionIndicator();
        return arranged;
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        PositionIndicator();
    }

    private void PositionIndicator()
    {
        if (_indicator is null || _track is null)
            return;
        if (SelectedIndex < 0 || ItemContainerGenerator.ContainerFromIndex(SelectedIndex) is not FrameworkElement item
            || item.ActualWidth <= 0 || !_track.IsAncestorOf(item))
        {
            _indicator.Visibility = Visibility.Hidden;
            _previousX = _previousWidth = double.NaN;
            return;
        }

        var x = item.TranslatePoint(new Point(), _track).X;
        var width = item.ActualWidth;
        if (Math.Abs(_previousX - x) < 0.01 && Math.Abs(_previousWidth - width) < 0.01)
            return;

        _indicator.Visibility = Visibility.Visible;
        Motion.SlideTo(_indicator, x, width, !double.IsNaN(_previousX) && IsLoaded, 400);
        _previousX = x;
        _previousWidth = width;
    }
}
