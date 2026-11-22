using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Screen.App;

internal static partial class DesktopWindowChrome
{
    // Opaque editors keep the native window's single outer contour. The
    // content backdrop continues to its edge, including on software-rendered video.
    public static void SetEditorContent(Window window, UIElement content, Brush outline)
    {
        var frame = new Border
        {
            Background = window.Background,
            BorderBrush = outline,
            BorderThickness = new Thickness(1),
            Child = content
        };
        window.Content = frame;
        window.StateChanged += (_, _) =>
            frame.BorderThickness = new Thickness(window.WindowState == WindowState.Maximized ? 0 : 1);
        Attach(window);
    }

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is not HwndSource source)
                return;

            int light = 0;
            _ = NativeMethods.DwmSetWindowAttribute(source.Handle, 20, ref light, sizeof(int));
            if (!window.AllowsTransparency)
            {
                int round = 2; // DWMWCP_ROUND
                int borderColor = 0xD6D1CA; // COLORREF, RGB #CAD1D6
                _ = NativeMethods.DwmSetWindowAttribute(source.Handle, 33, ref round, sizeof(int));
                _ = NativeMethods.DwmSetWindowAttribute(source.Handle, 34, ref borderColor, sizeof(int));
            }
            source.AddHook(ConstrainMaximize);
        };

        nint ConstrainMaximize(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (message != 0x0024) // WM_GETMINMAXINFO
                return 0;

            var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            var bounds = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == 0 || !GetMonitorInfo(monitor, ref bounds))
                return 0;

            // WindowChrome can clip the HWND to rcWork but leave the WPF layout
            // larger than that region. Size the HWND itself to the work area.
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            info.MaxPosition = new(bounds.Work.Left - bounds.Monitor.Left, bounds.Work.Top - bounds.Monitor.Top);
            info.MaxSize = new(bounds.Work.Right - bounds.Work.Left, bounds.Work.Bottom - bounds.Work.Top);
            double scale = NativeMethods.GetDpiForWindow(hwnd) / 96d;
            info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, (int)Math.Ceiling(window.MinWidth * scale));
            info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, (int)Math.Ceiling(window.MinHeight * scale));
            Marshal.StructureToPtr(info, lParam, false);
            handled = true;
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativeMethods.NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeMethods.NativeRect Monitor, Work;
        public int Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
