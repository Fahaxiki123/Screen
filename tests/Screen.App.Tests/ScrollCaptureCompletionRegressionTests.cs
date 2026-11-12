using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.App;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class ScrollCaptureCompletionRegressionTests
{
    [TestMethod]
    public async Task StationaryRejectedViewportSettlesWithoutRepeatedAlignmentRetries()
    {
        var first = Document(128, 180);
        var hovered = WithChangedPatch(first, 231);
        var captures = 0;
        var settlingSamples = 0;
        var sequence = new ScrollCaptureSequence(
            _ => Task.FromResult(++captures == 1 ? first : hovered),
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                settlingSamples++;
                return Task.CompletedTask;
            });
        Assert.AreEqual(ScrollFrameOutcome.Added, (await sequence.CaptureAsync()).Outcome);
        Assert.AreEqual(ScrollFrameOutcome.Rejected, (await sequence.CaptureAsync()).Outcome);

        var tail = await sequence.FlushAsync();

        Assert.AreEqual(ScrollFrameOutcome.Rejected, tail.Outcome,
            "Settling must not turn an unverified seam into an accepted frame.");
        Assert.IsTrue(settlingSamples <= 3,
            $"The current viewport is already stationary, but completion took {settlingSamples} settling samples.");
        Assert.AreEqual(1, sequence.FrameCount);
        var result = await sequence.ComposeAsync();
        Assert.IsTrue(result.IsComplete);
        CollectionAssert.AreEqual(first.Pixels, result.Image.Pixels);
    }

    [TestMethod]
    public async Task PersistentAnimationStopsWaitingAndPreservesTheVerifiedPrefix()
    {
        var first = Document(128, 180);
        var captures = 0;
        var settlingSamples = 0;
        var sequence = new ScrollCaptureSequence(
            _ => Task.FromResult(++captures == 1 ? first : WithChangedPatch(first, (byte)(captures * 13))),
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                settlingSamples++;
                Assert.IsTrue(settlingSamples <= 12,
                    "An animated cursor or widget must not make completion wait forever.");
                return Task.CompletedTask;
            });
        await sequence.CaptureAsync();

        var tail = await sequence.FlushAsync();

        Assert.AreEqual(ScrollFrameOutcome.Rejected, tail.Outcome);
        Assert.AreEqual(1, sequence.FrameCount,
            "Unverified animation frames must not be appended to the screenshot.");
        var result = await sequence.ComposeAsync();
        Assert.IsTrue(result.IsComplete);
        CollectionAssert.AreEqual(first.Pixels, result.Image.Pixels);
    }

    [TestMethod]
    public async Task FinishingAdmitsTheLastRealScrollBeforeSettling()
    {
        const int viewportHeight = 240;
        var document = Document(192, 600);
        int[] offsets = [0, 61, 140, 203, 203, 203];
        var captures = 0;
        var sequence = new ScrollCaptureSequence(
            _ => Task.FromResult(document.Crop(new PixelRect(0,
                offsets[Math.Min(captures++, offsets.Length - 1)], document.Width, viewportHeight))),
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        await sequence.CaptureAsync();
        await sequence.CaptureAsync();

        var tail = await sequence.FlushAsync();

        Assert.AreEqual(ScrollFrameOutcome.Unchanged, tail.Outcome);
        Assert.AreEqual(4, sequence.FrameCount,
            "Completion must still capture all real movement before the final stationary viewport.");
        var result = await sequence.ComposeAsync();
        Assert.IsTrue(result.IsComplete, result.FailureReason);
        CollectionAssert.AreEqual(document.Crop(new PixelRect(0, 0, document.Width, 443)).Pixels,
            result.Image.Pixels);
    }

    [TestMethod]
    public void RejectedLastViewportWarnsThatOutputContainsOnlyVerifiedContent()
    {
        var result = new StitchResult(Document(128, 180), [], true, null);
        var tail = new ScrollFrameCapture(ScrollFrameOutcome.Rejected, "No reliable overlap.");
        foreach (var action in Enum.GetValues<ScrollCaptureFinishAction>())
        {
            var warning = ScrollCaptureController.CompletionWarning(tail, result, action);
            Assert.IsNotNull(warning);
            StringAssert.Contains(warning, "末屏未能对齐");
            StringAssert.Contains(warning, "仅包含已可靠拼接的部分");
            if (action == ScrollCaptureFinishAction.Copy)
                StringAssert.Contains(warning, "已复制");
        }
    }

    [TestMethod]
    public void AnimatedTailReportsCompletedCaptureInsteadOfAskingToKeepWaiting()
    {
        var result = new StitchResult(Document(128, 180), [], true, null);
        var warning = ScrollCaptureController.CompletionWarning(
            new ScrollFrameCapture(ScrollFrameOutcome.Unstable), result, ScrollCaptureFinishAction.Copy);

        Assert.IsNotNull(warning);
        StringAssert.Contains(warning, "已复制");
        StringAssert.Contains(warning, "已结束采集");
        StringAssert.Contains(warning, "请检查末尾");
    }

    [TestMethod]
    public void CompleteStationaryCaptureDoesNotShowAPartialOutputWarning()
    {
        var result = new StitchResult(Document(128, 180), [], true, null);

        Assert.IsNull(ScrollCaptureController.CompletionWarning(
            new ScrollFrameCapture(ScrollFrameOutcome.Unchanged), result, ScrollCaptureFinishAction.Copy));
    }

    private static PixelBuffer Document(int width, int height)
    {
        var random = new Random(694);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (var index = 3; index < pixels.Length; index += 4)
            pixels[index] = 255;
        return new PixelBuffer(pixels, width, height, width * 4);
    }

    private static PixelBuffer WithChangedPatch(PixelBuffer source, byte shade)
    {
        var pixels = (byte[])source.Pixels.Clone();
        for (var y = 20; y < 28; y++)
        {
            for (var x = 20; x < 28; x++)
            {
                var offset = y * source.Stride + x * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = shade;
            }
        }
        return new PixelBuffer(pixels, source.Width, source.Height, source.Stride);
    }
}
