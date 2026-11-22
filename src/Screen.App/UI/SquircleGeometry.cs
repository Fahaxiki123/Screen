using System;
using System.Windows;
using System.Windows.Media;

namespace Screen.App;

internal static class SquircleGeometry
{
    internal const double TopPointerHeight = 6;

    public static StreamGeometry Create(double width, double height, double radius, double smoothing = 0.60, double? topPointerX = null)
    {
        var geometry = new StreamGeometry();
        if (width > 0 && height > 0 && double.IsFinite(width) && double.IsFinite(height))
        {
            using var context = geometry.Open();
            double maxExtent = Math.Min(width, height) / 2;
            double r = Math.Clamp(radius, 0, maxExtent);
            double top = topPointerX.HasValue ? TopPointerHeight : 0;
            if (r > 0.5)
            {
                DrawCorners(context, width, height, r, Math.Clamp(smoothing, 0, 1), maxExtent, top, topPointerX);
            }
            else
            {
                context.BeginFigure(new Point(0, top), true, true);
                DrawTopPointer(context, width, 0, topPointerX);
                context.LineTo(new Point(width, top), true, false);
                context.LineTo(new Point(width, top + height), true, false);
                context.LineTo(new Point(0, top + height), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static void DrawTopPointer(StreamGeometryContext context, double width, double shoulder, double? pointerX)
    {
        if (pointerX is not double requested || width < 2 * (shoulder + 1))
            return;

        // Join the nub directly to the two flat shoulders so the glass rim has no seam.
        double tip = Math.Clamp(requested, shoulder + 1, width - shoulder - 1);
        double left = Math.Max(shoulder, tip - 7);
        double right = Math.Min(width - shoulder, tip + 7);
        context.LineTo(new Point(left, TopPointerHeight), true, false);
        context.BezierTo(new Point((left + tip) / 2, TopPointerHeight), new Point(tip - 1, 0), new Point(tip, 0), true, true);
        context.BezierTo(new Point(tip + 1, 0), new Point((tip + right) / 2, TopPointerHeight), new Point(right, TopPointerHeight), true, true);
    }

    private static void DrawCorners(StreamGeometryContext context, double width, double height, double r, double smoothing, double maxExtent,
        double top, double? topPointerX)
    {
        // Leave room for both shoulders when a radius approaches half of the shorter side.
        double s = Math.Min(smoothing, maxExtent / r - 1);
        double p = (1 + s) * r;
        double arcMeasure = Math.PI / 2 * (1 - s);
        double arcSectionLength = Math.Sin(arcMeasure / 2) * r * Math.Sqrt(2);
        double beta = Math.PI / 4 * s;
        double p3ToP4 = r * Math.Tan(beta / 2);
        double c = p3ToP4 * Math.Cos(beta);
        double d = c * Math.Tan(beta);
        double b = (p - arcSectionLength - c - d) / 3;
        double a = 2 * b;
        var arcSize = new Size(r, r);

        // Rotate the same cubic–arc–cubic parameterization through the four corners.
        void Corner(Point origin, Vector along, Vector inward)
        {
            Point At(double x, double y) => origin + x * along + y * inward;
            context.LineTo(At(-p, 0), true, false);
            context.BezierTo(At(a - p, 0), At(a + b - p, 0), At(a + b + c - p, d), true, true);
            context.ArcTo(At(-d, p - a - b - c), arcSize, 0, false, SweepDirection.Clockwise, true, true);
            context.BezierTo(At(0, p - a - b), At(0, p - a), At(0, p), true, true);
        }

        context.BeginFigure(new Point(p, top), true, true);
        DrawTopPointer(context, width, p, topPointerX);
        Corner(new Point(width, top), new Vector(1, 0), new Vector(0, 1));
        Corner(new Point(width, top + height), new Vector(0, 1), new Vector(-1, 0));
        Corner(new Point(0, top + height), new Vector(-1, 0), new Vector(0, -1));
        Corner(new Point(0, top), new Vector(0, -1), new Vector(1, 0));
    }
}
