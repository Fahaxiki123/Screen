using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.App.Tests;

[TestClass]
public sealed class GlassThemeTests
{
    [STATestMethod]
    public void CompiledPaletteResolvesLegacyKeysAndTemplateResources()
    {
        var tokens = new ResourceDictionary { Source = new Uri("/Screen;component/Themes/Tokens.xaml", UriKind.Relative) };
        foreach (var name in new[] { "Accent", "OverlayBase", "OverlayBorder", "OverlayText", "OverlayTextDim", "OverlayIcon", "Neutral500" })
            Assert.IsInstanceOfType<SolidColorBrush>(tokens["Brush." + name], name);
        Assert.AreEqual(((Color)tokens["Color.Accent"]), ((SolidColorBrush)tokens["Brush.Accent"]).Color);
        var controls = new ResourceDictionary { Source = new Uri("/Screen;component/Themes/Controls.xaml", UriKind.Relative) };
        var panel = new GlassPanel { Content = "Glass", Style = (Style)controls[typeof(GlassPanel)] };
        panel.Resources.MergedDictionaries.Add(tokens);
        panel.Measure(new Size(200, 80));
        panel.Arrange(new Rect(0, 0, 200, 80));
        Assert.IsNotNull(panel.Template.FindName("Backdrop", panel));
        Assert.IsNull(panel.Effect, "A container that renders text must not carry a shadow.");
    }
}
