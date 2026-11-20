using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using DrawingRect = System.Drawing.Rectangle;

namespace Screen.App.Tests;

[TestClass]
public sealed class WindowFitTests
{
    [STATestMethod]
    [DataRow(460, true)]
    [DataRow(640, false)]
    [DataRow(960, false)]
    public void StylePanelKeepsEachGroupTogetherInsideTheViewport(int width, bool wraps)
    {
        var viewport = new Size(width, 340);
        var root = new Canvas { ClipToBounds = true };
        var panel = CaptureOverlayWindow.BuildStylePanel();
        root.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Screen;component/Themes/Tokens.xaml", UriKind.Relative) });
        root.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Screen;component/Themes/Controls.xaml", UriKind.Relative) });
        root.Children.Add(panel);
        panel.Visibility = Visibility.Visible;
        var groups = (WrapPanel)panel.Content;
        // At 34 DIP each, the three text weights, four sizes and seven colors
        // do not fit in one row at 460 DIP but do fit an ordinary desktop.
        foreach (var count in new[] { 3, 4, 7 })
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < count; i++)
                group.Children.Add(new Button { Width = 34, Height = 34 });
            groups.Children.Add(group);
        }
        CaptureOverlayWindow.PlaceStylePanel(panel, viewport, new Rect(8, 220, width - 16, 42), width - 20);
        root.Measure(viewport);
        root.Arrange(new Rect(viewport));
        var first = (StackPanel)groups.Children[0];
        var last = (StackPanel)groups.Children[2];
        Assert.AreEqual(wraps, last.TranslatePoint(new Point(), root).Y > first.TranslatePoint(new Point(), root).Y);
        foreach (StackPanel group in groups.Children)
        {
            foreach (Button child in group.Children)
            {
                var bounds = new Rect(child.TranslatePoint(new Point(), root), child.RenderSize);
                Assert.IsTrue(bounds.Left >= 8 && bounds.Right <= width - 8 && bounds.Bottom <= 212);
                Assert.IsTrue(bounds.Width > 0 && bounds.Height > 0);
            }
        }
    }

    [TestMethod]
    [DataRow(1920, 1040, 96, 820, 560, 920, 620)]
    [DataRow(1920, 1040, 192, 820, 520, 920, 520)]
    [DataRow(1366, 728, 168, 780, 416, 780, 416)]
    [DataRow(2560, 1392, 144, 820, 560, 920, 620)]
    public void SettingsLimitsFitTheTargetMonitorsWorkArea(int width, int height, int dpi,
        int minWidth, int minHeight, int normalWidth, int normalHeight)
    {
        var fit = SettingsWindow.FitToWorkArea(new Size(820, 560), new Size(920, 620),
            new DrawingRect(-width, 0, width, height), (uint)dpi);
        Assert.AreEqual(new Size(minWidth, minHeight), fit.Minimum);
        Assert.AreEqual(new Size(normalWidth, normalHeight), fit.Normal);
    }
}
