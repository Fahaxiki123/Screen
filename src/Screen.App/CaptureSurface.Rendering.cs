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
        PixelBuffer? compositedSource = null;
        if (hasMosaic)
        {
            compositedSource = GetMosaicComposite();
        }
        else
        {
            // Every mosaic was undone — drop the full-screen composite buffers
            // instead of carrying them until the window closes.
            ResetMosaicComposite();
        }
        dc.PushTransform(new ScaleTransform(ActualWidth / _frame.Width, ActualHeight / _frame.Height));
        dc.PushClip(new RectangleGeometry(ToRect(Selection)));
        if (hasMosaic)
        {
            dc.DrawImage(_mosaicCompositeSource!, new Rect(0, 0, _frame.Width, _frame.Height));
        }
        else if (_translatedSelectionSource is { } translation)
        {
            dc.DrawImage(translation, ToRect(Selection));
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

        dc.Pop();
        var full = new Rect(RenderSize);
        if (!Selection.IsEmpty && ShowSelectionChrome)
        {
            var bounds = SelectionInDips;
            var dim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(bounds));
            dc.DrawGeometry(_selectionCommitted ? _chrome.After : _chrome.Before, null, dim);
            dc.DrawRectangle(null, _chrome.Selection, bounds);
            DrawSelectionHandles(dc, bounds);
        }
        else if (Selection.IsEmpty)
        {
            dc.DrawRectangle(_chrome.Before, null, full);
        }

        if (!_selectionCommitted)
        {
            DrawMagnifier(dc);
        }
        else if (CanShowMosaicBrushCursor && Cursor == System.Windows.Input.Cursors.None)
        {
            dc.PushTransform(new ScaleTransform(ActualWidth / _frame.Width, ActualHeight / _frame.Height));
            var radius = CurrentStyle.Thickness / 2;
            dc.DrawEllipse(
                _chrome.BrushFill,
                _chrome.BrushRim,
                new Point(_cursor.X, _cursor.Y),
                radius,
                radius);
            dc.DrawEllipse(
                null,
                _chrome.BrushOutline,
                new Point(_cursor.X, _cursor.Y),
                Math.Max(1, radius - 1.5),
                Math.Max(1, radius - 1.5));
            dc.Pop();
        }
        AdornmentsRendered?.Invoke();
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
            dc.DrawRectangle(_chrome.HandleFill, new Pen(_accentBrush, 1), new Rect(point.X - 2.5, point.Y - 2.5, 5, 5));
        }
    }

    private readonly record struct MosaicSnapshot(
        Guid Id,
        PixelRect Bounds,
        int Variant,
        double Intensity,
        double Thickness,
        double Scale,
        PreciseAnnotationBounds? PreciseBounds,
        IReadOnlyList<PixelPoint> Points,
        IReadOnlySet<int> StrokeBreaks)
    {
        public MosaicSnapshot(AnnotationItem item) : this(
            item.Id, item.Bounds, item.Style.Variant, item.Style.Intensity, item.Style.Thickness,
            item.Style.MosaicScale, item.PreciseBounds, item.Points, item.StrokeBreaks)
        { }
    }

    internal bool IsShowingTranslation => _translatedSelection is not null;

    internal void SetTranslatedSelection(PixelBuffer? image)
    {
        if (ReferenceEquals(image, _translatedSelection))
        {
            return;
        }
        _translatedSelection = image;
        _translatedSelectionSource = image?.ToBitmapSource();
        _translatedFrame = null;
        ResetMosaicComposite();
        InvalidateVisual();
    }

    private void ResetMosaicComposite()
    {
        _mosaicComposite = null;
        _mosaicCompositeSource = null;
        _appliedMosaicSnapshots = null;
    }

    private PixelBuffer CompositingSource()
    {
        if (_translatedSelection is not { } translation)
        {
            return _sourceBuffer;
        }
        if (_translatedFrame is null)
        {
            _translatedFrame = _sourceBuffer with { Pixels = (byte[])_sourceBuffer.Pixels.Clone() };
            var rowBytes = translation.Width * 4;
            for (var y = 0; y < translation.Height; y++)
            {
                var offset = (Selection.Y + y) * _translatedFrame.Stride + Selection.X * 4;
                translation.Pixels.AsSpan(y * translation.Stride, rowBytes)
                    .CopyTo(_translatedFrame.Pixels.AsSpan(offset, rowBytes));
            }
        }
        return _translatedFrame;
    }

    /// <summary>
    /// Returns the full-screen buffer with every committed mosaic applied.
    /// Rebuilds are incremental: only the union of regions whose mosaics
    /// changed (old ∪ new bounds) is restored from the source and re-composited,
    /// so dragging or resizing a mosaic never copies the whole screen per frame.
    /// </summary>
    private PixelBuffer GetMosaicComposite()
    {
        var source = CompositingSource();
        if (_mosaicComposite is null)
        {
            _mosaicComposite = source with { Pixels = (byte[])source.Pixels.Clone() };
            _mosaicCompositeSource = new WriteableBitmap(
                _sourceBuffer.Width, _sourceBuffer.Height, 96, 96, PixelFormats.Bgra32, null);
            _mosaicCompositeSource.WritePixels(new Int32Rect(0, 0, source.Width, source.Height),
                _mosaicComposite.Pixels, source.Stride, 0);
            _appliedMosaicSnapshots = [];
            _mosaicCompositeDirty = true;
        }

        if (!_mosaicCompositeDirty)
        {
            return _mosaicComposite;
        }

        var mosaics = _annotations
            .Where(item => item.Kind == AnnotationKind.Mosaic)
            .OrderBy(item => item.ZIndex)
            .Select(item => _mosaicSourceContext is null ? item : item with { Bounds = CoverBounds(GetPreciseBounds(item)) })
            .ToArray();
        var fullBounds = new PixelRect(0, 0, _mosaicComposite.Width, _mosaicComposite.Height);
        var region = ExpandDirtyMosaicRegion(DirtyMosaicRegion(mosaics, _appliedMosaicSnapshots), mosaics, fullBounds);
        _appliedMosaicSnapshots = mosaics.Select(item => new MosaicSnapshot(item)).ToList();
        _mosaicCompositeDirty = false;
        if (region.IsEmpty)
        {
            // The annotation change did not involve mosaics (e.g. a text edit).
            return _mosaicComposite;
        }

        if (_mosaicSourceContext is not null)
        {
            CopyInto(RenderContextMosaics(region, mosaics), _mosaicComposite, region);
        }
        else
        {
            CopyInto(CompositeMosaicsRegion(source, region, mosaics), _mosaicComposite, region);
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
            var current = new MosaicSnapshot(item);
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

    private void DrawWorkingAnnotation(DrawingContext dc, PixelBuffer? compositedSource)
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
        var bounds = freehand
            ? BoundsAroundPoints(points, CurrentStyle.Thickness / 2)
            : RectFromPoints(_start, end);
        var item = new AnnotationItem(Guid.Empty, tool, points, bounds, CurrentStyle, null, null, int.MaxValue)
        {
            StrokeBreaks = _workingStrokeBreaks
        };
        if (tool == AnnotationKind.Mosaic)
        {
            var clipped = item.Bounds.Intersect(new PixelRect(0, 0, _frame.Width, _frame.Height)).Intersect(Selection);
            if (_mosaicSourceContext is not null && !clipped.IsEmpty)
            {
                var mosaics = _annotations.Where(annotation => annotation.Kind == AnnotationKind.Mosaic).Append(item).ToArray();
                dc.DrawImage(RenderContextMosaics(clipped, mosaics).ToBitmapSource(), ToRect(clipped));
            }
            else
            {
                DrawMosaicPreview(dc, compositedSource ?? CompositingSource(), item, Selection);
            }
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
                var radius = NumberRadius(item.Style);
                var numberPoint = item.Points.Count > 0 ? PointAt(0) : new PixelPoint(rect.X, rect.Y);
                var center = new Point(numberPoint.X, numberPoint.Y);
                var luminance = 0.2126 * color.ScR + 0.7152 * color.ScG + 0.0722 * color.ScB;
                var numberBrush = luminance > 0.179 ? Brushes.Black : Brushes.White;
                var outline = color == Colors.White ? new Pen(numberBrush, 1) : null;
                dc.DrawEllipse(brush, outline, center, radius, radius);
                var numberText = new FormattedText(
                    item.Number?.ToString() ?? string.Empty,
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    radius,
                    numberBrush,
                    1.0);
                dc.DrawText(numberText, new Point(center.X - numberText.Width / 2, center.Y - numberText.Height / 2));
                break;
        }
    }

    private static double NumberRadius(AnnotationStyle style) => Math.Max(5, style.FontSize / 2);

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

    private static void DrawMosaicPreview(DrawingContext dc, PixelBuffer source, AnnotationItem item, PixelRect selection)
    {
        var clipped = item.Bounds.Intersect(new PixelRect(0, 0, source.Width, source.Height)).Intersect(selection);
        if (clipped.IsEmpty)
        {
            return;
        }
        var preview = CompositeMosaicsRegion(source, clipped, [item]);
        dc.DrawImage(preview.ToBitmapSource(), ToRect(clipped));
    }

    private static void ApplyMosaic(PixelBuffer buffer, AnnotationItem item)
    {
        var clipped = item.Bounds.Intersect(new PixelRect(0, 0, buffer.Width, buffer.Height));
        if (clipped.IsEmpty)
        {
            return;
        }
        // The region compositor supplies complete pixel blocks or the blur
        // halo. This local effect never needs the entire annotation bitmap.
        var effected = buffer.Crop(clipped);
        var region = new PixelRect(0, 0, effected.Width, effected.Height);
        var localItem = OffsetItem(item, -clipped.X, -clipped.Y);
        if (localItem.Style.Variant % 100 == AnnotationVariants.MosaicBlur)
        {
            BoxBlur(effected, region, MosaicBlurRadius(localItem.Style));
        }
        else
        {
            Pixelate(effected, region, MosaicBlockSize(localItem.Style));
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

    private static int MosaicBlurRadius(AnnotationStyle style) =>
        Math.Max(1, (int)Math.Round(Math.Clamp((int)Math.Round(3 + style.Intensity * 8), 3, 12) * style.MosaicScale));

    private static int MosaicBlockSize(AnnotationStyle style) =>
        Math.Max(1, (int)Math.Round(Math.Clamp((int)Math.Round(6 + style.Intensity * 16), 6, 24) * style.MosaicScale));

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
        foreach (var (from, to) in StrokeSegments(item.Points, item.StrokeBreaks))
        {
            MarkCircle(mask, bounds, from.X, from.Y, radius);
            var steps = (int)Math.Ceiling(Distance(from, to) / Math.Max(1, radius / 2));
            for (var step = 1; step <= steps; step++)
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

    private void DrawSelectionHandles(DrawingContext dc, Rect bounds)
    {
        var cx = bounds.X + bounds.Width / 2;
        var cy = bounds.Y + bounds.Height / 2;
        void Draw(double x, double y) => dc.DrawImage(_chrome.Handle, new Rect(x - 8, y - 8, 16, 16));
        Draw(bounds.Left, bounds.Top);
        Draw(cx, bounds.Top);
        Draw(bounds.Right, bounds.Top);
        Draw(bounds.Left, cy);
        Draw(bounds.Right, cy);
        Draw(bounds.Left, bounds.Bottom);
        Draw(cx, bounds.Bottom);
        Draw(bounds.Right, bounds.Bottom);
    }

    internal Rect LoupeInDips
    {
        get
        {
            const double margin = 6, gap = 24;
            var viewport = CursorViewport;
            var diameter = Math.Min(128, Math.Max(1, Math.Min(viewport.Width, viewport.Height) - 2 * margin));
            var px = _cursor.X * ActualWidth / _frame.Width;
            var py = _cursor.Y * ActualHeight / _frame.Height;
            var x = px + gap + diameter + margin <= viewport.Right ? px + gap : px - diameter - gap;
            var y = py + gap + diameter + 54 <= viewport.Bottom ? py + gap : py - diameter - 54;
            return new Rect(Math.Clamp(x, viewport.Left + margin, Math.Max(viewport.Left + margin, viewport.Right - diameter - margin)),
                Math.Clamp(y, viewport.Top + margin, Math.Max(viewport.Top + margin, viewport.Bottom - diameter - margin)), diameter, diameter);
        }
    }

    private void DrawMagnifier(DrawingContext dc)
    {
        var point = SampledPixel;
        var pixel = new Point((point.X + 0.5) * ActualWidth / _frame.Width, (point.Y + 0.5) * ActualHeight / _frame.Height);
        dc.DrawLine(_chrome.Crosshair, new Point(0, pixel.Y), new Point(ActualWidth, pixel.Y));
        dc.DrawLine(_chrome.Crosshair, new Point(pixel.X, 0), new Point(pixel.X, ActualHeight));

        var lens = LoupeInDips;
        var radius = lens.Width / 2;
        var center = new Point(lens.X + radius, lens.Y + radius);
        var cellX = Magnification * ActualWidth / _frame.Width;
        var cellY = Magnification * ActualHeight / _frame.Height;
        var samplesX = lens.Width / cellX;
        var samplesY = lens.Height / cellY;
        var pixels = new ImageBrush(_source)
        {
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = new Rect((point.X + 0.5 - samplesX / 2) / _frame.Width, (point.Y + 0.5 - samplesY / 2) / _frame.Height,
                samplesX / _frame.Width, samplesY / _frame.Height),
            Stretch = Stretch.Fill
        };
        dc.DrawEllipse(_chrome.Before, _chrome.Outline, center, radius + 4, radius + 4);
        dc.DrawEllipse(null, _chrome.LoupeRing, center, radius + 2, radius + 2);
        dc.PushClip(new EllipseGeometry(center, radius, radius));
        dc.DrawRectangle(pixels, null, lens);
        if (cellX >= 5 && cellY >= 5)
        {
            for (var dx = 0.5 * cellX; dx < radius; dx += cellX)
            {
                dc.DrawLine(_chrome.PixelGrid, new Point(center.X - dx, lens.Top), new Point(center.X - dx, lens.Bottom));
                dc.DrawLine(_chrome.PixelGrid, new Point(center.X + dx, lens.Top), new Point(center.X + dx, lens.Bottom));
            }
            for (var dy = 0.5 * cellY; dy < radius; dy += cellY)
            {
                dc.DrawLine(_chrome.PixelGrid, new Point(lens.Left, center.Y - dy), new Point(lens.Right, center.Y - dy));
                dc.DrawLine(_chrome.PixelGrid, new Point(lens.Left, center.Y + dy), new Point(lens.Right, center.Y + dy));
            }
        }
        var box = new Rect(center.X - cellX / 2, center.Y - cellY / 2, cellX, cellY);
        dc.DrawRectangle(null, _chrome.PixelBorder, box);
        box.Inflate(1, 1);
        dc.DrawRectangle(null, _chrome.Outline, box);
        dc.Pop();
    }
}
