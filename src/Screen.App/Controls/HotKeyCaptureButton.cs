using System.Windows.Input;

namespace Screen.App;

/// <summary>A normal tab stop until explicitly activated. Only an armed button captures shortcuts.</summary>
internal sealed class HotKeyCaptureButton : Button
{
    public event Action<Key, ModifierKeys>? ShortcutEntered;

    public void EndCapture() => HudActiveButton.SetIsActive(this, false);

    protected override void OnClick()
    {
        base.OnClick();
        Focus();
        HudActiveButton.SetIsActive(this, true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        EndCapture();
        base.OnLostKeyboardFocus(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!e.Handled)
            e.Handled = HandleKey(e.Key == Key.System ? e.SystemKey : e.Key, e.KeyboardDevice.Modifiers, e.IsRepeat);
    }

    internal bool HandleKey(Key key, ModifierKeys modifiers, bool repeat)
    {
        if (!HudActiveButton.GetIsActive(this))
        {
            if (key != Key.Enter || modifiers != ModifierKeys.None)
                return false;
            if (!repeat)
                OnClick();
            return true;
        }

        if (key == Key.Tab && modifiers == ModifierKeys.None)
        {
            EndCapture();
            return false; // Bare Tab isn't a valid shortcut, so let it leave the editor.
        }
        if (key == Key.Escape && modifiers == ModifierKeys.None)
            EndCapture();
        else if (!repeat)
            ShortcutEntered?.Invoke(key, modifiers);
        return true;
    }
}
