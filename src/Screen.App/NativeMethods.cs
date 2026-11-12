using System.Runtime.InteropServices;
using System.Text;

namespace Screen.App;

internal static partial class NativeMethods
{
    internal const int WmHotKey = 0x0312;
    internal const uint ModAlt = 0x0001;
    internal const uint ModControl = 0x0002;
    internal const uint ModShift = 0x0004;
    internal const uint ModWin = 0x0008;
    internal const uint ModNoRepeat = 0x4000;
    internal const int GwlExStyle = -20;
    internal const long WsExTransparent = 0x00000020L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const uint DwmwaExtendedFrameBounds = 9;
    internal const uint DwmwaCloaked = 14;
    internal const int SwRestore = 9;
    internal const uint GaRoot = 2;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpShowWindow = 0x0040;
    internal const int WhKeyboardLl = 13;
    internal const int WhMouseLl = 14;
    internal const int WmLButtonDown = 0x0201;
    internal const int WmKeyDown = 0x0100;
    internal const int WmSysKeyDown = 0x0104;
    internal const int WmKeyUp = 0x0101;
    internal const int WmSysKeyUp = 0x0105;
    internal const int WmMouseWheel = 0x020A;
    internal const uint WdaExcludeFromCapture = 0x00000011;
    internal const uint MonitorDefaultToNearest = 2;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hWnd, int id);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int virtualKey);

    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint hWnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial nint GetWindowLongPtr(nint hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial nint SetWindowLongPtr(nint hWnd, int index, nint newLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out NativeRect rect);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(nint hWnd, StringBuilder text, int maximumCount);

    [LibraryImport("user32.dll")]
    internal static partial nint WindowFromPoint(NativePoint point);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPhysicalCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(NativePoint point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hWnd, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint hWnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hWnd);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hWnd, uint attribute, out NativeRect value, uint size);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hWnd, uint attribute, out int value, uint size);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredWrite(ref NativeCredential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredRead(string targetName, uint type, uint flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredDelete(string targetName, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    internal static partial void CredFree(nint buffer);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWindowsHookExW(int hookId, KeyboardHookProc callback, nint module, uint threadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    internal static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? moduleName);

    // DWMWA_USE_IMMERSIVE_DARK_MODE — makes the system title bar follow the
    // window's dark content instead of the system light theme.
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hWnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowDisplayAffinity(nint hWnd, uint affinity);

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    internal static uint GetDpiAtPoint(NativePoint point)
    {
        var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
        return monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0
            ? Math.Max(96, dpiX)
            : 96;
    }

    internal static bool TryGetPhysicalCursorPosition(out NativePoint point)
    {
        if (GetPhysicalCursorPos(out point) || GetCursorPos(out point))
        {
            return true;
        }

        try
        {
            var primary = System.Windows.Forms.SystemInformation.PrimaryMonitorSize;
            point = new NativePoint { X = primary.Width / 2, Y = primary.Height / 2 };
            return true;
        }
        catch
        {
            point = new NativePoint { X = 0, Y = 0 };
            return true;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);

    internal const uint DiNormal = 0x0003;

    [LibraryImport("user32.dll")]
    internal static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(nint hWnd, nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    internal static partial nint SelectObject(nint hdc, nint gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint gdiObject);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateDIBSection(
        nint hdc,
        ref BitmapInfoHeader header,
        uint usage,
        out nint bits,
        nint sectionHandle,
        uint offset);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    internal const int SmCursorWidth = 13;
    internal const int SmCursorHeight = 14;
    internal const int IdcArrow = 32512;
    internal const int IdcIBeam = 32513;
    internal const int IdcCross = 32515;

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)]
    internal static partial nint LoadCursor(nint hInstance, nint cursorName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetIconInfo(nint hIcon, out IconInfo iconInfo);

    [LibraryImport("gdi32.dll", EntryPoint = "GetObjectW")]
    internal static partial int GetBitmapObject(nint gdiObject, int bufferSize, out NativeBitmap bitmap);

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo
    {
        internal int IsIcon;
        internal uint XHotspot;
        internal uint YHotspot;
        internal nint MaskBitmap;
        internal nint ColorBitmap;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeBitmap
    {
        internal int BmType;
        internal int BmWidth;
        internal int BmHeight;
        internal int BmWidthBytes;
        internal ushort BmPlanes;
        internal ushort BmBitsPixel;
        internal nint BmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        internal uint BiSize;
        internal int BiWidth;
        internal int BiHeight;
        internal ushort BiPlanes;
        internal ushort BiBitCount;
        internal uint BiCompression;
        internal uint BiSizeImage;
        internal int BiXPelsPerMeter;
        internal int BiYPelsPerMeter;
        internal uint BiClrUsed;
        internal uint BiClrImportant;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorInfo(ref CursorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DrawIconEx(
        nint hdc,
        int xLeft,
        int yTop,
        nint cursor,
        int width,
        int height,
        uint stepIfAniCursor,
        nint brushFlicker,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct CursorInfo
    {
        internal uint CbSize;
        internal uint Flags;
        internal nint Cursor;
        internal NativePoint ScreenPosition;
    }

    internal delegate nint KeyboardHookProc(int code, nint wParam, nint lParam);

    internal delegate nint MouseHookProc(int code, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWindowsHookExW(int hookId, MouseHookProc callback, nint module, uint threadId);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        internal int X;
        internal int Y;

        internal NativePoint(int x, int y) => (X, Y) = (x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NativeCredential
    {
        internal uint Flags;
        internal uint Type;
        internal nint TargetName;
        internal nint Comment;
        internal long LastWritten;
        internal uint CredentialBlobSize;
        internal nint CredentialBlob;
        internal uint Persist;
        internal uint AttributeCount;
        internal nint Attributes;
        internal nint TargetAlias;
        internal nint UserName;
    }
}
