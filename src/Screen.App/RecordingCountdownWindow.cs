using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

internal sealed class RecordingCountdownWindow : Window
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _number;
    private readonly Ellipse _progressRing;
    private readonly ScaleTransform _numberScale = new();
    private readonly DispatcherTimer _timer;
    private int _remaining = 3;

    public RecordingCountdownWindow(PixelRect region)
    {
        Title = "Screen 录屏倒计时";
        var canvas = RecordingRegionWindow.CreateMask(this, region, "Recording.CountdownMask", out var cutout);
        const double diameter = 128;
        var face = new GlassPanel { Variant = GlassVariant.Solid, Width = diameter, Height = diameter, Padding = new Thickness(0) };
        face.SetResourceReference(GlassPanel.CornerRadiusProperty, "Recording.CountdownRadius");
        var content = new Grid();
        var track = Ring("Recording.RingTrack");
        _progressRing = Ring("Brush.White");
        // Dash units are multiples of stroke thickness: one dash travels exactly one circumference.
        var circumference = Math.PI * (_progressRing.Width - _progressRing.StrokeThickness) / _progressRing.StrokeThickness;
        _progressRing.StrokeDashArray = new DoubleCollection { circumference, circumference };
        _progressRing.RenderTransformOrigin = new Point(0.5, 0.5);
        _progressRing.RenderTransform = new RotateTransform(-90);
        content.Children.Add(track);
        content.Children.Add(_progressRing);
        _number = new TextBlock
        {
            Text = "3",
            FontSize = 56,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _numberScale
        };
        _number.SetResourceReference(TextBlock.FontFamilyProperty, "FontFamily.Display");
        _number.SetResourceReference(TextBlock.ForegroundProperty, "Brush.White");
        content.Children.Add(_number);
        face.Content = content;
        Canvas.SetLeft(face, cutout.Left + (cutout.Width - diameter) / 2);
        Canvas.SetTop(face, cutout.Top + (cutout.Height - diameter) / 2);
        canvas.Children.Add(face);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (--_remaining <= 0)
            {
                Finish();
                return;
            }
            _number.Text = _remaining.ToString();
            PlayBeatAnimation();
        };
        Loaded += (_, _) =>
        {
            PlayBeatAnimation();
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _completion.TrySetResult(false);
        };
    }

    private static Ellipse Ring(string resource)
    {
        var ring = new Ellipse { Width = 112, Height = 112, StrokeThickness = 3 };
        ring.SetResourceReference(Shape.StrokeProperty, resource);
        return ring;
    }

    private void PlayBeatAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;
        var pop = Motion.Tween(1.5, 1, 700, Motion.Spring());
        _numberScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _numberScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        _number.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 700));
        _progressRing.BeginAnimation(Shape.StrokeDashOffsetProperty, Motion.Tween(_progressRing.StrokeDashArray[0], 0, 700));
    }

    public Task<bool> Completion => _completion.Task;

    public void Finish()
    {
        _timer.Stop();
        if (_completion.TrySetResult(true))
            Close();
    }
}
