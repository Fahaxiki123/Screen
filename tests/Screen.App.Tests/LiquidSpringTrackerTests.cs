using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.App.Tests;

[TestClass]
public sealed class LiquidSpringTrackerTests
{
    [STATestMethod]
    public void BoundsIncludeNestedPaddingAndCenterColorRingOnBothAxes()
    {
        var track = new Grid();
        var target = new Border { Width = 32, Height = 20 };
        track.Children.Add(new Border
        {
            Padding = new Thickness(12, 5, 9, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = target
        });
        track.Measure(new Size(200, 100));
        track.Arrange(new Rect(0, 0, 200, 100));

        Assert.AreEqual(new Rect(12, 5, 32, 20), LiquidSpringTracker.GetTargetBounds(track, target, false));
        Assert.AreEqual(new Rect(16, 3, 24, 24), LiquidSpringTracker.GetTargetBounds(track, target, true));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void BothAxesKeepTheTuned60HzResponseLimitStretchAndSettleExactly(bool horizontal)
    {
        var motion = new LiquidSpringMotion(horizontal ? Axis.X : Axis.Y);
        motion.Snap(new Rect(0, 0, 20, 10));
        var target = new Rect(200, 200, 60, 50);

        const double referenceStep = 1d / 60;
        Assert.IsFalse(motion.Advance(target, referenceStep));
        if (horizontal)
        {
            Assert.AreEqual(200 * 420 * referenceStep * referenceStep, motion.Bounds.X, 0.000001);
            Assert.AreEqual(70d, motion.Bounds.Y);
            Assert.AreEqual(1.24, motion.ScaleX, 0.000001);
            Assert.AreEqual(1 - 0.24 * 0.34, motion.ScaleY, 0.000001);
        }
        else
        {
            Assert.AreEqual(200 * 380 * referenceStep * referenceStep, motion.Bounds.Y, 0.000001);
            Assert.AreEqual(70d, motion.Bounds.X);
            Assert.AreEqual(1.035, motion.ScaleY, 0.000001);
            Assert.AreEqual(1 - 0.035 * 0.25, motion.ScaleX, 0.000001);
        }

        bool settled = false;
        for (int i = 0; i < 300 && !settled; i++)
        {
            settled = motion.Advance(target, 1d / 60);
        }
        Assert.IsTrue(settled);
        Assert.AreEqual(target, motion.Bounds);
        Assert.AreEqual(1d, motion.ScaleX);
        Assert.AreEqual(1d, motion.ScaleY);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void EqualWallClockTimeProducesTheSameMotionOnHighRefreshDisplays(bool horizontal)
    {
        var origin = new Rect(2, 3, 90, 36);
        var axis = horizontal ? Axis.X : Axis.Y;
        foreach (int hz in new[] { 120, 240, 300 })
        {
            var baseline = new LiquidSpringMotion(axis);
            var highRefresh = new LiquidSpringMotion(axis);
            baseline.Snap(origin);
            highRefresh.Snap(origin);
            for (int tick = 0; tick < 30; tick++)
            {
                // Midflight retarget, including a large decrease in the indicator size.
                var target = tick < 5 ? new Rect(190, 115, 380, 300) : new Rect(25, 20, 2, 2);
                baseline.Advance(target, 1d / 60);
                for (int frame = 0; frame < hz / 60; frame++)
                    highRefresh.Advance(target, 1d / hz);
                Assert.AreEqual(baseline.Bounds.X, highRefresh.Bounds.X, 0.000001);
                Assert.AreEqual(baseline.Bounds.Y, highRefresh.Bounds.Y, 0.000001);
                Assert.AreEqual(baseline.Bounds.Width, highRefresh.Bounds.Width, 0.000001);
                Assert.AreEqual(baseline.Bounds.Height, highRefresh.Bounds.Height, 0.000001);
                Assert.AreEqual(baseline.ScaleX, highRefresh.ScaleX, 0.000001);
                Assert.AreEqual(baseline.ScaleY, highRefresh.ScaleY, 0.000001);
            }
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void DuplicateTimestampsAndStallsPreserveElapsedTime(bool horizontal)
    {
        var axis = horizontal ? Axis.X : Axis.Y;
        var motion = new LiquidSpringMotion(axis);
        var uninterrupted = new LiquidSpringMotion(axis);
        var start = new Rect(10, 10, 20, 20);
        var target = new Rect(110, 80, 60, 40);
        motion.Snap(start);
        uninterrupted.Snap(start);
        motion.Advance(target, 0);
        Assert.AreEqual(start, motion.Bounds);
        motion.Advance(target, 0.2);
        for (int frame = 0; frame < 60; frame++)
            uninterrupted.Advance(target, 1d / 300);
        Assert.AreEqual(uninterrupted.Bounds.X, motion.Bounds.X, 0.000001);
        Assert.AreEqual(uninterrupted.Bounds.Y, motion.Bounds.Y, 0.000001);
        Assert.IsTrue(motion.Advance(target, 5));
        Assert.AreEqual(target, motion.Bounds);
        Assert.AreEqual(1d, motion.ScaleX);
        Assert.AreEqual(1d, motion.ScaleY);
    }

    [TestMethod]
    public void ALayoutChangeOnlyOnTheCrossAxisIsFollowedWithoutSnapping()
    {
        var motion = new LiquidSpringMotion(Axis.X);
        motion.Snap(new Rect(10, 0, 30, 20));
        var resized = new Rect(10, 40, 30, 40);
        Assert.IsFalse(motion.Advance(resized, 1d / 60));
        Assert.AreEqual(14d, motion.Bounds.Y, 0.000001);
        Assert.AreEqual(27d, motion.Bounds.Height, 0.000001);
    }

    [STATestMethod]
    public void NewlyVisibleTargetSnapsToItsCurrentLocation()
    {
        var track = new Canvas();
        var indicator = new Border();
        var target = new Border { Width = 32, Height = 20 };
        Canvas.SetLeft(target, 48);
        Canvas.SetTop(target, 6);
        track.Children.Add(indicator);
        track.Children.Add(target);
        using var tracker = new LiquidSpringTracker(track, indicator);
        var window = new Window
        {
            Left = -30000,
            Top = -30000,
            Width = 200,
            Height = 100,
            ShowActivated = false,
            ShowInTaskbar = false,
            Content = track
        };
        try
        {
            tracker.MoveTo(target);
            window.Show();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            var transforms = (TransformGroup)indicator.RenderTransform;
            var translation = (TranslateTransform)transforms.Children[1];
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(48d, translation.X);
            Assert.AreEqual(6d, translation.Y);
            Assert.AreEqual(20d, indicator.Height);

            track.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(target, 86);
            track.Visibility = Visibility.Visible;
            window.UpdateLayout();
            Assert.AreEqual(86d, translation.X);

            target.Visibility = Visibility.Hidden;
            Assert.AreEqual(Visibility.Hidden, indicator.Visibility);
            Canvas.SetTop(target, 31);
            target.Width = 45;
            target.Visibility = Visibility.Visible;
            window.UpdateLayout();
            Assert.AreEqual(Visibility.Visible, indicator.Visibility);
            Assert.AreEqual(31d, translation.Y);
            Assert.AreEqual(45d, indicator.Width);
            var scale = (ScaleTransform)transforms.Children[0];
            Assert.AreEqual(1d, scale.ScaleX);
            Assert.AreEqual(1d, scale.ScaleY);

            tracker.MoveTo(null);
            Assert.AreEqual(Visibility.Hidden, indicator.Visibility);
        }
        finally
        {
            window.Close();
        }
    }
}
