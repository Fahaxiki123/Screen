using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.App;

namespace Screen.App.Tests;

[TestClass]
public sealed class SquircleGeometryTests
{
    [TestMethod]
    [DataRow(0, 30)]
    [DataRow(50, -1)]
    public void EmptySizeProducesFrozenEmptyClip(int width, int height)
    {
        var geometry = SquircleGeometry.Create(width, height, 10);
        Assert.IsTrue(geometry.IsFrozen);
        Assert.IsTrue(geometry.IsEmpty());
    }

    [TestMethod]
    public void SquareCornerKeepsTheWholeContentSurface()
    {
        var geometry = SquircleGeometry.Create(80, 40, 0);
        Assert.IsTrue(geometry.IsFrozen);
        Assert.AreEqual(new Rect(0, 0, 80, 40), geometry.Bounds);
        Assert.IsTrue(geometry.FillContains(new Point(0.5, 0.5)));
    }

    [TestMethod]
    public void DefaultSmoothingBuildsSymmetricCornersWithSixteenUnitShoulders()
    {
        const double width = 120, height = 80;
        var geometry = SquircleGeometry.Create(width, height, 10);
        var figure = PathGeometry.CreateFromGeometry(geometry).Figures[0];
        Assert.IsTrue(geometry.IsFrozen);
        Assert.IsTrue(figure.IsClosed);
        Assert.AreEqual(new Point(16, 0), figure.StartPoint);
        Assert.AreEqual(new Rect(0, 0, width, height), geometry.Bounds);
        foreach (double x in new[] { 1d, width - 1 })
            foreach (double y in new[] { 1d, height - 1 })
                Assert.IsFalse(geometry.FillContains(new Point(x, y)));
        Assert.IsTrue(geometry.FillContains(new Point(5, 5)));
        Assert.IsTrue(geometry.FillContains(new Point(width - 5, height - 5)));
    }

    [TestMethod]
    public void OversizedRadiusClampsBothRadiusAndShouldersToTheShorterSide()
    {
        var geometry = SquircleGeometry.Create(100, 20, 70);
        var figure = PathGeometry.CreateFromGeometry(geometry).Figures[0];
        Assert.AreEqual(new Point(10, 0), figure.StartPoint);
        Assert.IsTrue(geometry.FillContains(new Point(1, 10)));
        Assert.IsFalse(geometry.FillContains(new Point(1, 1)));
        Assert.AreEqual(new Rect(0, 0, 100, 20), geometry.Bounds);
    }

    [STATestMethod]
    public void TopPointerMovesWithTheTargetWithoutResizingOrDividingTheContent()
    {
        const int left = 16, top = 16;
        var content = new Border { MinWidth = 116, MinHeight = 27 };
        var glass = new SquircleGlassBorder
        {
            GlassBackground = Brushes.White,
            Padding = new Thickness(4),
            ShadowLevel = 0,
            Child = content
        };
        // A contrasting rim makes a stroke through the junction visible in the bitmap.
        glass.Resources["Brush.GlassRim"] = Brushes.Red;
        glass.Resources["Brush.OverlayBorder"] = Brushes.Red;
        var host = new Canvas();
        host.Children.Add(glass);
        Canvas.SetLeft(glass, left);
        Canvas.SetTop(glass, top);

        RenderTargetBitmap Render()
        {
            var size = new Size(160, 70);
            host.Measure(size);
            host.Arrange(new Rect(size));
            host.UpdateLayout();
            var bitmap = new RenderTargetBitmap(160, 70, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            return bitmap;
        }
        static Color Sample(BitmapSource bitmap, int x, int y)
        {
            var bgra = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bgra, 4, 0);
            return Color.FromArgb(bgra[3], bgra[2], bgra[1], bgra[0]);
        }

        var ordinary = Render();
        var panelSize = glass.DesiredSize;
        var contentSize = content.RenderSize;
        var contentOrigin = content.TranslatePoint(new Point(), host);
        const int first = 32, second = 85;
        Assert.AreEqual((byte)0, Sample(ordinary, left + first, top - 4).A);
        Assert.AreEqual((byte)0, Sample(ordinary, left + first, top - 1).A, "The centered highlight must not bleed onto the desktop.");
        foreach (int center in new[] { first, second })
        {
            glass.SetTopPointer(center);
            var withPointer = Render();
            Assert.AreEqual(panelSize, glass.DesiredSize);
            Assert.AreEqual(contentSize, content.RenderSize);
            Assert.AreEqual(contentOrigin, content.TranslatePoint(new Point(), host));
            Assert.AreEqual((byte)0, Sample(withPointer, left + center, top - 9).A);
            for (int y = top - 4; y <= top + 2; y++)
                Assert.IsTrue(Sample(withPointer, left + center, y).A > 120, $"Gap at {center}, {y}.");
            Assert.AreEqual(Colors.White, Sample(withPointer, left + center, top), "The rim must follow the tip, without a seam across its base.");
            int inactive = center == first ? second : first;
            Assert.AreEqual((byte)0, Sample(withPointer, left + inactive, top - 4).A);
        }
    }

    [STATestMethod]
    public void SolidButtonOutlineUsesTheContentContourAndLeavesTheShadowLayerUnclipped()
    {
        var button = new SquircleGlassBorder
        {
            Width = 70,
            Height = 30,
            CornerRadiusRadius = 6.5,
            ShowRim = false,
            OutlineBrush = Brushes.Red,
            OutlineThickness = 1
        };
        button.Measure(new Size(70, 30));
        button.Arrange(new Rect(0, 0, 70, 30));
        var shadows = (Canvas)button.Children[0];
        var surface = (Border)button.Children[1];
        var rim = (Canvas)button.Children[2];
        var edge = (System.Windows.Shapes.Path)rim.Children[1];
        Assert.IsNull(shadows.Clip);
        Assert.IsTrue(shadows.Children.Count > 0);
        Assert.AreSame(surface.Clip, rim.Clip);
        Assert.AreSame(rim.Clip, edge.Data);
        Assert.IsTrue(edge.Data.IsFrozen);
        Assert.AreSame(Brushes.Red, edge.Stroke);
        Assert.AreEqual(2d, edge.StrokeThickness); // One DIP remains inside the shared clip.
        Assert.AreEqual(Visibility.Collapsed, rim.Children[0].Visibility);
        Assert.AreEqual(Visibility.Visible, rim.Visibility);
        button.OutlineThickness = 0;
        Assert.AreEqual(Visibility.Collapsed, rim.Visibility);
    }

    [STATestMethod]
    public void OptInShadowCacheRespectsDpiAndLeavesTextAndRimLive()
    {
        var text = new TextBlock { Text = "Screen 设置" };
        var glass = new SquircleGlassBorder { ShadowLevel = 3, Child = text };
        var shadows = (Canvas)glass.Children[0];
        Assert.IsNull(shadows.CacheMode);

        VisualTreeHelper.SetRootDpi(glass, new DpiScale(1.5, 1.5));
        glass.CacheShadows = true;
        var cache = (BitmapCache)shadows.CacheMode;
        Assert.AreEqual(1.5, cache.RenderAtScale);
        Assert.IsNull(glass.CacheMode);
        Assert.IsNull(glass.Children[1].CacheMode);
        Assert.IsNull(glass.Children[2].CacheMode);
        Assert.IsNull(text.CacheMode);

        foreach (var width in new[] { 120, 180 })
        {
            glass.Measure(new Size(width, 80));
            glass.Arrange(new Rect(0, 0, width, 80));
            glass.UpdateLayout();
            Assert.AreSame(cache, shadows.CacheMode);
            Assert.AreEqual(3, shadows.Children.Count);
            foreach (System.Windows.Shapes.Path path in shadows.Children)
                Assert.AreEqual(new Rect(0, 0, width, 80), path.Data.Bounds);
        }

        glass.ShadowLevel = 0;
        Assert.IsNull(shadows.CacheMode);
        glass.ShadowLevel = 3;
        Assert.AreEqual(1.5, ((BitmapCache)shadows.CacheMode).RenderAtScale);
        glass.CacheShadows = false;
        Assert.IsNull(shadows.CacheMode);
    }

    [STATestMethod]
    public void TemplateAndInstanceCanOverrideTheInheritedGlassMaterial()
    {
        var template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:ui="clr-namespace:Screen.App;assembly=Screen" TargetType="Button">
                <ui:SquircleGlassBorder x:Name="Chrome" GlassBackground="{TemplateBinding Background}" />
            </ControlTemplate>
            """);
        var button = new Button { Template = template, Background = Brushes.Crimson };
        var inheritedMaterial = new LinearGradientBrush(Colors.White, Colors.GhostWhite, 90);
        button.Resources["Brush.ChromeGradient"] = inheritedMaterial;
        button.ApplyTemplate();
        var chrome = (SquircleGlassBorder)template.FindName("Chrome", button);
        var surface = (Border)VisualTreeHelper.GetChild(chrome, 1);
        Assert.AreSame(Brushes.Crimson, surface.Background);

        button.Background = null;
        Assert.AreSame(inheritedMaterial, surface.Background);
        chrome.GlassBackground = Brushes.RoyalBlue;
        Assert.AreSame(Brushes.RoyalBlue, surface.Background);
        chrome.ClearValue(SquircleGlassBorder.GlassBackgroundProperty);
        Assert.AreSame(inheritedMaterial, surface.Background);
    }
}
