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
            source.Crop(new PixelRect(0, offsets[index++], source.Width, 240))), new VerticalScrollStitcher());
        foreach (var unused in offsets)
        {
            Assert.AreEqual(ScrollFrameOutcome.Added, (await sequence.CaptureAsync()).Outcome);
        }
        var result = await sequence.StitchAsync(sequence.Snapshot());
        Assert.IsTrue(result.IsComplete, result.FailureReason);
        CollectionAssert.AreEqual(source.Crop(new PixelRect(0, 0, source.Width, 530)).Pixels, result.Image.Pixels);
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
        }, new VerticalScrollStitcher(), (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        if (seedFirst)
            await sequence.CaptureAsync();
        var pending = sequence.CaptureAsync();
        var finish = sequence.FlushAsync();
        Assert.IsFalse(finish.IsCompleted, "Completion must wait for acquisition already in progress.");
        gate.SetResult(last);
        await pending;
        Assert.AreEqual(ScrollFrameOutcome.Unchanged, (await finish).Outcome);
        Assert.AreEqual(seedFirst ? 2 : 1, sequence.FrameCount);
        var result = await sequence.StitchAsync(sequence.Snapshot());
        Assert.IsTrue(result.IsComplete, result.FailureReason);
        var expected = seedFirst ? source.Crop(new PixelRect(0, 0, source.Width, 319)) : last;
        CollectionAssert.AreEqual(expected.Pixels, result.Image.Pixels);
    }

    [TestMethod]
    public async Task CancelDuringAcquisitionCannotAppendTheLateFrame()
    {
        var gate = new TaskCompletionSource<PixelBuffer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = new ScrollCaptureSequence(_ => gate.Task, new VerticalScrollStitcher());
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

    private static PixelBuffer Document()
    {
        const int width = 192;
        const int height = 600;
        var random = new Random(694);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (var index = 3; index < pixels.Length; index += 4)
            pixels[index] = 255;
        return new PixelBuffer(pixels, width, height, width * 4);
    }
}
