using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Screen.App;

internal static class GlassMotion
{
    public static void Enter(FrameworkElement element, double fromY, double fromScale, int ms, int delay = 0,
        double overshootY = 0, double overshootScale = 1, FrameworkElement? opacityTarget = null)
    {
        var scale = new ScaleTransform(1, 1);
        var offset = new TranslateTransform();
        var transform = new TransformGroup();
        transform.Children.Add(scale);
        transform.Children.Add(offset);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = transform;
        (opacityTarget ?? element).BeginAnimation(UIElement.OpacityProperty, Frames((0, 0), (delay, 0), (delay + ms, 1)));
        var y = Frames((0, fromY), (delay, fromY), (delay + ms * 0.75, overshootY), (delay + ms, 0));
        offset.BeginAnimation(TranslateTransform.YProperty, y);
        var zoom = Frames((0, fromScale), (delay, fromScale), (delay + ms * 0.75, overshootScale), (delay + ms, 1));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
    }

    public static void Pulse(FrameworkElement element)
    {
        var scale = new ScaleTransform();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        var animation = Frames((0, 1), (70, 0.82), (155, 1.12), (280, 1));
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    public static void Rotate(FrameworkElement element)
    {
        var turn = new RotateTransform();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = turn;
        turn.BeginAnimation(RotateTransform.AngleProperty, Frames((0, 0), (95, -30), (320, 0)));
    }

    public static void Dip(FrameworkElement element)
    {
        var offset = new TranslateTransform();
        element.RenderTransform = offset;
        offset.BeginAnimation(TranslateTransform.YProperty, Frames((0, 0), (75, 2.5), (260, 0)));
    }

    private static DoubleAnimationUsingKeyFrames Frames(params (double Milliseconds, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        foreach (var (milliseconds, value) in frames)
        {
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
                new KeySpline(0.22, 1, 0.36, 1)));
        }
        return animation;
    }
}
