using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Screen.App;

internal sealed class HotKeyWindow : IDisposable
{
    private const uint VkF12 = 0x7B;
    private const int WmPriorityHotKey = 0x8001;
    private readonly HwndSource _source;
    private readonly Binding[] _bindings;
    private readonly NativeMethods.KeyboardHookProc _callback;
    private readonly HashSet<uint> _consumedKeys = [];
    private readonly HashSet<uint> _heldWhenRegistered = [];
    private nint _hook;
    private bool _preferScreen;

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
        _bindings =
        [
            CreateBinding(0x5343, () => CaptureRequested?.Invoke(this, EventArgs.Empty)),
            CreateBinding(0x5350, () => ClickThroughRecoveryRequested?.Invoke(this, EventArgs.Empty)),
            CreateBinding(0x5352, () => RecordRequested?.Invoke(this, EventArgs.Empty))
        ];
        _callback = PriorityKeyCallback;
        _source.AddHook(WndProc);
    }

    public event EventHandler? CaptureRequested;
    public event EventHandler? ClickThroughRecoveryRequested;
    public event EventHandler? RecordRequested;

    public Func<Key, ModifierKeys, bool>? TryCaptureInEditor { get; set; }

    public int LastNativeError { get; private set; }

    public bool RegisterCaptureHotKey(string hotKey) => Register(_bindings[0], hotKey, force: false);
    public bool RegisterClickThroughRecoveryHotKey(string hotKey) => Register(_bindings[1], hotKey, force: false);
    public bool RegisterRecordHotKey(string hotKey) => Register(_bindings[2], hotKey, force: false);

    internal bool RegisterCaptureHotKey(string hotKey, bool force) => Register(_bindings[0], hotKey, force);
    internal bool RegisterClickThroughRecoveryHotKey(string hotKey, bool force) => Register(_bindings[1], hotKey, force);
    internal bool RegisterRecordHotKey(string hotKey, bool force) => Register(_bindings[2], hotKey, force);

    /// <summary>
    /// Reinstalls the low-level keyboard hook when priority mode or F12 is active,
    /// recovering cleanly if Windows silently unhooked it after a CPU spike or lock/unlock.
    /// </summary>
    public bool RefreshHookIfNeeded()
    {
        if (!_preferScreen && !_bindings.Any(binding => binding.Chord?.Key == VkF12))
        {
            return true;
        }

        _consumedKeys.Clear();
        RemoveHook();
        return EnsureHook();
    }

    public bool TrySetPriority(bool preferScreen)
    {
        if (_preferScreen == preferScreen)
        {
            return true;
        }
        if (preferScreen)
        {
            if (!EnsureHook())
            {
                return false;
            }
            _preferScreen = true;
            foreach (var binding in _bindings)
            {
                binding.Native.Dispose();
            }
            return true;
        }

        _preferScreen = false;
        foreach (var binding in _bindings)
        {
            if (binding.Chord is { } chord && chord.Key != VkF12)
            {
                binding.Native.TryRegister(chord.Modifiers | NativeMethods.ModNoRepeat, chord.Key);
            }
        }
        ReleaseUnusedHook();
        return true;
    }

    private bool Register(Binding binding, string text, bool force)
    {
        LastNativeError = 0;
        if (!TryParseHotKey(text, out var modifiers, out var key))
        {
            return false;
        }
        var chord = new Chord(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        if (chord.Key == 0 || _bindings.Any(other => other != binding && other.Chord == chord))
        {
            return false;
        }
        if (UsesHook(chord))
        {
            if (!EnsureHook())
            {
                return false;
            }
            binding.Native.Dispose();
            // The keycap can install a new chord during its own KeyDown. That press
            // belongs to the editor until release, including any hardware repeats.
            if (binding.Chord != chord && NativeMethods.GetAsyncKeyState((int)chord.Key) < 0)
                _heldWhenRegistered.Add(chord.Key);
        }
        else if (!binding.Native.TryRegister(modifiers | NativeMethods.ModNoRepeat, chord.Key, force))
        {
            LastNativeError = Marshal.GetLastWin32Error();
            return false;
        }
        binding.Chord = chord;
        ReleaseUnusedHook();
        return true;
    }

    // RegisterHotKey reserves F12 for the debugger even when it is absent.
    private bool UsesHook(Chord chord) => _preferScreen || chord.Key == VkF12;

    private bool EnsureHook()
    {
        if (_hook == 0)
        {
            _hook = NativeMethods.SetWindowsHookExW(
                NativeMethods.WhKeyboardLl, _callback, NativeMethods.GetModuleHandle(null), 0);
            if (_hook == 0)
            {
                LastNativeError = Marshal.GetLastWin32Error();
            }
        }
        return _hook != 0;
    }

    private void ReleaseUnusedHook()
    {
        if (!_preferScreen && _consumedKeys.Count == 0 && !_bindings.Any(binding => binding.Chord?.Key == VkF12))
        {
            RemoveHook();
        }
    }

    private nint PriorityKeyCallback(int code, nint wParam, nint lParam)
    {
        const int injected = 0x10;
        if (code < 0 || (Marshal.ReadInt32(lParam, 8) & injected) != 0)
        {
            return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
        }
        var message = wParam.ToInt32();
        var key = (uint)Marshal.ReadInt32(lParam);
        if (message is NativeMethods.WmKeyUp or NativeMethods.WmSysKeyUp)
        {
            _heldWhenRegistered.Remove(key);
            if (_consumedKeys.Remove(key))
            {
                return 1;
            }
        }
        else if (message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown)
        {
            if (_consumedKeys.Contains(key))
            {
                return 1;
            }
            if (_heldWhenRegistered.Contains(key))
                return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
            foreach (var binding in _bindings)
            {
                if (binding.Chord is not { } chord || chord.Key != key || !UsesHook(chord)
                    || chord.Modifiers != CurrentModifiers())
                {
                    continue;
                }
                if (NativeMethods.PostMessageW(_source.Handle, WmPriorityHotKey, (nuint)binding.Id, (nint)key))
                {
                    _consumedKeys.Add(key);
                    return 1;
                }
                break;
            }
        }
        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static uint CurrentModifiers()
    {
        static bool Down(int key) => NativeMethods.GetAsyncKeyState(key) < 0;
        return (Down(0x11) ? NativeMethods.ModControl : 0)
             | (Down(0x10) ? NativeMethods.ModShift : 0)
             | (Down(0x12) ? NativeMethods.ModAlt : 0)
             | (Down(0x5B) || Down(0x5C) ? NativeMethods.ModWin : 0);
    }

    private Binding CreateBinding(int id, Action action) => new(id, action, new HotKeyRegistration(
        id,
        id + 1,
        (registrationId, modifiers, key) => NativeMethods.RegisterHotKey(_source.Handle, registrationId, modifiers, key),
        registrationId => NativeMethods.UnregisterHotKey(_source.Handle, registrationId)));

    public static bool TryParseHotKey(string text, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        if (!Screen.Core.HotKeyGrammar.TryParse(text, out var descriptor, allowUnmodifiedFunctionKey: true)
            || !Enum.TryParse(descriptor.KeyName, true, out key)
            || !Enum.IsDefined(key)
            || key == Key.None)
        {
            return false;
        }
        modifiers = (uint)descriptor.Modifiers;
        return true;
    }

    private nint WndProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message is not (NativeMethods.WmHotKey or WmPriorityHotKey))
        {
            return 0;
        }
        foreach (var binding in _bindings)
        {
            if (binding.Chord is not { } chord)
                continue;
            bool matches = message == NativeMethods.WmHotKey
                ? binding.Native.IsActiveId(wParam.ToInt32())
                : binding.Id == wParam.ToInt32() && chord.Key == (uint)lParam && UsesHook(chord);
            if (!matches)
                continue;

            // Windows can consume a registered combination before the focused keycap gets KeyDown.
            handled = true;
            if (TryCaptureInEditor?.Invoke(KeyInterop.KeyFromVirtualKey((int)chord.Key), (ModifierKeys)chord.Modifiers) != true)
                binding.Action();
            break;
        }
        return 0;
    }

    private void RemoveHook()
    {
        _heldWhenRegistered.Clear();
        if (_hook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
        GC.KeepAlive(_callback);
    }

    public void Dispose()
    {
        RemoveHook();
        foreach (var binding in _bindings)
        {
            binding.Native.Dispose();
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private readonly record struct Chord(uint Modifiers, uint Key);

    private sealed class Binding(int id, Action action, HotKeyRegistration native)
    {
        public int Id { get; } = id;
        public Action Action { get; } = action;
        public HotKeyRegistration Native { get; } = native;
        public Chord? Chord { get; set; }
    }
}
