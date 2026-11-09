using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Screen.App;

internal sealed class LowLevelKeyboardHook : IDisposable
{
    private readonly NativeMethods.KeyboardHookProc _callback;
    private readonly nint _targetWindow;
    private readonly bool _requireForegroundWindow;
    private nint _hook;
    private bool _enterDown;
    private bool _escapeDown;

    public LowLevelKeyboardHook(nint targetWindow, bool requireForegroundWindow = true)
    {
        _targetWindow = targetWindow;
        _requireForegroundWindow = requireForegroundWindow;
        _callback = HookCallback;
        _hook = NativeMethods.SetWindowsHookExW(
            NativeMethods.WhKeyboardLl,
            _callback,
            NativeMethods.GetModuleHandle(null),
            0);
        if (_hook == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to install the long-capture keyboard hook.");
        }
    }

    public event EventHandler? EnterPressed;
    public event EventHandler? EscapePressed;

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var virtualKey = Marshal.ReadInt32(lParam);
        var isEnter = virtualKey == KeyInterop.VirtualKeyFromKey(Key.Enter);
        var isEscape = virtualKey == KeyInterop.VirtualKeyFromKey(Key.Escape);
        if (!isEnter && !isEscape)
        {
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp)
        {
            if (isEnter)
                _enterDown = false;
            if (isEscape)
                _escapeDown = false;
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        }
        if (message is not NativeMethods.WmKeyDown and not NativeMethods.WmSysKeyDown)
        {
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        }

        const int llkhfInjected = 0x10;
        var flags = Marshal.ReadInt32(lParam, 8);
        if ((flags & llkhfInjected) != 0
            || (_requireForegroundWindow
                && (_targetWindow == 0 || NativeMethods.GetForegroundWindow() != _targetWindow)))
        {
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (isEnter)
        {
            // Never swallow a key nobody is listening for — a recording hook
            // installed globally would otherwise eat Enter in other apps.
            if (EnterPressed is null)
            {
                return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
            }
            if (!_enterDown)
            {
                _enterDown = true;
                EnterPressed?.Invoke(this, EventArgs.Empty);
            }
            return 1;
        }
        if (EscapePressed is null)
        {
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        }
        if (!_escapeDown)
        {
            _escapeDown = true;
            EscapePressed?.Invoke(this, EventArgs.Empty);
        }
        return 1;
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
