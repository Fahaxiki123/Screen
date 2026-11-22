using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Screen.App;

internal enum Axis { X, Y }

internal sealed class LiquidSpringTracker : IDisposable
{
    private readonly FrameworkElement _track;
    private readonly FrameworkElement _indicator;
    private readonly bool _colorRing;
    private readonly LiquidSpringMotion _motion;
    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _translation = new();
    private FrameworkElement? _target;
    private Rect _destination = Rect.Empty;
    private bool _snap = true;
    private bool _animating;
    private TimeSpan? _lastFrame;
    private long _lastUpdate;

    public LiquidSpringTracker(FrameworkElement track, FrameworkElement indicator, Axis axis = Axis.X, bool colorRing = false)
    {
        _track = track;
        _indicator = indicator;
        _colorRing = colorRing;
        _motion = new LiquidSpringMotion(axis);

        var transform = new TransformGroup();
        transform.Children.Add(_scale);
        transform.Children.Add(_translation);
        indicator.RenderTransform = transform;
        indicator.RenderTransformOrigin = new Point(0.5, 0.5);
        indicator.HorizontalAlignment = HorizontalAlignment.Left;
        indicator.VerticalAlignment = VerticalAlignment.Top;
        indicator.IsHitTestVisible = false;
        indicator.Visibility = Visibility.Hidden;

        track.Loaded += TrackLoaded;
        track.Unloaded += TrackUnloaded;
        track.IsVisibleChanged += VisibilityChanged;
        if (track.IsLoaded)
        {
            track.LayoutUpdated += LayoutUpdated;
        }
    }

    public void MoveTo(FrameworkElement? target, bool instant = false)
    {
        if (_target != target)
        {
            if (_target != null)
            {
                _target.IsVisibleChanged -= VisibilityChanged;
            }
            _target = target;
            if (target != null)
            {
                target.IsVisibleChanged += VisibilityChanged;
            }
        }
        _snap |= instant;
        UpdateTarget();
    }

    private void TrackLoaded(object sender, RoutedEventArgs e)
    {
        _track.LayoutUpdated -= LayoutUpdated;
        _track.LayoutUpdated += LayoutUpdated;
        _snap = true;
        UpdateTarget();
    }

    private void TrackUnloaded(object sender, RoutedEventArgs e)
    {
        _track.LayoutUpdated -= LayoutUpdated;
        Reset();
    }

    private void LayoutUpdated(object? sender, EventArgs e) => UpdateTarget();

    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            _snap = true;
            _track.InvalidateArrange();
        }
        else
        {
            Reset();
        }
    }

    private void UpdateTarget()
    {
        if (!_track.IsVisible || _target == null || !_target.IsVisible ||
            _target.ActualWidth <= 0 || _target.ActualHeight <= 0 || !_track.IsAncestorOf(_target))
        {
            Reset();
            return;
        }

        // Visibility changes precede the layout pass that restores a collapsed track.
        if (!_track.IsArrangeValid || !_target.IsArrangeValid)
        {
            return;
        }

        Rect bounds = GetTargetBounds(_track, _target, _colorRing);
        if (!_snap && bounds == _destination)
        {
            return;
        }

        _indicator.Visibility = Visibility.Visible;
        if (_snap)
        {
            _destination = bounds;
            _snap = false;
            _motion.Snap(bounds);
            Apply();
            Stop();
        }
        else
        {
            long now = Stopwatch.GetTimestamp();
            if (_animating)
            {
                // The old destination owned the time before this retarget, even
                // when several clicks or layout changes arrive between frames.
                _motion.Advance(_destination, Stopwatch.GetElapsedTime(_lastUpdate, now).TotalSeconds);
            }
            else
            {
                _animating = true;
                CompositionTarget.Rendering += Render;
            }
            _lastUpdate = now;
            _destination = bounds;
        }
    }

    internal static Rect GetTargetBounds(FrameworkElement track, FrameworkElement target, bool colorRing)
    {
        var point = target.TransformToAncestor(track).Transform(new Point(0, 0));
        double width = target.ActualWidth;
        double height = target.ActualHeight;
        if (colorRing)
        {
            return new Rect(point.X + (width - 24) / 2, point.Y + (height - 24) / 2, 24, 24);
        }
        return new Rect(point.X, point.Y, width, height);
    }

    private void Render(object? sender, EventArgs e)
    {
        var time = ((RenderingEventArgs)e).RenderingTime;
        if (time == _lastFrame)
        {
            return;
        }
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_lastUpdate, now).TotalSeconds;
        _lastUpdate = now;
        _lastFrame = time;
        bool settled = _motion.Advance(_destination, elapsed);
        Apply();
        if (settled)
        {
            Stop();
        }
    }

    private void Apply()
    {
        var bounds = _motion.Bounds;
        _translation.X = bounds.X;
        _translation.Y = bounds.Y;
        _indicator.Width = bounds.Width;
        _indicator.Height = bounds.Height;
        _scale.ScaleX = _motion.ScaleX;
        _scale.ScaleY = _motion.ScaleY;
    }

    private void Stop()
    {
        if (_animating)
        {
            CompositionTarget.Rendering -= Render;
            _animating = false;
        }
        _lastFrame = null;
    }

    private void Reset()
    {
        Stop();
        _destination = Rect.Empty;
        _snap = true;
        _indicator.Visibility = Visibility.Hidden;
    }

    public void Dispose()
    {
        MoveTo(null);
        _track.Loaded -= TrackLoaded;
        _track.Unloaded -= TrackUnloaded;
        _track.IsVisibleChanged -= VisibilityChanged;
        _track.LayoutUpdated -= LayoutUpdated;
    }
}

internal sealed class LiquidSpringMotion(Axis axis)
{
    private static readonly SpringStep HorizontalStep = SpringStep.At60Hz(420, 27);
    private static readonly SpringStep VerticalStep = SpringStep.At60Hz(380, 36);
    private double _velocity;
    private double _size;
    private double _sizeVelocity;

    public Rect Bounds { get; private set; }
    public double ScaleX { get; private set; } = 1;
    public double ScaleY { get; private set; } = 1;

    public void Snap(Rect target)
    {
        Bounds = target;
        _size = axis == Axis.X ? target.Width : target.Height;
        _velocity = _sizeVelocity = 0;
        ScaleX = ScaleY = 1;
    }

    public bool Advance(Rect target, double elapsed)
    {
        if (!(elapsed > 0))
            return false;
        // After this much real time both springs are at rest to subpixel
        // precision, including when the dispatcher resumes after suspension.
        if (elapsed >= 2)
        {
            Snap(target);
            return true;
        }

        bool horizontal = axis == Axis.X;
        double frames = elapsed * 60;
        var step = (horizontal ? HorizontalStep : VerticalStep).ForFrames(frames);
        double position = horizontal ? Bounds.X : Bounds.Y;
        double targetPosition = horizontal ? target.X : target.Y;
        double targetSize = horizontal ? target.Width : target.Height;
        step.Apply(targetPosition, ref position, ref _velocity);
        step.Apply(targetSize, ref _size, ref _sizeVelocity);
        double follow = 1 - Math.Pow(0.65, frames);

        if (horizontal)
        {
            double stretch = Math.Min(0.24, Math.Abs(_velocity) * 0.00028);
            ScaleX = 1 + stretch;
            ScaleY = 1 - stretch * 0.34;
            Bounds = new Rect(position, Bounds.Y + (target.Y - Bounds.Y) * follow,
                Math.Max(0, _size), Bounds.Height + (target.Height - Bounds.Height) * follow);
        }
        else
        {
            double stretch = Math.Min(0.035, Math.Abs(_velocity) * 0.00005);
            ScaleY = 1 + stretch;
            ScaleX = 1 - stretch * 0.25;
            Bounds = new Rect(Bounds.X + (target.X - Bounds.X) * follow, position,
                Bounds.Width + (target.Width - Bounds.Width) * follow, Math.Max(0, _size));
        }

        if (Math.Abs(Bounds.X - target.X) >= 0.15 || Math.Abs(Bounds.Y - target.Y) >= 0.15 ||
            Math.Abs(Bounds.Width - target.Width) >= 0.15 || Math.Abs(Bounds.Height - target.Height) >= 0.15 ||
            Math.Abs(_size - targetSize) >= 0.15 || Math.Abs(_velocity) >= 0.8 || Math.Abs(_sizeVelocity) >= 0.8)
            return false;
        Snap(target);
        return true;
    }

    // A reference step maps (displacement, velocity) to their values after one
    // 60 Hz tick. Taking its fractional power preserves the tuned 60 Hz path:
    // five steps at 300 Hz compose to exactly the same spring response.
    private readonly record struct SpringStep(double PositionRetention, double VelocityToPosition,
        double PositionToVelocity, double VelocityRetention)
    {
        public static SpringStep At60Hz(double stiffness, double damping)
        {
            const double dt = 1d / 60;
            double drag = 1 - damping * dt;
            return new SpringStep(1 - stiffness * dt * dt, drag * dt, -stiffness * dt, drag);
        }

        public void Apply(double target, ref double position, ref double velocity)
        {
            double displacement = position - target;
            position = target + PositionRetention * displacement + VelocityToPosition * velocity;
            velocity = PositionToVelocity * displacement + VelocityRetention * velocity;
        }

        public SpringStep ForFrames(double frames)
        {
            double midpoint = (PositionRetention + VelocityRetention) / 2;
            double difference = (PositionRetention - VelocityRetention) / 2;
            double discriminant = difference * difference + VelocityToPosition * PositionToVelocity;
            double mean, slope;
            if (discriminant < 0)
            {
                // The horizontal spring has an oscillating, exponentially decaying response.
                double frequency = Math.Sqrt(-discriminant);
                double magnitude = Math.Pow(Math.Sqrt(midpoint * midpoint - discriminant), frames);
                var (sin, cos) = Math.SinCos(frames * Math.Atan2(frequency, midpoint));
                mean = magnitude * cos;
                slope = magnitude * sin / frequency;
            }
            else
            {
                // The vertical spring combines two distinct real exponential decays.
                double spread = Math.Sqrt(discriminant);
                double slow = Math.Pow(midpoint + spread, frames);
                double fast = Math.Pow(midpoint - spread, frames);
                mean = (slow + fast) / 2;
                slope = (slow - fast) / (2 * spread);
            }
            return new SpringStep(mean + slope * difference, slope * VelocityToPosition,
                slope * PositionToVelocity, mean - slope * difference);
        }
    }
}
