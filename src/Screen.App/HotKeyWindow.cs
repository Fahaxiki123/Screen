using System.Windows.Input;
using System.Windows.Interop;

namespace Screen.App;

internal sealed class HotKeyWindow : IDisposable
{
    private const int CaptureHotKeyId = 0x5343;
    private const int ClickThroughRecoveryHotKeyId = 0x5350;
    private const int RecordHotKeyId = 0x5352;
    private readonly HwndSource _source;
    private readonly HotKeyRegistration _capture;
    private readonly HotKeyRegistration _clickThroughRecovery;
    private readonly HotKeyRegistration _record;

    public HotKeyWindow()
    {
        var parameters = new HwndSourceParameters("Screen.HotKey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = (int)(NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate)
        };
        _source = new HwndSource(parameters);
        _capture = CreateRegistration(CaptureHotKeyId, CaptureHotKeyId + 1);
        _clickThroughRecovery = CreateRegistration(ClickThroughRecoveryHotKeyId, ClickThroughRecoveryHotKeyId + 1);
        _record = CreateRegistration(RecordHotKeyId, RecordHotKeyId + 1);
        _source.AddHook(WndProc);
    }

    public event EventHandler? CaptureRequested;
    public event EventHandler? ClickThroughRecoveryRequested;
    public event EventHandler? RecordRequested;

    public bool RegisterCaptureHotKey(string hotKey)
    {
        if (!TryParseHotKey(hotKey, out var modifiers, out var key))
        {
            return false;
        }
        return _capture.TryRegister(
            modifiers | NativeMethods.ModNoRepeat,
            (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    public bool RegisterClickThroughRecoveryHotKey(string hotKey)
    {
        if (!TryParseHotKey(hotKey, out var modifiers, out var key, allowUnmodifiedFunctionKey: true))
        {
            return false;
        }
        return _clickThroughRecovery.TryRegister(
            modifiers | NativeMethods.ModNoRepeat,
            (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    public bool RegisterRecordHotKey(string hotKey)
    {
        if (!TryParseHotKey(hotKey, out var modifiers, out var key))
        {
            return false;
        }
        return _record.TryRegister(
            modifiers | NativeMethods.ModNoRepeat,
            (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    private HotKeyRegistration CreateRegistration(int firstId, int secondId) => new(
        firstId,
        secondId,
        (id, modifiers, key) => NativeMethods.RegisterHotKey(_source.Handle, id, modifiers, key),
        id => NativeMethods.UnregisterHotKey(_source.Handle, id));

    public static bool TryParseHotKey(
        string text,
        out uint modifiers,
        out Key key,
        bool allowUnmodifiedFunctionKey = false)
    {
        modifiers = 0;
        key = Key.None;
        if (!Screen.Core.HotKeyGrammar.TryParse(text, out var descriptor, allowUnmodifiedFunctionKey))
        {
            return false;
        }
        // The grammar layer validates structure and modifiers; only the key
        // name resolution is WPF-specific.
        if (!Enum.TryParse(descriptor.KeyName, true, out key) || key == Key.None)
        {
            return false;
        }
        modifiers = (uint)descriptor.Modifiers;
        return true;
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotKey && _capture.IsActiveId(wParam.ToInt32()))
        {
            handled = true;
            CaptureRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (message == NativeMethods.WmHotKey && _clickThroughRecovery.IsActiveId(wParam.ToInt32()))
        {
            handled = true;
            ClickThroughRecoveryRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (message == NativeMethods.WmHotKey && _record.IsActiveId(wParam.ToInt32()))
        {
            handled = true;
            RecordRequested?.Invoke(this, EventArgs.Empty);
        }
        return 0;
    }

    public void Dispose()
    {
        _capture.Dispose();
        _clickThroughRecovery.Dispose();
        _record.Dispose();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
