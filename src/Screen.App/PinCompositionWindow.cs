using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Screen.App;

/// <summary>DWM draws this GPU-backed visual surface over the pin's unchanged per-pixel input window.</summary>
internal sealed partial class PinCompositionWindow : Window
{
    private readonly nint _handle;
    private nint _thumbnail;
    private NativeMethods.NativeRect _lastBounds;
    private byte _opacity;
    public Grid Layers { get; } = new();
    public Button CloseVisual { get; }

    public PinCompositionWindow(PinWindow pin, Button closeVisual)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Background = Brushes.Transparent;
        Content = Layers;
        CloseVisual = closeVisual;

        try
        {
            _handle = new WindowInteropHelper(this).EnsureHandle();
            var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle);
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, (nint)(style.ToInt64() | NativeMethods.WsExNoActivate));
            int cloak = 1;
            Marshal.ThrowExceptionForHR(NativeMethods.DwmSetWindowAttribute(_handle, 13, ref cloak, sizeof(int)));
            HwndSource.FromHwnd(_handle).CompositionTarget.BackgroundColor = Colors.Transparent;
            var glass = new GlassMargins(-1, -1, -1, -1);
            Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(_handle, ref glass));
            Marshal.ThrowExceptionForHR(DwmRegisterThumbnail(new WindowInteropHelper(pin).Handle, _handle, out _thumbnail));
        }
        catch
        {
            Close();
            throw;
        }
    }

    public bool Fit(NativeMethods.NativeRect bounds, Thickness gutter, double opacity)
    {
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        if (width < 1 || height < 1)
            return false;
        Layers.Margin = gutter;
        bool resized = width != _lastBounds.Right - _lastBounds.Left || height != _lastBounds.Bottom - _lastBounds.Top;
        if (resized || bounds.Left != _lastBounds.Left || bounds.Top != _lastBounds.Top)
        {
            if (!NativeMethods.SetWindowPos(_handle, 0, bounds.Left, bounds.Top, width, height,
                    NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate))
                return false;
            _lastBounds = bounds;
        }
        byte alpha = (byte)Math.Round(255 * opacity);
        if (!resized && alpha == _opacity)
            return true;
        var properties = new ThumbnailProperties
        {
            Flags = 1 | 4 | 8 | 16, // destination, opacity, visible, source client area only
            Destination = new NativeMethods.NativeRect { Right = width, Bottom = height },
            Opacity = alpha,
            Visible = 1,
            SourceClientOnly = 1
        };
        _opacity = alpha;
        return DwmUpdateThumbnailProperties(_thumbnail, ref properties) >= 0;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_thumbnail != 0)
        {
            DwmUnregisterThumbnail(_thumbnail);
            _thumbnail = 0;
        }
        base.OnClosing(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GlassMargins(int Left, int Right, int Top, int Bottom);

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint Flags;
        public NativeMethods.NativeRect Destination, Source;
        public byte Opacity;
        public int Visible, SourceClientOnly;
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmExtendFrameIntoClientArea(nint hwnd, ref GlassMargins margins);
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmUnregisterThumbnail(nint thumbnail);
    [LibraryImport("dwmapi.dll")]
    private static partial int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);
}
