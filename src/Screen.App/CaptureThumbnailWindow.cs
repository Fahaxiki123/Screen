using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

internal sealed class CaptureThumbnailWindow : Window
{
    private static CaptureThumbnailWindow? _current;
    private readonly DispatcherTimer _timer = new();
    private readonly Stopwatch _visibleTime = new();
    private readonly GlassPanel _panel;
    private bool _dismissing;

    private CaptureThumbnailWindow(BitmapSource thumbnail, NativeMethods.NativePoint anchor)
    {
        Title = "Screen 截图缩略图";
        Width = 188;
        SizeToContent = SizeToContent.Height;
        var image = new Border
        {
            Width = 174,
            Height = Math.Clamp(174.0 * thumbnail.PixelHeight / thumbnail.PixelWidth, 32, 134),
            Background = new ImageBrush(thumbnail) { Stretch = Stretch.Uniform }
        };
        image.SetResourceReference(Border.CornerRadiusProperty, "Radius.Control");
        var stack = new StackPanel();
        stack.Children.Add(image);
        stack.Children.Add(new TextBlock
        {
            Text = "✓ 已拷贝到剪贴板",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(3, 6, 0, 2)
        });
        _panel = new GlassPanel { Variant = GlassVariant.Solid, Padding = new Thickness(6), Content = stack };
        Content = _panel;
        FeedbackPlacement.Attach(this, FeedbackAnchor.BottomRight, anchor, clickThrough: false);
        Loaded += (_, _) =>
        {
            if (SystemParameters.ClientAreaAnimation)
            {
                _panel.RenderTransform = new TranslateTransform();
                _panel.RenderTransform.BeginAnimation(TranslateTransform.XProperty, Motion.Tween(86, 0, 600, Motion.Spring()));
                _panel.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 600));
            }
            if (!IsMouseOver)
                ResumeTimer();
        };
        MouseEnter += (_, _) => { _visibleTime.Stop(); _timer.Stop(); };
        MouseLeave += (_, _) => ResumeTimer();
        MouseLeftButtonUp += (_, _) => Close();
        _timer.Tick += (_, _) => Dismiss();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_current == this)
                _current = null;
        };
    }

    public static void ShowCopied(PixelBuffer image, NativeMethods.NativePoint anchor)
    {
        _current?.Close();
        var factor = Math.Min(1, Math.Min(348.0 / image.Width, 268.0 / image.Height));
        var source = image.ToBitmapSource();
        var width = Math.Max(1, (int)Math.Round(image.Width * factor));
        var height = Math.Max(1, (int)Math.Round(image.Height * factor));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(source, new Rect(0, 0, width, height));
        var thumbnail = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        thumbnail.Render(visual);
        thumbnail.Freeze();
        _current = new CaptureThumbnailWindow(thumbnail, anchor);
        _current.Show();
    }

    private void ResumeTimer()
    {
        if (_dismissing)
            return;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, 2600 - _visibleTime.Elapsed.TotalMilliseconds));
        _visibleTime.Start();
        _timer.Start();
    }

    private void Dismiss()
    {
        _timer.Stop();
        _dismissing = true;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }
        var fade = Motion.Tween(1, 0, 300);
        fade.Completed += (_, _) => { if (IsVisible) Close(); };
        _panel.RenderTransform = new TranslateTransform();
        _panel.RenderTransform.BeginAnimation(TranslateTransform.XProperty, Motion.Tween(0, 86, 300));
        _panel.BeginAnimation(OpacityProperty, fade);
    }
}
