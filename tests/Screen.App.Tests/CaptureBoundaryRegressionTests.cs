using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.App;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class CaptureBoundaryRegressionTests
{
    private const int Width = 160;
    private const int Height = 120;
    private static readonly PixelRect Region = new(30, 25, 80, 60);

    [TestMethod]
    public void MosaicKeepsNativeCursorWhenBrushCursorCannotBeDrawn() => OnSta(() =>
    {
        var surface = CreateSurface();
        SetField(surface, "_cursor", new PixelPoint(60, 50));
        surface.Tool = CaptureTool.Mosaic;
        Assert.AreNotEqual(Cursors.None, surface.Cursor, "A surface without mouse-over cannot draw a replacement cursor.");
        surface.MosaicMode = MosaicDrawMode.Rectangle;
        Assert.AreEqual(Cursors.Cross, surface.Cursor);
        surface.Tool = CaptureTool.Pen;
        surface.Tool = CaptureTool.Mosaic;
        Assert.AreEqual(Cursors.Cross, surface.Cursor, "Reselecting rectangle mosaic must not hide the cursor.");
        SetField(surface, "_cursor", new PixelPoint(5, 5));
        Invoke(surface, "UpdateIdleCursor");
        Assert.AreEqual(Cursors.Arrow, surface.Cursor);
        surface.MosaicMode = MosaicDrawMode.Brush;
        SetField(surface, "_drawing", true);
        Invoke(surface, "UpdateIdleCursor");
        Assert.AreEqual(Cursors.Arrow, surface.Cursor, "Dragging outside the selection needs a visible fallback.");
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FreehandEndsAtFirstBoundaryIntersection(bool mosaic) => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Tool = mosaic ? CaptureTool.Mosaic : CaptureTool.Pen;
        surface.CurrentStyle = new AnnotationStyle(0xFFFF0000, 18, 16, AnnotationVariants.MosaicPixelate + 100);
        SetField(surface, "_start", new PixelPoint(60, 50));
        SetField(surface, "_drawing", true);
        WorkingPoints(surface).Add(new PixelPoint(60, 50));
        Invoke(surface, "FinishDrawing", new PixelPoint(160, 100));
        var annotation = Items(surface).Single();
        Assert.AreEqual(new PixelPoint(110, 75), annotation.Points[^1], "Clip the actual segment, not its two coordinates independently.");
        Assert.IsFalse(GetField<bool>(surface, "_drawing"));
        Assert.AreEqual(0, WorkingPoints(surface).Count);
        Assert.IsTrue(annotation.Points.All(point => point.X >= Region.X && point.X <= Region.Right
            && point.Y >= Region.Y && point.Y <= Region.Bottom));
        surface.Undo();
        Assert.AreEqual(0, Items(surface).Count, "The complete clipped stroke remains one undo operation.");
    });

    [TestMethod]
    [DataRow(AnnotationVariants.MosaicPixelate)]
    [DataRow(AnnotationVariants.MosaicBlur)]
    public void CommittedMosaicIsClippedWhenSelectionShrinks(int variant) => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.InitializeCommittedSelection(new PixelRect(10, 10, 135, 100), showSelectionChrome: false);
        surface.Tool = CaptureTool.Mosaic;
        surface.MosaicMode = MosaicDrawMode.Rectangle;
        surface.CurrentStyle = new AnnotationStyle(0xFFFF0000, 24, 16, variant, 0.8);
        Invoke(surface, "AddAnnotation", new PixelPoint(15, 15), new PixelPoint(140, 105));
        Render(surface); // Populate the incremental mosaic cache before resizing.
        surface.InitializeCommittedSelection(Region, showSelectionChrome: false);
        AssertOnlySelectionChanged(Render(CreateSurface()), Render(surface), Region);
        surface.Undo();
        CollectionAssert.AreEqual(Render(CreateSurface()), Render(surface));
    });

    [TestMethod]
    public void ThickPenAndWorkingPreviewCannotPaintOutsideSelection() => OnSta(() =>
    {
        var surface = CreateSurface();
        var baseline = Render(surface);
        surface.Tool = CaptureTool.Pen;
        surface.CurrentStyle = new AnnotationStyle(0xFFFF0000, 24, 16);
        SetField(surface, "_start", new PixelPoint(32, 30));
        SetField(surface, "_cursor", new PixelPoint(32, 78));
        WorkingPoints(surface).AddRange([new PixelPoint(32, 30), new PixelPoint(32, 78)]);
        SetField(surface, "_drawing", true);
        surface.InvalidateVisual();
        AssertOnlySelectionChanged(baseline, Render(surface), Region);
        Invoke(surface, "FinishDrawing", new PixelPoint(32, 78));
        surface.InvalidateVisual();
        AssertOnlySelectionChanged(baseline, Render(surface), Region);
    });

    [TestMethod]
    public void ShapeEndpointsAreRestrictedToSelectionAtCommit() => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Tool = CaptureTool.Rectangle;
        Invoke(surface, "AddAnnotation", new PixelPoint(40, 40), new PixelPoint(155, 115));
        var shape = Items(surface).Single();
        Assert.AreEqual(new PixelPoint(110, 85), shape.Points[^1]);
        Assert.AreEqual(Region.Right, shape.Bounds.Right);
        Assert.AreEqual(Region.Bottom, shape.Bounds.Bottom);
    });

    [TestMethod]
    public void LostMouseCaptureCancelsUnfinishedBrushAndSelectionGestures() => OnSta(() =>
    {
        var surface = CreateSurface();
        SetField(surface, "_drawing", true);
        SetField(surface, "_movingSelection", true);
        SetField(surface, "_resizingSelection", true);
        WorkingPoints(surface).Add(new PixelPoint(40, 40));
        Invoke(surface, "OnLostMouseCapture", new MouseEventArgs(Mouse.PrimaryDevice, 0));
        Assert.IsFalse(GetField<bool>(surface, "_drawing"));
        Assert.IsFalse(GetField<bool>(surface, "_movingSelection"));
        Assert.IsFalse(GetField<bool>(surface, "_resizingSelection"));
        Assert.AreEqual(0, WorkingPoints(surface).Count);
        Assert.AreEqual(0, Items(surface).Count);
        Assert.AreNotEqual(Cursors.None, surface.Cursor);
    });

    [TestMethod]
    public void PenStrokeBoundsEncloseCurvedTrajectoryAndClampCleanlyOnDrag() => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Tool = CaptureTool.Pen;
        surface.CurrentStyle = new AnnotationStyle(0xFFFF0000, 8, 16);
        WorkingPoints(surface).AddRange([
            new PixelPoint(40, 35),
            new PixelPoint(65, 75),
            new PixelPoint(90, 35)
        ]);
        Invoke(surface, "AddAnnotation", new PixelPoint(40, 35), new PixelPoint(90, 35));

        var pen = Items(surface).Single();
        Assert.IsGreaterThanOrEqualTo(79, pen.Bounds.Bottom,
            "Pen bounds must enclose intermediate stroke points plus half-thickness, not just start/end.");
        Assert.IsLessThanOrEqualTo(Region.Bottom, pen.Bounds.Bottom);
    });

    [TestMethod]
    public void RectangleMosaicResizePreservesExactBoundsWithoutBrushInflation() => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Tool = CaptureTool.Mosaic;
        surface.MosaicMode = MosaicDrawMode.Rectangle;
        surface.CurrentStyle = new AnnotationStyle(0xFFFF0000, 28, 16, AnnotationVariants.MosaicPixelate, 0.5);
        Invoke(surface, "AddAnnotation", new PixelPoint(40, 35), new PixelPoint(80, 65));

        var original = Items(surface).Single();
        Assert.AreEqual(new PixelRect(40, 35, 40, 30), original.Bounds);

        var firstTarget = new PixelRect(35, 30, 55, 40);
        var resizedOnce = (AnnotationItem)Invoke(surface, "ResizeAnnotation", original, original.Bounds, firstTarget)!;
        Assert.AreEqual(firstTarget, resizedOnce.Bounds);

        var secondTarget = new PixelRect(45, 38, 30, 22);
        var resizedTwice = (AnnotationItem)Invoke(surface, "ResizeAnnotation", resizedOnce, resizedOnce.Bounds, secondTarget)!;
        Assert.AreEqual(secondTarget, resizedTwice.Bounds);
    });

    [TestMethod]
    [DataRow(AnnotationVariants.MosaicPixelate)]
    [DataRow(AnnotationVariants.MosaicBlur)]
    public void OverlappingMosaicsProduceSeamlessCompositeMatchingFullRender(int variant) => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Tool = CaptureTool.Mosaic;
        surface.MosaicMode = MosaicDrawMode.Rectangle;
        surface.CurrentStyle = new AnnotationStyle(0xFFFF0000, 20, 16, variant, 0.75);

        Invoke(surface, "AddAnnotation", new PixelPoint(36, 30), new PixelPoint(82, 72));
        Invoke(surface, "AddAnnotation", new PixelPoint(58, 40), new PixelPoint(102, 78));
        Render(surface); // Populate initial _mosaicComposite cache.

        // Nudge only the upper mosaic so the incremental dirty rect slices across the lower mosaic.
        var top = Items(surface)[1];
        var moved = top with
        {
            Bounds = new PixelRect(top.Bounds.X + 5, top.Bounds.Y + 3, top.Bounds.Width, top.Bounds.Height),
            Points = top.Points.Select(p => new PixelPoint(p.X + 5, p.Y + 3)).ToArray()
        };
        Invoke(surface, "ReplaceAnnotation", moved);

        var incremental = Render(surface);

        // Fresh surface with the exact same final annotations rendered from clean pixels.
        var fresh = CreateSurface();
        Items(fresh).AddRange(Items(surface));
        SetField(fresh, "_mosaicCompositeDirty", true);
        fresh.InvalidateVisual();
        var groundTruth = Render(fresh);

        CollectionAssert.AreEqual(groundTruth, incremental,
            "Incremental overlapping mosaic rebuild must match a full clean render without block/halo seams.");
    });

    private static CaptureSurface CreateSurface()
    {
        var resources = new ResourceDictionary();
        foreach (var key in new[] { "Brush.Accent", "Brush.OverlayBase", "Brush.OverlayBorder", "Brush.OverlayText", "Brush.OverlayTextDim", "Brush.OverlayIcon", "Brush.Neutral500" })
        {
            resources[key] = Brushes.Black;
        }
        var pixels = new byte[Width * Height * 4];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = (y * Width + x) * 4;
                pixels[offset] = (byte)((x * 13 + y * 7) % 256);
                pixels[offset + 1] = (byte)((x * 3 + y * 11) % 256);
                pixels[offset + 2] = (byte)((x * 17 + y * 5) % 256);
                pixels[offset + 3] = 255;
            }
        }
        var frame = new CaptureFrame(pixels, Width, Height, Width * 4, 96, 96,
            new PixelRect(0, 0, Width, Height), CaptureColorSpace.Srgb, DateTimeOffset.UtcNow);
        var surface = new CaptureSurface(frame, [], new WindowSnapService(), resources);
        surface.InitializeCommittedSelection(Region, showSelectionChrome: false);
        surface.Measure(new Size(Width, Height));
        surface.Arrange(new Rect(0, 0, Width, Height));
        return surface;
    }

    private static byte[] Render(CaptureSurface surface)
    {
        surface.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        return pixels;
    }

    private static void AssertOnlySelectionChanged(byte[] before, byte[] after, PixelRect selection)
    {
        var changedInside = 0;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var offset = (y * Width + x) * 4;
                var equal = before.AsSpan(offset, 4).SequenceEqual(after.AsSpan(offset, 4));
                if (x < selection.X || x >= selection.Right || y < selection.Y || y >= selection.Bottom)
                {
                    Assert.IsTrue(equal, $"Annotation changed pixel ({x}, {y}) outside selection.");
                }
                else if (!equal)
                {
                    changedInside++;
                }
            }
        }
        Assert.IsGreaterThan(0, changedInside, "The annotation must still be visible within the selection.");
    }

    private static List<AnnotationItem> Items(CaptureSurface surface) => GetField<List<AnnotationItem>>(surface, "_annotations");
    private static List<PixelPoint> WorkingPoints(CaptureSurface surface) => GetField<List<PixelPoint>>(surface, "_workingPoints");
    private static T GetField<T>(CaptureSurface surface, string name) =>
        (T)typeof(CaptureSurface).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface)!;
    private static void SetField(CaptureSurface surface, string name, object value) =>
        typeof(CaptureSurface).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(surface, value);
    private static object? Invoke(CaptureSurface surface, string name, params object[] arguments) =>
        typeof(CaptureSurface).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(surface, arguments);

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
