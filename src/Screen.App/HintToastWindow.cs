using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ShapePath = System.Windows.Shapes.Path;

namespace Screen.App;

internal sealed class HintToastWindow : Window
{
    private static HintToastWindow? _current;
    private readonly DispatcherTimer _timer = new();

    private HintToastWindow(string message, bool success, NativeMethods.NativePoint? anchor)
    {
        Title = "Screen 提示";
        SizeToContent = SizeToContent.WidthAndHeight;
        var panel = new GlassPanel { Variant = GlassVariant.Solid, Padding = new Thickness(16, 9, 16, 9) };
        var label = new TextBlock
        {
            Text = message,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (success)
        {
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var check = new ShapePath();
            check.SetResourceReference(StyleProperty, "SuccessCheckStyle");
            stack.Children.Add(check);
            label.Margin = new Thickness(0, 12, 0, 0);
            label.FontWeight = FontWeights.SemiBold;
            stack.Children.Add(label);
            panel.Width = panel.Height = 156;
            panel.SetResourceReference(GlassPanel.CornerRadiusProperty, "Radius.Card");
            panel.Content = stack;
            Loaded += (_, _) =>
            {
                if (!SystemParameters.ClientAreaAnimation)
                    return;
                check.BeginAnimation(Shape.StrokeDashOffsetProperty, Motion.Tween(20, 0, 400));
                panel.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 450));
                var scale = new ScaleTransform();
                panel.RenderTransformOrigin = new Point(0.5, 0.5);
                panel.RenderTransform = scale;
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Tween(0.92, 1, 450, Motion.Spring()));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Tween(0.92, 1, 450, Motion.Spring()));
            };
        }
        else
        {
            panel.Content = label;
        }
        Content = panel;
        FeedbackPlacement.Attach(this, success ? FeedbackAnchor.Center : FeedbackAnchor.Top, anchor, clickThrough: true);
        _timer.Interval = TimeSpan.FromMilliseconds(success ? 1750 : 2200);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (!success || !SystemParameters.ClientAreaAnimation)
            {
                Close();
                return;
            }
            var fade = Motion.Tween(1, 0, 250);
            fade.Completed += (_, _) => { if (IsVisible) Close(); };
            panel.BeginAnimation(OpacityProperty, fade);
        };
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => { _timer.Stop(); if (_current == this) _current = null; };
    }

    public static void Show(string message) => Display(message, false, null);
    public static void Success(string message, NativeMethods.NativePoint? anchor = null) => Display(message, true, anchor);

    private static void Display(string message, bool success, NativeMethods.NativePoint? anchor)
    {
        _current?.Close();
        _current = new HintToastWindow(message, success, anchor);
        _current.Show();
    }
}
