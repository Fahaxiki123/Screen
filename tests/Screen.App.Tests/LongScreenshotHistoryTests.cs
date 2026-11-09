using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.App;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class LongScreenshotHistoryTests
{
    [TestMethod]
    public void CropAndAnnotationsUndoAndRedoInTheirOriginalOrder() => OnSta(() =>
    {
        var editor = CreateEditor();
        try
        {
            DrawRectangle(Surface(editor), 20, 20, 60, 60);
            var original = Surface(editor).ExportEditingState().Current.Items[0];
            Invoke(editor, "SetZoom", 2d, false);
            Crop(editor, new PixelRect(10, 10, 100, 80));
            var croppedAnnotation = Surface(editor).ExportEditingState().Current.Items[0];
            Assert.AreEqual(original.Id, croppedAnnotation.Id);
            Assert.AreEqual(new PixelRect(10, 10, 40, 40), croppedAnnotation.Bounds);
            Assert.IsFalse(Surface(editor).CanUndo, "The crop must be undone before earlier annotations.");
            DrawRectangle(Surface(editor), 65, 50, 90, 70);

            Invoke(editor, "UndoEdit");
            Assert.AreEqual(1, Surface(editor).ExportEditingState().Current.Items.Length);
            Assert.AreEqual(new PixelRect(10, 10, 100, 80), Region(editor));
            Invoke(editor, "UndoEdit");
            Assert.AreEqual(new PixelRect(0, 0, 160, 120), Region(editor));
            Assert.AreEqual(original, Surface(editor).ExportEditingState().Current.Items[0]);
            Assert.AreEqual(2d, Field<double>(editor, "_zoom"));
            Assert.IsFalse(Field<bool>(editor, "_fitWidth"));
            Invoke(editor, "UndoEdit");
            Assert.AreEqual(0, Surface(editor).ExportEditingState().Current.Items.Length);

            Invoke(editor, "RedoEdit");
            Assert.AreEqual(original, Surface(editor).ExportEditingState().Current.Items[0]);
            Invoke(editor, "RedoEdit");
            Assert.AreEqual(new PixelRect(10, 10, 100, 80), Region(editor));
            Assert.AreEqual(croppedAnnotation.Bounds, Surface(editor).ExportEditingState().Current.Items[0].Bounds);
            Invoke(editor, "RedoEdit");
            Assert.AreEqual(2, Surface(editor).ExportEditingState().Current.Items.Length);
        }
        finally { editor.Close(); }
    });

    [TestMethod]
    public void EditingAfterUndoingCropDiscardsCropRedo() => OnSta(() =>
    {
        var editor = CreateEditor();
        try
        {
            DrawRectangle(Surface(editor), 20, 20, 60, 60);
            Crop(editor, new PixelRect(10, 10, 100, 80));
            Invoke(editor, "UndoEdit");
            DrawRectangle(Surface(editor), 80, 60, 100, 90);
            Invoke(editor, "RedoEdit");
            Assert.AreEqual(new PixelRect(0, 0, 160, 120), Region(editor));
            Assert.AreEqual(2, Surface(editor).ExportEditingState().Current.Items.Length);
            Assert.IsFalse(Field<LongScreenshotHistory>(editor, "_cropHistory").CanRedo);
        }
        finally { editor.Close(); }
    });

    [TestMethod]
    public void NewCropDiscardsAnOlderUndoneAnnotationBranch() => OnSta(() =>
    {
        var editor = CreateEditor();
        try
        {
            DrawRectangle(Surface(editor), 20, 20, 60, 60);
            DrawRectangle(Surface(editor), 80, 60, 100, 90);
            Surface(editor).Undo();
            Assert.IsTrue(Surface(editor).CanRedo);
            Crop(editor, new PixelRect(10, 10, 100, 80));
            Invoke(editor, "UndoEdit");
            Assert.IsFalse(Surface(editor).CanRedo);
            Invoke(editor, "RedoEdit");
            Assert.AreEqual(new PixelRect(10, 10, 100, 80), Region(editor));
            Assert.AreEqual(1, Surface(editor).ExportEditingState().Current.Items.Length);
        }
        finally { editor.Close(); }
    });

    [TestMethod]
    public void FullImageCropIsNoOpAndClosingReleasesCropHistory() => OnSta(() =>
    {
        var editor = CreateEditor();
        try
        {
            DrawRectangle(Surface(editor), 20, 20, 60, 60);
            Surface(editor).Undo();
            Crop(editor, new PixelRect(0, 0, 160, 120));
            Assert.IsTrue(Surface(editor).CanRedo);
            var history = Field<LongScreenshotHistory>(editor, "_cropHistory");
            Assert.AreEqual(0, history.RetainedStateCount);
            Crop(editor, new PixelRect(10, 10, 100, 80));
            Assert.AreEqual(1, history.RetainedStateCount);
            editor.Close();
            Assert.AreEqual(0, history.RetainedStateCount);
            Assert.IsNull(typeof(LongScreenshotEditorWindow).GetField("_sourceImage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        }
        finally { editor.Close(); }
    });

    [TestMethod]
    public void CropHistoryHasFixedCapacityAcrossUndoAndRedo()
    {
        var history = new LongScreenshotHistory();
        for (var index = 0; index < 30; index++)
        {
            history.RecordCrop(StateAt(index));
        }
        Assert.AreEqual(20, history.RetainedStateCount);
        var state = StateAt(30);
        for (var index = 0; index < 20; index++)
        {
            state = history.Undo(state)!;
            Assert.AreEqual(20, history.RetainedStateCount);
        }
        Assert.AreEqual(10, state.SourceRegion.X);
        Assert.IsNull(history.Undo(state));
        for (var index = 0; index < 20; index++)
        {
            state = history.Redo(state)!;
        }
        Assert.AreEqual(30, state.SourceRegion.X);
        Assert.AreEqual(20, history.RetainedStateCount);
        history.Clear();
        Assert.AreEqual(0, history.RetainedStateCount);
    }

    [TestMethod]
    public void CropRebasesGeometryStyleAndNumberAcrossWorkingScaleChanges()
    {
        var item = new AnnotationItem(Guid.NewGuid(), AnnotationKind.Arrow, [new PixelPoint(300, 400), new PixelPoint(340, 420)],
            new PixelRect(280, 380, 80, 60), new AnnotationStyle(0xFFFF0000, 4, 22), null, null, 0);
        var result = LongScreenshotHistory.RebaseAnnotations(new AnnotationSnapshot([item], 7),
            new PixelRect(200, 400, 800, 700), previousFactor: 2, nextFactor: 1);
        Assert.AreEqual(new PixelPoint(400, 400), result.Current.Items[0].Points[0]);
        Assert.AreEqual(new PixelRect(360, 360, 160, 120), result.Current.Items[0].Bounds);
        Assert.AreEqual(8d, result.Current.Items[0].Style.Thickness);
        Assert.AreEqual(44d, result.Current.Items[0].Style.FontSize);
        Assert.AreEqual(item.Id, result.Current.Items[0].Id);
        Assert.AreEqual(7, result.Current.NextNumber);
        Assert.AreEqual(0, result.History.Undo.Length);
    }

    private static LongScreenshotEditState StateAt(int x) => new(
        new PixelRect(x, 0, 100, 100),
        new AnnotationEditingState(new AnnotationSnapshot([], 1), AnnotationHistoryState.Empty, null),
        CaptureTool.Rectangle, 0xFFFF0000, 4, 1, false, 0, 0);

    private static LongScreenshotEditorWindow CreateEditor()
    {
        var resources = new ResourceDictionary();
        foreach (var key in new[] { "Brush.Accent", "Brush.OverlayBase", "Brush.OverlayBorder", "Brush.OverlayText", "Brush.OverlayTextDim", "Brush.OverlayIcon", "Brush.Neutral500" })
        {
            resources[key] = Brushes.Black;
        }
        var pixels = new byte[160 * 120 * 4];
        for (var index = 3; index < pixels.Length; index += 4)
            pixels[index] = 255;
        return new LongScreenshotEditorWindow(new PixelBuffer(pixels, 160, 120, 160 * 4), resources);
    }

    private static void Crop(LongScreenshotEditorWindow editor, PixelRect region)
    {
        Invoke(editor, "BeginCrop");
        Surface(editor).InitializeCommittedSelection(region);
        Invoke(editor, "ApplyCrop");
    }

    private static void DrawRectangle(CaptureSurface surface, int x1, int y1, int x2, int y2)
    {
        surface.Tool = CaptureTool.Rectangle;
        Invoke(surface, "AddAnnotation", new PixelPoint(x1, y1), new PixelPoint(x2, y2));
    }

    private static CaptureSurface Surface(LongScreenshotEditorWindow editor) => Field<CaptureSurface>(editor, "_surface");
    private static PixelRect Region(LongScreenshotEditorWindow editor) => Field<PixelRect>(editor, "_sourceRegion");
    private static T Field<T>(object target, string field) =>
        (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string method, params object[] arguments) =>
        target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            { action(); }
            catch (Exception exception) { error = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "STA crop regression test timed out.");
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}
