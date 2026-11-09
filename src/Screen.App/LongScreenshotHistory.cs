using Screen.Core;

namespace Screen.App;

// History contains coordinates and immutable annotation records only. It never
// retains a cropped bitmap or a WPF surface; each restored view uses the single
// source image owned by the editor.
internal sealed record LongScreenshotEditState(
    PixelRect SourceRegion,
    AnnotationEditingState Annotations,
    CaptureTool Tool,
    uint Color,
    double Thickness,
    double Zoom,
    bool FitWidth,
    double HorizontalOffset,
    double VerticalOffset);

internal sealed class LongScreenshotHistory(int maximumStates = 20)
{
    private readonly List<LongScreenshotEditState> _undo = [];
    private readonly List<LongScreenshotEditState> _redo = [];
    private readonly int _maximumStates = maximumStates > 0
        ? maximumStates
        : throw new ArgumentOutOfRangeException(nameof(maximumStates));

    public int RetainedStateCount => _undo.Count + _redo.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void RecordCrop(LongScreenshotEditState before)
    {
        // A new crop replaces the previous redo branch, including any undone
        // annotations on the image that is about to become a history entry.
        _undo.Add(before with
        {
            Annotations = before.Annotations with
            {
                History = before.Annotations.History with { Redo = [] }
            }
        });
        _redo.Clear();
        if (_undo.Count > _maximumStates)
        {
            _undo.RemoveAt(0);
        }
    }

    public LongScreenshotEditState? Undo(LongScreenshotEditState current)
    {
        if (_undo.Count == 0)
        {
            return null;
        }
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        return previous;
    }

    public LongScreenshotEditState? Redo(LongScreenshotEditState current)
    {
        if (_redo.Count == 0)
        {
            return null;
        }
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(current);
        return next;
    }

    public void ClearRedo() => _redo.Clear();

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public static AnnotationEditingState RebaseAnnotations(
        AnnotationSnapshot annotations,
        PixelRect cropInOriginal,
        int previousFactor,
        int nextFactor)
    {
        var scale = previousFactor / (double)nextFactor;
        var dx = cropInOriginal.X / (double)nextFactor;
        var dy = cropInOriginal.Y / (double)nextFactor;
        var items = annotations.Items.Select(item =>
        {
            var scaled = CaptureSurface.ScaleItem(item, scale);
            return scaled with
            {
                Points = scaled.Points.Select(point => new PixelPoint(point.X - dx, point.Y - dy)).ToArray(),
                Bounds = scaled.Bounds with
                {
                    X = (int)Math.Round(item.Bounds.X * scale - dx),
                    Y = (int)Math.Round(item.Bounds.Y * scale - dy)
                }
            };
        }).ToArray();
        return new(new AnnotationSnapshot(items, annotations.NextNumber), AnnotationHistoryState.Empty, null);
    }
}
