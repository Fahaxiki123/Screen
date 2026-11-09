using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Screen.App;

/// <summary>
/// Shared vector icons for the annotation toolbars. The designs combine thin
/// outlines (stroked) and solid tiles (filled); a dual-path icon inside a
/// Viewbox keeps both layers scaled and aligned as one unit, with the current
/// button Foreground driving both brushes.
/// </summary>
internal static class ToolbarIcons
{
    public static readonly Geometry MosaicFrame = BuildMosaicFrame();
    public static readonly Geometry MosaicBlocks = BuildMosaicBlocks();

    public static FrameworkElement DualIcon(
        Geometry strokeGeometry,
        Geometry fillGeometry,
        double strokeThickness)
    {
        var fillPath = new System.Windows.Shapes.Path
        {
            Data = fillGeometry,
            Stretch = Stretch.None
        };
        fillPath.SetBinding(
            Shape.FillProperty,
            new Binding(nameof(Button.Foreground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });
        var strokePath = new System.Windows.Shapes.Path
        {
            Data = strokeGeometry,
            Stretch = Stretch.None,
            StrokeThickness = strokeThickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        strokePath.SetBinding(
            Shape.StrokeProperty,
            new Binding(nameof(Button.Foreground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(fillPath);
        canvas.Children.Add(strokePath);
        return new Viewbox
        {
            Width = 20,
            Height = 20,
            Stretch = Stretch.Uniform,
            Child = canvas
        };
    }

    // Mosaic: rounded square frame with scattered solid tiles inside
    // (coordinates transcribed from the design image's pixel grid).
    private static Geometry BuildMosaicFrame()
    {
        var geometry = new RectangleGeometry(new Rect(2.5, 2, 19, 20), 3, 3);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry BuildMosaicBlocks()
    {
        var group = new GeometryGroup();
        foreach (var block in new[]
                 {
                     new Rect(5.5, 5, 2, 2),
                     new Rect(17.5, 7, 4, 3),
                     new Rect(10.5, 9, 2, 2),
                     new Rect(19.5, 10, 2, 4),
                     new Rect(13.5, 11, 4, 2),
                     new Rect(5.5, 13, 2, 2),
                     new Rect(9.5, 14, 4, 3),
                     new Rect(16.5, 14, 5, 3),
                     new Rect(13.5, 17, 4, 3),
                     new Rect(19.5, 17, 2, 3)
                 })
        {
            group.Children.Add(new RectangleGeometry(block));
        }
        group.Freeze();
        return group;
    }
}
