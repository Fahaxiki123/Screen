using System.Windows;
using System.Windows.Input;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Input handling for CaptureSurface (mouse and keyboard events).
/// </summary>
internal sealed partial class CaptureSurface
{
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_selectionCommitted)
        {
            return;
        }
        // Walk the sample ladder: wheel up zooms in, wheel down zooms out.
        _magnifierSampleIndex = Math.Clamp(
            _magnifierSampleIndex + (e.Delta > 0 ? -1 : 1),
            0,
            MagnifierSamples.Length - 1);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        Focus();
        _start = ToPixel(e.GetPosition(this));
        _cursor = _start;

        if (_selectionCommitted)
        {
            var resizeHandle = HitTestSelectionHandle(_start);
            if (resizeHandle != SelectionResizeHandle.None)
            {
                CaptureMouse();
                SelectAnnotation(null);
                _resizingSelection = true;
                _resizeHandle = resizeHandle;
                _selectionAtDragStart = Selection;
                SelectedWindow = null;
                Cursor = CursorForResizeHandle(resizeHandle);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            // Annotation hit tolerances can extend beyond their visible edge.
            // Outside the capture only the selection handles may be operated.
            if (!Contains(Selection, _start))
            {
                SelectAnnotation(null);
                UpdateIdleCursor();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }

        var annotationResizeHandle = AllowAnnotationEditing ? HitTestAnnotationHandle(_start) : SelectionResizeHandle.None;
        if (annotationResizeHandle != SelectionResizeHandle.None && _selectedAnnotationId is Guid selectedId)
        {
            var selected = _annotations.First(item => item.Id == selectedId);
            CaptureMouse();
            BeginAnnotationGesture();
            _resizingAnnotation = true;
            _annotationResizeHandle = annotationResizeHandle;
            _annotationAtDragStart = selected;
            _selectionAtDragStart = GetVisualBounds(selected);
            Cursor = CursorForResizeHandle(annotationResizeHandle);
            e.Handled = true;
            return;
        }

        var annotationHit = _selectionCommitted && AllowAnnotationEditing
            ? HitTestAnnotation(_start)
            : null;
        if (annotationHit is not null)
        {
            CaptureMouse();
            BeginAnnotationGesture();
            _annotationWasSelected = _selectedAnnotationId == annotationHit.Id;
            SelectAnnotation(annotationHit.Id);
            _annotationAtDragStart = annotationHit;
            _draggingAnnotation = true;
            _annotationMoved = false;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (Tool == CaptureTool.Select || Selection.IsEmpty)
        {
            if (_selectionCommitted)
            {
                SelectAnnotation(null);
                if (!Contains(Selection, _start))
                {
                    InvalidateVisual();
                    return;
                }
                if (!ShowSelectionChrome)
                {
                    InvalidateVisual();
                    return;
                }
                CaptureMouse();
                _movingSelection = true;
                _selectionAtDragStart = Selection;
                SelectedWindow = null;
                Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }
            CaptureMouse();
            _draggingSelection = true;
            _selectionCommitted = false;
            SelectAnnotation(null);
            SelectedWindow = null;
            Selection = new PixelRect((int)_start.X, (int)_start.Y, 1, 1);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            if (!Contains(Selection, _start))
            {
                return;
            }
            CaptureMouse();
            SelectAnnotation(null);
            _drawing = true;
            _workingPoints.Clear();
            _workingPoints.Add(_start);
        }
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _cursor = ToPixel(e.GetPosition(this));
        if (_drawing && e.LeftButton != MouseButtonState.Pressed)
        {
            // A release can be missed when the pointer crosses another native
            // window. Do not let a later button press continue this old stroke.
            _drawing = false;
            _workingPoints.Clear();
            ReleaseMouseCapture();
        }
        if (_resizingSelection && e.LeftButton == MouseButtonState.Pressed)
        {
            Selection = SelectionRegionGeometry.Resize(
                _selectionAtDragStart,
                _resizeHandle,
                _cursor,
                new PixelRect(0, 0, _frame.Width, _frame.Height));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (_resizingAnnotation && e.LeftButton == MouseButtonState.Pressed && _annotationAtDragStart is not null)
        {
            var resized = SelectionRegionGeometry.Resize(
                _selectionAtDragStart,
                _annotationResizeHandle,
                _cursor,
                Selection,
                minimumSize: 4);
            ReplaceAnnotation(ResizeAnnotation(_annotationAtDragStart, _selectionAtDragStart, resized));
        }
        else if (_draggingAnnotation && e.LeftButton == MouseButtonState.Pressed && _annotationAtDragStart is not null)
        {
            var dx = (int)Math.Round(_cursor.X - _start.X);
            var dy = (int)Math.Round(_cursor.Y - _start.Y);
            if (!_annotationMoved && Math.Abs(dx) + Math.Abs(dy) >= 2)
            {
                _annotationMoved = true;
            }
            if (_annotationMoved)
            {
                var bounds = GetVisualBounds(_annotationAtDragStart);
                dx = ClampDelta(dx, Selection.X - bounds.X, Selection.Right - bounds.Right);
                dy = ClampDelta(dy, Selection.Y - bounds.Y, Selection.Bottom - bounds.Bottom);
                ReplaceAnnotation(OffsetItem(_annotationAtDragStart, dx, dy));
            }
        }
        else if (_movingSelection && e.LeftButton == MouseButtonState.Pressed)
        {
            var dx = (int)Math.Round(_cursor.X - _start.X);
            var dy = (int)Math.Round(_cursor.Y - _start.Y);
            Selection = new PixelRect(
                Math.Clamp(_selectionAtDragStart.X + dx, 0, _frame.Width - _selectionAtDragStart.Width),
                Math.Clamp(_selectionAtDragStart.Y + dy, 0, _frame.Height - _selectionAtDragStart.Height),
                _selectionAtDragStart.Width,
                _selectionAtDragStart.Height);
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (_draggingSelection && e.LeftButton == MouseButtonState.Pressed)
        {
            Selection = RectFromPoints(_start, _cursor).Intersect(new PixelRect(0, 0, _frame.Width, _frame.Height));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (_drawing && e.LeftButton == MouseButtonState.Pressed
                 && (Tool == CaptureTool.Pen || (Tool == CaptureTool.Mosaic && MosaicMode == MosaicDrawMode.Brush)))
        {
            if (!Contains(Selection, _cursor))
            {
                // Finish at the first boundary crossing. Clamping every later
                // outside point would paint a false line along the border.
                FinishDrawing(_cursor);
            }
            else if (_workingPoints.Count == 0 || Distance(_workingPoints[^1], _cursor) >= 1.5)
            {
                _workingPoints.Add(_cursor);
            }
        }
        else if (!_draggingSelection && !_drawing && !_selectionCommitted && (Selection.IsEmpty || Tool == CaptureTool.Select))
        {
            var screenPoint = new PixelPoint(_frame.ScreenBounds.X + _cursor.X, _frame.ScreenBounds.Y + _cursor.Y);
            var target = _snapService.HitTest(_windows, screenPoint);
            if (target is not null)
            {
                SelectedWindow = target;
                Selection = new PixelRect(
                    target.Bounds.X - _frame.ScreenBounds.X,
                    target.Bounds.Y - _frame.ScreenBounds.Y,
                    target.Bounds.Width,
                    target.Bounds.Height).Intersect(new PixelRect(0, 0, _frame.Width, _frame.Height));
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        UpdateIdleCursor();
        InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        _cursor = ToPixel(e.GetPosition(this));
        UpdateIdleCursor();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _cursor = ToPixel(e.GetPosition(this));
        if (_resizingSelection)
        {
            _resizingSelection = false;
            _resizeHandle = SelectionResizeHandle.None;
            ReleaseMouseCapture();
            UpdateIdleCursor();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (_resizingAnnotation)
        {
            _resizingAnnotation = false;
            _annotationResizeHandle = SelectionResizeHandle.None;
            _annotationAtDragStart = null;
            CompleteAnnotationGesture();
            ReleaseMouseCapture();
            UpdateIdleCursor();
            e.Handled = true;
        }
        else if (_draggingAnnotation)
        {
            _draggingAnnotation = false;
            var textToEdit = _annotationWasSelected && !_annotationMoved ? _annotationAtDragStart?.Id : null;
            CompleteAnnotationGesture();
            _annotationAtDragStart = null;
            ReleaseMouseCapture();
            if (textToEdit is { } id)
            {
                EditTextAnnotation(id);
            }
            e.Handled = true;
        }
        else if (_movingSelection)
        {
            _movingSelection = false;
            ReleaseMouseCapture();
            Cursor = Cursors.SizeAll;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
        else if (_draggingSelection)
        {
            _draggingSelection = false;
            if (Selection.Width < 3 || Selection.Height < 3)
            {
                var target = _snapService.HitTest(
                    _windows,
                    new PixelPoint(_frame.ScreenBounds.X + _cursor.X, _frame.ScreenBounds.Y + _cursor.Y));
                if (target is not null)
                {
                    SelectedWindow = target;
                    Selection = new PixelRect(
                        target.Bounds.X - _frame.ScreenBounds.X,
                        target.Bounds.Y - _frame.ScreenBounds.Y,
                        target.Bounds.Width,
                        target.Bounds.Height).Intersect(new PixelRect(0, 0, _frame.Width, _frame.Height));
                }
                else
                {
                    Selection = default;
                    ReleaseMouseCapture();
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }
            }
            _selectionCommitted = true;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            SelectionCommitted?.Invoke(this, EventArgs.Empty);
        }
        else if (_drawing)
        {
            FinishDrawing(_cursor);
        }
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        UpdateIdleCursor();
        InvalidateVisual();
    }

    private void FinishDrawing(PixelPoint point)
    {
        _drawing = false;
        var freehand = Tool == CaptureTool.Pen || Tool == CaptureTool.Mosaic && MosaicMode == MosaicDrawMode.Brush;
        var end = freehand ? ClipStrokeEnd(point) : ClampAnnotationPoint(point);
        if (freehand && (_workingPoints.Count == 0 || Distance(_workingPoints[^1], end) >= 1.5))
        {
            _workingPoints.Add(end);
        }
        // Release before a text prompt is opened. The drawing flag is already
        // clear, so capture-loss cleanup retains the completed stroke's points.
        ReleaseMouseCapture();
        if (IsMeaningfulAnnotationGesture(_start, end))
        {
            AddAnnotation(_start, end);
        }
        else
        {
            SelectAnnotation(null);
        }
        _workingPoints.Clear();
    }

    private PixelPoint ClipStrokeEnd(PixelPoint point)
    {
        var previous = _workingPoints.Count > 0 ? _workingPoints[^1] : _start;
        var dx = point.X - previous.X;
        var dy = point.Y - previous.Y;
        var fraction = 1.0;
        if (point.X < Selection.X && dx < 0)
            fraction = Math.Min(fraction, (Selection.X - previous.X) / dx);
        else if (point.X > Selection.Right && dx > 0)
            fraction = Math.Min(fraction, (Selection.Right - previous.X) / dx);
        if (point.Y < Selection.Y && dy < 0)
            fraction = Math.Min(fraction, (Selection.Y - previous.Y) / dy);
        else if (point.Y > Selection.Bottom && dy > 0)
            fraction = Math.Min(fraction, (Selection.Bottom - previous.Y) / dy);
        return ClampAnnotationPoint(new PixelPoint(previous.X + fraction * dx, previous.Y + fraction * dy));
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        // Alt-tab, another window or an interrupted gesture must still leave
        // the displayed transform undoable as one operation.
        CompleteAnnotationGesture();
        _draggingAnnotation = false;
        _resizingAnnotation = false;
        _annotationAtDragStart = null;
        _movingSelection = false;
        _resizingSelection = false;
        _draggingSelection = false;
        if (_drawing)
        {
            _drawing = false;
            _workingPoints.Clear();
        }
        UpdateIdleCursor();
        InvalidateVisual();
    }

    private void UpdateIdleCursor()
    {
        if (_resizingSelection || _resizingAnnotation)
        {
            Cursor = CursorForResizeHandle(_resizingSelection ? _resizeHandle : _annotationResizeHandle);
            return;
        }
        if (_movingSelection || _draggingAnnotation)
        {
            Cursor = Cursors.SizeAll;
            return;
        }
        if (!_selectionCommitted || _draggingSelection)
        {
            Cursor = Cursors.Cross;
            return;
        }
        if (_drawing)
        {
            Cursor = CanShowMosaicBrushCursor ? Cursors.None : Contains(Selection, _cursor) ? Cursors.Cross : Cursors.Arrow;
            return;
        }
        var resizeHandle = HitTestSelectionHandle(_cursor);
        if (resizeHandle != SelectionResizeHandle.None)
        {
            Cursor = CursorForResizeHandle(resizeHandle);
            return;
        }

        if (!Contains(Selection, _cursor))
        {
            Cursor = Cursors.Arrow;
            return;
        }

        var annotationHandle = AllowAnnotationEditing ? HitTestAnnotationHandle(_cursor) : SelectionResizeHandle.None;
        if (annotationHandle != SelectionResizeHandle.None)
        {
            Cursor = CursorForResizeHandle(annotationHandle);
            return;
        }
        if (AllowAnnotationEditing && HitTestAnnotation(_cursor) is not null)
        {
            Cursor = Cursors.SizeAll;
            return;
        }

        Cursor = Tool switch
        {
            CaptureTool.Select when Contains(Selection, _cursor) => Cursors.SizeAll,
            CaptureTool.Select => Cursors.Arrow,
            CaptureTool.Mosaic when CanShowMosaicBrushCursor => Cursors.None,
            _ => Cursors.Cross
        };
    }

    private static Cursor CursorForResizeHandle(SelectionResizeHandle handle) => handle switch
    {
        SelectionResizeHandle.TopLeft or SelectionResizeHandle.BottomRight => Cursors.SizeNWSE,
        SelectionResizeHandle.TopRight or SelectionResizeHandle.BottomLeft => Cursors.SizeNESW,
        SelectionResizeHandle.Top or SelectionResizeHandle.Bottom => Cursors.SizeNS,
        SelectionResizeHandle.Left or SelectionResizeHandle.Right => Cursors.SizeWE,
        _ => Cursors.Arrow
    };

    private SelectionResizeHandle HitTestSelectionHandle(PixelPoint point)
    {
        if (!_selectionCommitted || Selection.IsEmpty || !ShowSelectionChrome)
        {
            return SelectionResizeHandle.None;
        }

        // Keep the target comfortable at every monitor scale while retaining
        // physical-pixel selection coordinates internally.
        var scaleX = _frame.Width / Math.Max(1, ActualWidth);
        var scaleY = _frame.Height / Math.Max(1, ActualHeight);
        var hitX = Math.Max(7, 9 * scaleX);
        var hitY = Math.Max(7, 9 * scaleY);
        var nearLeft = Math.Abs(point.X - Selection.X) <= hitX;
        var nearRight = Math.Abs(point.X - Selection.Right) <= hitX;
        var nearTop = Math.Abs(point.Y - Selection.Y) <= hitY;
        var nearBottom = Math.Abs(point.Y - Selection.Bottom) <= hitY;
        var withinHorizontalSpan = point.X >= Selection.X - hitX && point.X <= Selection.Right + hitX;
        var withinVerticalSpan = point.Y >= Selection.Y - hitY && point.Y <= Selection.Bottom + hitY;

        if (nearLeft && nearTop)
            return SelectionResizeHandle.TopLeft;
        if (nearRight && nearTop)
            return SelectionResizeHandle.TopRight;
        if (nearLeft && nearBottom)
            return SelectionResizeHandle.BottomLeft;
        if (nearRight && nearBottom)
            return SelectionResizeHandle.BottomRight;
        if (nearTop && withinHorizontalSpan)
            return SelectionResizeHandle.Top;
        if (nearBottom && withinHorizontalSpan)
            return SelectionResizeHandle.Bottom;
        if (nearLeft && withinVerticalSpan)
            return SelectionResizeHandle.Left;
        if (nearRight && withinVerticalSpan)
            return SelectionResizeHandle.Right;
        return SelectionResizeHandle.None;
    }

    private SelectionResizeHandle HitTestAnnotationHandle(PixelPoint point)
    {
        if (_selectedAnnotationId is not Guid id)
        {
            return SelectionResizeHandle.None;
        }
        var item = _annotations.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null)
        {
            return SelectionResizeHandle.None;
        }
        var bounds = Inflate(GetVisualBounds(item), 5);
        var scaleX = _frame.Width / Math.Max(1, ActualWidth);
        var scaleY = _frame.Height / Math.Max(1, ActualHeight);
        var hitX = Math.Max(7, 9 * scaleX);
        var hitY = Math.Max(7, 9 * scaleY);
        var nearLeft = Math.Abs(point.X - bounds.X) <= hitX;
        var nearRight = Math.Abs(point.X - bounds.Right) <= hitX;
        var nearTop = Math.Abs(point.Y - bounds.Y) <= hitY;
        var nearBottom = Math.Abs(point.Y - bounds.Bottom) <= hitY;
        var withinHorizontal = point.X >= bounds.X - hitX && point.X <= bounds.Right + hitX;
        var withinVertical = point.Y >= bounds.Y - hitY && point.Y <= bounds.Bottom + hitY;
        if (nearLeft && nearTop)
            return SelectionResizeHandle.TopLeft;
        if (nearRight && nearTop)
            return SelectionResizeHandle.TopRight;
        if (nearLeft && nearBottom)
            return SelectionResizeHandle.BottomLeft;
        if (nearRight && nearBottom)
            return SelectionResizeHandle.BottomRight;
        if (nearTop && withinHorizontal)
            return SelectionResizeHandle.Top;
        if (nearBottom && withinHorizontal)
            return SelectionResizeHandle.Bottom;
        if (nearLeft && withinVertical)
            return SelectionResizeHandle.Left;
        if (nearRight && withinVertical)
            return SelectionResizeHandle.Right;
        return SelectionResizeHandle.None;
    }
}
