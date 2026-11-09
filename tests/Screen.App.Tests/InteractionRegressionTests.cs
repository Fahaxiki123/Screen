using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.App;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class InteractionRegressionTests
{
    [TestMethod]
    public void TextCardsKeepCopyEnterAndUndoWhileEscapeStillCancelsCapture() => OnSta(() =>
    {
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true };
        foreach (var key in new[] { Key.C, Key.Enter, Key.Z, Key.Y, Key.S, Key.Left, Key.Delete })
        {
            text.IsReadOnly = true;
            Assert.IsTrue(CaptureKeyboardRouting.PreserveTextInput(key, text));
            text.IsReadOnly = false;
            Assert.IsTrue(CaptureKeyboardRouting.PreserveTextInput(key, text));
        }
        Assert.IsFalse(CaptureKeyboardRouting.PreserveTextInput(Key.Escape, text));
        Assert.IsFalse(CaptureKeyboardRouting.PreserveTextInput(Key.C, new Button()));
        Assert.IsFalse(CaptureKeyboardRouting.PreserveTextInput(Key.Enter, null));
    });

    [TestMethod]
    public void ActivatingAnExistingTargetPreservesItsWindowPlacement()
    {
        var calls = new List<string>();
        WindowActivation.Activate(123, _ => false, _ => calls.Add("restore"), _ => calls.Add("foreground"));
        CollectionAssert.AreEqual(new[] { "foreground" }, calls);
        calls.Clear();
        WindowActivation.Activate(123, _ => true, _ => calls.Add("restore"), _ => calls.Add("foreground"));
        CollectionAssert.AreEqual(new[] { "restore", "foreground" }, calls);
        calls.Clear();
        WindowActivation.Activate(0, _ => throw new AssertFailedException(), _ => calls.Add("restore"), _ => calls.Add("foreground"));
        Assert.AreEqual(0, calls.Count);
    }

    [TestMethod]
    public void OccupiedReplacementLeavesPreviousHotKeyRegisteredAndRoutable()
    {
        var registered = new Dictionary<int, uint>();
        var calls = new List<string>();
        using var registration = new HotKeyRegistration(10, 11, (id, _, key) =>
        {
            calls.Add($"register:{id}:{key}");
            if (key == 99)
            {
                return false;
            }
            registered.Add(id, key);
            return true;
        }, id =>
        {
            calls.Add($"unregister:{id}");
            registered.Remove(id);
        });
        Assert.IsTrue(registration.TryRegister(2, 65));
        Assert.IsFalse(registration.TryRegister(2, 99));
        Assert.AreEqual(65u, registered[10]);
        Assert.IsTrue(registration.IsActiveId(10));
        Assert.IsFalse(registration.IsActiveId(11));
        Assert.IsFalse(calls.Any(call => call.StartsWith("unregister:", StringComparison.Ordinal)));
        var previousCalls = calls.Count;
        Assert.IsTrue(registration.TryRegister(2, 65));
        Assert.AreEqual(previousCalls, calls.Count, "Reapplying the current key must not release it.");
    }

    [TestMethod]
    public void SuccessfulHotKeyReplacementReservesNewKeyBeforeReleasingOldKey()
    {
        var calls = new List<string>();
        using var registration = new HotKeyRegistration(10, 11,
            (id, _, key) => { calls.Add($"register:{id}:{key}"); return true; },
            id => calls.Add($"unregister:{id}"));
        Assert.IsTrue(registration.TryRegister(2, 65));
        Assert.IsTrue(registration.TryRegister(2, 66));
        CollectionAssert.AreEqual(new[] { "register:10:65", "register:11:66", "unregister:10" }, calls);
        Assert.IsTrue(registration.IsActiveId(11));
        Assert.IsFalse(registration.IsActiveId(10));
        registration.Dispose();
        Assert.AreEqual("unregister:11", calls[^1]);
        var count = calls.Count;
        registration.Dispose();
        Assert.AreEqual(count, calls.Count);
    }

    [TestMethod]
    public void MovingEarlierAnnotationUndoesWholeGestureWithoutDeletingNewerAnnotation() => OnSta(() =>
    {
        var surface = CreateSurface();
        DrawShape(surface, 10, 10, 40, 40);
        DrawShape(surface, 70, 60, 100, 90);
        var original = Items(surface)[0];
        var newer = Items(surface)[1];
        Invoke(surface, "BeginAnnotationGesture");
        Invoke(surface, "ReplaceAnnotation", Move(original, 4, 2));
        Invoke(surface, "ReplaceAnnotation", Move(original, 12, 8));
        Invoke(surface, "CompleteAnnotationGesture");
        surface.Undo();
        Assert.AreEqual(2, Items(surface).Count);
        Assert.AreEqual(original, Items(surface)[0]);
        Assert.AreEqual(newer, Items(surface)[1]);
        surface.Redo();
        Assert.AreEqual(original.Bounds.X + 12, Items(surface)[0].Bounds.X);
        Assert.AreEqual(original.Bounds.Y + 8, Items(surface)[0].Bounds.Y);
        surface.Undo();
        surface.Undo();
        Assert.AreEqual(1, Items(surface).Count, "One further Undo must remove the second drawing, not another mouse-move step.");
    });

    [TestMethod]
    public void ResizingAndStylingAreSeparateUndoableOperations() => OnSta(() =>
    {
        var surface = CreateSurface();
        DrawShape(surface, 10, 10, 40, 40);
        var original = Items(surface)[0];
        Invoke(surface, "BeginAnnotationGesture");
        var resized = (AnnotationItem)Invoke(surface, "ResizeAnnotation", original, original.Bounds, new PixelRect(10, 10, 60, 50))!;
        Invoke(surface, "ReplaceAnnotation", resized);
        Invoke(surface, "CompleteAnnotationGesture");
        Invoke(surface, "SelectAnnotation", original.Id);
        var restyled = original.Style with { Argb = 0xFF00FF00, Thickness = 9 };
        surface.ApplyStyleToSelected(restyled);
        surface.Undo();
        Assert.AreEqual(resized.Bounds, Items(surface)[0].Bounds);
        Assert.AreEqual(original.Style, Items(surface)[0].Style);
        surface.Undo();
        Assert.AreEqual(original, Items(surface)[0]);
        surface.Redo();
        surface.Redo();
        Assert.AreEqual(resized.Bounds, Items(surface)[0].Bounds);
        Assert.AreEqual(restyled, Items(surface)[0].Style);
        Assert.AreEqual(restyled, surface.SelectedAnnotationStyle);
    });

    [TestMethod]
    public void NoOpGesturePreservesRedoAndNewDrawingStartsNewHistoryBranch() => OnSta(() =>
    {
        var surface = CreateSurface();
        DrawShape(surface, 10, 10, 40, 40);
        DrawShape(surface, 70, 60, 100, 90);
        surface.Undo();
        var original = Items(surface)[0];
        Invoke(surface, "BeginAnnotationGesture");
        Invoke(surface, "ReplaceAnnotation", Move(original, 3, 2));
        Invoke(surface, "ReplaceAnnotation", Move(original, 0, 0));
        Invoke(surface, "CompleteAnnotationGesture");
        Assert.IsTrue(surface.CanRedo);
        DrawShape(surface, 60, 50, 90, 80);
        Assert.IsFalse(surface.CanRedo);
        surface.Redo();
        Assert.AreEqual(2, Items(surface).Count);
    });

    [TestMethod]
    public void UndoNumberAnnotationRestoresNextNumber() => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Tool = CaptureTool.Number;
        Invoke(surface, "AddAnnotation", new PixelPoint(15, 15), new PixelPoint(15, 15));
        Invoke(surface, "AddAnnotation", new PixelPoint(30, 30), new PixelPoint(30, 30));
        surface.Undo();
        Invoke(surface, "AddAnnotation", new PixelPoint(45, 45), new PixelPoint(45, 45));
        Assert.AreEqual(2, Items(surface)[1].Number);
        Assert.IsFalse(surface.CanRedo);
    });

    [TestMethod]
    public void TextAndBoundsAreRestoredTogetherByHistory()
    {
        var initial = new AnnotationItem(Guid.NewGuid(), AnnotationKind.Text, [new PixelPoint(10, 10)],
            new PixelRect(10, 10, 40, 20), new AnnotationStyle(0xFFFFFFFF, 2, 16), "before", null, 0);
        var changed = initial with { Text = "after\nsecond line", Bounds = new PixelRect(10, 10, 75, 40) };
        var before = AnnotationSnapshot.Capture([initial], 1);
        var after = AnnotationSnapshot.Capture([changed], 1);
        var history = new AnnotationHistory();
        history.Record(before, after);
        var undone = history.Undo(after)!;
        Assert.AreEqual("before", undone.Items[0].Text);
        Assert.AreEqual(initial.Bounds, undone.Items[0].Bounds);
        var redone = history.Redo(undone)!;
        Assert.AreEqual(changed.Text, redone.Items[0].Text);
        Assert.AreEqual(changed.Bounds, redone.Items[0].Bounds);
    }

    private static CaptureSurface CreateSurface()
    {
        var resources = new ResourceDictionary();
        foreach (var key in new[] { "Brush.Accent", "Brush.OverlayBase", "Brush.OverlayBorder", "Brush.OverlayText", "Brush.OverlayTextDim", "Brush.OverlayIcon", "Brush.Neutral500" })
        {
            resources[key] = Brushes.Black;
        }
        var frame = new CaptureFrame(new byte[160 * 120 * 4], 160, 120, 160 * 4, 96, 96,
            new PixelRect(0, 0, 160, 120), CaptureColorSpace.Srgb, DateTimeOffset.UtcNow);
        var surface = new CaptureSurface(frame, [], new WindowSnapService(), resources);
        surface.InitializeCommittedSelection(new PixelRect(0, 0, 160, 120));
        return surface;
    }

    private static void DrawShape(CaptureSurface surface, int x1, int y1, int x2, int y2)
    {
        surface.Tool = CaptureTool.Rectangle;
        Invoke(surface, "AddAnnotation", new PixelPoint(x1, y1), new PixelPoint(x2, y2));
    }

    private static AnnotationItem Move(AnnotationItem item, int dx, int dy) => item with
    {
        Bounds = item.Bounds with { X = item.Bounds.X + dx, Y = item.Bounds.Y + dy },
        Points = item.Points.Select(point => new PixelPoint(point.X + dx, point.Y + dy)).ToArray()
    };

    private static List<AnnotationItem> Items(CaptureSurface surface) =>
        (List<AnnotationItem>)typeof(CaptureSurface).GetField("_annotations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;

    private static object? Invoke(CaptureSurface surface, string method, params object[] arguments) =>
        typeof(CaptureSurface).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, arguments);

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            { action(); }
            catch (Exception exception) { error = exception; }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "STA regression test timed out.");
        if (error is not null)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
