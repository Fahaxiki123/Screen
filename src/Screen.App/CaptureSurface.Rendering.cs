using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Rendering logic for CaptureSurface (OnRender, drawing methods, mosaic effects).
/// </summary>
internal sealed partial class CaptureSurface
{
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        RefreshProcessingVisual();
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // Keep the dimmed area outside the selection untouched. An annotation
        // can meet the boundary or outlive a later selection resize.
        dc.DrawImage(_source, new Rect(0, 0, ActualWidth, ActualHeight));
        var hasMosaic = _annotations.Any(item => item.Kind == AnnotationKind.Mosaic);
        PixelBuffer compositedSource;
        if (hasMosaic)
        {
            compositedSource = GetMosaicComposite();
        }
        else
        {
            // Every mosaic was undone — drop the full-screen composite buffers
            // instead of carrying them until the window closes.
            _mosaicComposite = null;
            _mosaicCompositeSource = null;
            _appliedMosaicSnapshots = null;
            compositedSource = _sourceBuffer;
        }
        dc.PushTransform(new ScaleTransform(ActualWidth / _frame.Width, ActualHeight / _frame.Height));
        dc.PushClip(new RectangleGeometry(ToRect(Selection)));
        if (hasMosaic)
        {
            dc.DrawImage(_mosaicCompositeSource!, new Rect(0, 0, _frame.Width, _frame.Height));
        }
        DrawAnnotations(
            dc,
            _annotations.Where(item => item.Kind != AnnotationKind.Mosaic),
            new Vector(0, 0));
        DrawSelectedAnnotation(dc);
        if (_drawing)
        {
            DrawWorkingAnnotation(dc, compositedSource);
        }
        dc.Pop();

        var full = new RectangleGeometry(new Rect(0, 0, _frame.Width, _frame.Height));
        if (!Selection.IsEmpty && ShowSelectionChrome)
        {
            var selected = new RectangleGeometry(ToRect(Selection));
            var dim = new CombinedGeometry(GeometryCombineMode.Exclude, full, selected);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(148, 0, 0, 0)), null, dim);
            dc.DrawRectangle(null, new Pen(_accentBrush, 1), ToRect(Selection));
            DrawSelectionHandles(dc);
            DrawSizeLabel(dc);
        }
        else if (Selection.IsEmpty)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(112, 0, 0, 0)), null, full.Rect);
        }

        if (!_selectionCommitted)
        {
            DrawMagnifier(dc);
        }
        else if (CanShowMosaicBrushCursor && Cursor == System.Windows.Input.Cursors.None)
        {
            var radius = CurrentStyle.Thickness / 2;
            dc.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
                new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1.5),
                new Point(_cursor.X, _cursor.Y),
                radius,
                radius);
            dc.DrawEllipse(
                null,
                new Pen(new SolidColorBrush(Color.FromArgb(180, 35, 40, 48)), 1),
                new Point(_cursor.X, _cursor.Y),
                Math.Max(1, radius - 1.5),
                Math.Max(1, radius - 1.5));
        }
        dc.Pop();
    }

    private void DrawSelectedAnnotation(DrawingContext dc)
    {
        if (_selectedAnnotationId is not Guid id)
        {
            return;
        }
        var item = _annotations.FirstOrDefault(annotation => annotation.Id == id);
        if (item is null)
        {
            SelectAnnotation(null);
            return;
        }
        var rect = ToRect(Inflate(GetVisualBounds(item), 5));
        var pen = new Pen(_accentBrush, 1.5)
        {
            DashStyle = new DashStyle(new double[] { 4, 3 }, 0)
        };
        var selectionWash = _accentBrush.CloneCurrentValue();
        selectionWash.Opacity = 0.07;
        dc.DrawRoundedRectangle(selectionWash, pen, rect, 6, 6);
        foreach (var point in new[]
                 {
                     rect.TopLeft,
                     new Point(rect.Left + rect.Width / 2, rect.Top),
                     rect.TopRight,
                     new Point(rect.Right, rect.Top + rect.Height / 2),
                     rect.BottomRight,
                     new Point(rect.Left + rect.Width / 2, rect.Bottom),
                     rect.BottomLeft,
                     new Point(rect.Left, rect.Top + rect.Height / 2)
                 })
        {
            dc.DrawRectangle(Brushes.White, new Pen(_accentBrush, 1), new Rect(point.X - 2.5, point.Y - 2.5, 5, 5));
        }
    }

    private readonly record struct MosaicSnapshot(
        Guid Id,
        PixelRect Bounds,
        int Variant,
        double Intensity,
        double Thickness);

    /// <summary>
    /// Returns the full-screen buffer with every committed mosaic applied.
    /// Rebuilds are incremental: only the union of regions whose mosaics
    /// changed (old ∪ new bounds) is restored from the source and re-composited,
    /// so dragging or resizing a mosaic never copies the whole screen per frame.
    /// </summary>
    private PixelBuffer GetMosaicComposite()
    {
        if (_mosaicComposite is null)
        {
            var pixels = new byte[_sourceBuffer.Pixels.Length];
            _sourceBuffer.Pixels.AsSpan().CopyTo(pixels);
            _mosaicComposite = new PixelBuffer(
                pixels, _sourceBuffer.Width, _sourceBuffer.Height, _sourceBuffer.Stride);
            _mosaicCompositeSource = new WriteableBitmap(
                _sourceBuffer.Width, _sourceBuffer.Height, 96, 96, PixelFormats.Bgra32, null);
            _appliedMosaicSnapshots = null;
            _mosaicCompositeDirty = true;
        }

        if (!_mosaicCompositeDirty)
        {
            return _mosaicComposite;
        }

        var mosaics = _annotations
            .Where(item => item.Kind == AnnotationKind.Mosaic)
            .OrderBy(item => item.ZIndex)
            .ToArray();
        var region = DirtyMosaicRegion(mosaics, _appliedMosaicSnapshots)
            .Intersect(new PixelRect(0, 0, _mosaicComposite.Width, _mosaicComposite.Height));
        _appliedMosaicSnapshots = mosaics
            .Select(item => new MosaicSnapshot(
                item.Id, item.Bounds, item.Style.Variant, item.Style.Intensity, item.Style.Thickness))
            .ToList();
        _mosaicCompositeDirty = false;
        if (region.IsEmpty)
        {
            // The annotation change did not involve mosaics (e.g. a text edit).
            return _mosaicComposite;
        }

        CopyRegion(_sourceBuffer, _mosaicComposite, region);
        foreach (var mosaic in mosaics)
        {
            // Re-apply every mosaic over the dirty region in stacking order so
            // overlapping mosaics keep compositing correctly.
            var local = mosaic with { Bounds = mosaic.Bounds.Intersect(region) };
            if (!local.Bounds.IsEmpty)
            {
                ApplyMosaic(_mosaicComposite, local);
            }
        }
        var stride = _mosaicComposite.Stride;
        _mosaicCompositeSource!.WritePixels(
            new Int32Rect(region.X, region.Y, region.Width, region.Height),
            _mosaicComposite.Pixels,
            stride,
            region.Y * stride + region.X * 4);
        return _mosaicComposite;
    }

    private PixelRect DirtyMosaicRegion(AnnotationItem[] mosaics, List<MosaicSnapshot>? previous)
    {
        if (previous is null)
        {
            return new PixelRect(0, 0, _sourceBuffer.Width, _sourceBuffer.Height);
        }
        var dirty = new PixelRect(0, 0, 0, 0);
        var previousById = previous.ToDictionary(snapshot => snapshot.Id);
        foreach (var item in mosaics)
        {
            var current = new MosaicSnapshot(
                item.Id, item.Bounds, item.Style.Variant, item.Style.Intensity, item.Style.Thickness);
            if (previousById.Remove(item.Id, out var old))
            {
                if (old == current)
                {
                    continue;
                }
                dirty = UnionRegion(dirty, old.Bounds);
            }
            dirty = UnionRegion(dirty, item.Bounds);
        }
        foreach (var removed in previousById.Values)
        {
            dirty = UnionRegion(dirty, removed.Bounds);
        }
        return dirty;
    }

    private static PixelRect UnionRegion(PixelRect first, PixelRect second)
    {
        if (first.IsEmpty)
        {
            return second;
        }
        if (second.IsEmpty)
        {
            return first;
        }
        var x = Math.Min(first.X, second.X);
        var y = Math.Min(first.Y, second.Y);
        return new PixelRect(
            x, y, Math.Max(first.Right, second.Right) - x, Math.Max(first.Bottom, second.Bottom) - y);
    }

    private static void CopyRegion(PixelBuffer source, PixelBuffer destination, PixelRect region)
    {
        var rowBytes = region.Width * 4;
        for (var y = region.Y; y < region.Bottom; y++)
        {
            var offset = y * source.Stride + region.X * 4;
            source.Pixels.AsSpan(offset, rowBytes).CopyTo(destination.Pixels.AsSpan(offset, rowBytes));
        }
    }

    private void DrawWorkingAnnotation(DrawingContext dc, PixelBuffer compositedSource)
    {
        var tool = Tool switch
        {
            CaptureTool.Rectangle => AnnotationKind.Rectangle,
            CaptureTool.Ellipse => AnnotationKind.Ellipse,
            CaptureTool.Line => AnnotationKind.Line,
            CaptureTool.Arrow => AnnotationKind.Arrow,
            CaptureTool.Pen => AnnotationKind.Pen,
            CaptureTool.Mosaic => AnnotationKind.Mosaic,
            _ => AnnotationKind.Line
        };
        var freehand = tool == AnnotationKind.Pen || (tool == AnnotationKind.Mosaic && MosaicMode == MosaicDrawMode.Brush);
        var end = ClampAnnotationPoint(_cursor);
        IReadOnlyList<PixelPoint> points = freehand ? _workingPoints : new[] { _start, end };
        var bounds = tool == AnnotationKind.Mosaic && MosaicMode == MosaicDrawMode.Brush
            ? BoundsAroundPoints(points, CurrentStyle.Thickness / 2).Intersect(Selection)
            : RectFromPoints(_start, end);
        var item = new AnnotationItem(Guid.Empty, tool, points, bounds, CurrentStyle, null, null, int.MaxValue);
        if (tool == AnnotationKind.Mosaic)
        {
            DrawMosaicPreview(dc, compositedSource, item);
        }
        else
        {
            DrawItem(dc, item, new Vector());
        }
    }

    // Callers must filter mosaics out: they are composited into the base image
    // by GetMosaicComposite / RenderSelection, never drawn as vector overlays.
    private static void DrawAnnotations(
        DrawingContext dc,
        IEnumerable<AnnotationItem> items,
        Vector offset)
    {
        foreach (var item in items.OrderBy(item => item.ZIndex))
        {
            DrawItem(dc, item, offset);
        }
    }

    private static void DrawItem(DrawingContext dc, AnnotationItem item, Vector offset)
    {
        var color = Color.FromArgb(
            (byte)(item.Style.Argb >> 24),
            (byte)(item.Style.Argb >> 16),
            (byte)(item.Style.Argb >> 8),
            (byte)item.Style.Argb);
        var brush = new SolidColorBrush(color);
        var pen = new Pen(brush, item.Style.Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        var rect = new Rect(
            item.Bounds.X + offset.X,
            item.Bounds.Y + offset.Y,
            item.Bounds.Width,
            item.Bounds.Height);
        PixelPoint PointAt(int index) => new(item.Points[index].X + offset.X, item.Points[index].Y + offset.Y);

        switch (item.Kind)
        {
            case AnnotationKind.Rectangle:
                dc.DrawRoundedRectangle(null, pen, rect, 1, 1);
                break;
            case AnnotationKind.Ellipse:
                dc.DrawEllipse(null, pen, new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
                break;
            case AnnotationKind.Line:
                if (item.Points.Count >= 2)
                {
                    var a = PointAt(0);
                    var b = PointAt(1);
                    dc.DrawLine(pen, new Point(a.X, a.Y), new Point(b.X, b.Y));
                }
                break;
            case AnnotationKind.Arrow:
                if (item.Points.Count >= 2)
                {
                    var a = PointAt(0);
                    var b = PointAt(1);
                    DrawArrow(dc, pen, new Point(a.X, a.Y), new Point(b.X, b.Y));
                }
                break;
            case AnnotationKind.Pen:
                if (item.Points.Count >= 2)
                {
                    var geometry = new StreamGeometry();
                    using var context = geometry.Open();
                    var first = PointAt(0);
                    context.BeginFigure(new Point(first.X, first.Y), false, false);
                    for (var i = 1; i < item.Points.Count; i++)
                    {
                        var point = PointAt(i);
                        context.LineTo(new Point(point.X, point.Y), true, false);
                    }
                    geometry.Freeze();
                    dc.DrawGeometry(null, pen, geometry);
                }
                break;
            case AnnotationKind.Text:
                var formatted = CreateTextLayout(item.Text ?? string.Empty, item.Style, Math.Max(1, rect.Width));
                dc.DrawText(formatted, rect.TopLeft);
                break;
            case AnnotationKind.Number:
                var radius = Math.Max(12, item.Style.FontSize * 0.62);
                var center = rect.TopLeft;
                dc.DrawEllipse(brush, null, center, radius, radius);
                var numberText = new FormattedText(
                    item.Number?.ToString() ?? string.Empty,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    radius,
                    Brushes.White,
                    1.0);
                dc.DrawText(numberText, new Point(center.X - numberText.Width / 2, center.Y - numberText.Height / 2));
                break;
        }
    }

    private static void DrawArrow(DrawingContext dc, Pen pen, Point start, Point end)
    {
        dc.DrawLine(pen, start, end);
        var vector = start - end;
        if (vector.Length < 1)
        {
            return;
        }
        vector.Normalize();
        var perpendicular = new Vector(-vector.Y, vector.X);
        var size = Math.Max(10, pen.Thickness * 4);
        dc.DrawLine(pen, end, end + vector * size + perpendicular * size * 0.45);
        dc.DrawLine(pen, end, end + vector * size - perpendicular * size * 0.45);
    }

    private static void DrawMosaicPreview(DrawingContext dc, PixelBuffer source, AnnotationItem item)
    {
        var clipped = item.Bounds.Intersect(new PixelRect(0, 0, source.Width, source.Height));
        if (clipped.IsEmpty)
        {
            return;
        }
        var preview = source.Crop(clipped);
        ApplyMosaic(preview, OffsetItem(item, -clipped.X, -clipped.Y));
        dc.DrawImage(preview.ToBitmapSource(), ToRect(clipped));
    }

    private static void ApplyMosaic(PixelBuffer buffer, AnnotationItem item)
    {
        var clipped = item.Bounds.Intersect(new PixelRect(0, 0, buffer.Width, buffer.Height));
        if (clipped.IsEmpty)
        {
            return;
        }
        // Work on a copy of just the affected region instead of the whole
        // buffer: the effect is position-local, so a sub-rect yields exactly
        // the same pixels while allocating only the annotation's size.
        var effected = buffer.Crop(clipped);
        var region = new PixelRect(0, 0, effected.Width, effected.Height);
        var localItem = OffsetItem(item, -clipped.X, -clipped.Y);
        if (localItem.Style.Variant % 100 == AnnotationVariants.MosaicBlur)
        {
            BoxBlur(effected, region, Math.Clamp((int)Math.Round(3 + localItem.Style.Intensity * 8), 3, 12));
        }
        else
        {
            Pixelate(effected, region, Math.Clamp((int)Math.Round(6 + localItem.Style.Intensity * 16), 6, 24));
        }

        var mask = BuildMosaicMask(localItem, region);
        for (var y = 0; y < region.Height; y++)
        {
            var maskRow = y * region.Width;
            var targetRow = (clipped.Y + y) * buffer.Stride + clipped.X * 4;
            var sourceRow = y * effected.Stride;
            for (var x = 0; x < region.Width; x++)
            {
                if (!mask[maskRow + x])
                {
                    continue;
                }
                effected.Pixels.AsSpan(sourceRow + x * 4, 4)
                    .CopyTo(buffer.Pixels.AsSpan(targetRow + x * 4, 4));
            }
        }
    }

    private static void Pixelate(PixelBuffer buffer, PixelRect bounds, int block)
    {
        for (var top = bounds.Y; top < bounds.Bottom; top += block)
            for (var left = bounds.X; left < bounds.Right; left += block)
            {
                long b = 0, g = 0, r = 0, a = 0, count = 0;
                var bottom = Math.Min(top + block, bounds.Bottom);
                var right = Math.Min(left + block, bounds.Right);
                for (var y = top; y < bottom; y++)
                    for (var x = left; x < right; x++)
                    {
                        var index = y * buffer.Stride + x * 4;
                        b += buffer.Pixels[index];
                        g += buffer.Pixels[index + 1];
                        r += buffer.Pixels[index + 2];
                        a += buffer.Pixels[index + 3];
                        count++;
                    }
                for (var y = top; y < bottom; y++)
                    for (var x = left; x < right; x++)
                    {
                        var index = y * buffer.Stride + x * 4;
                        buffer.Pixels[index] = (byte)(b / count);
                        buffer.Pixels[index + 1] = (byte)(g / count);
                        buffer.Pixels[index + 2] = (byte)(r / count);
                        buffer.Pixels[index + 3] = (byte)(a / count);
                    }
            }
    }

    private static void BoxBlur(PixelBuffer buffer, PixelRect bounds, int radius)
    {
        var width = bounds.Width;
        var height = bounds.Height;
        var horizontal = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        for (var y = 0; y < height; y++)
        {
            long b = 0, g = 0, r = 0, a = 0;
            var initialRight = Math.Min(width - 1, radius);
            for (var x = 0; x <= initialRight; x++)
            {
                var source = (bounds.Y + y) * buffer.Stride + (bounds.X + x) * 4;
                b += buffer.Pixels[source];
                g += buffer.Pixels[source + 1];
                r += buffer.Pixels[source + 2];
                a += buffer.Pixels[source + 3];
            }
            for (var x = 0; x < width; x++)
            {
                var left = Math.Max(0, x - radius);
                var right = Math.Min(width - 1, x + radius);
                var count = right - left + 1;
                var target = (y * width + x) * 4;
                horizontal[target] = (byte)(b / count);
                horizontal[target + 1] = (byte)(g / count);
                horizontal[target + 2] = (byte)(r / count);
                horizontal[target + 3] = (byte)(a / count);
                var remove = x - radius;
                var add = x + radius + 1;
                if (remove >= 0)
                {
                    var index = (bounds.Y + y) * buffer.Stride + (bounds.X + remove) * 4;
                    b -= buffer.Pixels[index];
                    g -= buffer.Pixels[index + 1];
                    r -= buffer.Pixels[index + 2];
                    a -= buffer.Pixels[index + 3];
                }
                if (add < width)
                {
                    var index = (bounds.Y + y) * buffer.Stride + (bounds.X + add) * 4;
                    b += buffer.Pixels[index];
                    g += buffer.Pixels[index + 1];
                    r += buffer.Pixels[index + 2];
                    a += buffer.Pixels[index + 3];
                }
            }
        }

        for (var x = 0; x < width; x++)
        {
            long b = 0, g = 0, r = 0, a = 0;
            var initialBottom = Math.Min(height - 1, radius);
            for (var y = 0; y <= initialBottom; y++)
            {
                var source = (y * width + x) * 4;
                b += horizontal[source];
                g += horizontal[source + 1];
                r += horizontal[source + 2];
                a += horizontal[source + 3];
            }
            for (var y = 0; y < height; y++)
            {
                var top = Math.Max(0, y - radius);
                var bottom = Math.Min(height - 1, y + radius);
                var count = bottom - top + 1;
                var target = (bounds.Y + y) * buffer.Stride + (bounds.X + x) * 4;
                buffer.Pixels[target] = (byte)(b / count);
                buffer.Pixels[target + 1] = (byte)(g / count);
                buffer.Pixels[target + 2] = (byte)(r / count);
                buffer.Pixels[target + 3] = (byte)(a / count);
                var remove = y - radius;
                var add = y + radius + 1;
                if (remove >= 0)
                {
                    var index = (remove * width + x) * 4;
                    b -= horizontal[index];
                    g -= horizontal[index + 1];
                    r -= horizontal[index + 2];
                    a -= horizontal[index + 3];
                }
                if (add < height)
                {
                    var index = (add * width + x) * 4;
                    b += horizontal[index];
                    g += horizontal[index + 1];
                    r += horizontal[index + 2];
                    a += horizontal[index + 3];
                }
            }
        }
    }

    private static bool[] BuildMosaicMask(AnnotationItem item, PixelRect bounds)
    {
        var mask = new bool[checked(bounds.Width * bounds.Height)];
        if (item.Style.Variant < 100)
        {
            Array.Fill(mask, true);
            return mask;
        }
        var radius = Math.Max(3, item.Style.Thickness / 2);
        if (item.Points.Count == 1)
        {
            MarkCircle(mask, bounds, item.Points[0].X, item.Points[0].Y, radius);
            return mask;
        }
        for (var index = 1; index < item.Points.Count; index++)
        {
            var from = item.Points[index - 1];
            var to = item.Points[index];
            var distance = Math.Max(1, Distance(from, to));
            var steps = Math.Max(1, (int)Math.Ceiling(distance / Math.Max(1, radius / 2)));
            for (var step = 0; step <= steps; step++)
            {
                var t = step / (double)steps;
                MarkCircle(mask, bounds, from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t, radius);
            }
        }
        return mask;
    }

    private static void MarkCircle(bool[] mask, PixelRect bounds, double centerX, double centerY, double radius)
    {
        var left = Math.Max(bounds.X, (int)Math.Floor(centerX - radius));
        var right = Math.Min(bounds.Right - 1, (int)Math.Ceiling(centerX + radius));
        var top = Math.Max(bounds.Y, (int)Math.Floor(centerY - radius));
        var bottom = Math.Min(bounds.Bottom - 1, (int)Math.Ceiling(centerY + radius));
        var squared = radius * radius;
        for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                if (dx * dx + dy * dy <= squared)
                {
                    mask[(y - bounds.Y) * bounds.Width + x - bounds.X] = true;
                }
            }
    }

    private void DrawSelectionHandles(DrawingContext dc)
    {
        var points = new[]
        {
            new Point(Selection.X, Selection.Y), new Point(Selection.X + Selection.Width / 2.0, Selection.Y),
            new Point(Selection.Right, Selection.Y), new Point(Selection.X, Selection.Y + Selection.Height / 2.0),
            new Point(Selection.Right, Selection.Y + Selection.Height / 2.0), new Point(Selection.X, Selection.Bottom),
            new Point(Selection.X + Selection.Width / 2.0, Selection.Bottom), new Point(Selection.Right, Selection.Bottom)
        };
        foreach (var point in points)
        {
            dc.DrawEllipse(Brushes.White, new Pen(_accentBrush, 1), point, 4, 4);
        }
    }

    private void DrawSizeLabel(DrawingContext dc)
    {
        var text = new FormattedText(
            $"{Selection.Width} × {Selection.Height}",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            13,
            _overlayTextBrush,
            1.0);
        var x = Selection.X;
        var y = Selection.Y > 34 ? Selection.Y - 30 : Selection.Y + 8;
        dc.DrawRoundedRectangle(
            _overlayBaseBrush,
            null,
            new Rect(x, y, text.Width + 16, 24), 6, 6);
        dc.DrawText(text, new Point(x + 8, y + 3));
    }

    private void DrawMagnifier(DrawingContext dc)
    {
        // Professional HUD-style magnifier + color card. Layout follows a
        // strict vertical center axis: magnifier → color value → coordinates
        // → shortcut hint. Only the swatch sits left of the axis.
        const int imageWidth = MagnifierWidth;
        const int pad = 10;
        const int gap = 20;
        const int outerPadding = 12;

        // The sample keeps the display aspect (230:160), so pixel blocks stay
        // square at every zoom level. The panel size is FIXED: zooming only
        // changes the sampled area (i.e. the magnified content).
        var sampleWidth = MagnifierSamples[_magnifierSampleIndex];
        var srcH = Math.Max(1, (int)Math.Round(sampleWidth * MagnifierHeight / (double)MagnifierWidth));
        const int imageHeight = MagnifierHeight;
        var cellSize = imageWidth / (double)sampleWidth;

        const double valueAreaHeight = 30;
        const double coordsHeight = 16;
        var panelWidth = imageWidth + pad * 2;
        var panelHeight = imageHeight + 125;

        var sourceRect = new Int32Rect(
            Math.Clamp((int)_cursor.X - sampleWidth / 2, 0, Math.Max(0, _frame.Width - sampleWidth)),
            Math.Clamp((int)_cursor.Y - srcH / 2, 0, Math.Max(0, _frame.Height - srcH)),
            Math.Min(sampleWidth, _frame.Width),
            Math.Min(srcH, _frame.Height));
        var srcW = sourceRect.Width;

        // Positioning: right-below of the cursor by default; flips near edges.
        var candidates = new[]
        {
            new Point(_cursor.X + gap, _cursor.Y + gap),
            new Point(_cursor.X - panelWidth - gap, _cursor.Y + gap),
            new Point(_cursor.X + gap, _cursor.Y - panelHeight - gap),
            new Point(_cursor.X - panelWidth - gap, _cursor.Y - panelHeight - gap)
        };
        double x = outerPadding;
        double y = outerPadding;
        foreach (var candidate in candidates)
        {
            var boundedX = Math.Clamp(candidate.X, outerPadding, Math.Max(outerPadding, _frame.Width - panelWidth - outerPadding));
            var boundedY = Math.Clamp(candidate.Y, outerPadding, Math.Max(outerPadding, _frame.Height - panelHeight - outerPadding));
            var panel = new PixelRect((int)boundedX - pad, (int)boundedY - pad, panelWidth, panelHeight);
            if (MagnifierAvoidanceArea.IsEmpty || panel.Intersect(MagnifierAvoidanceArea).IsEmpty)
            {
                x = boundedX;
                y = boundedY;
                break;
            }
        }

        // Card chrome: graphite glass, hairline border, very soft shadow.
        var panelRect = new Rect(x - pad, y - pad, panelWidth, panelHeight);
        for (var shadowLayer = 4; shadowLayer >= 1; shadowLayer--)
        {
            var inset = 4 - shadowLayer;
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb((byte)(6 + shadowLayer * 4), 0, 0, 0)),
                null,
                new Rect(
                    panelRect.X - inset,
                    panelRect.Y + 2 + inset * 2,
                    panelRect.Width + inset * 2,
                    panelRect.Height + inset * 2),
                12 + inset,
                12 + inset);
        }
        dc.DrawRoundedRectangle(
            _overlayBaseBrush,
            new Pen(_overlayBorderBrush, 1),
            panelRect,
            12,
            12);

        var cardInnerLeft = panelRect.X + pad;
        var cardInnerWidth = panelWidth - pad * 2;
        var cardCenterX = cardInnerLeft + cardInnerWidth / 2.0;

        // Magnified pixels: one frozen bitmap per frame, exact integer blocks.
        var magnifierStride = imageWidth * 4;
        for (var row = 0; row < srcH; row++)
        {
            var fillTop = row * imageHeight / srcH;
            var fillBottom = (row + 1) * imageHeight / srcH;
            var sourceRow = (sourceRect.Y + row) * _sourceBuffer.Stride + sourceRect.X * 4;
            for (var col = 0; col < srcW; col++)
            {
                var source = sourceRow + col * 4;
                var b = _sourceBuffer.Pixels[source];
                var g = _sourceBuffer.Pixels[source + 1];
                var r = _sourceBuffer.Pixels[source + 2];
                var fillLeft = col * imageWidth / srcW;
                var fillRight = (col + 1) * imageWidth / srcW;
                for (var dy = fillTop; dy < fillBottom; dy++)
                {
                    var target = dy * magnifierStride + fillLeft * 4;
                    for (var dx = fillLeft; dx < fillRight; dx++)
                    {
                        _magnifierBuffer[target] = b;
                        _magnifierBuffer[target + 1] = g;
                        _magnifierBuffer[target + 2] = r;
                        _magnifierBuffer[target + 3] = 255;
                        target += 4;
                    }
                }
            }
        }
        var magnifierSource = BitmapSource.Create(
            imageWidth,
            imageHeight,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            _magnifierBuffer,
            magnifierStride);
        magnifierSource.Freeze();

        dc.PushClip(new RectangleGeometry(new Rect(x, y, imageWidth, imageHeight), 9, 9));
        dc.DrawImage(magnifierSource, new Rect(x, y, imageWidth, imageHeight));

        // Faint pixel grid for positioning (hidden at wide zooms).
        if (cellSize >= 5)
        {
            var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1);
            for (var col = 1; col < srcW; col++)
            {
                var gridX = x + col * imageWidth / (double)srcW;
                dc.DrawLine(gridPen, new Point(gridX, y), new Point(gridX, y + imageHeight));
            }
            for (var row = 1; row < srcH; row++)
            {
                var gridY = y + row * imageHeight / (double)srcH;
                dc.DrawLine(gridPen, new Point(x, gridY), new Point(x + imageWidth, gridY));
            }
        }

        // Crosshair: full-span hairlines + small sampling box, visible on any
        // background via a dark under-stroke.
        var cursorCol = (int)_cursor.X - sourceRect.X;
        var cursorRow = (int)_cursor.Y - sourceRect.Y;
        var blockLeft = x + cursorCol * imageWidth / (double)srcW;
        var blockTop = y + cursorRow * imageHeight / (double)srcH;
        var blockWidth = x + (cursorCol + 1) * imageWidth / (double)srcW - blockLeft;
        var blockHeight = y + (cursorRow + 1) * imageHeight / (double)srcH - blockTop;
        var focusX = blockLeft + blockWidth / 2.0;
        var focusY = blockTop + blockHeight / 2.0;
        var darkLine = new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 3);
        var lightLine = new Pen(new SolidColorBrush(Color.FromArgb(225, 255, 255, 255)), 1);
        dc.DrawLine(darkLine, new Point(x, focusY), new Point(x + imageWidth, focusY));
        dc.DrawLine(darkLine, new Point(focusX, y), new Point(focusX, y + imageHeight));
        dc.DrawLine(lightLine, new Point(x, focusY), new Point(x + imageWidth, focusY));
        dc.DrawLine(lightLine, new Point(focusX, y), new Point(focusX, y + imageHeight));
        var boxSize = Math.Clamp((blockWidth + blockHeight) / 2.0, 12, 20);
        var box = new Rect(focusX - boxSize / 2.0, focusY - boxSize / 2.0, boxSize, boxSize);
        dc.DrawRectangle(null, darkLine, box);
        dc.DrawRectangle(null, lightLine, box);
        dc.Pop();

        // Zoom badge (top-right, quiet).
        var zoom = imageWidth / (double)sampleWidth;
        var zoomLabel = Math.Abs(zoom - Math.Round(zoom)) < 0.05
            ? $"{zoom:0}×"
            : $"{zoom:0.0}×";
        var zoomText = new FormattedText(
            zoomLabel,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            PanelUi, 11.5, _overlayTextBrush, 1.0);
        var badge = new Rect(x + imageWidth - 8 - zoomText.Width - 16, y + 8, zoomText.Width + 16, 22);
        dc.DrawRoundedRectangle(
            _overlayBaseBrush,
            null,
            badge,
            6,
            6);
        dc.DrawText(
            zoomText,
            new Point(
                badge.X + (badge.Width - zoomText.Width) / 2.0,
                badge.Y + (badge.Height - zoomText.Height) / 2.0));

        // Divider → color value row. Strictly symmetric: swatch left, value
        // centered on the card axis, empty right column.
        var divider1Y = y + imageHeight + 10;
        dc.DrawLine(
            new Pen(_overlayBorderBrush, 1),
            new Point(cardInnerLeft, divider1Y),
            new Point(cardInnerLeft + cardInnerWidth, divider1Y));

        var valueTop = divider1Y + 10;
        var argb = GetPixelColor(_cursor);
        var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        var swatch = new Rect(cardInnerLeft, valueTop + 2, 26, 26);
        dc.DrawRoundedRectangle(
            new SolidColorBrush(color),
            new Pen(new SolidColorBrush(Color.FromArgb(51, 255, 255, 255)), 1),
            swatch,
            6,
            6);

        var valueText = ColorRgbMode ? ColorText.ToRgb(argb) : ColorText.ToHex(argb);
        var valueBrush = _overlayTextBrush;
        var value = new FormattedText(
            valueText,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            PanelMonoBold, 19, valueBrush, 1.0);
        if (value.Width > cardInnerWidth - 60)
        {
            var scaledSize = Math.Max(14.0, 19.0 * (cardInnerWidth - 60) / value.Width);
            value = new FormattedText(
                valueText,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                PanelMonoBold, scaledSize, valueBrush, 1.0);
        }
        dc.DrawText(
            value,
            new Point(cardCenterX - value.Width / 2.0, valueTop + (valueAreaHeight - value.Height) / 2.0));

        // Divider → coordinates, centered as one group.
        var divider2Y = valueTop + valueAreaHeight + 8;
        dc.DrawLine(
            new Pen(_overlayBorderBrush, 1),
            new Point(cardInnerLeft, divider2Y),
            new Point(cardInnerLeft + cardInnerWidth, divider2Y));

        var coordsTop = divider2Y + 9;
        var labelBrush = _overlayTextDimBrush;
        var numberBrush = _overlayIconBrush;
        var dotBrush = _neutral500Brush;
        var screenX = (int)_cursor.X + _frame.ScreenBounds.X;
        var screenY = (int)_cursor.Y + _frame.ScreenBounds.Y;
        var coordParts = new (string Text, Brush Brush, Typeface Font)[]
        {
            ("X", labelBrush, PanelUi),
            (screenX.ToString(System.Globalization.CultureInfo.InvariantCulture), numberBrush, PanelMono),
            ("·", dotBrush, PanelUi),
            ("Y", labelBrush, PanelUi),
            (screenY.ToString(System.Globalization.CultureInfo.InvariantCulture), numberBrush, PanelMono)
        };
        var coordTexts = coordParts
            .Select(part => new FormattedText(
                part.Text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                part.Font, 13, part.Brush, 1.0))
            .ToArray();
        var totalCoordWidth = coordTexts.Sum(text => text.Width + 6) - 6;
        var coordX = cardCenterX - totalCoordWidth / 2.0;
        foreach (var text in coordTexts)
        {
            dc.DrawText(text, new Point(coordX, coordsTop + (coordsHeight - text.Height) / 2.0));
            coordX += text.Width + 6;
        }

        // Divider → shortcut hint.
        var divider3Y = coordsTop + coordsHeight + 8;
        dc.DrawLine(
            new Pen(_overlayBorderBrush, 1),
            new Point(cardInnerLeft, divider3Y),
            new Point(cardInnerLeft + cardInnerWidth, divider3Y));

        var footerTop = divider3Y + 8;
        if (!string.IsNullOrEmpty(ColorStatus))
        {
            var hint = new FormattedText(
                ColorStatus,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                PanelUi, 11, _overlayTextDimBrush, 1.0);
            dc.DrawText(hint, new Point(cardCenterX - hint.Width / 2.0, footerTop));
        }
    }
}
