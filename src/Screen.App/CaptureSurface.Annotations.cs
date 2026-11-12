using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Annotation management for CaptureSurface (add, edit, undo/redo, rendering selection).
/// </summary>
internal sealed partial class CaptureSurface
{
    public void ClearAnnotationSelection() => SelectAnnotation(null);

    public void ApplyStyleToSelected(AnnotationStyle style)
    {
        if (_selectedAnnotationId is not Guid id)
        {
            return;
        }
        var index = _annotations.FindIndex(item => item.Id == id);
        if (index < 0)
        {
            return;
        }
        var item = _annotations[index];
        var before = CaptureAnnotationSnapshot();
        var updated = item with { Style = style };
        if (item.Kind == AnnotationKind.Text)
        {
            updated = updated with
            {
                Bounds = MeasureTextBounds(new PixelPoint(item.Bounds.X, item.Bounds.Y), item.Text ?? string.Empty, style)
            };
        }
        _annotations[index] = updated;
        RecordAnnotationEdit(before);
        _mosaicCompositeDirty = true;
        SelectedAnnotationChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void Undo()
    {
        EndAnnotationInteraction();
        if (_history.Undo(CaptureAnnotationSnapshot()) is { } previous)
        {
            RestoreAnnotationSnapshot(previous);
            AnnotationContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Redo()
    {
        EndAnnotationInteraction();
        if (_history.Redo(CaptureAnnotationSnapshot()) is { } next)
        {
            RestoreAnnotationSnapshot(next);
            AnnotationContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private AnnotationSnapshot CaptureAnnotationSnapshot() => AnnotationSnapshot.Capture(_annotations, _nextNumber);

    internal AnnotationEditingState ExportEditingState()
    {
        EndAnnotationInteraction();
        return new(CaptureAnnotationSnapshot(), _history.Export(), _selectedAnnotationId);
    }

    internal void ImportEditingState(AnnotationEditingState state)
    {
        _history.Restore(state.History);
        _selectedAnnotationId = state.SelectedId;
        RestoreAnnotationSnapshot(state.Current);
    }

    internal void CompletePendingAnnotationEdit() => EndAnnotationInteraction();

    private void RecordAnnotationEdit(AnnotationSnapshot before)
    {
        if (_history.Record(before, CaptureAnnotationSnapshot()))
        {
            AnnotationEdited?.Invoke(this, EventArgs.Empty);
            AnnotationContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RestoreAnnotationSnapshot(AnnotationSnapshot snapshot)
    {
        _annotations.Clear();
        _annotations.AddRange(snapshot.Items);
        _nextNumber = snapshot.NextNumber;
        if (!_annotations.Any(item => item.Id == _selectedAnnotationId))
        {
            _selectedAnnotationId = null;
        }
        _mosaicCompositeDirty = true;
        SelectedAnnotationChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void BeginAnnotationGesture() => _annotationGestureStart = CaptureAnnotationSnapshot();

    private void CompleteAnnotationGesture()
    {
        if (_annotationGestureStart is { } before)
        {
            RecordAnnotationEdit(before);
            _annotationGestureStart = null;
        }
    }

    private void EndAnnotationInteraction()
    {
        var wasTransforming = _draggingAnnotation || _resizingAnnotation;
        CompleteAnnotationGesture();
        _draggingAnnotation = false;
        _resizingAnnotation = false;
        _annotationAtDragStart = null;
        if (wasTransforming && IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
    }

    public PixelBuffer RenderSelection()
    {
        if (Selection.IsEmpty)
        {
            throw new InvalidOperationException("请先选择截图区域。");
        }

        var baseImage = _frame.ToPixelBuffer().Crop(Selection);
        foreach (var item in _annotations.Where(item => item.Kind == AnnotationKind.Mosaic))
        {
            ApplyMosaic(baseImage, OffsetItem(item, -Selection.X, -Selection.Y));
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(baseImage.ToBitmapSource(), new Rect(0, 0, Selection.Width, Selection.Height));
            DrawAnnotations(
                dc,
                _annotations.Where(item => item.Kind != AnnotationKind.Mosaic),
                new Vector(-Selection.X, -Selection.Y));
        }
        var bitmap = new RenderTargetBitmap(Selection.Width, Selection.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap.ToPixelBuffer();
    }

    /// <summary>
    /// Renders the annotations (kept in this surface's coordinate space) onto a
    /// different, full-resolution base image. Used by the long-screenshot editor,
    /// which edits a downscaled working copy but exports at the original size.
    /// </summary>
    internal PixelBuffer RenderSelection(PixelBuffer baseImage, PixelRect selectionInBaseImage, double coordinateScale)
    {
        if (selectionInBaseImage.IsEmpty)
        {
            throw new InvalidOperationException("请先选择截图区域。");
        }

        var baseCropped = baseImage.Crop(selectionInBaseImage);
        foreach (var item in _annotations.Where(item => item.Kind == AnnotationKind.Mosaic))
        {
            ApplyMosaic(
                baseCropped,
                OffsetItem(ScaleItem(item, coordinateScale), -selectionInBaseImage.X, -selectionInBaseImage.Y));
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(baseCropped.ToBitmapSource(), new Rect(0, 0, selectionInBaseImage.Width, selectionInBaseImage.Height));
            DrawAnnotations(
                dc,
                _annotations.Where(item => item.Kind != AnnotationKind.Mosaic)
                    .Select(item => ScaleItem(item, coordinateScale)),
                new Vector(-selectionInBaseImage.X, -selectionInBaseImage.Y));
        }
        var bitmap = new RenderTargetBitmap(
            selectionInBaseImage.Width,
            selectionInBaseImage.Height,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap.ToPixelBuffer();
    }

    internal static AnnotationItem ScaleItem(AnnotationItem item, double scale) => item with
    {
        Points = item.Points.Select(point => new PixelPoint(point.X * scale, point.Y * scale)).ToArray(),
        Bounds = new PixelRect(
            (int)Math.Round(item.Bounds.X * scale),
            (int)Math.Round(item.Bounds.Y * scale),
            Math.Max(1, (int)Math.Round(item.Bounds.Width * scale)),
            Math.Max(1, (int)Math.Round(item.Bounds.Height * scale))),
        Style = item.Style with
        {
            Thickness = item.Style.Thickness * scale,
            FontSize = item.Style.FontSize * scale
        }
    };

    private void AddAnnotation(PixelPoint start, PixelPoint end)
    {
        start = ClampAnnotationPoint(start);
        end = ClampAnnotationPoint(end);
        var before = CaptureAnnotationSnapshot();
        string? text = null;
        int? number = null;
        if (Tool == CaptureTool.Text)
        {
            var maxContentWidth = Math.Min(276 * _frame.DpiX / 96.0, Math.Max(1, Selection.Right - start.X));
            var dialog = new TextPromptWindow(
                CurrentStyle,
                ToScreenPoint(start),
                maxContentWidth: maxContentWidth);
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
            {
                return;
            }
            text = dialog.Value;
        }
        else if (Tool == CaptureTool.Number)
        {
            number = _nextNumber++;
        }

        var kind = Tool switch
        {
            CaptureTool.Rectangle => AnnotationKind.Rectangle,
            CaptureTool.Ellipse => AnnotationKind.Ellipse,
            CaptureTool.Line => AnnotationKind.Line,
            CaptureTool.Arrow => AnnotationKind.Arrow,
            CaptureTool.Pen => AnnotationKind.Pen,
            CaptureTool.Text => AnnotationKind.Text,
            CaptureTool.Number => AnnotationKind.Number,
            CaptureTool.Mosaic => AnnotationKind.Mosaic,
            _ => throw new InvalidOperationException("Select is not an annotation tool.")
        };
        var points = kind == AnnotationKind.Pen || (kind == AnnotationKind.Mosaic && MosaicMode == MosaicDrawMode.Brush)
            ? _workingPoints.Select(ClampAnnotationPoint).ToArray()
            : new[] { start, end };
        var bounds = kind == AnnotationKind.Text
            ? MeasureTextBounds(start, text ?? string.Empty, CurrentStyle)
            : kind == AnnotationKind.Number
                ? new PixelRect((int)start.X, (int)start.Y, 1, 1)
            : kind == AnnotationKind.Pen || (kind == AnnotationKind.Mosaic && MosaicMode == MosaicDrawMode.Brush)
                ? BoundsAroundPoints(points, CurrentStyle.Thickness / 2).Intersect(Selection)
            : RectFromPoints(start, end);
        var annotation = new AnnotationItem(
            Guid.NewGuid(), kind, points, bounds, CurrentStyle, text, number, _annotations.Count);
        _annotations.Add(annotation);
        // A newly drawn mark is finished, not implicitly in transform mode.
        // The user can explicitly choose it later with the pointer tool.
        SelectAnnotation(null);
        _mosaicCompositeDirty = true;
        RecordAnnotationEdit(before);
    }

    private bool IsMeaningfulAnnotationGesture(PixelPoint start, PixelPoint end)
    {
        if (Tool is CaptureTool.Text or CaptureTool.Number)
        {
            return true;
        }
        var scale = Math.Max(
            _frame.Width / Math.Max(1, ActualWidth),
            _frame.Height / Math.Max(1, ActualHeight));
        var minimum = Math.Max(3, 5 * scale);
        if (Tool is CaptureTool.Rectangle or CaptureTool.Ellipse)
        {
            return Math.Abs(end.X - start.X) >= minimum
                   && Math.Abs(end.Y - start.Y) >= minimum;
        }
        if (Tool is CaptureTool.Line or CaptureTool.Arrow)
        {
            return Distance(start, end) >= minimum;
        }
        if (Tool is CaptureTool.Pen || Tool == CaptureTool.Mosaic && MosaicMode == MosaicDrawMode.Brush)
        {
            if (_workingPoints.Count < 2)
            {
                return false;
            }
            var pathLength = 0.0;
            for (var index = 1; index < _workingPoints.Count; index++)
            {
                pathLength += Distance(_workingPoints[index - 1], _workingPoints[index]);
            }
            return pathLength >= minimum;
        }
        return Math.Abs(end.X - start.X) >= minimum
               && Math.Abs(end.Y - start.Y) >= minimum;
    }

    private AnnotationItem? HitTestAnnotation(PixelPoint point) => _annotations
        .OrderByDescending(item => item.ZIndex)
        .FirstOrDefault(item => IsAnnotationHit(item, point));

    private void EditTextAnnotation(Guid id)
    {
        var index = _annotations.FindIndex(item => item.Id == id && item.Kind == AnnotationKind.Text);
        if (index < 0)
        {
            return;
        }
        var item = _annotations[index];
        var before = CaptureAnnotationSnapshot();
        var origin = new PixelPoint(item.Bounds.X, item.Bounds.Y);
        var maxContentWidth = Math.Min(276 * _frame.DpiX / 96.0, Math.Max(1, Selection.Right - origin.X));
        var dialog = new TextPromptWindow(
            item.Style,
            ToScreenPoint(origin),
            item.Text,
            maxContentWidth);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value))
        {
            return;
        }
        _annotations[index] = item with
        {
            Text = dialog.Value,
            Bounds = MeasureTextBounds(origin, dialog.Value, item.Style)
        };
        RecordAnnotationEdit(before);
        InvalidateVisual();
    }

    private void ReplaceAnnotation(AnnotationItem annotation)
    {
        var index = _annotations.FindIndex(item => item.Id == annotation.Id);
        if (index >= 0)
        {
            _annotations[index] = annotation;
            _mosaicCompositeDirty = true;
            InvalidateVisual();
        }
    }

    private PixelRect MeasureTextBounds(PixelPoint origin, string text, AnnotationStyle style)
    {
        var availableWidth = Math.Max(1, Selection.Right - origin.X);
        var layout = CreateTextLayout(text, style, Math.Min(276 * _frame.DpiX / 96.0, availableWidth));
        return new PixelRect(
            (int)Math.Floor(origin.X),
            (int)Math.Floor(origin.Y),
            Math.Max(1, (int)Math.Ceiling(layout.WidthIncludingTrailingWhitespace + 2)),
            Math.Max(1, (int)Math.Ceiling(layout.Height + 2)));
    }

    private void SelectAnnotation(Guid? id)
    {
        if (_selectedAnnotationId == id)
        {
            return;
        }
        _selectedAnnotationId = id;
        SelectedAnnotationChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private PixelRect GetVisualBounds(AnnotationItem item)
    {
        if (item.Kind != AnnotationKind.Number)
        {
            return item.Bounds;
        }
        var radius = Math.Max(12, item.Style.FontSize * 0.62);
        var center = item.Points.Count > 0
            ? item.Points[0]
            : new PixelPoint(item.Bounds.X, item.Bounds.Y);
        return new PixelRect(
            (int)Math.Floor(center.X - radius),
            (int)Math.Floor(center.Y - radius),
            Math.Max(1, (int)Math.Ceiling(radius * 2)),
            Math.Max(1, (int)Math.Ceiling(radius * 2)));
    }

    private bool IsAnnotationHit(AnnotationItem item, PixelPoint point)
    {
        var scale = Math.Max(
            _frame.Width / Math.Max(1, ActualWidth),
            _frame.Height / Math.Max(1, ActualHeight));
        var tolerance = Math.Max(6 * scale, item.Style.Thickness / 2 + 4 * scale);
        if (item.Kind is AnnotationKind.Line or AnnotationKind.Arrow or AnnotationKind.Pen
            || (item.Kind == AnnotationKind.Mosaic && item.Style.Variant >= 100))
        {
            if (item.Points.Count == 1)
            {
                return Distance(item.Points[0], point) <= tolerance;
            }
            for (var index = 1; index < item.Points.Count; index++)
            {
                if (DistanceToSegment(point, item.Points[index - 1], item.Points[index]) <= tolerance)
                {
                    return true;
                }
            }
            return false;
        }
        var bounds = GetVisualBounds(item);
        return item.Kind switch
        {
            AnnotationKind.Rectangle => IsRectangleBorderHit(bounds, point, tolerance),
            AnnotationKind.Ellipse => IsEllipseBorderHit(bounds, point, tolerance),
            AnnotationKind.Number => IsNumberHit(item, point, tolerance),
            AnnotationKind.Text or AnnotationKind.Mosaic =>
                Contains(Inflate(bounds, (int)Math.Ceiling(tolerance)), point),
            _ => false
        };
    }

    private static bool IsRectangleBorderHit(PixelRect bounds, PixelPoint point, double tolerance)
    {
        if (!Contains(Inflate(bounds, (int)Math.Ceiling(tolerance)), point))
        {
            return false;
        }
        var distance = Math.Min(
            Math.Min(Math.Abs(point.X - bounds.X), Math.Abs(point.X - bounds.Right)),
            Math.Min(Math.Abs(point.Y - bounds.Y), Math.Abs(point.Y - bounds.Bottom)));
        return distance <= tolerance;
    }

    private static bool IsEllipseBorderHit(PixelRect bounds, PixelPoint point, double tolerance)
    {
        var radiusX = Math.Max(0.5, bounds.Width / 2.0);
        var radiusY = Math.Max(0.5, bounds.Height / 2.0);
        var dx = point.X - (bounds.X + radiusX);
        var dy = point.Y - (bounds.Y + radiusY);
        var normalizedRadius = Math.Sqrt(dx * dx / (radiusX * radiusX) + dy * dy / (radiusY * radiusY));
        return Math.Abs(normalizedRadius - 1) * Math.Min(radiusX, radiusY) <= tolerance;
    }

    private static bool IsNumberHit(AnnotationItem item, PixelPoint point, double tolerance)
    {
        var center = item.Points.Count > 0
            ? item.Points[0]
            : new PixelPoint(item.Bounds.X, item.Bounds.Y);
        var radius = Math.Max(12, item.Style.FontSize * 0.62);
        return Distance(center, point) <= radius + tolerance;
    }

    private AnnotationItem ResizeAnnotation(AnnotationItem item, PixelRect oldBounds, PixelRect newBounds)
    {
        var scaleX = newBounds.Width / (double)Math.Max(1, oldBounds.Width);
        var scaleY = newBounds.Height / (double)Math.Max(1, oldBounds.Height);
        PixelPoint Map(PixelPoint point) => new(
            newBounds.X + (point.X - oldBounds.X) * scaleX,
            newBounds.Y + (point.Y - oldBounds.Y) * scaleY);

        if (item.Kind == AnnotationKind.Number)
        {
            var center = new PixelPoint(newBounds.X + newBounds.Width / 2.0, newBounds.Y + newBounds.Height / 2.0);
            var style = item.Style with { FontSize = Math.Clamp(Math.Min(newBounds.Width, newBounds.Height) / 1.24, 10, 160) };
            return item with
            {
                Points = [center],
                Bounds = new PixelRect((int)center.X, (int)center.Y, 1, 1),
                Style = style
            };
        }

        var points = item.Points.Select(Map).ToArray();
        if (item.Kind == AnnotationKind.Text)
        {
            var style = item.Style with { FontSize = Math.Clamp(item.Style.FontSize * scaleY, 10, 160) };
            return item with
            {
                Points = points,
                Bounds = newBounds,
                Style = style
            };
        }
        var isFreehandStroke = item.Kind == AnnotationKind.Pen
                               || (item.Kind == AnnotationKind.Mosaic && item.Style.Variant >= 100);
        var bounds = isFreehandStroke
            ? BoundsAroundPoints(points, item.Style.Thickness / 2)
            : newBounds;
        _mosaicCompositeDirty = true;
        return item with { Points = points, Bounds = bounds };
    }

    private static FormattedText CreateTextLayout(string text, AnnotationStyle style, double maxWidth)
    {
        var weight = style.Variant switch
        {
            AnnotationVariants.TextMedium => FontWeights.SemiBold,
            AnnotationVariants.TextBold => FontWeights.Bold,
            _ => FontWeights.Normal
        };
        var color = Color.FromArgb(
            (byte)(style.Argb >> 24),
            (byte)(style.Argb >> 16),
            (byte)(style.Argb >> 8),
            (byte)style.Argb);
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"), FontStyles.Normal, weight, FontStretches.Normal),
            style.FontSize,
            new SolidColorBrush(color),
            1.0)
        {
            MaxTextWidth = maxWidth,
            LineHeight = style.FontSize * 1.22
        };
        return formatted;
    }
}
