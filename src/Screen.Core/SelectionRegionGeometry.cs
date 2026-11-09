namespace Screen.Core;

public enum SelectionResizeHandle
{
    None,
    TopLeft,
    Top,
    TopRight,
    Left,
    Right,
    BottomLeft,
    Bottom,
    BottomRight
}

public static class SelectionRegionGeometry
{
    public static PixelRect Resize(
        PixelRect original,
        SelectionResizeHandle handle,
        PixelPoint pointer,
        PixelRect limits,
        int minimumSize = 1)
    {
        if (handle == SelectionResizeHandle.None || original.IsEmpty || limits.IsEmpty)
        {
            return original;
        }

        minimumSize = Math.Max(1, Math.Min(minimumSize, Math.Min(limits.Width, limits.Height)));
        var pointerX = (int)Math.Round(pointer.X);
        var pointerY = (int)Math.Round(pointer.Y);
        var left = original.X;
        var top = original.Y;
        var right = original.Right;
        var bottom = original.Bottom;

        if (handle is SelectionResizeHandle.TopLeft or SelectionResizeHandle.Left or SelectionResizeHandle.BottomLeft)
        {
            left = Math.Clamp(pointerX, limits.X, Math.Max(limits.X, right - minimumSize));
        }
        if (handle is SelectionResizeHandle.TopRight or SelectionResizeHandle.Right or SelectionResizeHandle.BottomRight)
        {
            right = Math.Clamp(pointerX, Math.Min(limits.Right, left + minimumSize), limits.Right);
        }
        if (handle is SelectionResizeHandle.TopLeft or SelectionResizeHandle.Top or SelectionResizeHandle.TopRight)
        {
            top = Math.Clamp(pointerY, limits.Y, Math.Max(limits.Y, bottom - minimumSize));
        }
        if (handle is SelectionResizeHandle.BottomLeft or SelectionResizeHandle.Bottom or SelectionResizeHandle.BottomRight)
        {
            bottom = Math.Clamp(pointerY, Math.Min(limits.Bottom, top + minimumSize), limits.Bottom);
        }

        return new PixelRect(left, top, right - left, bottom - top);
    }
}
