using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// 3-2-1 Liquid Sapphire optical orb countdown over the recording region.
/// Everything outside the region stays dimmed, while a luminous frosted-sapphire
/// medallion pulses cleanly at the region center before capture begins.
/// </summary>
internal sealed class RecordingCountdownWindow : Window
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _number;
    private readonly Grid _orb;
    private readonly Ellipse _pulseRing;
    private readonly ScaleTransform _numberScale = new(1, 1);
    private readonly DispatcherTimer _timer;
    private int _remaining = 3;

    public RecordingCountdownWindow(PixelRect region)
    {
        Title = "Screen 录屏倒计时";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        UseLayoutRounding = true;

        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            region.X + region.Width / 2,
            region.Y + region.Height / 2)) / 96.0;
        Left = screen.Left / dpi;
        Top = screen.Top / dpi;
        Width = screen.Width / dpi;
        Height = screen.Height / dpi;

        // Region rect in this window's DIP space.
        var rx = (region.X - screen.Left) / dpi;
        var ry = (region.Y - screen.Top) / dpi;
        var rw = region.Width / dpi;
        var rh = region.Height / dpi;

        var mask = RecordingTheme.Brush("Brush.WindowBackground", "#0A1222").Clone();
        mask.Opacity = 0.56;
        var canvas = new Canvas { ClipToBounds = true };
        canvas.Children.Add(MaskRect(mask, 0, 0, Width, ry));
        canvas.Children.Add(MaskRect(mask, 0, ry + rh, Width, Height - ry - rh));
        canvas.Children.Add(MaskRect(mask, 0, ry, rx, rh));
        canvas.Children.Add(MaskRect(mask, rx + rw, ry, Width - rx - rw, rh));

        const double orbDiameter = 128;
        _orb = new Grid
        {
            Width = orbDiameter + 32,
            Height = orbDiameter + 32
        };

        _pulseRing = new Ellipse
        {
            Width = orbDiameter,
            Height = orbDiameter,
            Stroke = new SolidColorBrush(Color.FromArgb(0x90, 0x7D, 0xD3, 0xFC)),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1)
        };

        var orbFill = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.32, 0.24),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.68,
            RadiusY = 0.68,
            GradientStops = new GradientStopCollection
            {
                new(Color.FromArgb(0xEB, 0x1A, 0x35, 0x5E), 0),
                new(Color.FromArgb(0xF2, 0x0E, 0x1B, 0x33), 0.65),
                new(Color.FromArgb(0xF8, 0x09, 0x12, 0x24), 1)
            }
        };
        var orbRim = new LinearGradientBrush(
            Color.FromArgb(0x90, 0xBA, 0xE6, 0xFD),
            Color.FromArgb(0x30, 0x38, 0xBD, 0xF8),
            new Point(0, 0),
            new Point(1, 1));

        var numberBrush = new LinearGradientBrush(
            Colors.White,
            Color.FromRgb(0x93, 0xC5, 0xFD),
            new Point(0, 0),
            new Point(0, 1));

        _number = new TextBlock
        {
            Text = "3",
            FontSize = 52,
            FontWeight = FontWeights.Bold,
            Foreground = numberBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -10, 0, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _numberScale
        };

        var readyLabel = new TextBlock
        {
            Text = "READY",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Segoe UI Variable Text"),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = RecordingTheme.Brush("Brush.AccentText", "#7DD3FC"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 20),
            Opacity = 0.88
        };

        var coreCircle = new Border
        {
            Width = orbDiameter,
            Height = orbDiameter,
            CornerRadius = new CornerRadius(orbDiameter / 2),
            Background = orbFill,
            BorderBrush = orbRim,
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = Color.FromRgb(0x0E, 0xA5, 0xE9),
                BlurRadius = 32,
                ShadowDepth = 0,
                Opacity = 0.35
            },
            Child = new Grid
            {
                Children = { _number, readyLabel }
            }
        };

        _orb.Children.Add(_pulseRing);
        _orb.Children.Add(coreCircle);
        Canvas.SetLeft(_orb, rx + (rw - _orb.Width) / 2);
        Canvas.SetTop(_orb, ry + (rh - _orb.Height) / 2);
        canvas.Children.Add(_orb);
        Content = canvas;

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

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(
                handle,
                NativeMethods.GwlExStyle,
                (nint)(style | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate));
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
        };
        Loaded += (_, _) =>
        {
            PlayBeatAnimation();
            _timer.Start();
        };
    }

    private void PlayBeatAnimation()
    {
        var pop = new DoubleAnimation(1.18, 1.0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _numberScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _numberScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        if (_pulseRing.RenderTransform is ScaleTransform ringScale)
        {
            var expand = new DoubleAnimation(1.0, 1.22, TimeSpan.FromMilliseconds(720))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            var fade = new DoubleAnimation(0.75, 0.0, TimeSpan.FromMilliseconds(720));
            ringScale.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
            ringScale.BeginAnimation(ScaleTransform.ScaleYProperty, expand);
            _pulseRing.BeginAnimation(OpacityProperty, fade);
        }
    }

    private static Border MaskRect(Brush mask, double left, double top, double width, double height)
    {
        var rect = new Border
        {
            Background = mask,
            Width = Math.Max(0, width),
            Height = Math.Max(0, height)
        };
        Canvas.SetLeft(rect, left);
        Canvas.SetTop(rect, top);
        return rect;
    }

    public Task Completion => _completion.Task;

    /// <summary>Stops the countdown and closes; idempotent and cancel-safe.</summary>
    public void Finish()
    {
        _timer.Stop();
        _completion.TrySetResult();
        Close();
    }
}
