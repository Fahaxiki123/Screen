using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace Screen.App;

internal sealed class HintToastWindow : Window
{
    // WPF measures dashes in pen widths. The check has two line segments in the 24-unit canvas.
    private static readonly double CheckDashLength = (Math.Sqrt(6 * 6 + 6 * 6) + Math.Sqrt(9 * 9 + 13.5 * 13.5)) / AppleVectorIcons.CheckStrokeWidth;
    private static HintToastWindow? _current;
    private readonly DispatcherTimer _lifetime = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SquircleGlassBorder _card;
    private readonly NativeMethods.NativePoint _anchor;
    private readonly Path? _check;

    private HintToastWindow(string message, bool copied, NativeMethods.NativePoint anchor)
    {
        _anchor = anchor;
        Title = "Flick 提示";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var row = new DockPanel { LastChildFill = true };
        if (copied)
        {
            row.Children.Add(CreateCheckBadge(out _check));
        }
        var label = new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.FontFamilyProperty, "Font.UI");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextPrimary");
        row.Children.Add(label);
        _card = new SquircleGlassBorder
        {
            CornerRadiusRadius = 8,
            Margin = new Thickness(12),
            Padding = new Thickness(13, 8, 13, 8),
            Child = row
        };
        Content = _card;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            if (_check is { } check)
            {
                GlassMotion.Enter(_card, 14, 0.92, 300, overshootY: -1.5, overshootScale: 1.02);
                check.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(CheckDashLength, 0, TimeSpan.FromMilliseconds(320))
                {
                    BeginTime = TimeSpan.FromMilliseconds(90)
                });
            }
            else
            {
                _card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            }
            _lifetime.Start();
        };
        _lifetime.Tick += (_, _) =>
        {
            _lifetime.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
            fade.Completed += (_, _) =>
            {
                if (IsVisible)
                    Close();
            };
            _card.BeginAnimation(OpacityProperty, fade);
        };
        Closed += (_, _) =>
        {
            _lifetime.Stop();
            if (ReferenceEquals(_current, this))
                _current = null;
        };
    }

    internal static Grid CreateCheckBadge(out Path check)
    {
        var circle = new Ellipse();
        circle.SetResourceReference(Shape.FillProperty, "Brush.Success");
        var badge = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.Children.Add(circle);
        check = new Path
        {
            Data = AppleVectorIcons.Outline("check"),
            Stroke = Brushes.White,
            StrokeThickness = AppleVectorIcons.CheckStrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeDashOffset = CheckDashLength,
            StrokeDashArray = new DoubleCollection { CheckDashLength, CheckDashLength }
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(check);
        badge.Children.Add(new Viewbox { Width = 15, Height = 15, Child = canvas });
        return badge;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle,
            (nint)(style | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate));
        NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);

        var monitor = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(_anchor.X, _anchor.Y));
        var work = monitor.WorkingArea;
        var scale = NativeMethods.GetDpiAtPoint(_anchor) / 96d;
        _card.MaxWidth = Math.Min(520, Math.Max(1, work.Width / scale - 32));
        _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _card.DesiredSize;
        var x = work.Left + (int)Math.Round((work.Width - size.Width * scale) / 2);
        var y = _check is not null
            ? work.Bottom - (int)Math.Round((size.Height + 16) * scale)
            : work.Top + (int)Math.Round(12 * scale);
        NativeMethods.SetWindowPos(handle, new nint(-1), x, y, 0, 0, NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
    }

    public static void Show(string message)
    {
        NativeMethods.TryGetPhysicalCursorPosition(out var cursor);
        ShowToast(message, copied: false, cursor);
    }

    public static void ShowCopied(NativeMethods.NativePoint captureCenter) =>
        ShowToast("已复制到剪贴板", copied: true, captureCenter);

    private static void ShowToast(string message, bool copied, NativeMethods.NativePoint anchor)
    {
        _current?.Close();
        _current = new HintToastWindow(message, copied, anchor);
        _current.Show();
    }
}
