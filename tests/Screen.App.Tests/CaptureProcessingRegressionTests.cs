using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.App;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class CaptureProcessingRegressionTests
{
    private const int Width = 160;
    private const int Height = 120;
    private static readonly PixelRect _region = new(30, 24, 80, 60);

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void ScanGlowStaysInsideSelectionAndNeverChangesExport(int scale) => OnSta(() =>
    {
        var surface = CreateSurface(_region, scale);
        var before = Render(surface, scale);
        var exportBefore = surface.RenderSelection().Pixels;
        surface.SetProcessing(true);
        surface.SetProcessing(true);
        Assert.IsTrue(surface.IsProcessing);
        Assert.IsTrue(ScanTransform(surface).HasAnimatedProperties);
        AssertOnlySelectionChanged(before, Render(surface, scale), _region, scale);
        CollectionAssert.AreEqual(exportBefore, surface.RenderSelection().Pixels,
            "Processing feedback must never become part of the OCR or saved image.");
        surface.SetProcessing(false);
        surface.SetProcessing(false);
        Assert.IsFalse(surface.IsProcessing);
        Assert.IsFalse(ScanTransform(surface).HasAnimatedProperties, "Stopping must remove the repeating animation clock.");
        CollectionAssert.AreEqual(before, Render(surface, scale));
    });

    [TestMethod]
    public void UnloadingStopsAnimationAndClearsGlow() => OnSta(() =>
    {
        var surface = CreateSurface(_region);
        var before = Render(surface);
        surface.SetProcessing(true);
        surface.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.IsFalse(surface.IsProcessing);
        Assert.IsFalse(ScanTransform(surface).HasAnimatedProperties);
        CollectionAssert.AreEqual(before, Render(surface));
        surface.SetProcessing(true);
        AssertOnlySelectionChanged(before, Render(surface), _region, 1);
        surface.SetProcessing(false);
    });

    [TestMethod]
    public void ScanReclipsWhenSelectionChangesAndSupportsOnePixelRegions() => OnSta(() =>
    {
        var surface = CreateSurface(_region);
        var before = Render(surface);
        surface.SetProcessing(true);
        var tinyRegion = new PixelRect(95, 75, 1, 1);
        surface.InitializeCommittedSelection(tinyRegion, showSelectionChrome: false);
        AssertOnlySelectionChanged(before, Render(surface), tinyRegion, 1);
        surface.InitializeCommittedSelection(new PixelRect(0, 0, 0, 0), showSelectionChrome: false);
        Assert.IsFalse(ScanTransform(surface).HasAnimatedProperties, "No animation should keep running for an empty selection.");
        surface.InitializeCommittedSelection(_region, showSelectionChrome: false);
        Assert.IsTrue(ScanTransform(surface).HasAnimatedProperties);
        AssertOnlySelectionChanged(before, Render(surface), _region, 1);
        surface.SetProcessing(false);
    });

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void RecognizedLinesMapFromTheCropClipAtItsEdgesAndNeverEnterExport(int scale) => OnSta(() =>
    {
        var surface = CreateSurface(_region, scale);
        var before = Render(surface, scale);
        var export = surface.RenderSelection().Pixels;
        var document = new OcrDocument("en", "A", [new OcrLine("A", new PixelRect(-4, 12, 30, 14), []),
            new OcrLine("outside", new PixelRect(_region.Width + 8, 0, 4, 4), [])]);
        surface.ShowOcrLines(document);
        var layer = (System.Windows.Controls.Canvas)VisualTreeHelper.GetChild(surface, 1);
        var line = layer.Children.OfType<System.Windows.Controls.Border>().Single();
        Assert.AreEqual(26 * scale, line.ActualWidth);
        Assert.AreEqual(14 * scale, line.ActualHeight);
        Assert.AreEqual(0d, System.Windows.Controls.Canvas.GetLeft(line));
        Assert.AreEqual(12 * scale, System.Windows.Controls.Canvas.GetTop(line));
        Assert.AreEqual(new Vector(_region.X * scale, _region.Y * scale), VisualTreeHelper.GetOffset(layer));
        Assert.IsFalse(layer.IsHitTestVisible);

        // Check the settled state without sleeping for animation clocks.
        var changes = new PixelRect(_region.X, _region.Y + 4, 34, 30);
        surface.Arrange(new Rect(0, 0, Width * scale + 1, Height * scale));
        surface.Arrange(new Rect(0, 0, Width * scale, Height * scale));
        AssertOnlySelectionChanged(before, Render(surface, scale), changes, scale);
        CollectionAssert.AreEqual(export, surface.RenderSelection().Pixels);
        surface.ShowOcrLines(null);
        CollectionAssert.AreEqual(before, Render(surface, scale));
        surface.ShowOcrLines(document);
        surface.InitializeCommittedSelection(new PixelRect(6, 8, 45, 25), showSelectionChrome: false);
        Assert.AreEqual(0, layer.Children.Count, "Old OCR boxes must not move onto a new crop.");
    });

    [TestMethod]
    public void UndoRedoNotifyTextOperationsOnlyWhenImageContentChanges() => OnSta(() =>
    {
        var surface = CreateSurface(_region);
        var before = surface.RenderSelection().Pixels;
        var notifications = new List<byte[]>();
        var newEdits = 0;
        surface.AnnotationContentChanged += (_, _) => notifications.Add(surface.RenderSelection().Pixels);
        surface.AnnotationEdited += (_, _) => newEdits++;
        surface.Undo();
        surface.Redo();
        Assert.AreEqual(0, notifications.Count, "Empty history must not cancel a valid text operation.");
        Assert.AreEqual(0, newEdits);

        surface.Tool = CaptureTool.Rectangle;
        typeof(CaptureSurface).GetMethod("AddAnnotation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(surface, [new PixelPoint(40, 35), new PixelPoint(85, 70)]);
        var annotated = surface.RenderSelection().Pixels;
        Assert.IsFalse(before.AsSpan().SequenceEqual(annotated));
        Assert.AreEqual(1, notifications.Count, "A new annotation must also invalidate text results.");
        Assert.AreEqual(1, newEdits, "Only a new edit should notify history consumers.");
        notifications.Clear();

        surface.Undo();
        Assert.AreEqual(1, notifications.Count, "Undo must invalidate OCR and translation of the previous image.");
        CollectionAssert.AreEqual(before, notifications[0], "Subscribers must see the restored content.");
        surface.Undo();
        Assert.AreEqual(1, notifications.Count, "An exhausted undo must not send another edit event.");

        surface.Redo();
        Assert.AreEqual(2, notifications.Count, "Redo must also invalidate results from the undone image.");
        CollectionAssert.AreEqual(annotated, notifications[1]);
        surface.Redo();
        Assert.AreEqual(2, notifications.Count, "An exhausted redo must not send another edit event.");
        Assert.AreEqual(1, newEdits, "Undo and redo must not be recorded as fresh edits by the long screenshot editor.");
    });

    private static CaptureSurface CreateSurface(PixelRect selection, int scale = 1)
    {
        var resources = new ResourceDictionary();
        foreach (var key in new[] { "Brush.Accent", "Brush.OverlayBase", "Brush.OverlayBorder", "Brush.OverlayText", "Brush.OverlayTextDim", "Brush.OverlayIcon", "Brush.Neutral500" })
        {
            resources[key] = new SolidColorBrush(Color.FromRgb(167, 207, 223));
        }
        var pixels = new byte[Width * Height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 40;
            pixels[i + 1] = 35;
            pixels[i + 2] = 30;
            pixels[i + 3] = 255;
        }
        var frame = new CaptureFrame(pixels, Width, Height, Width * 4, 96, 96,
            new PixelRect(0, 0, Width, Height), CaptureColorSpace.Srgb, DateTimeOffset.UtcNow);
        var surface = new CaptureSurface(frame, [], new WindowSnapService(), resources);
        surface.InitializeCommittedSelection(selection, showSelectionChrome: false);
        surface.Measure(new Size(Width * scale, Height * scale));
        surface.Arrange(new Rect(0, 0, Width * scale, Height * scale));
        return surface;
    }

    private static TranslateTransform ScanTransform(CaptureSurface surface) =>
        (TranslateTransform)typeof(CaptureSurface).GetField("_processingTranslate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;

    private static byte[] Render(CaptureSurface surface, int scale = 1)
    {
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Width * scale, Height * scale, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[Width * Height * scale * scale * 4];
        bitmap.CopyPixels(pixels, Width * scale * 4, 0);
        return pixels;
    }

    private static void AssertOnlySelectionChanged(byte[] before, byte[] after, PixelRect selection, int scale)
    {
        var changedInside = 0;
        for (var y = 0; y < Height * scale; y++)
        {
            for (var x = 0; x < Width * scale; x++)
            {
                var offset = (y * Width * scale + x) * 4;
                var equal = before.AsSpan(offset, 4).SequenceEqual(after.AsSpan(offset, 4));
                if (x < selection.X * scale || x >= selection.Right * scale || y < selection.Y * scale || y >= selection.Bottom * scale)
                {
                    Assert.IsTrue(equal, $"Scan glow changed pixel ({x}, {y}) outside the selection.");
                }
                else if (!equal)
                {
                    changedInside++;
                }
            }
        }
        Assert.IsGreaterThan(0, changedInside, "The processing glow must remain visible inside the selection.");
    }

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
