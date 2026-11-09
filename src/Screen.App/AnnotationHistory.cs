using Screen.Core;

namespace Screen.App;

internal sealed record AnnotationSnapshot(AnnotationItem[] Items, int NextNumber)
{
    public static AnnotationSnapshot Capture(IEnumerable<AnnotationItem> items, int nextNumber) =>
        new(items.ToArray(), nextNumber);

    public bool HasSameContent(AnnotationSnapshot other) =>
        NextNumber == other.NextNumber && Items.Length == other.Items.Length
        && Items.Zip(other.Items).All(pair => SameItem(pair.First, pair.Second));

    private static bool SameItem(AnnotationItem first, AnnotationItem second) =>
        ReferenceEquals(first, second)
        || first.Id == second.Id && first.Kind == second.Kind && first.Bounds == second.Bounds
        && first.Style == second.Style && first.Text == second.Text && first.Number == second.Number
        && first.ZIndex == second.ZIndex && first.Points.SequenceEqual(second.Points);
}

internal sealed class AnnotationHistory
{
    private readonly Stack<AnnotationSnapshot> _undo = new();
    private readonly Stack<AnnotationSnapshot> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public bool Record(AnnotationSnapshot before, AnnotationSnapshot after)
    {
        if (before.HasSameContent(after))
        {
            return false;
        }
        _undo.Push(before);
        _redo.Clear();
        return true;
    }

    public AnnotationHistoryState Export() => new(_undo.ToArray(), _redo.ToArray());

    public void Restore(AnnotationHistoryState state)
    {
        _undo.Clear();
        _redo.Clear();
        foreach (var snapshot in state.Undo.Reverse())
        {
            _undo.Push(snapshot);
        }
        foreach (var snapshot in state.Redo.Reverse())
        {
            _redo.Push(snapshot);
        }
    }

    public AnnotationSnapshot? Undo(AnnotationSnapshot current)
    {
        if (!_undo.TryPop(out var previous))
        {
            return null;
        }
        _redo.Push(current);
        return previous;
    }

    public AnnotationSnapshot? Redo(AnnotationSnapshot current)
    {
        if (!_redo.TryPop(out var next))
        {
            return null;
        }
        _undo.Push(current);
        return next;
    }
}

internal sealed record AnnotationHistoryState(AnnotationSnapshot[] Undo, AnnotationSnapshot[] Redo)
{
    public static AnnotationHistoryState Empty { get; } = new([], []);
}

internal sealed record AnnotationEditingState(
    AnnotationSnapshot Current,
    AnnotationHistoryState History,
    Guid? SelectedId);
