using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;

namespace Screen.App;

internal static class AppleVectorIcons
{
    internal const double StrokeWidth = 1.75;
    internal const double CheckStrokeWidth = 2.1;

    private sealed class Glyph
    {
        public Geometry Outline { get; }
        public Geometry? Solid { get; }

        public Glyph(Geometry outline, Geometry? solid = null)
        {
            outline.Freeze();
            solid?.Freeze();
            Outline = outline;
            Solid = solid;
        }
    }

    private static readonly Dictionary<string, Glyph> Glyphs = new(StringComparer.Ordinal)
    {
        ["zoom-in"] = new(new GeometryGroup { Children = { new EllipseGeometry(new Point(10.25, 10.25), 6.5, 6.5), Geometry.Parse("M15 15l5.25 5.25 M7.25 10.25h6m-3-3v6") } }),
        ["zoom-out"] = new(new GeometryGroup { Children = { new EllipseGeometry(new Point(10.25, 10.25), 6.5, 6.5), Geometry.Parse("M15 15l5.25 5.25 M7.25 10.25h6") } }),
        ["line"] = new(Geometry.Parse("M5 18.5L19 5.5")),
        ["minus"] = new(Geometry.Parse("M5 12h14")),
        ["number"] = new(new GeometryGroup { Children = { new EllipseGeometry(new Point(12, 12), 8.25, 8.25), Geometry.Parse("M9.75 9.75 12.5 7.5v9m-2.75 0h5.5") } }),
        ["crop"] = new(Geometry.Parse("M6.75 3v12.75a1.5 1.5 0 0 0 1.5 1.5H21 M3 6.75h12.75a1.5 1.5 0 0 1 1.5 1.5V21")),
        ["redo"] = new(Geometry.Parse("M15 15l6-6m0 0-6-6M21 9H9a6 6 0 0 0 0 12h3")),
        ["rect"] = new(new RectangleGeometry(new Rect(3.75, 5.25, 16.5, 13.5), 2.5, 2.5)),
        ["ellipse"] = new(new EllipseGeometry(new Point(12, 12), 8.25, 8.25)),
        ["arrow"] = new(Geometry.Parse("M4.5 19.5 L19.5 4.5 M8.25 4.5 H19.5 V15.75")),
        ["pen"] = new(Geometry.Parse("m16.862 4.487 1.687-1.688a1.875 1.875 0 1 1 2.652 2.652L6.832 19.82a4.5 4.5 0 0 1-1.897 1.13l-2.685.8.8-2.685a4.5 4.5 0 0 1 1.13-1.897L16.863 4.487Zm0 0L19.5 7.125")),
        ["text"] = new(Geometry.Parse("M5.5 6.75V5.25A.75.75 0 0 1 6.25 4.5h11.5a.75.75 0 0 1 .75.75v1.5M12 4.5v15m-3.25 0h6.5")),
        ["mosaic"] = new(new RectangleGeometry(new Rect(3.75, 3.75, 16.5, 16.5), 3, 3), MosaicTiles()),
        ["pin"] = new(new GeometryGroup { Transform = new RotateTransform(35, 12, 12), Children = { Geometry.Parse("M8.5 4.5h7M9.75 4.5v4.25L7.5 12v1.5h9V12l-2.25-3.25V4.5M12 13.5V20") } }),
        ["ocr"] = new(Geometry.Parse("M4 8V6a2 2 0 0 1 2-2h2M16 4h2a2 2 0 0 1 2 2v2M20 16v2a2 2 0 0 1-2 2h-2M8 20H6a2 2 0 0 1-2-2v-2 M8 9.25h8M8 12.25h5.5M8 15.25h6.5")),
        ["translate"] = new(Geometry.Parse("m10.5 21 5.25-11.25L21 21m-9-3h7.5M3 5.621a48.474 48.474 0 0 1 6-.371m0 0c1.12 0 2.233.038 3.334.114M9 5.25V3m3.334 2.364C11.176 10.658 7.69 15.08 3 17.502m9.334-12.138c.896.061 1.785.147 2.666.257m-4.589 8.495a18.023 18.023 0 0 1-3.827-5.802")),
        ["scroll"] = new(new GeometryGroup
        {
            Children = { new RectangleGeometry(new Rect(5.25, 3.5, 13.5, 17), 2.5, 2.5), Geometry.Parse("M9.5 12.5 L12 15 L14.5 12.5 M12 8.5 V15") }
        }),
        ["video"] = new(Geometry.Parse("m15.75 10.5 4.72-4.72a.75.75 0 0 1 1.28.53v11.38a.75.75 0 0 1-1.28.53l-4.72-4.72M4.5 18.75h9a2.25 2.25 0 0 0 2.25-2.25v-9a2.25 2.25 0 0 0-2.25-2.25h-9A2.25 2.25 0 0 0 2.25 7.5v9a2.25 2.25 0 0 0 2.25 2.25Z")),
        ["undo"] = new(Geometry.Parse("M9 15 3 9m0 0 6-6M3 9h12a6 6 0 0 1 0 12h-3")),
        ["save"] = new(Geometry.Parse("M3 16.5v2.25A2.25 2.25 0 0 0 5.25 21h13.5A2.25 2.25 0 0 0 21 18.75V16.5M16.5 12 12 16.5m0 0L7.5 12m4.5 4.5V3")),
        ["close"] = new(Geometry.Parse("M6 18 18 6M6 6l12 12")),
        ["check"] = new(Geometry.Parse("m4.5 12.75 6 6 9-13.5")),
        ["speaker"] = new(Geometry.Parse("M19.114 5.636a9 9 0 0 1 0 12.728M16.463 8.288a5.25 5.25 0 0 1 0 7.424M6.75 8.25l4.72-4.72a.75.75 0 0 1 1.28.53v15.88a.75.75 0 0 1-1.28.53l-4.72-4.72H4.51c-.88 0-1.704-.507-1.938-1.354A9.009 9.009 0 0 1 2.25 12c0-.83.112-1.633.322-2.396C2.806 8.756 3.63 8.25 4.51 8.25H6.75Z")),
        ["mic"] = new(Geometry.Parse("M12 18.75a6 6 0 0 0 6-6v-1.5m-6 7.5a6 6 0 0 1-6-6v-1.5m6 7.5v3.75m-3.75 0h7.5M12 15.75a3 3 0 0 1-3-3V4.5a3 3 0 1 1 6 0v8.25a3 3 0 0 1-3 3Z")),
        ["cursor"] = new(Geometry.Parse("M15.042 21.672 13.684 16.6m0 0-2.51 2.225.569-9.47 5.227 7.917-3.286-.672ZM12 2.25V4.5m5.834.166-1.591 1.591M20.25 10.5H18M7.757 14.743l-1.59 1.59M6 10.5H3.75m4.007-4.243-1.59-1.59")),
        ["copy"] = new(Geometry.Parse("M16.5 8.25V6a2.25 2.25 0 0 0-2.25-2.25H6A2.25 2.25 0 0 0 3.75 6v8.25A2.25 2.25 0 0 0 6 16.5h2.25m8.25-8.25H18a2.25 2.25 0 0 1 2.25 2.25V18A2.25 2.25 0 0 1 18 20.25h-7.5A2.25 2.25 0 0 1 8.25 18v-1.5m8.25-8.25h-6a2.25 2.25 0 0 0-2.25 2.25v6")),
        ["keyboard"] = new(new GeometryGroup
        {
            Children =
            {
                new RectangleGeometry(new Rect(2.75, 5, 18.5, 14), 2.5, 2.5),
                Geometry.Parse("M6.5 9.25h.01M10.25 9.25h.01M13.75 9.25h.01M17.5 9.25h.01M8.25 12.25h.01M12 12.25h.01M15.75 12.25h.01M7.5 15.5h9")
            }
        }),
        ["settings"] = new(new GeometryGroup
        {
            Children = { Geometry.Parse("M9.594 3.94c.09-.542.56-.94 1.11-.94h2.593c.55 0 1.02.398 1.11.94l.213 1.281c.063.374.313.686.645.87.074.04.147.083.22.127.325.196.72.257 1.075.124l1.217-.456a1.125 1.125 0 0 1 1.37.49l1.296 2.247a1.125 1.125 0 0 1-.26 1.431l-1.003.827c-.293.241-.438.613-.43.992a7.723 7.723 0 0 1 0 .255c-.008.378.137.75.43.991l1.004.827c.424.35.534.955.26 1.43l-1.298 2.247a1.125 1.125 0 0 1-1.369.491l-1.217-.456c-.355-.133-.75-.072-1.076.124a6.47 6.47 0 0 1-.22.128c-.331.183-.581.495-.644.869l-.213 1.281c-.09.543-.56.94-1.11.94h-2.594c-.55 0-1.019-.398-1.11-.94l-.213-1.281c-.062-.374-.312-.686-.644-.87a6.52 6.52 0 0 1-.22-.127c-.325-.196-.72-.257-1.076-.124l-1.217.456a1.125 1.125 0 0 1-1.369-.49l-1.297-2.247a1.125 1.125 0 0 1 .26-1.431l1.004-.827c.292-.24.437-.613.43-.991a6.932 6.932 0 0 1 0-.255c.007-.38-.138-.751-.43-.992l-1.004-.827a1.125 1.125 0 0 1-.26-1.43l1.297-2.247a1.125 1.125 0 0 1 1.37-.491l1.216.456c.356.133.751.072 1.076-.124.072-.044.146-.086.22-.128.332-.183.582-.495.644-.869l.214-1.28Z"), new EllipseGeometry(new Point(12, 12), 3, 3) }
        })
    };

    private static Geometry MosaicTiles()
    {
        var group = new GeometryGroup();
        foreach (var (x, y) in new[] { (7d, 7d), (14d, 7d), (10.5, 10.5), (7d, 14d), (14d, 14d) })
        {
            group.Children.Add(new RectangleGeometry(new Rect(x, y, 3, 3), 0.5, 0.5));
        }
        return group;
    }

    internal static Geometry Outline(string name) => Glyphs[name].Outline;

    public static Viewbox Create(string name, double size = 20)
    {
        var glyph = Glyphs[name];
        var outline = new WpfPath
        {
            Data = glyph.Outline,
            StrokeThickness = name == "check" ? CheckStrokeWidth : StrokeWidth,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        var foreground = new Binding
        {
            RelativeSource = RelativeSource.Self,
            Path = new PropertyPath(TextElement.ForegroundProperty)
        };
        outline.SetBinding(Shape.StrokeProperty, foreground);
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(outline);
        if (glyph.Solid != null)
        {
            var solid = new WpfPath { Data = glyph.Solid };
            solid.SetBinding(Shape.FillProperty, foreground);
            canvas.Children.Add(solid);
        }
        return new Viewbox
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Child = canvas,
            IsHitTestVisible = false
        };
    }
}
