using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;
using DrawingRect = System.Drawing.Rectangle;

namespace Screen.App.Tests;

[TestClass]
public sealed class WindowFitTests
{
    [STATestMethod]
    [DataRow(460, true, 0, 0)]
    [DataRow(640, false, 0, 0)]
    [DataRow(960, false, 0, 0)]
    [DataRow(460, true, 800, 180)]
    public void StylePanelKeepsEachGroupTogetherInsideTheViewport(int width, bool wraps, int x, int y)
    {
        var viewport = new Rect(x, y, width, 340);
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
        CaptureOverlayWindow.PlaceStylePanel(panel, viewport, new Rect(x + 8, y + 220, width - 16, 42), x + width - 20);
        root.Measure(new Size(viewport.Right, viewport.Bottom));
        root.Arrange(new Rect(0, 0, viewport.Right, viewport.Bottom));
        var first = (StackPanel)groups.Children[0];
        var last = (StackPanel)groups.Children[2];
        Assert.AreEqual(wraps, last.TranslatePoint(new Point(), root).Y > first.TranslatePoint(new Point(), root).Y);
        foreach (StackPanel group in groups.Children)
        {
            foreach (Button child in group.Children)
            {
                var bounds = new Rect(child.TranslatePoint(new Point(), root), child.RenderSize);
                Assert.IsTrue(bounds.Left >= x + 8 && bounds.Right <= viewport.Right - 8 && bounds.Top >= y + 8 && bounds.Bottom <= y + 212);
                Assert.IsTrue(bounds.Width > 0 && bounds.Height > 0);
            }
        }
    }

    [TestMethod]
    public void CaptureViewportUsesRealMonitorPixelsAndTheCurrentCanvasScale()
    {
        PixelRect[] monitors = [new(-1600, 300, 1600, 900), new(0, 0, 1920, 1080)];
        var frame = new PixelRect(-1600, 0, 3520, 1200);
        var size = new Size(1760, 600);
        var left = new Rect(0, 150, 800, 450);
        var right = new Rect(800, 0, 960, 540);
        Rect View(PixelRect anchor) => CaptureSurface.DisplayViewport(frame, monitors, size, anchor);
        Assert.AreEqual(left, View(new PixelRect(1599, 400, 1, 1)));
        Assert.AreEqual(right, View(new PixelRect(1600, 400, 1, 1)));
        Assert.AreEqual(right, View(new PixelRect(1540, 350, 170, 300)), "The larger visible part of a spanning selection hosts the toolbar.");
        Assert.AreEqual(right, View(new PixelRect(1500, 0, 1, 1)), "A gap in the monitor layout is not a valid panel viewport.");
        Assert.AreEqual(new Rect(size), CaptureSurface.DisplayViewport(frame, [], size, new PixelRect(0, 0, 1, 1)));

        PixelRect[] stacked = [new(400, -900, 1200, 900), new(0, 0, 1920, 1080)];
        var stackedFrame = new PixelRect(0, -900, 1920, 1980);
        Assert.AreEqual(new Rect(200, 0, 600, 450),
            CaptureSurface.DisplayViewport(stackedFrame, stacked, new Size(960, 990), new PixelRect(700, 899, 1, 1)));
        Assert.AreEqual(new Rect(0, 450, 960, 540),
            CaptureSurface.DisplayViewport(stackedFrame, stacked, new Size(960, 990), new PixelRect(700, 900, 1, 1)));
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
