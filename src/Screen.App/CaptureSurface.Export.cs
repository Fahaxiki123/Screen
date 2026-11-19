using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureSurface
{
    private sealed record MosaicSourceContext(PixelBuffer Source, PixelRect Region, int Scale);
    private MosaicSourceContext? _mosaicSourceContext;

    // A cropped/downsampled editor view still samples mosaics from its original
    // image. Cropping the view must not move the block grid or remove blur input.
    internal void SetMosaicSourceContext(PixelBuffer source, PixelRect region, int scale)
    {
        _mosaicSourceContext = new(source, region, scale);
        ResetMosaicComposite();
        InvalidateVisual();
    }

    internal static PixelBuffer RenderAnnotatedRegion(
        PixelBuffer source,
        PixelRect selection,
        IReadOnlyList<AnnotationItem> annotations,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        selection = selection.Intersect(new PixelRect(0, 0, source.Width, source.Height));
        if (selection.IsEmpty)
        {
            throw new InvalidOperationException("请先选择截图区域。");
        }
        var baseImage = CompositeMosaicsRegion(source, selection, annotations, cancellationToken);
        var vectors = annotations.Where(item => item.Kind != AnnotationKind.Mosaic).ToArray();
        if (vectors.Length == 0)
        {
            return baseImage;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(baseImage.ToBitmapSource(), new Rect(0, 0, selection.Width, selection.Height));
            DrawAnnotations(dc, vectors, new Vector(-selection.X, -selection.Y));
        }
        var bitmap = new RenderTargetBitmap(selection.Width, selection.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        cancellationToken.ThrowIfCancellationRequested();
        return bitmap.ToPixelBuffer();
    }

    internal static PixelBuffer CompositeMosaicsRegion(
        PixelBuffer source,
        PixelRect selection,
        IReadOnlyList<AnnotationItem> annotations,
        CancellationToken cancellationToken = default)
    {
        var fullBounds = new PixelRect(0, 0, source.Width, source.Height);
        selection = selection.Intersect(fullBounds);
        if (selection.IsEmpty)
        {
            throw new ArgumentOutOfRangeException(nameof(selection));
        }
        var plan = PlanMosaicComposition(fullBounds, selection, annotations, cancellationToken);
        var region = plan.SourceRegion;
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Stages.Length == 0)
        {
            // PixelBuffer data is immutable to callers; do not round-trip an
            // unannotated full image through WPF or allocate a duplicate array.
            return selection == fullBounds ? source : source.Crop(selection);
        }

        var composited = source.Crop(region);
        foreach (var stage in plan.Stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Evaluate only complete blocks / the required blur halo. Pixels
            // outside this stage's requested output are input for other stages
            // and must not be accidentally processed a second time.
            var effect = composited.Crop(Offset(stage.Input, -region.X, -region.Y));
            ApplyMosaic(effect, RasterOffset(stage.Annotation, -stage.Input.X, -stage.Input.Y));
            for (var row = stage.Output.Y; row < stage.Output.Bottom; row++)
            {
                var sourceOffset = (row - stage.Input.Y) * effect.Stride + (stage.Output.X - stage.Input.X) * 4;
                var targetOffset = (row - region.Y) * composited.Stride + (stage.Output.X - region.X) * 4;
                effect.Pixels.AsSpan(sourceOffset, stage.Output.Width * 4)
                    .CopyTo(composited.Pixels.AsSpan(targetOffset, stage.Output.Width * 4));
            }
        }
        return region == selection
            ? composited
            : composited.Crop(Offset(selection, -region.X, -region.Y));
    }

    private sealed record MosaicStage(AnnotationItem Annotation, PixelRect Input, PixelRect Output);
    private sealed record MosaicPlan(PixelRect SourceRegion, MosaicStage[] Stages);

    internal static PixelRect RequiredMosaicSourceRegion(
        PixelRect sourceBounds, PixelRect selection, IReadOnlyList<AnnotationItem> annotations) =>
        PlanMosaicComposition(sourceBounds, selection.Intersect(sourceBounds), annotations, CancellationToken.None).SourceRegion;

    private static MosaicPlan PlanMosaicComposition(
        PixelRect sourceBounds, PixelRect selection, IReadOnlyList<AnnotationItem> annotations, CancellationToken token)
    {
        var mosaics = annotations.Where(item => item.Kind == AnnotationKind.Mosaic).OrderBy(item => item.ZIndex).ToArray();
        var stages = new List<MosaicStage>();
        var required = selection;
        // Work backwards: later effects can sample pixels changed by earlier
        // effects outside the final crop. Expand by their kernels, never by a
        // whole annotation rectangle (which can span an enormous long image).
        for (var index = mosaics.Length - 1; index >= 0; index--)
        {
            token.ThrowIfCancellationRequested();
            var mosaic = mosaics[index];
            var bounds = mosaic.Bounds.Intersect(sourceBounds);
            var output = required.Intersect(bounds);
            if (output.IsEmpty)
            {
                continue;
            }
            var input = RequiredMosaicSamples(mosaic, bounds, output);
            stages.Add(new MosaicStage(mosaic, input, output));
            required = UnionRegion(required, input);
        }
        stages.Reverse();
        return new MosaicPlan(required, stages.ToArray());
    }

    private static PixelRect RequiredMosaicSamples(AnnotationItem item, PixelRect effectBounds, PixelRect output)
    {
        if (item.Style.Variant % 100 == AnnotationVariants.MosaicBlur)
        {
            // BoxBlur performs one horizontal and one vertical pass. Each
            // output therefore depends on this radius in each dimension.
            return Inflate(output, MosaicBlurRadius(item.Style)).Intersect(effectBounds);
        }
        var block = MosaicBlockSize(item.Style);
        var left = effectBounds.X + (output.X - effectBounds.X) / block * block;
        var top = effectBounds.Y + (output.Y - effectBounds.Y) / block * block;
        var right = (int)Math.Min(effectBounds.Right,
            effectBounds.X + ((long)output.Right - effectBounds.X + block - 1) / block * block);
        var bottom = (int)Math.Min(effectBounds.Bottom,
            effectBounds.Y + ((long)output.Bottom - effectBounds.Y + block - 1) / block * block);
        return new PixelRect(left, top, right - left, bottom - top);
    }

    private static AnnotationItem RasterOffset(AnnotationItem item, int x, int y) => item with
    {
        // Geometry has already been rounded in original-image coordinates.
        // Re-rounding fractional bounds after a local shift can move a .5 tie.
        Bounds = Offset(item.Bounds, x, y),
        PreciseBounds = null,
        Points = item.Points.Select(point => new PixelPoint(point.X + x, point.Y + y)).ToArray()
    };

    private static PixelRect ExpandMosaicInfluence(PixelRect dirty, IReadOnlyList<AnnotationItem> mosaics, PixelRect sourceBounds)
    {
        foreach (var item in mosaics.OrderBy(item => item.ZIndex))
        {
            var bounds = item.Bounds.Intersect(sourceBounds);
            var overlap = dirty.Intersect(bounds);
            if (!overlap.IsEmpty)
            {
                // Both a complete pixel block and a symmetric blur kernel
                // propagate an input change over the same bounded support.
                dirty = UnionRegion(dirty, RequiredMosaicSamples(item, bounds, overlap));
            }
        }
        return dirty;
    }

    private PixelRect ExpandDirtyMosaicRegion(PixelRect dirty, IReadOnlyList<AnnotationItem> mosaics, PixelRect viewBounds)
    {
        if (dirty.IsEmpty)
        {
            return dirty;
        }
        if (_mosaicSourceContext is not { } context)
        {
            return ExpandMosaicInfluence(dirty, mosaics, viewBounds).Intersect(viewBounds);
        }
        var originalDirty = new PixelRect(context.Region.X + dirty.X * context.Scale, context.Region.Y + dirty.Y * context.Scale,
            dirty.Width * context.Scale, dirty.Height * context.Scale);
        var items = mosaics.Select(item => OffsetItem(ScaleItem(item, context.Scale), context.Region.X, context.Region.Y)).ToArray();
        var affected = ExpandMosaicInfluence(originalDirty, items, new PixelRect(0, 0, context.Source.Width, context.Source.Height))
            .Intersect(context.Region);
        if (affected.IsEmpty)
        {
            return affected;
        }
        return CoverBounds(new PreciseAnnotationBounds(
            (affected.X - context.Region.X) / (double)context.Scale,
            (affected.Y - context.Region.Y) / (double)context.Scale,
            affected.Width / (double)context.Scale, affected.Height / (double)context.Scale)).Intersect(viewBounds);
    }

    private PixelBuffer RenderContextMosaics(PixelRect region, IReadOnlyList<AnnotationItem> mosaics)
    {
        var context = _mosaicSourceContext!;
        var originalRegion = new PixelRect(
            context.Region.X + region.X * context.Scale,
            context.Region.Y + region.Y * context.Scale,
            region.Width * context.Scale,
            region.Height * context.Scale).Intersect(context.Region);
        var originalItems = mosaics.Select(item => OffsetItem(ScaleItem(item, context.Scale), context.Region.X, context.Region.Y)).ToArray();
        var original = CompositeMosaicsRegion(context.Source, originalRegion, originalItems);
        return context.Scale == 1 ? original : LongScreenshotEditorWindow.Downscale(original, context.Scale);
    }

    private static void CopyInto(PixelBuffer source, PixelBuffer destination, PixelRect region)
    {
        for (var row = 0; row < region.Height; row++)
        {
            source.Pixels.AsSpan(row * source.Stride, region.Width * 4)
                .CopyTo(destination.Pixels.AsSpan((region.Y + row) * destination.Stride + region.X * 4, region.Width * 4));
        }
    }
}
