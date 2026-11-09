using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Screen.App.Tests;

[TestClass]
public sealed class TimelineInteractionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PressingEitherTrimHandlePreservesItsExistingBoundary(bool endHandle)
    {
        OnSta(() =>
        {
            var timeline = CreateTimeline(10);
            Invoke(timeline, "SetTrimStart", 2d);
            Invoke(timeline, "SetTrimEnd", 8d);
            var handle = Handle(timeline, endHandle);
            var point = new Point(handle.Left + handle.Width / 4, handle.Top + handle.Height / 2);
            var changes = 0;
            timeline.TrimChanged += (_, _) => changes++;

            Invoke(timeline, "BeginDrag", point);

            Assert.AreEqual(2d, timeline.TrimStart, 0.000001);
            Assert.AreEqual(8d, timeline.TrimEnd, 0.000001);
            Assert.AreEqual(0, changes, "Pressing a grip must not jump to the mouse's offset within it.");

            Invoke(timeline, "ApplyDrag", new Point(point.X + 40, point.Y));

            if (endHandle)
            {
                Assert.AreEqual(2d, timeline.TrimStart, 0.000001);
                Assert.IsTrue(timeline.TrimEnd > 8);
            }
            else
            {
                Assert.IsTrue(timeline.TrimStart > 2);
                Assert.AreEqual(8d, timeline.TrimEnd, 0.000001);
            }
            Assert.AreEqual(1, changes);
        });
    }

    [TestMethod]
    [DataRow(0d)]
    [DataRow(0.05d)]
    [DataRow(0.199d)]
    public void VeryShortClipKeepsAValidTrimRangeWhenEitherHandleIsDragged(double duration)
    {
        OnSta(() =>
        {
            var timeline = CreateTimeline(duration);
            foreach (var endHandle in new[] { false, true })
            {
                var handle = Handle(timeline, endHandle);
                Invoke(timeline, "BeginDrag", new Point(handle.Left + handle.Width / 2, handle.Height / 2));
                Invoke(timeline, "ApplyDrag", new Point(-1000, 10));
                AssertValidRange(timeline);
                Invoke(timeline, "ApplyDrag", new Point(2000, 10));
                AssertValidRange(timeline);
                Invoke(timeline, "CompleteDrag", (object?)null);
            }
            Assert.AreEqual(0d, timeline.TrimStart, 0.000001);
            Assert.AreEqual(timeline.Maximum, timeline.TrimEnd, 0.000001);
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MinimumTrimIntervalStillLetsEachHandleBeDraggedIndependently(bool endHandle)
    {
        OnSta(() =>
        {
            var timeline = CreateTimeline(3600);
            Invoke(timeline, "SetTrimStart", 1800d);
            Invoke(timeline, "SetTrimEnd", 1800.2d);
            var start = Handle(timeline, false);
            var end = Handle(timeline, true);
            Assert.IsTrue(start.Right <= end.Left, "A very small selected interval must retain separate grips.");
            Assert.IsTrue(end.Left - start.Right < 1, "This test must exercise a subpixel selected interval.");
            var handle = endHandle ? end : start;
            var point = new Point(handle.Left + handle.Width / 2, handle.Height / 2);

            Assert.AreEqual(endHandle ? "TrimEnd" : "TrimStart", Invoke(timeline, "HitTest", point, timeline.ActualWidth)!.ToString());
            Invoke(timeline, "BeginDrag", point);
            Invoke(timeline, "ApplyDrag", new Point(point.X + (endHandle ? 40 : -40), point.Y));

            if (endHandle)
            {
                Assert.AreEqual(1800d, timeline.TrimStart, 0.000001);
                Assert.IsTrue(timeline.TrimEnd > 1800.2);
            }
            else
            {
                Assert.IsTrue(timeline.TrimStart < 1800);
                Assert.AreEqual(1800.2d, timeline.TrimEnd, 0.000001);
            }
            AssertValidRange(timeline);
        });
    }

    [TestMethod]
    public void LosingMouseCaptureCompletesScrubOnceAndSubsequentMovementDoesNotSeek()
    {
        OnSta(() =>
        {
            var timeline = CreateTimeline(10);
            var started = 0;
            var completed = 0;
            timeline.ScrubStarted += (_, _) => started++;
            timeline.ScrubCompleted += (_, _) => completed++;
            Invoke(timeline, "BeginDrag", new Point(timeline.ActualWidth / 2, timeline.ActualHeight / 2));
            Invoke(timeline, "ApplyDrag", new Point(timeline.ActualWidth * 0.6, 10));
            var position = timeline.Value;
            Assert.IsTrue(position > 5);

            Invoke(timeline, "OnLostMouseCapture", new MouseEventArgs(Mouse.PrimaryDevice, 0));
            Invoke(timeline, "OnLostMouseCapture", new MouseEventArgs(Mouse.PrimaryDevice, 1));
            Invoke(timeline, "OnMouseMove", new MouseEventArgs(Mouse.PrimaryDevice, 2));

            Assert.AreEqual(1, started);
            Assert.AreEqual(1, completed, "Repeated capture-loss notifications must finish a drag only once.");
            Assert.AreEqual(position, timeline.Value, 0.000001);
        });
    }

    private static TimelineBar CreateTimeline(double duration)
    {
        var timeline = new TimelineBar { Maximum = duration };
        timeline.Measure(new Size(640, 26));
        timeline.Arrange(new Rect(0, 0, 640, 26));
        return timeline;
    }

    private static Rect Handle(TimelineBar timeline, bool endHandle)
    {
        var mode = Enum.Parse(typeof(TimelineBar).GetNestedType("DragMode", BindingFlags.NonPublic)!, endHandle ? "TrimEnd" : "TrimStart");
        var position = Invoke(timeline, "XFor", endHandle ? timeline.TrimEnd : timeline.TrimStart, timeline.ActualWidth)!;
        return (Rect)typeof(TimelineBar).GetMethod("HandleRect", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [position, timeline.ActualWidth, timeline.ActualHeight, mode])!;
    }

    private static void AssertValidRange(TimelineBar timeline)
    {
        Assert.IsTrue(double.IsFinite(timeline.TrimStart));
        Assert.IsTrue(double.IsFinite(timeline.TrimEnd));
        Assert.IsTrue(timeline.TrimStart >= 0);
        Assert.IsTrue(timeline.TrimStart <= timeline.TrimEnd);
        Assert.IsTrue(timeline.TrimEnd <= timeline.Maximum);
        Assert.IsTrue(timeline.Value >= timeline.TrimStart && timeline.Value <= timeline.TrimEnd);
    }

    private static object? Invoke(TimelineBar timeline, string method, params object?[] arguments) =>
        typeof(TimelineBar).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(timeline, arguments);

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                error = exception;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "STA timeline regression test timed out.");
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
