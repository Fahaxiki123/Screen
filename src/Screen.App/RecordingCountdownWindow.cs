using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// 3-2-1 countdown over the recording region.  The window spans the whole
/// virtual screen: everything OUTSIDE the region gets the dimmed mask (the
/// region itself stays clear), and the number counts down at its center.
/// The region outline is NOT drawn here — a single persistent border window
/// owns it for the whole flow so the outline never blinks between phases.
/// <see cref="Completion"/> drives the orchestration.
/// </summary>
internal sealed class RecordingCountdownWindow : Window
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock _number;
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

        var mask = RecordingTheme.Brush("Brush.WindowBackground", "#15223A").Clone();
        mask.Opacity = 0.5;
        var canvas = new Canvas { ClipToBounds = true };
        // Four bands around the region = the mask; the region itself is left
        // untouched so the user sees exactly what is about to be recorded.
        canvas.Children.Add(MaskRect(mask, 0, 0, Width, ry));
        canvas.Children.Add(MaskRect(mask, 0, ry + rh, Width, Height - ry - rh));
        canvas.Children.Add(MaskRect(mask, 0, ry, rx, rh));
        canvas.Children.Add(MaskRect(mask, rx + rw, ry, Width - rx - rw, rh));

        _number = new TextBlock
        {
            Text = "3",
            FontSize = 112,
            FontWeight = FontWeights.Bold,
            Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#E8F1F7"),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24,
                Color = Colors.Black,
                Opacity = 0.55,
                ShadowDepth = 0
            }
        };
        canvas.Children.Add(_number);
        Canvas.SetLeft(_number, rx + rw / 2 - 40);
        Canvas.SetTop(_number, ry + rh / 2 - 75);
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
        };

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(
                handle,
                NativeMethods.GwlExStyle,
                (nint)(style | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate));
        };
        Loaded += (_, _) =>
        {
            // Center the number properly once the real size is known.
            _number.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(_number, rx + (rw - _number.DesiredSize.Width) / 2);
            Canvas.SetTop(_number, ry + (rh - _number.DesiredSize.Height) / 2);
            _timer.Start();
        };
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
