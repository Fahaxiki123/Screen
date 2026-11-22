using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

internal sealed class RecordingCountdownWindow : Window
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _number;
    private readonly Ellipse _progressArc;
    private const double ArcLength = Math.PI * (78 - 2.5) / 2.5;
    private readonly Ellipse _pulseRing;
    private readonly ScaleTransform _numberScale = new(1, 1);
    private readonly DispatcherTimer _timer;
    private int _remaining = 3;

    public RecordingCountdownWindow(PixelRect region)
    {
        Title = "Flick 录屏倒计时";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        UseLayoutRounding = true;

        // The persistent recording border already dims the desktop. This window
        // only hosts the 88-DIP orb and the largest frame of its expanding ring.
        Width = Height = 132;
        var center = new NativeMethods.NativePoint(region.X + region.Width / 2, region.Y + region.Height / 2);
        double scale = NativeMethods.GetDpiAtPoint(center) / 96d;
        int extent = (int)Math.Ceiling(Width * scale);
        var bounds = new System.Drawing.Rectangle(center.X - extent / 2, center.Y - extent / 2, extent, extent);
        Left = bounds.Left / scale;
        Top = bounds.Top / scale;
        var canvas = new Canvas { ClipToBounds = true };

        var orb = new Grid { Width = 88, Height = 88 };
        _pulseRing = new Ellipse
        {
            Stroke = RecordingTheme.Brush("Brush.Accent", "#007AFF"),
            StrokeThickness = 1.5,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform()
        };
        var glass = new Ellipse
        {
            Fill = RecordingTheme.Brush("Brush.ChromeGradient", "#D9FFFFFF"),
            Stroke = RecordingTheme.Brush("Brush.GlassRim", "#F5FFFFFF"),
            StrokeThickness = 2,
            Effect = new DropShadowEffect { Color = Color.FromRgb(15, 23, 42), BlurRadius = 12, ShadowDepth = 2, Opacity = 0.2 }
        };
        _progressArc = new Ellipse
        {
            Width = 78,
            Height = 78,
            StrokeThickness = 2.5,
            Stroke = RecordingTheme.Brush("Brush.Accent", "#007AFF"),
            StrokeDashArray = new DoubleCollection { ArcLength, ArcLength },
            StrokeDashCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-90)
        };
        _number = new TextBlock
        {
            Text = "3",
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 34,
            FontWeight = FontWeights.SemiBold,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#1D1D1F"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _numberScale
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(_number, FontNumeralAlignment.Tabular);
        orb.Children.Add(_pulseRing);
        orb.Children.Add(glass);
        orb.Children.Add(_progressArc);
        orb.Children.Add(_number);
        Canvas.SetLeft(orb, (Width - orb.Width) / 2);
        Canvas.SetTop(orb, (Height - orb.Height) / 2);
        canvas.Children.Add(orb);
        RecordingRegionWindow.Attach(this, canvas, bounds);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
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

    private void PlayBeatAnimation()
    {
        _progressArc.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(ArcLength, 0, TimeSpan.FromSeconds(1)));
        var pop = new DoubleAnimation(1.06, 1.0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _numberScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _numberScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        if (_pulseRing.RenderTransform is ScaleTransform ringScale)
        {
            var expand = new DoubleAnimation(0.92, 1.44, TimeSpan.FromMilliseconds(750))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var fade = new DoubleAnimation(0.55, 0.0, TimeSpan.FromMilliseconds(750));
            ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
            ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, expand);
            _pulseRing.BeginAnimation(OpacityProperty, fade);
        }
    }

    public Task<bool> Completion => _completion.Task;

    /// <summary>Stops the countdown and closes; idempotent and cancel-safe.</summary>
    public void Finish()
    {
        _timer.Stop();
        if (_completion.TrySetResult(true))
            Close();
    }
}
