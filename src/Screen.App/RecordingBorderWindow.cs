using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Full-virtual-screen dimming mask with an optical camera-viewfinder frame:
/// hairline guide + four system-blue corner brackets + compact resolution
/// badge. Click-through everywhere and carries WDA_EXCLUDEFROMCAPTURE so none
/// of the viewfinder chrome ever enters the recorded video.
/// </summary>
internal sealed class RecordingBorderWindow : Window
{
    public RecordingBorderWindow(PixelRect region)
    {
        Title = "Flick 录制区域";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
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

        var mask = RecordingTheme.Brush("Brush.SelectionMask", "#570F172A");
        var canvas = new Canvas { ClipToBounds = true };
        // Four dimmed bands around the region; the region itself stays crystal clear.
        canvas.Children.Add(MaskRect(mask, 0, 0, Width, ry));
        canvas.Children.Add(MaskRect(mask, 0, ry + rh, Width, Height - ry - rh));
        canvas.Children.Add(MaskRect(mask, 0, ry, rx, rh));
        canvas.Children.Add(MaskRect(mask, rx + rw, ry, Width - rx - rw, rh));

        // Subtle continuous 1px optical guide outside the region so no inner pixel is covered
        var guide = new Border
        {
            BorderBrush = RecordingTheme.Brush("Brush.OverlayBorder", "#210F172A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Width = rw + 2,
            Height = rh + 2
        };
        Canvas.SetLeft(guide, rx - 1);
        Canvas.SetTop(guide, ry - 1);
        canvas.Children.Add(guide);

        // Four Leica-style luminous viewfinder corner brackets
        var arm = Math.Clamp(Math.Min(rw, rh) * 0.12, 14, 22);
        var bracketStroke = RecordingTheme.Brush("Brush.Accent", "#007AFF");
        var bracketGlow = new DropShadowEffect
        {
            Color = Color.FromRgb(0, 122, 255),
            BlurRadius = 10,
            ShadowDepth = 0,
            Opacity = 0.24
        };

        canvas.Children.Add(CornerBracket(rx - 1.5, ry - 1.5, arm, top: true, left: true, bracketStroke, bracketGlow));
        canvas.Children.Add(CornerBracket(rx + rw + 1.5, ry - 1.5, arm, top: true, left: false, bracketStroke, bracketGlow));
        canvas.Children.Add(CornerBracket(rx - 1.5, ry + rh + 1.5, arm, top: false, left: true, bracketStroke, bracketGlow));
        canvas.Children.Add(CornerBracket(rx + rw + 1.5, ry + rh + 1.5, arm, top: false, left: false, bracketStroke, bracketGlow));

        // Compact viewfinder spec badge at top-left (when space above permits)
        if (ry >= 28)
        {
            var dimensions = new TextBlock
            {
                Text = $"{region.Width} × {region.Height}",
                FontSize = 11,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                Foreground = RecordingTheme.Brush("Brush.TextPrimary", "#1D1D1F")
            };
            System.Windows.Documents.Typography.SetNumeralAlignment(dimensions, FontNumeralAlignment.Tabular);
            var specBadge = new SquircleGlassBorder
            {
                CornerRadiusRadius = 7.5,
                Padding = new Thickness(8, 3, 8, 3),
                GlassBackground = RecordingTheme.Brush("Brush.ChromeGradient", "#D9FFFFFF"),
                Child = dimensions
            };
            Canvas.SetLeft(specBadge, rx);
            Canvas.SetTop(specBadge, ry - 25);
            canvas.Children.Add(specBadge);
        }

        RecordingRegionWindow.Attach(this, canvas, screen);
    }

    private static System.Windows.Shapes.Path CornerBracket(
        double cornerX,
        double cornerY,
        double arm,
        bool top,
        bool left,
        Brush stroke,
        Effect glow)
    {
        var dx = left ? arm : -arm;
        var dy = top ? arm : -arm;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(cornerX, cornerY + dy), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(cornerX, cornerY), isStroked: true, isSmoothJoin: true);
            ctx.LineTo(new Point(cornerX + dx, cornerY), isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geometry,
            Stroke = stroke,
            StrokeThickness = 2.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Effect = glow
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
}
