using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lucide.NET;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.App.Tests;

[TestClass]
public sealed class GlassInteractionTests
{
    [STATestMethod]
    public void NamedAndCatalogCheckmarksInheritForegroundAndRenderAtTheSameVisualWeight()
    {
        static byte[] RenderIcon(FrameworkElement icon)
        {
            var host = new Grid { Width = 16, Height = 16 };
            TextElement.SetForeground(host, Brushes.Lime);
            host.Children.Add(icon);
            host.Measure(new Size(16, 16));
            host.Arrange(new Rect(0, 0, 16, 16));
            var bitmap = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);
            var pixels = new byte[16 * 16 * 4];
            bitmap.CopyPixels(pixels, 16 * 4, 0);
            return pixels;
        }
        var catalog = new LucideIconElement { Kind = LucideIconKind.Check, Width = 16, Height = 16 };
        var actual = RenderIcon(catalog);
        var shared = RenderIcon(AppleVectorIcons.Create("check", 16));
        CollectionAssert.AreEqual(shared, actual);
        Assert.IsTrue(Enumerable.Range(0, 16 * 16).Any(i => actual[i * 4 + 1] > 100 && actual[i * 4 + 2] == 0));
        var keyboard = RenderIcon(new LucideIconElement { Kind = LucideIconKind.Keyboard });
        Assert.IsTrue(Enumerable.Range(0, 16 * 16).Any(i => keyboard[i * 4 + 1] > 100));
        CollectionAssert.AreNotEqual(actual, keyboard);
    }

    [STATestMethod]
    public void ToastCheckStillHasStrokeLeftToRevealAtTheHalfwayPoint()
    {
        HintToastWindow.CreateCheckBadge(out var check);
        int InkAt(double offset)
        {
            check.StrokeDashOffset = offset;
            check.Measure(new Size(24, 24));
            check.Arrange(new Rect(0, 0, 24, 24));
            var bitmap = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(check);
            var pixels = new byte[24 * 24 * 4];
            bitmap.CopyPixels(pixels, 24 * 4, 0);
            return Enumerable.Range(0, 24 * 24).Count(i => pixels[4 * i + 3] > 32);
        }
        double start = check.StrokeDashOffset;
        int initial = InkAt(start), halfway = InkAt(start / 2), complete = InkAt(0);
        Assert.IsTrue(initial < complete / 8);
        Assert.IsTrue(halfway > complete * 0.3 && halfway < complete * 0.7);
    }

    [STATestMethod]
    public void CheckedSwitchKeepsItsRightInsetWhenPressedAndReleased()
    {
        var themes = (ResourceDictionary)Application.LoadComponent(new Uri("/Screen;component/Themes/GraphiteControls.xaml", UriKind.Relative));
        var toggle = new PressableSwitch { Style = (Style)themes["AppleSwitchStyle"], IsChecked = true };
        var window = new Window { Content = toggle, Left = -30000, Top = -30000, Width = 90, Height = 75, UseLayoutRounding = true, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show();
            var knob = (Border)toggle.Template.FindName("Knob", toggle);
            var track = (Grid)VisualTreeHelper.GetChild(toggle, 0);
            foreach (var pressed in new[] { true, false })
            {
                toggle.Press(pressed);
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                var right = knob.TranslatePoint(new Point(knob.ActualWidth, 0), track).X;
                Assert.AreEqual(pressed ? 19.5 : 18, knob.Width, 0.01);
                Assert.AreEqual(2, track.ActualWidth - right, 0.02);
            }
        }
        finally { window.Close(); }
    }

    private sealed class PressableSwitch : ToggleButton
    {
        public void Press(bool value) => IsPressed = value;
    }
}
