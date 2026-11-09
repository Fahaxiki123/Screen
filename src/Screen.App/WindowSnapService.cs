using System.Runtime.InteropServices;
using System.Text;
using Screen.Core;

namespace Screen.App;

internal sealed class WindowSnapService
{
    public IReadOnlyList<WindowTarget> GetWindows(PixelRect displayBounds, nint excludedWindow = 0)
    {
        var result = new List<WindowTarget>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (handle == excludedWindow
                || !NativeMethods.IsWindowVisible(handle)
                || NativeMethods.IsIconic(handle)
                || NativeMethods.GetAncestor(handle, NativeMethods.GaRoot) != handle
                || IsCloaked(handle))
            {
                return true;
            }

            var bounds = GetBounds(handle);
            var clipped = bounds.Intersect(displayBounds);
            if (clipped.Width < 32 || clipped.Height < 24)
            {
                return true;
            }

            var titleLength = NativeMethods.GetWindowTextLengthW(handle);
            var title = string.Empty;
            if (titleLength > 0)
            {
                var builder = new StringBuilder(titleLength + 1);
                NativeMethods.GetWindowTextW(handle, builder, builder.Capacity);
                title = builder.ToString();
            }
            result.Add(new WindowTarget(handle, title, bounds));
            return true;
        }, 0);
        return result;
    }

    public WindowTarget? HitTest(IReadOnlyList<WindowTarget> windows, PixelPoint screenPoint) =>
        windows.FirstOrDefault(window =>
            screenPoint.X >= window.Bounds.X && screenPoint.X < window.Bounds.Right
            && screenPoint.Y >= window.Bounds.Y && screenPoint.Y < window.Bounds.Bottom);

    private static PixelRect GetBounds(nint handle)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                handle,
                NativeMethods.DwmwaExtendedFrameBounds,
                out NativeMethods.NativeRect frame,
                (uint)Marshal.SizeOf<NativeMethods.NativeRect>()) != 0)
        {
            NativeMethods.GetWindowRect(handle, out frame);
        }
        return new PixelRect(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
    }

    private static bool IsCloaked(nint handle) =>
        NativeMethods.DwmGetWindowAttribute(
            handle,
            NativeMethods.DwmwaCloaked,
            out int cloaked,
            sizeof(int)) == 0
        && cloaked != 0;
}
