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
    public void CommittedSelectionStartsWithoutAnAnnotationTool() => OnSta(() =>
    {
        var surface = CreateSurface();
        Assert.IsTrue(surface.IsSelectionCommitted);
        Assert.AreEqual(CaptureTool.Select, surface.Tool);
        Assert.IsFalse(surface.CanUndo);
    });

    [TestMethod]
    public void PinRendersSourcePixelsToEveryEdgeWhenNotHovered() => OnSta(() =>
    {
        const int width = 48;
        const int height = 32;
        const int stride = width * 4;
        var pixels = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = (byte)(x * 5);
                pixels[offset + 1] = (byte)(y * 7);
                pixels[offset + 2] = 200;
                pixels[offset + 3] = 255;
            }
        }

        var pin = new PinWindow(new PixelBuffer(pixels, width, height, stride), new PixelRect(0, 0, width, height));
        var content = (FrameworkElement)pin.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var actual = new byte[pixels.Length];
        bitmap.CopyPixels(actual, stride, 0);

        CollectionAssert.AreEqual(pixels, actual);
    });

    [TestMethod]
    public void PinWindowTemplateClipsOnlyTheCornersAndSharesItsOutlineWithTheHoverStroke() => OnSta(() =>
    {
        const int width = 80, height = 60;
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 1] = 170;
            pixels[i + 3] = 255;
        }
        var pin = new PinWindow(new PixelBuffer(pixels, width, height, width * 4),
            new PixelRect(-32000, -32000, width, height))
        { ShowActivated = false, Left = -32000, Top = -32000 };
        try
        {
            pin.Show();
            pin.UpdateLayout();
            var presenter = (System.Windows.Controls.Decorator)pin.Template.FindName("PART_PinChrome", pin);
            var host = (System.Windows.Controls.Grid)presenter.Child;
            var hitSurface = (System.Windows.Shapes.Path)host.Children[0];
            var artwork = (System.Windows.Controls.Grid)host.Children[1];
            var imageLayer = (FrameworkElement)artwork.Children[1];
            var outlineLayer = (System.Windows.Controls.Canvas)artwork.Children[2];
            var outline = (System.Windows.Shapes.Path)outlineLayer.Children[0];
            Assert.IsTrue(imageLayer.Clip.IsFrozen);
            Assert.AreSame(imageLayer.Clip, hitSurface.Data);
            Assert.AreSame(imageLayer.Clip, outline.Data);
            Assert.AreEqual(1.8, outline.StrokeThickness);
            Assert.AreEqual(0d, outline.Opacity);

            var dpi = VisualTreeHelper.GetDpi(host);
            var rendered = new RenderTargetBitmap((int)Math.Round(presenter.ActualWidth * dpi.DpiScaleX),
                (int)Math.Round(presenter.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rendered.Render(presenter);
            var origin = host.TranslatePoint(new Point(), presenter);
            var actual = new byte[width * height * 4];
            rendered.CopyPixels(new Int32Rect((int)Math.Round(origin.X * dpi.DpiScaleX),
                (int)Math.Round(origin.Y * dpi.DpiScaleY), width, height), actual, width * 4, 0);
            foreach (var (x, y) in new[] { (0, height / 2), (width - 1, height / 2), (width / 2, 0), (width / 2, height - 1) })
                Assert.AreEqual(0xFF00AA00u, PixelAt(actual, width * 4, x, y));
            foreach (var (x, y) in new[] { (0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1) })
                Assert.IsTrue(actual[(y * width + x) * 4 + 3] < 100, "Only the soft shadow can reach a clipped corner.");

            // With a short pin the lower edge of its artwork moves up from under a stationary mouse.
            var edge = host.TranslatePoint(new Point(host.ActualWidth / 2, host.ActualHeight - 0.25), presenter);
            Assert.AreSame(hitSurface, presenter.InputHitTest(edge));
            var transform = (TransformGroup)artwork.RenderTransform;
            var scale = (ScaleTransform)transform.Children[0];
            scale.ScaleX = scale.ScaleY = 1.015;
            ((TranslateTransform)transform.Children[1]).Y = -2;
            Assert.AreSame(hitSurface, presenter.InputHitTest(edge));
            Assert.IsTrue(presenter.TranslatePoint(edge, artwork).Y > artwork.ActualHeight);
        }
        finally
        {
            pin.Close();
        }
    });

    [TestMethod]
    public void LargePinRetainsItsHitWindowAndRestoresArtworkWhenTheCompositorCloses() => OnSta(() =>
    {
        const int width = 1480, height = 900;
        var pin = new PinWindow(new PixelBuffer(new byte[width * height * 4], width, height, width * 4),
            new PixelRect(-32000, -32000, width, height))
        { Left = -32000, Top = -32000, ShowActivated = false };
        try
        {
            pin.Show();
            pin.UpdateLayout();
            var handle = new System.Windows.Interop.WindowInteropHelper(pin).Handle;
            var image = (Visual)pin.Content;
            var imageSource = (System.Windows.Interop.HwndSource)PresentationSource.FromVisual(image)!;
            Assert.AreNotEqual(nint.Zero, handle);
            Assert.IsTrue(pin.AllowsTransparency);
            if (imageSource.Handle != handle)
            {
                var renderer = (PinCompositionWindow)imageSource.RootVisual;
                Assert.AreEqual(0, NativeMethods.DwmGetWindowAttribute(imageSource.Handle, NativeMethods.DwmwaCloaked,
                    out int cloak, sizeof(int)));
                Assert.AreNotEqual(0, cloak);
                // The original, fully functional layout must take over if its DWM visual source disappears.
                renderer.Close();
                pin.UpdateLayout();
            }
            Assert.AreEqual(handle, ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(image)!).Handle);
            pin.SetClickThrough(true);
            Assert.AreNotEqual(0L, NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExTransparent);
            pin.SetClickThrough(false);
            Assert.AreEqual(0L, NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64() & NativeMethods.WsExTransparent);
        }
        finally
        {
            pin.Close();
        }
    });

    [TestMethod]
    [DataRow(160, 120)]
    [DataRow(12, 8)]
    public void PinWheelZoomReturnsToTheOriginalPhysicalSize(int width, int height) => OnSta(() =>
    {
        var pin = new PinWindow(new PixelBuffer(new byte[width * height * 4], width, height, width * 4),
            new PixelRect(-32000, -32000, width, height))
        { ShowActivated = false, Left = -32000, Top = -32000 };
        try
        {
            pin.Show();
            var originalSize = new Size(pin.Width, pin.Height);
            void Wheel(int delta) => pin.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
            {
                RoutedEvent = Mouse.MouseWheelEvent
            });
            Wheel(Mouse.MouseWheelDeltaForOneLine);
            Assert.IsTrue(pin.Width > originalSize.Width);
            Wheel(-Mouse.MouseWheelDeltaForOneLine);
            Assert.AreEqual(originalSize, new Size(pin.Width, pin.Height));
            Wheel(-Mouse.MouseWheelDeltaForOneLine);
            Assert.IsTrue(pin.Width <= originalSize.Width);
        }
        finally
        {
            pin.Close();
        }
    });

    [TestMethod]
    public void SelectionDragIncludesTheLastRowAndColumnInEitherDirection()
    {
        var first = new PixelPoint(0, 0);
        var last = new PixelPoint(1919, 1079);
        var full = new PixelRect(0, 0, 1920, 1080);

        Assert.AreEqual(full, CaptureSurface.SelectionFromDrag(first, last));
        Assert.AreEqual(full, CaptureSurface.SelectionFromDrag(last, first));
    }

    [TestMethod]
    public void MagnifierAndColorCopyUseThePixelContainingTheCursorOnAScaledSurface() => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.InitializeCommittedSelection(default);
        surface.Arrange(new Rect(0, 0, Width / 1.25, Height / 1.25));
        var cursor = (PixelPoint)Invoke(surface, "ToPixel", new Point(9.5, 6))!;
        SetField(surface, "_cursor", cursor); // Physical position (11.875, 7.5).
        Invoke(surface, "RefreshCaptureChrome");
        var source = GetField<PixelBuffer>(surface, "_sourceBuffer");
        var expected = PixelAt(source.Pixels, source.Stride, 11, 7);
        Assert.AreEqual(expected, surface.HoverColor);
        Assert.AreEqual(ColorText.ToHex(expected), GetField<System.Windows.Controls.TextBlock>(surface, "_colorText").Text);
        Assert.AreEqual("  ·  11, 7", GetField<System.Windows.Controls.TextBlock>(surface, "_coordinates").Text);

        surface.Arrange(new Rect(0, 0, Width / 1.5, Height / 1.5));
        Assert.AreEqual(new PixelPoint(1, 1), Invoke(surface, "ToPixel", new Point(1 / 1.5, 1 / 1.5)));
    });

    [TestMethod]
    public void MovingOntoTheDesktopClearsThePreviousWindowHighlight() => OnSta(() =>
    {
        var window = new WindowTarget(1, "Window", Region);
        var surface = CreateSurface([window]);
        surface.InitializeCommittedSelection(default);
        SetField(surface, "_cursor", new PixelPoint(40, 32));
        Assert.IsTrue((bool)Invoke(surface, "UpdateWindowSelection")!);
        Assert.AreEqual(window, surface.SelectedWindow);
        Assert.AreEqual(Region, surface.Selection);

        SetField(surface, "_cursor", new PixelPoint(2, 2));
        Assert.IsTrue((bool)Invoke(surface, "UpdateWindowSelection")!);
        Assert.IsNull(surface.SelectedWindow);
        Assert.IsTrue(surface.Selection.IsEmpty);
    });

    [TestMethod]
    public void DeletingASelectedMarkPreservesOtherMarksAndUndoOrder() => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Tool = CaptureTool.Rectangle;
        Invoke(surface, "AddAnnotation", new PixelPoint(40, 32), new PixelPoint(74, 61));
        Invoke(surface, "AddAnnotation", new PixelPoint(57, 44), new PixelPoint(85, 71));
        var first = Items(surface)[0];
        var second = Items(surface)[1];
        Invoke(surface, "SelectAnnotation", first.Id);

        Assert.IsTrue(surface.DeleteSelectedAnnotation());
        Assert.AreEqual(second, Items(surface).Single());
        Assert.IsNull(surface.SelectedAnnotationStyle);
        Assert.IsFalse(surface.DeleteSelectedAnnotation());

        Invoke(surface, "AddAnnotation", new PixelPoint(63, 47), new PixelPoint(98, 73));
        Assert.IsGreaterThan(second.ZIndex, Items(surface)[1].ZIndex);
        surface.Undo();
        surface.Undo();
        CollectionAssert.AreEqual(new[] { first, second }, Items(surface));
        surface.Redo();
        Assert.AreEqual(second, Items(surface).Single());
    });

    [TestMethod]
    public void TranslatedPreviewAndExportKeepEditableAnnotationsAboveTheTranslation() => OnSta(() =>
    {
        var surface = CreateSurface();
        var translation = FillSelection(0xFF204C88);
        surface.SetTranslatedSelection(translation);
        surface.Tool = CaptureTool.Rectangle;
        surface.CurrentStyle = new AnnotationStyle(0xFFFF0000, 6, 24);
        Invoke(surface, "AddAnnotation", new PixelPoint(40, 40), new PixelPoint(90, 70));
        var annotation = Items(surface).Single();

        surface.Tool = CaptureTool.Arrow;
        Assert.IsTrue(surface.IsShowingTranslation);
        var exported = surface.RenderSelection();
        var preview = Render(surface);
        Assert.AreEqual(0xFFFF0000u, PixelAt(exported.Pixels, exported.Stride, 30, 15));
        Assert.AreEqual(0xFFFF0000u, PixelAt(preview, Width * 4, 60, 40));
        Assert.AreEqual(0xFF204C88u, PixelAt(preview, Width * 4, 60, 55));

        Invoke(surface, "SelectAnnotation", annotation.Id);
        Assert.IsTrue(surface.DeleteSelectedAnnotation());
        exported = surface.RenderSelection();
        Assert.AreEqual(0xFF204C88u, PixelAt(exported.Pixels, exported.Stride, 30, 15));

        surface.SetTranslatedSelection(null);
        Assert.IsFalse(surface.IsShowingTranslation);
        CollectionAssert.AreEqual(surface.RenderOriginalSelection().Pixels, surface.RenderSelection().Pixels);
    });

    [TestMethod]
    public void ComparingTranslationKeepsMosaicPixelsProtectedAndExportsTheSelectedViewImmediately() => OnSta(() =>
    {
        var surface = CreateSurface();
        DrawExportMosaic(surface, AnnotationVariants.MosaicPixelate);
        surface.ClearAnnotationSelection();
        var originalPreview = Render(surface);
        var originalExport = surface.RenderSelection().Pixels;
        var translation = FillSelection(0xFF204C88);
        surface.SetTranslatedSelection(translation);
        var translatedPreview = Render(surface);
        var translatedExport = surface.RenderSelection().Pixels;
        surface.SetTranslatedSelection(null);

        void AssertMosaicMatches(byte[] expected)
        {
            var displayed = Render(surface);
            for (int y = 32; y < 78; y++)
                for (int x = 37; x < 93; x++)
                    Assert.AreEqual(PixelAt(expected, Width * 4, x, y), PixelAt(displayed, Width * 4, x, y));
        }
        surface.SetTranslatedSelection(translation, animateComparison: true);
        AssertMosaicMatches(translatedPreview);
        CollectionAssert.AreEqual(translatedExport, surface.RenderSelection().Pixels);
        surface.SetTranslatedSelection(null, animateComparison: true);
        AssertMosaicMatches(originalPreview);
        CollectionAssert.AreEqual(originalExport, surface.RenderSelection().Pixels);
    });

    [TestMethod]
    public void WhiteNumberRemainsReadableAndItsSizeChangesTheRenderedBadge() => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.SetTranslatedSelection(FillSelection(0xFF204C88));
        surface.Tool = CaptureTool.Number;
        surface.CurrentStyle = new AnnotationStyle(0xFFFFFFFF, 4, 24);
        Invoke(surface, "AddAnnotation", new PixelPoint(70, 55), new PixelPoint(70, 55));
        var small = surface.RenderSelection();
        Assert.AreEqual(0xFF204C88u, PixelAt(small.Pixels, small.Stride, 58, 30));

        var item = Items(surface).Single();
        Invoke(surface, "SelectAnnotation", item.Id);
        surface.ApplyStyleToSelected(item.Style with { FontSize = 44 });
        var large = surface.RenderSelection();
        Assert.AreEqual(0xFFFFFFFFu, PixelAt(large.Pixels, large.Stride, 58, 30));
        Assert.IsTrue(Enumerable.Range(21, 18).Any(y => Enumerable.Range(33, 14)
            .Any(x => PixelAt(large.Pixels, large.Stride, x, y) == 0xFF000000u)),
            "The digit must contrast against the white badge rather than disappearing.");
    });

    [TestMethod]
    [DataRow(false, AnnotationVariants.MosaicPixelate)]
    [DataRow(true, AnnotationVariants.MosaicBlur + 100)]
    public void MosaicSuppressesWindowTextUntilUndone(bool useBrush, int variant) => OnSta(() =>
    {
        var surface = CreateSurface();
        var target = new WindowTarget(1, "Source", Region);
        typeof(CaptureSurface).GetProperty(nameof(CaptureSurface.SelectedWindow))!.SetValue(surface, target);

        var start = new PixelPoint(40, 35);
        var end = new PixelPoint(90, 70);
        surface.Tool = CaptureTool.Rectangle;
        Invoke(surface, "AddAnnotation", start, end);
        Assert.AreSame(target, surface.UnmaskedTextWindow);

        surface.Tool = CaptureTool.Mosaic;
        surface.MosaicMode = useBrush ? MosaicDrawMode.Brush : MosaicDrawMode.Rectangle;
        surface.CurrentStyle = new AnnotationStyle(0xFFFF0000, 16, 16, variant);
        WorkingPoints(surface).AddRange([start, end]);
        Invoke(surface, "AddAnnotation", start, end);
        Assert.AreSame(target, surface.SelectedWindow);
        Assert.IsNull(surface.UnmaskedTextWindow, "The shared direct-text path must skip the target window.");

        surface.Undo();
        Assert.AreSame(target, surface.UnmaskedTextWindow);
        surface.Redo();
        Assert.IsNull(surface.UnmaskedTextWindow);
    });

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public void OriginalSizeMosaicScalesBlockWidthAndKeepsOddImageEdges(int factor) => OnSta(() =>
    {
        var surface = CreateSurface();
        DrawExportMosaic(surface, AnnotationVariants.MosaicPixelate);
        var original = OriginalImage(factor, x => (byte)(x % 256));

        var output = surface.RenderSelection(original, new PixelRect(0, 0, Width * factor, Height * factor), factor);

        Assert.AreEqual(original.Width, output.Width);
        Assert.AreEqual(original.Height, output.Height);
        var left = 35 * factor;
        var block = 14 * factor;
        var rowOffset = 35 * factor * output.Stride;
        var firstBlockAverage = (byte)(left + (block - 1) / 2);
        Assert.AreEqual(firstBlockAverage, output.Pixels[rowOffset + left * 4]);
        Assert.AreEqual(firstBlockAverage, output.Pixels[rowOffset + (left + block - 1) * 4]);
        Assert.AreNotEqual(firstBlockAverage, output.Pixels[rowOffset + (left + block) * 4]);
        Assert.AreEqual(original.Pixels[^4], output.Pixels[^4]);
    });

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public void OriginalSizeMosaicScalesBlurRadius(int factor) => OnSta(() =>
    {
        var surface = CreateSurface();
        DrawExportMosaic(surface, AnnotationVariants.MosaicBlur);
        var pulse = 60 * factor;
        var original = OriginalImage(factor, x => x == pulse ? byte.MaxValue : (byte)0);

        var output = surface.RenderSelection(original, new PixelRect(0, 0, Width * factor, Height * factor), factor);

        var radius = 7 * factor;
        var rowOffset = 35 * factor * output.Stride;
        Assert.AreEqual((byte)(255 / (radius * 2 + 1)), output.Pixels[rowOffset + (pulse - radius) * 4]);
        Assert.AreEqual((byte)0, output.Pixels[rowOffset + (pulse - radius - 1) * 4]);
    });

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

    private static CaptureSurface CreateSurface(IReadOnlyList<WindowTarget>? windows = null)
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
        var surface = new CaptureSurface(frame, windows ?? [], new WindowSnapService(), resources);
        surface.InitializeCommittedSelection(Region, showSelectionChrome: false);
        surface.Measure(new Size(Width, Height));
        surface.Arrange(new Rect(0, 0, Width, Height));
        return surface;
    }

    private static PixelBuffer FillSelection(uint argb)
    {
        var pixels = new byte[Region.Width * Region.Height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)argb;
            pixels[i + 1] = (byte)(argb >> 8);
            pixels[i + 2] = (byte)(argb >> 16);
            pixels[i + 3] = (byte)(argb >> 24);
        }
        return new PixelBuffer(pixels, Region.Width, Region.Height, Region.Width * 4);
    }

    private static uint PixelAt(byte[] pixels, int stride, int x, int y) =>
        BitConverter.ToUInt32(pixels, y * stride + x * 4);

    private static void DrawExportMosaic(CaptureSurface surface, int variant)
    {
        surface.Tool = CaptureTool.Mosaic;
        surface.MosaicMode = MosaicDrawMode.Rectangle;
        surface.CurrentStyle = new AnnotationStyle(0xFFFF0000, 16, 16, variant, 0.5);
        Invoke(surface, "AddAnnotation", new PixelPoint(35, 30), new PixelPoint(95, 80));
    }

    private static PixelBuffer OriginalImage(int factor, Func<int, byte> blue)
    {
        var width = Width * factor - 1;
        var height = Height * factor - 1;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                pixels[offset] = blue(x);
                pixels[offset + 3] = 255;
            }
        }
        return new PixelBuffer(pixels, width, height, stride);
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
