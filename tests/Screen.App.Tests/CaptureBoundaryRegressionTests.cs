using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.App;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class CaptureBoundaryRegressionTests
{
    private const int Width = 160;
    private const int Height = 120;
    private static readonly PixelRect _region = new(30, 25, 80, 60);

    [TestMethod]
    public void BorderlessPinKeepsSourcePixelsEverywhereExceptTheRoundedCorners() => OnSta(() =>
    {
        const int width = 48;
        const int height = 32;
        const int stride = width * 4;
        const int radius = 12;
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

        var pin = CreateThemedPin(new PixelBuffer(pixels, width, height, stride));
        ChoosePinOption(pin, "描边颜色", "无描边");
        var rendered = RenderPin(pin, width, height);
        AssertPinCornersTransparent(rendered, width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // If even the outer corner of this pixel is inside the arc, it must be unchanged.
                var dx = Math.Max(0, radius - Math.Min(x, width - 1 - x));
                var dy = Math.Max(0, radius - Math.Min(y, height - 1 - y));
                if (dx * dx + dy * dy <= radius * radius)
                    Assert.AreEqual(PixelAt(pixels, stride, x, y), PixelAt(rendered, stride, x, y), $"Source pixel changed at ({x}, {y}).");
            }
        }
    });

    [TestMethod]
    [DataRow("淡描边", "Pin.OutlineSubtle")]
    [DataRow("浅色描边", "Pin.OutlineLight")]
    [DataRow("深色描边", "Pin.OutlineDark")]
    [DataRow("蓝色描边", "Brush.Accent")]
    public void PinOutlineKeepsItsSelectedColorAndStaysInsideRoundedCornersAfterResize(string outlineName, string brushKey) => OnSta(() =>
    {
        const byte sourceChannel = 24;
        var image = FillSelection(0xFF181818);
        var pin = CreateThemedPin(image);
        ChoosePinOption(pin, "描边颜色", outlineName);
        var stroke = ((SolidColorBrush)pin.FindResource(brushKey)).Color;
        int Blend(byte channel) => (stroke.A * channel + (255 - stroke.A) * sourceChannel + 127) / 255;

        foreach (var (width, height) in new[] { (image.Width, image.Height), (120, 90), (40, 30) })
        {
            var rendered = RenderPin(pin, width, height);
            AssertPinCornersTransparent(rendered, width, height);
            foreach (var (x, y) in new[] { (width / 2, 0), (width / 2, height - 1), (0, height / 2), (width - 1, height / 2) })
            {
                var offset = (y * width + x) * 4;
                Assert.AreEqual(Blend(stroke.B), (int)rendered[offset], 1);
                Assert.AreEqual(Blend(stroke.G), (int)rendered[offset + 1], 1);
                Assert.AreEqual(Blend(stroke.R), (int)rendered[offset + 2], 1);
                Assert.AreEqual((byte)255, rendered[offset + 3]);
            }
            Assert.AreEqual(0xFF181818u, PixelAt(rendered, width * 4, width / 2, 1), "The default stroke is one DIP wide.");
        }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PinHoverOnlyChangesTheCloseButton(bool borderless) => OnSta(() =>
    {
        var image = FillSelection(0xFF181818);
        var pin = CreateThemedPin(image);
        if (borderless)
            ChoosePinOption(pin, "描边颜色", "无描边");
        AssertPinChoice(pin, "描边颜色", borderless ? "无描边" : "淡描边");
        AssertPinChoice(pin, "描边粗细", "1 px");
        var resting = RenderPin(pin, image.Width, image.Height);
        var close = ((Grid)pin.Content).Children.OfType<Button>().Single();
        Assert.AreEqual(Visibility.Collapsed, close.Visibility);
        AssertPinCornersTransparent(resting, image.Width, image.Height);
        var edge = PixelAt(resting, image.Stride, image.Width / 2, 0);
        Assert.AreEqual(borderless, edge == 0xFF181818u, "Only an explicitly borderless pin should have no stroke at rest.");

        void Hover(RoutedEvent routedEvent)
        {
            pin.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = routedEvent });
            // Check the settled frame, not the first frame of a possible hover transition.
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
                (_, _) => frame.Continue = false, pin.Dispatcher);
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }
        Hover(Mouse.MouseEnterEvent);
        Assert.AreEqual(Visibility.Visible, close.Visibility);
        var hovered = RenderPin(pin, image.Width, image.Height);
        AssertPinCornersTransparent(hovered, image.Width, image.Height);
        Assert.AreEqual(edge, PixelAt(hovered, image.Stride, image.Width / 2, 0));
        Hover(Mouse.MouseLeaveEvent);
        Assert.AreEqual(Visibility.Collapsed, close.Visibility);
        CollectionAssert.AreEqual(resting, RenderPin(pin, image.Width, image.Height));
    });

    [TestMethod]
    public void PinWidthsRenderIndependentlyOfColorAndCarryForwardToNewPins() => OnSta(() =>
    {
        var image = FillSelection(0xFF181818);
        var saved = new AppSettings();
        var changes = new List<(PinOutlineColor? Color, double? Width)>();
        void Save(PinOutlineColor? color, double? width)
        {
            changes.Add((color, width));
            if (color.HasValue)
                saved.PinOutlineColor = color.Value;
            if (width.HasValue)
                saved.PinOutlineWidth = width.Value;
        }
        PinWindow NewPin() => CreateThemedPin(image, saved, Save);

        var pin = NewPin();
        ChoosePinOption(pin, "描边颜色", "蓝色描边");
        Assert.AreEqual(((PinOutlineColor?)PinOutlineColor.Blue, (double?)null), changes.Last());
        var accent = ((SolidColorBrush)pin.FindResource("Brush.Accent")).Color;
        foreach (var width in new[] { 0.5, 1, 1.5, 2, 3, 4 })
        {
            var label = FormattableString.Invariant($"{width} px");
            ChoosePinOption(pin, "描边粗细", label);
            AssertPinChoice(pin, "描边颜色", "蓝色描边");
            Assert.AreEqual(((PinOutlineColor?)null, (double?)width), changes.Last());
            var rendered = RenderPin(pin, image.Width, image.Height);
            // Total blue coverage across the edge measures the real width, including half-pixel strokes.
            double coverage = 0;
            for (var y = 0; y < 5; y++)
                coverage += (rendered[y * image.Stride + image.Width / 2 * 4] - 24d) / (accent.B - 24d);
            Assert.AreEqual(width, coverage, 0.08, $"The selected {label} must be visible in the rendered pixels.");
        }

        ChoosePinOption(pin, "描边颜色", "无描边");
        AssertPinChoice(pin, "描边粗细", "4 px");
        ChoosePinOption(pin, "描边颜色", "无描边");
        var next = NewPin();
        AssertPinChoice(next, "描边颜色", "无描边");
        AssertPinChoice(next, "描边粗细", "4 px");
        Assert.AreEqual(0xFF181818u, PixelAt(RenderPin(next, image.Width, image.Height), image.Stride, image.Width / 2, 0));
        ChoosePinOption(next, "描边颜色", "蓝色描边");
        var reopened = RenderPin(next, image.Width, image.Height);
        Assert.AreEqual((int)accent.B, (int)reopened[3 * image.Stride + image.Width / 2 * 4], "Choosing a new color restores the saved 4 DIP width.");
        AssertPinChoice(pin, "描边颜色", "无描边");
    });

    private static IEnumerable<MenuItem> PinOptions(PinWindow pin, string group)
    {
        var menu = ((FrameworkElement)pin.Content).ContextMenu!;
        return menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, group)).Items.OfType<MenuItem>();
    }

    private static void ChoosePinOption(PinWindow pin, string group, string name)
    {
        var item = PinOptions(pin, group).Single(item => Equals(item.Header, name));
        // MenuItem toggles its own IsChecked before it raises Click on real input.
        typeof(MenuItem).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(item, null);
        pin.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        AssertPinChoice(pin, group, name);
    }

    private static void AssertPinChoice(PinWindow pin, string group, string name)
    {
        var items = PinOptions(pin, group).ToArray();
        Assert.IsTrue(items.All(item => item.IsCheckable));
        CollectionAssert.AreEqual(new[] { name }, items.Where(item => item.IsChecked).Select(item => item.Header).ToArray());
    }

    private static void AssertPinCornersTransparent(byte[] pixels, int width, int height)
    {
        foreach (var x in new[] { 0, width - 1 })
        {
            foreach (var y in new[] { 0, height - 1 })
                Assert.AreEqual(0u, PixelAt(pixels, width * 4, x, y), $"Pin corner at ({x}, {y}) must be transparent.");
        }
    }

    private static PinWindow CreateThemedPin(PixelBuffer image, AppSettings? saved = null, Action<PinOutlineColor?, double?>? save = null)
    {
        var bounds = new PixelRect(0, 0, image.Width, image.Height);
        var pin = saved is null ? new PinWindow(image, bounds) : new PinWindow(image, bounds, saved.PinOutlineColor, saved.PinOutlineWidth, save);
        pin.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Screen;component/Themes/Tokens.xaml", UriKind.Relative) });
        var menus = new ResourceDictionary { Source = new Uri("/Screen;component/Themes/ShellMenus.xaml", UriKind.Relative) };
        foreach (var key in new[] { "Pin.OutlineSubtle", "Pin.OutlineLight", "Pin.OutlineDark" })
            pin.Resources[key] = menus[key];
        return pin;
    }

    private static byte[] RenderPin(PinWindow pin, int width, int height)
    {
        var content = (FrameworkElement)pin.Content;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

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
        var target = new WindowTarget(1, "Source", _region);
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
    public void FreehandReleasedOutsideEndsAtFirstBoundaryIntersection(bool mosaic) => OnSta(() =>
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
        Assert.IsTrue(annotation.Points.All(point => point.X >= _region.X && point.X <= _region.Right
            && point.Y >= _region.Y && point.Y <= _region.Bottom));
        surface.Undo();
        Assert.AreEqual(0, Items(surface).Count, "The complete clipped stroke remains one undo operation.");
    });

    [TestMethod]
    [DataRow(AnnotationVariants.MosaicPixelate)]
    [DataRow(AnnotationVariants.MosaicBlur)]
    public void MosaicBrushResumesOnReentryWithoutJoiningTheGap(int variant) => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Tool = CaptureTool.Mosaic;
        surface.MosaicMode = MosaicDrawMode.Brush;
        surface.CurrentStyle = new AnnotationStyle(0, 10, 16, variant + 100, 0.8);
        var original = Render(surface);
        var start = new PixelPoint(42, 36);
        SetField(surface, "_start", start);
        SetField(surface, "_drawing", true);
        WorkingPoints(surface).Add(start);
        void Move(int x, int y)
        {
            var point = new PixelPoint(x, y);
            SetField(surface, "_cursor", point);
            Invoke(surface, "UpdateFreehandDrawing", point);
            surface.InvalidateVisual();
        }

        // Exit through the right edge, travel down outside, then cross back in at a different height.
        Move(92, 36);
        Move(140, 36);
        Assert.IsTrue(GetField<bool>(surface, "_drawing"), "Leaving the selection while pressed must not finish a mosaic brush stroke.");
        Move(140, 74);
        Move(55, 74);
        Move(42, 74);
        Move(9, 74);
        var preview = Render(surface);
        Invoke(surface, "FinishDrawing", new PixelPoint(9, 30)); // Release at a different height, still outside.
        surface.InvalidateVisual();
        var committed = Render(surface);

        foreach (var pixels in new[] { preview, committed })
        {
            AssertOnlySelectionChanged(original, pixels, _region);
            foreach (var y in new[] { 36, 74 })
            {
                var edge = (y * Width + _region.Right - 8) * 4;
                Assert.IsFalse(original.AsSpan(edge, 8 * 4).SequenceEqual(pixels.AsSpan(edge, 8 * 4)),
                    $"The brush should paint up to the edge on both exit and return (row {y}).");
            }
            var gap = (55 * Width + _region.X) * 4;
            Assert.IsTrue(original.AsSpan(gap, _region.Width * 4).SequenceEqual(pixels.AsSpan(gap, _region.Width * 4)),
                "Moving outside must not connect the two runs by a line across the selection or along its edge.");
        }
        Assert.IsNull(Invoke(surface, "HitTestAnnotation", new PixelPoint(109, 55)), "The gap must not select an invisible stroke.");
        Assert.IsNotNull(Invoke(surface, "HitTestAnnotation", new PixelPoint(95, 74)));
        var expectedExport = new PixelBuffer(committed, Width, Height, Width * 4).Crop(_region);
        CollectionAssert.AreEqual(expectedExport.Pixels, surface.RenderSelection().Pixels, "Export must preserve the same gap as the preview.");
        surface.Undo();
        CollectionAssert.AreEqual(original, Render(surface), "One undo must remove the entire mouse gesture.");
        surface.Redo();
        CollectionAssert.AreEqual(committed, Render(surface));
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
        surface.InitializeCommittedSelection(_region, showSelectionChrome: false);
        AssertOnlySelectionChanged(Render(CreateSurface()), Render(surface), _region);
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
        AssertOnlySelectionChanged(baseline, Render(surface), _region);
        Invoke(surface, "FinishDrawing", new PixelPoint(32, 78));
        surface.InvalidateVisual();
        AssertOnlySelectionChanged(baseline, Render(surface), _region);
    });

    [TestMethod]
    public void ShapeEndpointsAreRestrictedToSelectionAtCommit() => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Tool = CaptureTool.Rectangle;
        Invoke(surface, "AddAnnotation", new PixelPoint(40, 40), new PixelPoint(155, 115));
        var shape = Items(surface).Single();
        Assert.AreEqual(new PixelPoint(110, 85), shape.Points[^1]);
        Assert.AreEqual(_region.Right, shape.Bounds.Right);
        Assert.AreEqual(_region.Bottom, shape.Bounds.Bottom);
    });

    [TestMethod]
    public void LostMouseCaptureCancelsUnfinishedBrushAndSelectionGestures() => OnSta(() =>
    {
        var surface = CreateSurface();
        surface.Tool = CaptureTool.Mosaic;
        surface.MosaicMode = MosaicDrawMode.Brush;
        surface.CurrentStyle = new AnnotationStyle(0, 10, 16, AnnotationVariants.MosaicPixelate + 100);
        SetField(surface, "_start", new PixelPoint(40, 40));
        SetField(surface, "_drawing", true);
        SetField(surface, "_movingSelection", true);
        SetField(surface, "_resizingSelection", true);
        WorkingPoints(surface).Add(new PixelPoint(40, 40));
        Invoke(surface, "UpdateFreehandDrawing", new PixelPoint(140, 40));
        Invoke(surface, "OnLostMouseCapture", new MouseEventArgs(Mouse.PrimaryDevice, 0));
        Assert.IsFalse(GetField<bool>(surface, "_drawing"));
        Assert.IsFalse(GetField<bool>(surface, "_movingSelection"));
        Assert.IsFalse(GetField<bool>(surface, "_resizingSelection"));
        Assert.AreEqual(0, WorkingPoints(surface).Count);
        Assert.AreEqual(0, Items(surface).Count);
        Assert.AreNotEqual(Cursors.None, surface.Cursor);

        var start = new PixelPoint(42, 72);
        var end = new PixelPoint(55, 72);
        SetField(surface, "_start", start);
        SetField(surface, "_drawing", true);
        WorkingPoints(surface).Add(start);
        Invoke(surface, "FinishDrawing", end);
        CollectionAssert.AreEqual(new[] { start, end }, Items(surface).Single().Points.ToArray(),
            "A new press after capture loss must not resume a run from the previous exit.");
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
        Assert.IsLessThanOrEqualTo(_region.Bottom, pen.Bounds.Bottom);
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
        surface.InitializeCommittedSelection(_region, showSelectionChrome: false);
        surface.Measure(new Size(Width, Height));
        surface.Arrange(new Rect(0, 0, Width, Height));
        return surface;
    }

    private static PixelBuffer FillSelection(uint argb)
    {
        var pixels = new byte[_region.Width * _region.Height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)argb;
            pixels[i + 1] = (byte)(argb >> 8);
            pixels[i + 2] = (byte)(argb >> 16);
            pixels[i + 3] = (byte)(argb >> 24);
        }
        return new PixelBuffer(pixels, _region.Width, _region.Height, _region.Width * 4);
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
