using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Screen.App;

internal static class Motion
{
    public static BackEase Spring(double amplitude = 0.3) => new() { Amplitude = amplitude, EasingMode = EasingMode.EaseOut };

    public static DoubleAnimation Tween(double from, double to, int ms, IEasingFunction? ease = null, int delayMs = 0) =>
        new(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease,
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            FillBehavior = FillBehavior.Stop
        };

    public static void SlideTo(FrameworkElement indicator, double x, double width, bool animate = true, int durationMs = 450)
    {
        var translation = indicator.RenderTransform as TranslateTransform;
        if (translation is null || translation.IsFrozen)
            indicator.RenderTransform = translation = new TranslateTransform();
        var previousX = translation.X;
        var previousWidth = indicator.Width;
        translation.BeginAnimation(TranslateTransform.XProperty, null);
        indicator.BeginAnimation(FrameworkElement.WidthProperty, null);
        translation.X = x;
        indicator.Width = width;
        if (!animate || !SystemParameters.ClientAreaAnimation || !double.IsFinite(previousWidth))
            return;
        translation.BeginAnimation(TranslateTransform.XProperty, Tween(previousX, x, durationMs, Spring()));
        indicator.BeginAnimation(FrameworkElement.WidthProperty, Tween(previousWidth, width, durationMs, Spring()));
    }

    public static void PopIn(FrameworkElement element, bool horizontal = false, int delayMs = 0, bool menu = false)
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;
        var scale = new ScaleTransform();
        var offset = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(offset);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = transforms;
        var start = horizontal ? 0.92 : menu ? 0.94 : 0.96;
        var ease = Spring(0.35);
        var pop = Tween(start, 1, 380, ease, delayMs);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        offset.BeginAnimation(horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty,
            Tween(horizontal ? -12 : -6, 0, 380, ease, delayMs));
        // Delay the reveal too: a delayed panel must not flash before its clock starts.
        var opacity = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, TimeSpan.Zero));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(delayMs)));
        opacity.KeyFrames.Add(new LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(delayMs + 380)));
        element.BeginAnimation(UIElement.OpacityProperty, opacity);
    }

    public static void PopSwatch(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;
        var transform = new ScaleTransform();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = transform;
        var pop = Tween(0.6, 1, 450, Spring(0.5));
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }
}
