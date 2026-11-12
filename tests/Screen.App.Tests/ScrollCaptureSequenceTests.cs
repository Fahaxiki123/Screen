using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.App;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class ScrollCaptureSequenceTests
{
    [TestMethod]
    public async Task ContinuousManualMovementKeepsEveryIntermediateViewport()
    {
        var source = Document();
        int[] offsets = [0, 61, 140, 203, 290];
        var index = 0;
        var sequence = new ScrollCaptureSequence(_ => Task.FromResult(
            source.Crop(new PixelRect(0, offsets[index++], source.Width, 240))));
        foreach (var unused in offsets)
        {
            Assert.AreEqual(ScrollFrameOutcome.Added, (await sequence.CaptureAsync()).Outcome);
        }
        var result = await sequence.ComposeAsync();
        Assert.IsTrue(result.IsComplete, result.FailureReason);
        CollectionAssert.AreEqual(source.Crop(new PixelRect(0, 0, source.Width, 530)).Pixels, result.Image.Pixels);
    }

    [TestMethod]
    public async Task IncrementalPreviewKeepsOneHeaderAndFooterAndDoesNotChangePublishedBitmaps()
    {
        const int viewportHeight = 240;
        const int header = 21;
        const int footer = 23;
        var source = Document();
        int[] offsets = [0, 61, 140];
        var frames = offsets.Select(offset => source.Crop(new PixelRect(0, offset, source.Width, viewportHeight))).ToArray();
        foreach (var frame in frames)
        {
            PaintRows(frame, 0, header, 20);
            PaintRows(frame, frame.Height - footer, footer, 80);
        }
        var index = 0;
        var sequence = new ScrollCaptureSequence(_ => Task.FromResult(frames[index++]));
        await sequence.CaptureAsync();
        await sequence.UpdatePreviewAsync();
        Assert.AreEqual(ScrollFrameOutcome.Added, (await sequence.CaptureAsync()).Outcome);
        var earlier = await sequence.UpdatePreviewAsync();
        var earlierPixels = earlier.Image.ToPixelBuffer().Pixels;

        Assert.AreEqual(ScrollFrameOutcome.Added, (await sequence.CaptureAsync()).Outcome);
        var preview = await sequence.UpdatePreviewAsync();
        var result = await sequence.ComposeAsync();
        var expected = source.Crop(new PixelRect(0, 0, source.Width, viewportHeight + offsets[^1]));
        PaintRows(expected, 0, header, 20);
        PaintRows(expected, expected.Height - footer, footer, 80);

        Assert.AreEqual(frames.Length, preview.FrameCount);
        Assert.AreEqual(expected.Height, preview.Height);
        CollectionAssert.AreEqual(offsets[1..], result.Seams.Select(seam => seam.OutputY).ToArray());
        CollectionAssert.AreEqual(expected.Pixels, result.Image.Pixels);
        CollectionAssert.AreEqual(expected.Pixels, preview.Image.ToPixelBuffer().Pixels);
        CollectionAssert.AreEqual(earlierPixels, earlier.Image.ToPixelBuffer().Pixels);
    }

    [TestMethod]
    public async Task CoalescedPreviewRescalesAndPreservesOddSeamsAndLastPixelBlock()
    {
        const int width = 33;
        const int viewportHeight = 600;
        const int distance = 401;
        const int frames = 12;
        var source = Document(width, viewportHeight + (frames - 1) * distance);
        var index = 0;
        var sequence = new ScrollCaptureSequence(_ => Task.FromResult(
            source.Crop(new PixelRect(0, index++ * distance, width, viewportHeight))));

        for (var count = 1; count <= frames; count++)
        {
            Assert.AreEqual(ScrollFrameOutcome.Added, (await sequence.CaptureAsync()).Outcome);
            if (count is 1 or 9 or 10)
            {
                var preview = await sequence.UpdatePreviewAsync();
                Assert.AreEqual(count, preview.FrameCount);
                Assert.AreEqual(count == 10 ? 17 : width, preview.Image.PixelWidth);
            }
        }
        var latest = await sequence.UpdatePreviewAsync();
        var downscaled = latest.Image.ToPixelBuffer();
        Assert.AreEqual(frames, latest.FrameCount);
        Assert.AreEqual(source.Height, latest.Height);
        Assert.AreEqual((source.Height + 1) / 2, downscaled.Height);
        for (var y = 0; y < downscaled.Height; y++)
        {
            for (var x = 0; x < downscaled.Width; x++)
            {
                var rows = Math.Min(2, source.Height - y * 2);
                var columns = Math.Min(2, source.Width - x * 2);
                for (var channel = 0; channel < 3; channel++)
                {
                    var sum = 0;
                    for (var row = 0; row < rows; row++)
                    {
                        for (var column = 0; column < columns; column++)
                        {
                            sum += source.Pixels[(y * 2 + row) * source.Stride + (x * 2 + column) * 4 + channel];
                        }
                    }
                    Assert.AreEqual((byte)(sum / (rows * columns)), downscaled.Pixels[y * downscaled.Stride + x * 4 + channel]);
                }
            }
        }
        var result = await sequence.ComposeAsync();
        CollectionAssert.AreEqual(source.Pixels, result.Image.Pixels);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FinishWaitsForPendingFirstOrLastFrame(bool seedFirst)
    {
        var source = Document();
        var first = source.Crop(new PixelRect(0, 0, source.Width, 240));
        var last = source.Crop(new PixelRect(0, 79, source.Width, 240));
        var gate = new TaskCompletionSource<PixelBuffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var sequence = new ScrollCaptureSequence(_ =>
        {
            calls++;
            if (seedFirst && calls == 1)
                return Task.FromResult(first);
            return calls == (seedFirst ? 2 : 1) ? gate.Task : Task.FromResult(last);
        }, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        if (seedFirst)
            await sequence.CaptureAsync();
        var pending = sequence.CaptureAsync();
        var finish = sequence.FlushAsync();
        Assert.IsFalse(finish.IsCompleted, "Completion must wait for acquisition already in progress.");
        gate.SetResult(last);
        await pending;
        Assert.AreEqual(ScrollFrameOutcome.Unchanged, (await finish).Outcome);
        Assert.AreEqual(seedFirst ? 2 : 1, sequence.FrameCount);
        var result = await sequence.ComposeAsync();
        Assert.IsTrue(result.IsComplete, result.FailureReason);
        var expected = seedFirst ? source.Crop(new PixelRect(0, 0, source.Width, 319)) : last;
        CollectionAssert.AreEqual(expected.Pixels, result.Image.Pixels);
    }

    [TestMethod]
    public async Task CancelDuringAcquisitionCannotAppendTheLateFrame()
    {
        var gate = new TaskCompletionSource<PixelBuffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = new ScrollCaptureSequence(_ => gate.Task);
        var pending = sequence.CaptureAsync();
        sequence.Cancel();
        gate.SetResult(Document().Crop(new PixelRect(0, 0, 192, 240)));
        try
        {
            await pending;
            Assert.Fail("A late frame must observe cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
        Assert.AreEqual(0, sequence.FrameCount);
    }

    [TestMethod]
    public void SparseLowContrastTextMovementIsNotAnUnchangedPage()
    {
        var first = new byte[192 * 240 * 4];
        Array.Fill(first, (byte)255);
        var second = (byte[])first.Clone();
        for (var x = 20; x < 170; x++)
        {
            var offset = (117 * 192 + x) * 4;
            second[offset] = second[offset + 1] = second[offset + 2] = 250;
        }
        Assert.IsFalse(ScrollCaptureSequence.AreNearlyEqual(
            new PixelBuffer(first, 192, 240, 192 * 4), new PixelBuffer(second, 192, 240, 192 * 4)));
    }

    [TestMethod]
    public void RejectedSeamsNeverCountAsReachingTheBottom()
    {
        var bottom = new ScrollCaptureBottomDetector();
        for (var index = 0; index < 5; index++)
            Assert.IsFalse(bottom.Observe(ScrollFrameOutcome.Rejected));
        Assert.IsFalse(bottom.Observe(ScrollFrameOutcome.Unchanged));
        Assert.IsFalse(bottom.Observe(ScrollFrameOutcome.Unchanged));
        Assert.IsFalse(bottom.Observe(ScrollFrameOutcome.Added));
        Assert.IsFalse(bottom.Observe(ScrollFrameOutcome.Unchanged));
        Assert.IsFalse(bottom.Observe(ScrollFrameOutcome.Unchanged));
        Assert.IsTrue(bottom.Observe(ScrollFrameOutcome.Unchanged));
    }

    private static PixelBuffer Document(int width = 192, int height = 600)
    {
        var random = new Random(694);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (var index = 3; index < pixels.Length; index += 4)
            pixels[index] = 255;
        return new PixelBuffer(pixels, width, height, width * 4);
    }

    private static void PaintRows(PixelBuffer frame, int startY, int count, byte shade)
    {
        for (var y = startY; y < startY + count; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                var offset = y * frame.Stride + x * 4;
                frame.Pixels[offset] = frame.Pixels[offset + 1] = frame.Pixels[offset + 2] = shade;
            }
        }
    }
}
