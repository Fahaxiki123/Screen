using System.Runtime.InteropServices;

namespace Screen.App;

/// <summary>
/// Global low-level mouse hook used for the recording click-ripple effect.
/// Never swallows events — it only observes left-button-down and forwards.
/// </summary>
internal sealed class LowLevelMouseHook : IDisposable
{
    private readonly NativeMethods.MouseHookProc _callback;
    private nint _hook;

    public event EventHandler<NativeMethods.NativePoint>? LeftButtonDown;

    public LowLevelMouseHook()
    {
        _callback = HookCallback;
        _hook = NativeMethods.SetWindowsHookExW(
            NativeMethods.WhMouseLl,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);
        if (_hook == 0)
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to install the recording mouse hook.");
        }
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && wParam.ToInt32() == NativeMethods.WmLButtonDown)
        {
            // MSLLHOOKSTRUCT starts with a POINT: two int32s.
            var point = new NativeMethods.NativePoint(
                Marshal.ReadInt32(lParam),
                Marshal.ReadInt32(lParam, 4));
            LeftButtonDown?.Invoke(this, point);
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
        GC.KeepAlive(_callback);
    }
}
