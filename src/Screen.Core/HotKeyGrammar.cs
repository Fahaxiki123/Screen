namespace Screen.Core;

/// <summary>
/// Pure parser for user-editable hotkey strings such as "Ctrl+Alt+A" or "F9".
/// Produces RegisterHotKey-compatible modifier flags plus the key name; key
/// names follow WPF's System.Windows.Input.Key enum spelling ("A", "F5",
/// "Oem3"). Living in Core keeps this logic unit-testable without a WPF host;
/// callers map <see cref="HotKeyDescriptor.KeyName"/> to their key type and
/// reject names their platform does not know.
/// </summary>
public static class HotKeyGrammar
{
    [Flags]
    public enum ModifierFlags : uint
    {
        None = 0,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Windows = 0x0008,
    }

    public readonly record struct HotKeyDescriptor(ModifierFlags Modifiers, string KeyName);

    // Key enum names that are themselves modifiers (or the null key) can never
    // form a usable hotkey combination.
    private static readonly HashSet<string> InvalidKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "None",
        "LeftCtrl", "RightCtrl",
        "LeftShift", "RightShift",
        "LeftAlt", "RightAlt",
        "LWin", "RWin",
    };

    public static bool TryParse(
        string text,
        out HotKeyDescriptor descriptor,
        bool allowUnmodifiedFunctionKey = false)
    {
        descriptor = default;
        var modifiers = ModifierFlags.None;
        string? keyName = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!seen.Add(part))
            {
                return false;
            }
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierFlags.Control;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierFlags.Shift;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierFlags.Alt;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierFlags.Windows;
            }
            else if (keyName is not null)
            {
                // Two non-modifier parts ("Ctrl+A+B") is never a valid hotkey.
                return false;
            }
            else
            {
                keyName = part;
            }
        }
        if (keyName is null || InvalidKeyNames.Contains(keyName))
        {
            return false;
        }
        var unmodifiedFunctionKey = allowUnmodifiedFunctionKey && IsFunctionKeyName(keyName);
        if (modifiers == ModifierFlags.None && !unmodifiedFunctionKey)
        {
            return false;
        }
        descriptor = new HotKeyDescriptor(modifiers, keyName);
        return true;
    }

    private static bool IsFunctionKeyName(string name) =>
        name.Length is 2 or 3
        && (name[0] is 'F' or 'f')
        && int.TryParse(name[1..], out var number)
        && number is >= 1 and <= 24;
}
