using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Screen.App;

/// <summary>
/// Minimal Feishu-style seek bar drawn directly instead of templating a
/// Slider (code-built Slider templates crash: Track does not implement
/// IAddChild under FrameworkElementFactory).  Includes the trim bar: two
/// draggable end handles delimit the section that plays and gets exported.
/// </summary>
internal sealed class TimelineBar : FrameworkElement
{
    private const double MinTrimGap = 0.2;
    private const double HandleWidth = 11;
    private const double HandleHitPadding = 5;
    private const double PlayheadWidth = 2.4;

    // Detached timeline tests use the same compiled tokens as an application window.
    private readonly Brush _surfaceBrush = GlassTheme.Brush("Brush.Neutral100");
    private readonly Brush _excludedDim = GlassTheme.Brush("Brush.Dim");
    private readonly Brush _activeZone = GlassTheme.Brush("Brush.AccentSubtle");
    private readonly Pen _tick = new(GlassTheme.Brush("Brush.Separator"), 1);
    private readonly Pen _edge = new(GlassTheme.Brush("Brush.Accent"), 1.6);
    private readonly Brush _handle = GlassTheme.Brush("Brush.PrimaryAction");
    private readonly Brush _hoverHandle = GlassTheme.Brush("Brush.AccentHover");
    private readonly Brush _grip = GlassTheme.Brush("Brush.Black");
    private readonly Brush _playhead = GlassTheme.Brush("Brush.White");
    private readonly double _handleRadius = GlassTheme.Radius("Radius.Key").TopLeft;
    private enum DragMode { None, Thumb, TrimStart, TrimEnd }

    private double _value;
    private double _maximum = 0.1;
    private double _trimStart;
    private double _trimEnd = 0.1;
    private DragMode _dragMode;
    private DragMode _hoverMode;
    private double _dragOffsetX;

    public event EventHandler? ValueChanged;
    public event EventHandler? ScrubStarted;
    public event EventHandler? ScrubCompleted;
    public event EventHandler? TrimChanged;

    public TimelineBar() => Cursor = System.Windows.Input.Cursors.Hand;

    public double Maximum
    {
        get => _maximum;
        set
        {
            _maximum = Math.Max(0.001, value);
            _trimStart = 0;
            _trimEnd = _maximum;
            _value = Math.Clamp(_value, 0, _maximum);
            InvalidateVisual();
        }
    }

    public double TrimStart => _trimStart;
    public double TrimEnd => _trimEnd;
    public bool IsTrimmed => _trimStart > 0.01 || _trimEnd < _maximum - 0.01;

    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, _trimStart, Math.Max(_trimStart, _trimEnd));
            if (Math.Abs(clamped - _value) < 0.0001)
            {
                return;
            }
            _value = clamped;
            InvalidateVisual();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetTrimStart(double seconds)
    {
        var clamped = Math.Clamp(seconds, 0, Math.Max(0, _trimEnd - MinTrimGap));
        if (Math.Abs(clamped - _trimStart) < 0.0001)
        {
            return;
        }
        _trimStart = clamped;
        if (_value < _trimStart)
        {
            _value = _trimStart;
        }
        InvalidateVisual();
        TrimChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetTrimEnd(double seconds)
    {
        var clamped = Math.Clamp(seconds, Math.Min(_maximum, _trimStart + MinTrimGap), _maximum);
        if (Math.Abs(clamped - _trimEnd) < 0.0001)
        {
            return;
        }
        _trimEnd = clamped;
        if (_value > _trimEnd)
        {
            _value = _trimEnd;
        }
        InvalidateVisual();
        TrimChanged?.Invoke(this, EventArgs.Empty);
    }

    private static double EffectiveHandleWidth(double width) => Math.Min(HandleWidth, width / 2);

    private double XFor(double seconds, double width)
    {
        var handleWidth = EffectiveHandleWidth(width);
        return handleWidth + Math.Clamp(seconds / _maximum, 0, 1) * Math.Max(0, width - 2 * handleWidth);
    }

    private static Rect HandleRect(double x, double width, double height, DragMode mode)
    {
        var handleWidth = EffectiveHandleWidth(width);
        // Handles sit outside their trim boundaries so even a tiny selected
        // interval retains two separate grips instead of overlapping them.
        return new Rect(mode == DragMode.TrimStart ? x - handleWidth : x, 0, handleWidth, height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var xs = XFor(_trimStart, width);
        var xe = XFor(_trimEnd, width);

        // Cinema filmstrip track + subtle vertical tick marks
        dc.DrawRectangle(_surfaceBrush, null, new Rect(0, 0, width, height));
        for (var tickX = 24.0; tickX < width - 8; tickX += 28.0)
        {
            dc.DrawLine(_tick, new Point(tickX, 4), new Point(tickX, Math.Max(4, height - 4)));
        }

        var startHandle = HandleRect(xs, width, height, DragMode.TrimStart);
        var endHandle = HandleRect(xe, width, height, DragMode.TrimEnd);

        // Dim trimmed-out regions outside the handles
        if (startHandle.Left > 0.5)
        {
            dc.DrawRectangle(_excludedDim, null, new Rect(0, 0, startHandle.Left, height));
        }
        if (endHandle.Right < width - 0.5)
        {
            dc.DrawRectangle(_excludedDim, null, new Rect(endHandle.Right, 0, width - endHandle.Right, height));
        }

        // Highlight the retained range; its handles sit outside the boundaries
        if (xe > xs)
        {
            dc.DrawRectangle(_activeZone, null, new Rect(xs, 1, xe - xs, Math.Max(0, height - 2)));
            dc.DrawLine(_edge, new Point(xs, 0.8), new Point(xe, 0.8));
            dc.DrawLine(_edge, new Point(xs, height - 0.8), new Point(xe, height - 0.8));
        }

        var x = XFor(_value, width);
        DrawHandle(dc, startHandle, DragMode.TrimStart);
        DrawHandle(dc, endHandle, DragMode.TrimEnd);

        var playheadWidth = Math.Min(PlayheadWidth, Math.Max(0, xe - xs));
        var playheadX = Math.Clamp(x, xs, Math.Max(xs, xe - playheadWidth));
        var inset = Math.Min(1.5, height / 2);
        dc.DrawRectangle(_playhead, null, new Rect(playheadX, inset, playheadWidth, Math.Max(0, height - 2 * inset)));
    }

    private void DrawHandle(DrawingContext dc, Rect rect, DragMode mode)
    {
        var brush = _dragMode == mode || _hoverMode == mode ? _hoverHandle : _handle;
        dc.DrawRoundedRectangle(brush, null, rect, _handleRadius, _handleRadius);
        var squareHalf = mode == DragMode.TrimStart
            ? new Rect(rect.Left + rect.Width / 2, rect.Top, rect.Width / 2, rect.Height)
            : new Rect(rect.Left, rect.Top, rect.Width / 2, rect.Height);
        dc.DrawRectangle(brush, null, squareHalf);

        // A short inset mark makes each handle legible on bright footage
        var center = rect.Left + rect.Width / 2;
        var slotHeight = Math.Clamp(rect.Height * 0.42, 4, 12);
        var slotTop = rect.Top + (rect.Height - slotHeight) / 2;
        dc.DrawRectangle(_grip, null, new Rect(center - 1, slotTop, 2, slotHeight));
    }

    private DragMode HitTest(Point point, double width)
    {
        var xs = XFor(_trimStart, width);
        var xe = XFor(_trimEnd, width);
        var startRect = HandleRect(xs, width, ActualHeight, DragMode.TrimStart);
        var endRect = HandleRect(xe, width, ActualHeight, DragMode.TrimEnd);
        var startCenter = startRect.Left + startRect.Width / 2;
        var endCenter = endRect.Left + endRect.Width / 2;
        startRect.Inflate(HandleHitPadding, 3);
        endRect.Inflate(HandleHitPadding, 3);
        if (startRect.Contains(point) && endRect.Contains(point))
        {
            return Math.Abs(point.X - startCenter) <= Math.Abs(point.X - endCenter)
                ? DragMode.TrimStart
                : DragMode.TrimEnd;
        }
        if (startRect.Contains(point))
        {
            return DragMode.TrimStart;
        }
        if (endRect.Contains(point))
        {
            return DragMode.TrimEnd;
        }
        return DragMode.Thumb;
    }

    private void ApplyDrag(Point point)
    {
        if (ActualWidth <= 0)
        {
            return;
        }
        var handleWidth = EffectiveHandleWidth(ActualWidth);
        var usableWidth = Math.Max(0.001, ActualWidth - 2 * handleWidth);
        var ratio = Math.Clamp((point.X - _dragOffsetX - handleWidth) / usableWidth, 0, 1);
        switch (_dragMode)
        {
            case DragMode.TrimStart:
                SetTrimStart(ratio * _maximum);
                break;
            case DragMode.TrimEnd:
                SetTrimEnd(ratio * _maximum);
                break;
            default:
                Value = ratio * _maximum;
                break;
        }
    }

    private void BeginDrag(Point point)
    {
        _dragMode = HitTest(point, ActualWidth);
        _dragOffsetX = _dragMode switch
        {
            DragMode.TrimStart => point.X - XFor(_trimStart, ActualWidth),
            DragMode.TrimEnd => point.X - XFor(_trimEnd, ActualWidth),
            _ => 0
        };
        Cursor = _dragMode is DragMode.TrimStart or DragMode.TrimEnd
            ? System.Windows.Input.Cursors.SizeWE
            : System.Windows.Input.Cursors.Hand;
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        ApplyDrag(point);
    }

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!CaptureMouse())
        {
            return;
        }
        BeginDrag(e.GetPosition(this));
        e.Handled = true;
        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        if (_dragMode != DragMode.None)
        {
            if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
            {
                CompleteDrag(e.GetPosition(this));
            }
            else
            {
                ApplyDrag(e.GetPosition(this));
            }
        }
        else
        {
            var hoverMode = HitTest(e.GetPosition(this), ActualWidth);
            if (hoverMode != _hoverMode)
            {
                _hoverMode = hoverMode;
                Cursor = hoverMode is DragMode.TrimStart or DragMode.TrimEnd
                    ? System.Windows.Input.Cursors.SizeWE
                    : System.Windows.Input.Cursors.Hand;
                InvalidateVisual();
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_dragMode != DragMode.None)
        {
            ApplyDrag(e.GetPosition(this));
            CompleteDrag(e.GetPosition(this));
            e.Handled = true;
        }
        base.OnMouseLeftButtonUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        CompleteDrag(null);
        base.OnLostMouseCapture(e);
    }

    private void CompleteDrag(Point? point)
    {
        if (_dragMode == DragMode.None)
        {
            return;
        }
        _dragMode = DragMode.None;
        _dragOffsetX = 0;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        _hoverMode = IsMouseOver && point.HasValue ? HitTest(point.Value, ActualWidth) : DragMode.None;
        Cursor = _hoverMode is DragMode.TrimStart or DragMode.TrimEnd
            ? System.Windows.Input.Cursors.SizeWE
            : System.Windows.Input.Cursors.Hand;
        InvalidateVisual();
        ScrubCompleted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        if (_dragMode == DragMode.None && _hoverMode != DragMode.None)
        {
            _hoverMode = DragMode.None;
            Cursor = System.Windows.Input.Cursors.Hand;
            InvalidateVisual();
        }
        base.OnMouseLeave(e);
    }
}
