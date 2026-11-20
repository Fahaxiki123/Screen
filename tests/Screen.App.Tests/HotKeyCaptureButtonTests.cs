using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.App.Tests;

[TestClass]
public sealed class HotKeyCaptureButtonTests
{
    [STATestMethod]
    public void NavigatingAndCancellingNeverRequestsARegistration()
    {
        var button = new HotKeyCaptureButton();
        var requested = new List<(Key, ModifierKeys)>();
        button.ShortcutEntered += (key, modifiers) => requested.Add((key, modifiers));

        Assert.IsFalse(button.HandleKey(Key.Tab, ModifierKeys.None, false));
        Assert.IsFalse(button.HandleKey(Key.Tab, ModifierKeys.Shift, false));
        Assert.IsFalse(button.HandleKey(Key.F8, ModifierKeys.None, false));
        Assert.IsTrue(button.HandleKey(Key.Enter, ModifierKeys.None, false));
        Assert.IsTrue(HudActiveButton.GetIsActive(button));
        Assert.IsFalse(button.HandleKey(Key.Tab, ModifierKeys.None, false));
        Assert.IsFalse(HudActiveButton.GetIsActive(button));
        button.HandleKey(Key.Enter, ModifierKeys.None, false);
        Assert.IsTrue(button.HandleKey(Key.Escape, ModifierKeys.None, false));
        Assert.IsFalse(HudActiveButton.GetIsActive(button));
        Assert.IsTrue(requested.Count == 0);

        button.HandleKey(Key.Enter, ModifierKeys.None, false);
        button.HandleKey(Key.Enter, ModifierKeys.None, true);
        Assert.IsTrue(button.HandleKey(Key.R, ModifierKeys.Control | ModifierKeys.Shift, false));
        CollectionAssert.AreEqual(new[] { (Key.R, ModifierKeys.Control | ModifierKeys.Shift) }, requested);
        button.EndCapture();
        Assert.IsFalse(button.HandleKey(Key.Tab, ModifierKeys.None, false));
        // Modified Tab remains assignable, but only after explicitly starting another capture.
        button.HandleKey(Key.Enter, ModifierKeys.None, false);
        Assert.IsTrue(button.HandleKey(Key.Tab, ModifierKeys.Shift, false));
        Assert.AreEqual((Key.Tab, ModifierKeys.Shift), requested.Last());
    }
}
