using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;
using Vortice.DXGI;

namespace Screen.App.Tests;

[TestClass]
public sealed class CaptureAuditFixTests
{
    [TestMethod]
    public void ScreenshotRoutingRejectsEveryRotationAndUnknownCursorBeforePixelRead()
    {
        Assert.IsFalse(DxgiScreenCaptureService.NeedsGdiFallback(CaptureColorSpace.Srgb, ModeRotation.Identity, true));
        foreach (var rotation in new[] { 0, 2, 3, 4 })
        {
            Assert.IsTrue(DxgiScreenCaptureService.NeedsGdiFallback(CaptureColorSpace.Srgb, (ModeRotation)rotation, true));
        }
        Assert.IsTrue(DxgiScreenCaptureService.NeedsGdiFallback(CaptureColorSpace.Srgb, ModeRotation.Identity, false));
        Assert.IsTrue(DxgiScreenCaptureService.NeedsGdiFallback(CaptureColorSpace.Hdr10, ModeRotation.Identity, true));
    }

    [TestMethod]
    public async Task EarlyScreenshotFallbackDoesNotRetryOrRequireAPrimaryPixelBufferAsync()
    {
        var primary = new CaptureStub();
        var fallback = new CaptureStub();
        var attempts = 0;
        var resets = 0;
        using var service = new HybridScreenCaptureService(primary, fallback, () => resets++, Timeout.InfiniteTimeSpan,
            (_, _) => { attempts++; return Task.FromResult<CaptureFrame?>(null); });
        var frame = await service.CaptureAtAsync(new PixelPoint(5, 5));
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(0, primary.Calls);
        Assert.AreEqual(1, fallback.Calls);
        Assert.AreEqual(0, resets);
        Assert.AreEqual(8, frame.Width);
    }

    [TestMethod]
    public void DirectTextRequiresTheWholeControlOrLineToFit()
    {
        var selection = new PixelRect(20, 20, 100, 30);
        Assert.IsTrue(DirectTextExtractor.IsTextFullySelected(new PixelRect(21, 21, 98, 28), selection));
        Assert.IsFalse(DirectTextExtractor.IsTextFullySelected(new PixelRect(19, 20, 100, 30), selection));
        var partial = DirectTextExtractor.BuildDocumentFromBoundedLines(
            [("This includes text outside the selection", new PixelRect(19, 20, 100, 30))], selection);
        Assert.IsNull(partial, "Even 99% overlap cannot safely clip an indivisible UIA string.");
    }

    [TestMethod]
    [DataRow((int)CaptureTool.Pen)]
    [DataRow((int)CaptureTool.Mosaic)]
    public void EnlargingExistingBrushMatchesNewBrushAndSupportsUndo(int toolValue) => OnSta(() =>
    {
        var tool = (CaptureTool)toolValue;
        var surface = CreateSurface(Pattern(160, 120));
        DrawBrush(surface, tool, 20);
        var item = Items(surface).Single();
        Invoke(surface, "SelectAnnotation", item.Id);
        surface.ApplyStyleToSelected(item.Style with { Thickness = 48 });
        Assert.AreEqual(48, Items(surface).Single().Bounds.Height);
        var enlarged = surface.RenderSelection();
        var fresh = CreateSurface(Pattern(160, 120));
        DrawBrush(fresh, tool, 48);
        CollectionAssert.AreEqual(fresh.RenderSelection().Pixels, enlarged.Pixels);
        surface.Undo();
        Assert.AreEqual(20, Items(surface).Single().Bounds.Height);
        surface.Redo();
        CollectionAssert.AreEqual(enlarged.Pixels, surface.RenderSelection().Pixels);
    });

    [TestMethod]
    public void DrawingOnExistingMarksRequiresAnExplicitEditingIntent()
    {
        var id = Guid.NewGuid();
        Assert.IsFalse(CaptureSurface.ShouldEditAnnotation(CaptureTool.Mosaic, ModifierKeys.None, null, id));
        Assert.IsFalse(CaptureSurface.ShouldEditAnnotation(CaptureTool.Pen, ModifierKeys.None, null, id));
        Assert.IsTrue(CaptureSurface.ShouldEditAnnotation(CaptureTool.Select, ModifierKeys.None, null, id));
        Assert.IsTrue(CaptureSurface.ShouldEditAnnotation(CaptureTool.Pen, ModifierKeys.Control, null, id));
        Assert.IsTrue(CaptureSurface.ShouldEditAnnotation(CaptureTool.Pen, ModifierKeys.None, id, id));
    }

    [TestMethod]
    [DataRow(10)]
    [DataRow(11)]
    public void CroppedExportMatchesPreviewWithOverlappingMosaics(int variant) => OnSta(() =>
    {
        var surface = CreateSurface(Pattern(160, 120));
        Items(surface).Add(Mosaic(new PixelRect(25, 20, 85, 70), variant, 0));
        Items(surface).Add(Mosaic(new PixelRect(65, 35, 65, 70), variant == 10 ? 11 : 10, 1));
        var selection = new PixelRect(47, 43, 50, 39);
        surface.InitializeCommittedSelection(selection, false);
        var preview = ((PixelBuffer)Invoke(surface, "GetMosaicComposite")!).Crop(selection);
        CollectionAssert.AreEqual(preview.Pixels, surface.RenderSelection().Pixels);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InPlaceTranslationExportsLocalPixelsAndVectorsWithoutADesktopClone(bool withVector) => OnSta(() =>
    {
        var surface = CreateSurface(Pattern(160, 120));
        var selection = new PixelRect(30, 20, 80, 60);
        surface.InitializeCommittedSelection(selection, false);
        var translation = Pattern(80, 60);
        surface.SetTranslatedSelection(translation);
        var reference = CreateSurface(translation);
        if (withVector)
        {
            surface.Tool = reference.Tool = CaptureTool.Rectangle;
            surface.CurrentStyle = reference.CurrentStyle = new AnnotationStyle(0xFFFF0000, 4, 16);
            Invoke(surface, "AddAnnotation", new PixelPoint(40, 35), new PixelPoint(75, 60));
            Invoke(reference, "AddAnnotation", new PixelPoint(10, 15), new PixelPoint(45, 40));
        }

        var output = surface.RenderSelection();
        Assert.AreEqual(80, output.Width);
        Assert.AreEqual(60, output.Height);
        CollectionAssert.AreEqual(reference.RenderSelection().Pixels, output.Pixels,
            "Vector annotations must retain their position relative to the translated selection.");
        Assert.IsNull(typeof(CaptureSurface).GetField("_translatedFrame", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(surface),
            "Exporting a translated selection without mosaics must not create an entire translated desktop.");
        if (!withVector)
        {
            Assert.AreSame(translation.Pixels, output.Pixels);
        }
    });

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public void FullResolutionExportKeepsOverlapOrderGridAndBlurHalo(int factor) => OnSta(() =>
    {
        var original = Pattern(160 * factor - 1, 120 * factor - 1);
        var surface = CreateSurface(Pattern(160, 120));
        // Store out of order: ZIndex, not storage order, determines compositing.
        Items(surface).Add(Mosaic(new PixelRect(65, 35, 65, 70), 11, 1));
        Items(surface).Add(Mosaic(new PixelRect(25, 20, 85, 70), 10, 0));
        var annotations = Items(surface).Select(item => CaptureSurface.ScaleItem(item, factor)).ToArray();
        var full = CaptureSurface.RenderAnnotatedRegion(original, new PixelRect(0, 0, original.Width, original.Height), annotations);
        var selection = new PixelRect(47 * factor, 43 * factor, 50 * factor, 39 * factor);
        var cropped = surface.RenderSelection(original, selection, factor);
        CollectionAssert.AreEqual(full.Crop(selection).Pixels, cropped.Pixels);
        Assert.AreEqual(selection.Width, cropped.Width);
    });

    [TestMethod]
    public void LongImagePreviewUsesOriginalContextOutsideCropAndFractionalEdges() => OnSta(() =>
    {
        var source = Pattern(240, 180);
        var region = new PixelRect(57, 43, 121, 101);
        const int factor = 2;
        var originalItems = new[]
        {
            Mosaic(new PixelRect(34, 24, 81, 81), 10, 0),
            Mosaic(new PixelRect(85, 67, 80, 80), 11, 1)
        };
        var working = LongScreenshotEditorWindow.Downscale(source.Crop(region), factor);
        var surface = CreateSurface(working);
        surface.SetMosaicSourceContext(source, region, factor);
        Items(surface).AddRange(originalItems.Select(item =>
            CaptureSurface.OffsetItem(CaptureSurface.ScaleItem(item, 1d / factor), -region.X / (double)factor, -region.Y / (double)factor)));
        var preview = (PixelBuffer)Invoke(surface, "GetMosaicComposite")!;
        var expected = LongScreenshotEditorWindow.Downscale(CaptureSurface.CompositeMosaicsRegion(source, region, originalItems), factor);
        CollectionAssert.AreEqual(expected.Pixels, preview.Pixels);
    });

    [TestMethod]
    public void RepeatedCropScaleChangesDoNotRoundMosaicGridOrigins()
    {
        var original = Mosaic(new PixelRect(15, 17, 37, 41), 10, 0);
        var first = LongScreenshotHistory.RebaseAnnotations(new AnnotationSnapshot([original], 1),
            new PixelRect(11, 13, 80, 80), 3, 2).Current.Items[0];
        var second = LongScreenshotHistory.RebaseAnnotations(new AnnotationSnapshot([first], 1),
            new PixelRect(7, 9, 60, 60), 2, 1).Current.Items[0];
        var restoredToOriginal = CaptureSurface.OffsetItem(second, 18, 22);
        var expected = CaptureSurface.ScaleItem(original, 3);
        Assert.AreEqual(expected.Bounds, restoredToOriginal.Bounds);
        Assert.AreEqual(expected.PreciseBounds, restoredToOriginal.PreciseBounds);
        Assert.AreEqual(expected.Style.MosaicScale, restoredToOriginal.Style.MosaicScale);
    }

    [TestMethod]
    [DataRow(10, 18, 18)]
    [DataRow(11, 25, 23)]
    public void HugeMosaicBoundsRequireOnlyBlocksOrHaloAroundSmallSelection(int variant, int width, int height)
    {
        var sourceBounds = new PixelRect(0, 0, 100000, 100000);
        var selection = new PixelRect(50001, 60003, 7, 5);
        var item = Mosaic(new PixelRect(13, 17, 80000, 90000), variant, 0);
        var required = CaptureSurface.RequiredMosaicSourceRegion(sourceBounds, selection, [item]);
        Assert.AreEqual(width, required.Width);
        Assert.AreEqual(height, required.Height);
        Assert.AreEqual(selection, selection.Intersect(required));

        var layered = new[] { item, item with { Id = Guid.NewGuid(), ZIndex = 1, Style = item.Style with { Variant = 11 } } };
        var layeredRequired = CaptureSurface.RequiredMosaicSourceRegion(sourceBounds, selection, layered);
        Assert.IsTrue(layeredRequired.Width < 100 && layeredRequired.Height < 100,
            "Overlapping whole-page effects must expand kernel dependencies, not the whole page.");
    }

    [TestMethod]
    [DataRow(10)]
    [DataRow(11)]
    public void BoundedMosaicsMatchFullReferenceIncludingMasksAlphaAndImageEdges(int firstVariant)
    {
        var source = Pattern(320, 240);
        for (var offset = 0; offset < source.Pixels.Length; offset += 4)
        {
            var alpha = (byte)(128 + offset / 4 % 128);
            for (var channel = 0; channel < 3; channel++)
                source.Pixels[offset + channel] = (byte)(source.Pixels[offset + channel] * alpha / 255);
            source.Pixels[offset + 3] = alpha;
        }
        var brush = Mosaic(new PixelRect(40, 30, 240, 160), 111, 1) with
        {
            Points = [new PixelPoint(20, 160), new PixelPoint(155, 45), new PixelPoint(300, 200)],
            Style = new AnnotationStyle(0xFFFF0000, 27, 16, 111, 0.3)
        };
        var annotations = new[]
        {
            Mosaic(new PixelRect(83, 47, 150, 125), 10, 2) with { Style = new AnnotationStyle(0, 20, 16, 10, 0.75) { MosaicScale = 2 } },
            brush,
            Mosaic(new PixelRect(-17, -11, 350, 270), firstVariant, 0),
            Mosaic(new PixelRect(90, 70, 150, 120), 110, 3) with
            {
                Points = [new PixelPoint(115, 105)],
                Style = new AnnotationStyle(0, 50, 16, 110, 0.5)
            }
        };
        var reference = FullMosaicReference(source, annotations);
        foreach (var selection in new[]
        {
            new PixelRect(119, 98, 17, 11), new PixelRect(0, 0, 13, 9), new PixelRect(301, 220, 19, 20)
        })
        {
            CollectionAssert.AreEqual(reference.Crop(selection).Pixels,
                CaptureSurface.CompositeMosaicsRegion(source, selection, annotations).Pixels,
                $"A bounded composition changed the original effect at {selection}.");
        }
    }

    [TestMethod]
    public void SmallMosaicExportDoesNotAllocateAnotherFullSourceImage()
    {
        var source = Pattern(1024, 1024);
        var annotations = new[] { Mosaic(new PixelRect(0, 0, 1024, 1024), 10, 0), Mosaic(new PixelRect(0, 0, 1024, 1024), 11, 1) };
        var selection = new PixelRect(503, 507, 17, 13);
        CaptureSurface.CompositeMosaicsRegion(source, selection, annotations); // Warm this path before measuring.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var output = CaptureSurface.CompositeMosaicsRegion(source, selection, annotations);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(selection.Width, output.Width);
        Assert.IsTrue(allocated < 1024 * 1024, $"Small export allocated {allocated} bytes; a full source copy alone would be 4 MiB.");
    }

    [TestMethod]
    public void ChangedMosaicOutsideCroppedViewStillInvalidatesDownstreamBlurHalo() => OnSta(() =>
    {
        var source = Pattern(320, 240);
        var view = new PixelRect(100, 100, 50, 50);
        var original = new[] { Mosaic(new PixelRect(75, 105, 20, 20), 10, 0), Mosaic(new PixelRect(0, 0, 320, 240), 11, 1) };
        var surface = CreateSurface(source.Crop(view));
        surface.SetMosaicSourceContext(source, view, 1);
        Items(surface).AddRange(original.Select(item => CaptureSurface.OffsetItem(item, -view.X, -view.Y)));
        Invoke(surface, "GetMosaicComposite");
        var moved = CaptureSurface.OffsetItem(Items(surface)[0], 5, 0);
        Invoke(surface, "ReplaceAnnotation", moved);
        original[0] = CaptureSurface.OffsetItem(original[0], 5, 0);
        var expected = FullMosaicReference(source, original).Crop(view);
        CollectionAssert.AreEqual(expected.Pixels, ((PixelBuffer)Invoke(surface, "GetMosaicComposite")!).Pixels,
            "The changed mark is outside the view, but its pixels are sampled by the visible blur edge.");
    });

    private static PixelBuffer FullMosaicReference(PixelBuffer source, IEnumerable<AnnotationItem> annotations)
    {
        var reference = source with { Pixels = (byte[])source.Pixels.Clone() };
        var apply = typeof(CaptureSurface).GetMethod("ApplyMosaic", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var item in annotations.OrderBy(item => item.ZIndex))
            apply.Invoke(null, [reference, item]);
        return reference;
    }

    [TestMethod]
    public async Task UnannotatedExportKeepsOriginalPixelsAndCancellationWorksAsync()
    {
        var source = Pattern(160, 120);
        var full = new PixelRect(0, 0, source.Width, source.Height);
        var output = await ScreenshotRenderWorker.RenderAsync(source, full, [], CancellationToken.None);
        Assert.AreSame(source.Pixels, output.Pixels, "An unannotated full image needs no additional full-size bitmap.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ScreenshotRenderWorker.RenderAsync(source, full, [], cancellation.Token));
    }

    private static CaptureSurface CreateSurface(PixelBuffer image)
    {
        var resources = new ResourceDictionary();
        foreach (var key in new[] { "Brush.Accent", "Brush.OverlayBase", "Brush.OverlayBorder", "Brush.OverlayText", "Brush.OverlayTextDim", "Brush.OverlayIcon", "Brush.Neutral500" })
            resources[key] = Brushes.Black;
        var frame = new CaptureFrame(image.Pixels, image.Width, image.Height, image.Stride, 96, 96,
            new PixelRect(0, 0, image.Width, image.Height), CaptureColorSpace.Srgb, DateTimeOffset.UtcNow);
        var surface = new CaptureSurface(frame, [], new WindowSnapService(), resources);
        surface.InitializeCommittedSelection(new PixelRect(0, 0, image.Width, image.Height), false);
        surface.Measure(new Size(image.Width, image.Height));
        surface.Arrange(new Rect(0, 0, image.Width, image.Height));
        return surface;
    }

    private static void DrawBrush(CaptureSurface surface, CaptureTool tool, int thickness)
    {
        surface.Tool = tool;
        surface.MosaicMode = MosaicDrawMode.Brush;
        surface.CurrentStyle = new AnnotationStyle(0xFFFF0000, thickness, 16, tool == CaptureTool.Mosaic ? 110 : 0, 0.75);
        var points = (List<PixelPoint>)Field(surface, "_workingPoints");
        points.AddRange([new PixelPoint(50, 60), new PixelPoint(90, 60)]);
        Invoke(surface, "AddAnnotation", points[0], points[1]);
    }

    private static AnnotationItem Mosaic(PixelRect bounds, int variant, int zIndex) => new(Guid.NewGuid(), AnnotationKind.Mosaic,
        [new PixelPoint(bounds.X, bounds.Y), new PixelPoint(bounds.Right, bounds.Bottom)], bounds,
        new AnnotationStyle(0xFFFF0000, 20, 16, variant, 0.75), null, null, zIndex);

    private static PixelBuffer Pattern(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                pixels[offset] = (byte)((x * 13 + y * 7) % 256);
                pixels[offset + 1] = (byte)((x * 3 + y * 11) % 256);
                pixels[offset + 2] = (byte)((x * 17 + y * 5) % 256);
                pixels[offset + 3] = 255;
            }
        return new PixelBuffer(pixels, width, height, width * 4);
    }

    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static List<AnnotationItem> Items(CaptureSurface surface) => (List<AnnotationItem>)Field(surface, "_annotations");
    private static object? Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void OnSta(Action work)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { work(); } catch (Exception exception) { failure = exception; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(20)), "Offscreen rendering timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class CaptureStub : IScreenCaptureService
    {
        public int Calls { get; private set; }
        public Task<CaptureFrame> CaptureAtAsync(PixelPoint point, CancellationToken cancellationToken = default)
        {
            Calls++;
            var image = Pattern(8, 8);
            return Task.FromResult(new CaptureFrame(image.Pixels, 8, 8, image.Stride, 96, 96,
                new PixelRect(0, 0, 8, 8), CaptureColorSpace.Srgb, DateTimeOffset.UtcNow));
        }
        public void Dispose() { }
    }
}
