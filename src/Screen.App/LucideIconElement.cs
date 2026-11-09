using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using Lucide.NET;

namespace Screen.App;

/// <summary>
/// Lightweight WPF renderer for the official Lucide vector catalog. It keeps the
/// icon assets in Lucide.NET.Core and avoids loading a full SVG rendering stack.
/// </summary>
internal sealed class LucideIconElement : FrameworkElement
{
    private static readonly ConcurrentDictionary<LucideIconKind, IReadOnlyList<IconPrimitive>> Cache = new();

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(LucideIconKind),
        typeof(LucideIconElement),
        new FrameworkPropertyMetadata(LucideIconKind.Square, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke),
        typeof(Brush),
        typeof(LucideIconElement),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(LucideIconElement),
        new FrameworkPropertyMetadata(1.75d, FrameworkPropertyMetadataOptions.AffectsRender));

    public LucideIconKind Kind
    {
        get => (LucideIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 20 : Math.Min(20, availableSize.Width),
        double.IsInfinity(availableSize.Height) ? 20 : Math.Min(20, availableSize.Height));

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(ActualWidth, ActualHeight) / 24.0;
        var offsetX = (ActualWidth - 24 * scale) / 2;
        var offsetY = (ActualHeight - 24 * scale) / 2;
        var pen = new Pen(Stroke, StrokeThickness / scale)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        foreach (var primitive in Cache.GetOrAdd(Kind, ParseIcon))
        {
            primitive.Draw(drawingContext, pen);
        }
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static IReadOnlyList<IconPrimitive> ParseIcon(LucideIconKind kind)
    {
        var svg = Lucide.NET.Lucide.GetSvg(kind, new LucideOptions { IncludeXmlns = false });
        var document = XDocument.Parse(svg);
        var primitives = new List<IconPrimitive>();
        foreach (var element in document.Root!.Elements())
        {
            IconPrimitive? primitive = element.Name.LocalName switch
            {
                "path" => PathPrimitive(element),
                "line" => new LinePrimitive(Point(element, "x1", "y1"), Point(element, "x2", "y2")),
                "polyline" => PointsPrimitive(element, false),
                "polygon" => PointsPrimitive(element, true),
                "circle" => new EllipsePrimitive(
                    Value(element, "cx"), Value(element, "cy"), Value(element, "r"), Value(element, "r")),
                "ellipse" => new EllipsePrimitive(
                    Value(element, "cx"), Value(element, "cy"), Value(element, "rx"), Value(element, "ry")),
                "rect" => new RectanglePrimitive(
                    Value(element, "x"), Value(element, "y"), Value(element, "width"), Value(element, "height"),
                    OptionalValue(element, "rx"), OptionalValue(element, "ry")),
                _ => null
            };
            if (primitive is not null)
            {
                primitives.Add(primitive);
            }
        }
        return primitives;
    }

    private static IconPrimitive PathPrimitive(XElement element)
    {
        var geometry = Geometry.Parse((string?)element.Attribute("d") ?? string.Empty);
        geometry.Freeze();
        return new GeometryPrimitive(geometry);
    }

    private static IconPrimitive PointsPrimitive(XElement element, bool closed)
    {
        var values = ((string?)element.Attribute("points") ?? string.Empty)
            .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            if (values.Length >= 2)
            {
                context.BeginFigure(new Point(values[0], values[1]), false, closed);
                for (var index = 2; index + 1 < values.Length; index += 2)
                {
                    context.LineTo(new Point(values[index], values[index + 1]), true, false);
                }
            }
        }
        geometry.Freeze();
        return new GeometryPrimitive(geometry);
    }

    private static Point Point(XElement element, string x, string y) => new(Value(element, x), Value(element, y));
    private static double Value(XElement element, string attribute) =>
        double.Parse((string?)element.Attribute(attribute) ?? "0", CultureInfo.InvariantCulture);
    private static double OptionalValue(XElement element, string attribute) =>
        element.Attribute(attribute) is { Value: var value }
            ? double.Parse(value, CultureInfo.InvariantCulture)
            : 0;

    private abstract record IconPrimitive
    {
        public abstract void Draw(DrawingContext context, Pen pen);
    }

    private sealed record GeometryPrimitive(Geometry Geometry) : IconPrimitive
    {
        public override void Draw(DrawingContext context, Pen pen) => context.DrawGeometry(null, pen, Geometry);
    }

    private sealed record LinePrimitive(Point Start, Point End) : IconPrimitive
    {
        public override void Draw(DrawingContext context, Pen pen) => context.DrawLine(pen, Start, End);
    }

    private sealed record EllipsePrimitive(double X, double Y, double RadiusX, double RadiusY) : IconPrimitive
    {
        public override void Draw(DrawingContext context, Pen pen) =>
            context.DrawEllipse(null, pen, new Point(X, Y), RadiusX, RadiusY);
    }

    private sealed record RectanglePrimitive(
        double X,
        double Y,
        double Width,
        double Height,
        double RadiusX,
        double RadiusY) : IconPrimitive
    {
        public override void Draw(DrawingContext context, Pen pen) =>
            context.DrawRoundedRectangle(null, pen, new Rect(X, Y, Width, Height), RadiusX, RadiusY);
    }
}
