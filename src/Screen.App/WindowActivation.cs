using System.Windows;

namespace Screen.App;

internal static class WindowActivation
{
    public static void ShowTopmost(Window window)
    {
        // Recording stops while another application still owns the foreground.
        // Keep the result above it even when Windows declines a focus change.
        window.Topmost = true;
        window.ShowActivated = true;
        window.ContentRendered += ActivateAfterFirstRender;
        window.Closed += CancelPendingActivation;
        window.Show();

        void ActivateAfterFirstRender(object? sender, EventArgs args)
        {
            CancelPendingActivation(sender, args);
            if (window.IsVisible && window.WindowState != WindowState.Minimized)
            {
                // One request after layout is ready; never reclaim focus when
                // the user switches applications or minimizes this window.
                window.Activate();
            }
        }

        void CancelPendingActivation(object? sender, EventArgs args)
        {
            window.ContentRendered -= ActivateAfterFirstRender;
            window.Closed -= CancelPendingActivation;
        }
    }

    public static void Activate(nint handle) => Activate(
        handle,
        NativeMethods.IsIconic,
        window => NativeMethods.ShowWindow(window, NativeMethods.SwRestore),
        window => NativeMethods.SetForegroundWindow(window));

    internal static void Activate(
        nint handle,
        Func<nint, bool> isMinimized,
        Action<nint> restore,
        Action<nint> setForeground)
    {
        if (handle == 0)
        {
            return;
        }
        // SW_RESTORE also unmaximizes a maximized window. The capture region
        // was chosen before activation, so preserve its existing placement.
        if (isMinimized(handle))
        {
            restore(handle);
        }
        setForeground(handle);
    }
}
