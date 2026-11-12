using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Screen.App;

/// <summary>
/// Horizontal volume slider drawn directly (code-built Slider templates are
/// off-limits, see TimelineBar).  Range 0..1 with live ValueChanged while
/// dragging, styled to match the moon-blue timeline.
/// </summary>
internal sealed class VolumeBar : FrameworkElement
{
    private static readonly Brush TrackBrush = RecordingTheme.Brush("Brush.Border", "#39556F");
    private static readonly Brush FillBrush = RecordingTheme.Brush("Brush.Accent", "#A7CFDF");
    private bool _dragging;
    private double _value;

    public event EventHandler? ValueChanged;

    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(clamped - _value) < 0.001)
            {
                return;
            }
            _value = clamped;
            InvalidateVisual();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public VolumeBar() => Cursor = System.Windows.Input.Cursors.Hand;

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var radius = height / 2;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, width, height), radius, radius);
        var x = _value * width;
        if (x > 0.5)
        {
            dc.DrawRoundedRectangle(FillBrush, null, new Rect(0, 0, x, height), radius, radius);
        }
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        CaptureMouse();
        _dragging = true;
        ApplyDrag(e.GetPosition(this));
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_dragging)
        {
            ApplyDrag(e.GetPosition(this));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }
        base.OnMouseLeftButtonUp(e);
    }

    private void ApplyDrag(Point point)
    {
        if (ActualWidth > 0)
        {
            Value = Math.Clamp(point.X / ActualWidth, 0, 1);
        }
    }
}

/// <summary>Shared recording chrome tokens, with frozen fallbacks for standalone control previews.</summary>
internal static class RecordingTheme
{
    internal static Brush Brush(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush resource)
        {
            return resource;
        }

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        brush.Freeze();
        return brush;
    }
}
