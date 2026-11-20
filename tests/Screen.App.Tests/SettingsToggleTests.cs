using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.App.Tests;

[TestClass]
public sealed class SettingsToggleTests
{
    [STATestMethod]
    public void TrackAndThumbReflectBoundStateBeforeAnimationOrWindowLoad()
    {
        var toggle = new ToggleButton { IsChecked = true };
        var resources = new ResourceDictionary { Source = new Uri("/Screen;component/Themes/SettingsControls.xaml", UriKind.Relative) };
        toggle.Style = (Style)resources["ToggleSwitchStyle"];
        toggle.Measure(new Size(40, 24));
        toggle.Arrange(new Rect(0, 0, 40, 24));
        var track = (Border)toggle.Template.FindName("ColorTrack", toggle);
        var thumb = (TranslateTransform)toggle.Template.FindName("ThumbMove", toggle);
        Assert.IsFalse(toggle.IsLoaded);
        Assert.AreEqual(1d, track.Opacity);
        Assert.AreEqual(16d, thumb.X);
        toggle.IsChecked = false;
        Assert.AreEqual(0d, track.Opacity);
        Assert.AreEqual(0d, thumb.X);
        toggle.IsChecked = true;
        Assert.AreEqual(1d, track.Opacity);
        Assert.AreEqual(16d, thumb.X);
    }
}
