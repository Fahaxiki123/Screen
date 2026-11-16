using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.App;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class ScrollCaptureCompletionRegressionTests
{
    [TestMethod]
    public async Task RejectedViewportSettlesWithoutRepeatedAlignmentRetries()
    {
        var first = Document(128, 180);
        var replacement = new PixelBuffer((byte[])first.Pixels.Clone(), first.Width, first.Height, first.Stride);
        for (var index = 0; index < replacement.Pixels.Length; index += 4)
        {
            replacement.Pixels[index] ^= 0xff;
            replacement.Pixels[index + 1] ^= 0xff;
            replacement.Pixels[index + 2] ^= 0xff;
        }
        var captures = 0;
        var settlingSamples = 0;
        var sequence = new ScrollCaptureSequence(
            _ => Task.FromResult(++captures == 1 ? first : replacement),
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

        Assert.AreEqual(ScrollFrameOutcome.Unstable, tail.Outcome);
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

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OutputFailureRetainsTheSameStitchedImageForRetry(bool firstAttemptSaves)
    {
        var firstAction = firstAttemptSaves ? ScrollCaptureFinishAction.Save : ScrollCaptureFinishAction.Copy;
        var document = Document(128, 180);
        var captures = 0;
        var sequence = new ScrollCaptureSequence(_ =>
        {
            captures++;
            return Task.FromResult(document);
        }, (_, _) => Task.CompletedTask);
        var attempts = new List<(ScrollCaptureFinishAction Action, PixelBuffer Image, bool AcquisitionStopped)>();
        using var controller = new ScrollCaptureController(
            new SelectionRegion(new PixelRect(0, 0, document.Width, document.Height), null),
            new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json")), sequence,
            (action, image) =>
            {
                attempts.Add((action, image, sequence.CancellationToken.IsCancellationRequested));
                if (attempts.Count == 1)
                {
                    throw new IOException("Output unavailable");
                }
                return (action, null);
            });
        var completions = 0;
        var cancellations = 0;
        controller.Completed += (_, _) => completions++;
        controller.Cancelled += (_, _) => cancellations++;

        await controller.FinishAsync(firstAction);

        Assert.AreEqual(0, completions);
        Assert.AreEqual(0, cancellations);
        var captureCountAfterComposition = captures;
        var retryAction = firstAction == ScrollCaptureFinishAction.Copy
            ? ScrollCaptureFinishAction.Save : ScrollCaptureFinishAction.Copy;
        await controller.FinishAsync(retryAction);
        await controller.FinishAsync(retryAction);

        Assert.HasCount(2, attempts);
        Assert.AreEqual(firstAction, attempts[0].Action);
        Assert.AreEqual(retryAction, attempts[1].Action);
        Assert.IsTrue(attempts.All(attempt => attempt.AcquisitionStopped));
        Assert.AreSame(attempts[0].Image, attempts[1].Image);
        CollectionAssert.AreEqual(document.Pixels, attempts[1].Image.Pixels);
        Assert.AreEqual(captureCountAfterComposition, captures);
        Assert.AreEqual(1, completions);
        Assert.AreEqual(0, cancellations);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EnterRetryUsesTheLastFailedActionAndTheSameImage(bool saves)
    {
        var action = saves ? ScrollCaptureFinishAction.Save : ScrollCaptureFinishAction.Edit;
        var document = Document(128, 180);
        var sequence = new ScrollCaptureSequence(_ => Task.FromResult(document), (_, _) => Task.CompletedTask);
        var attempts = new List<(ScrollCaptureFinishAction Action, PixelBuffer Image)>();
        using var controller = new ScrollCaptureController(
            new SelectionRegion(new PixelRect(0, 0, document.Width, document.Height), null),
            new JsonSettingsStore(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json")), sequence,
            (currentAction, image) =>
            {
                attempts.Add((currentAction, image));
                if (attempts.Count == 1)
                {
                    throw new IOException("Output unavailable");
                }
                return (currentAction, null);
            });

        await controller.RetryOutputAsync();
        await controller.FinishAsync(action);
        await controller.RetryOutputAsync();

        Assert.HasCount(2, attempts);
        Assert.AreEqual(action, attempts[1].Action);
        Assert.AreSame(attempts[0].Image, attempts[1].Image);
    }

    [TestMethod]
    [DataRow(0, -1, true)]
    [DataRow(0, 0x10, false)]
    [DataRow(0, 0x11, false)]
    [DataRow(0, 0x12, false)]
    [DataRow(0, 0x5b, false)]
    [DataRow(0x10, -1, false)]
    [DataRow(0x20, -1, false)]
    public void CaptureShortcutsIgnoreModifiedAndInjectedKeys(int flags, int pressedModifier, bool expected)
    {
        Assert.AreEqual(expected, LowLevelKeyboardHook.IsPlainKeyPress(flags, key => key == pressedModifier));
    }

    [STATestMethod]
    public void PngEncodingFailurePreservesExistingDestinationAndSuccessfulRetryReplacesIt()
    {
        var directory = Directory.CreateTempSubdirectory("screen-png-");
        var destination = Path.Combine(directory.FullName, "existing.png");
        try
        {
            BitmapUtilities.SavePng(Document(16, 16), destination);
            var existingBytes = File.ReadAllBytes(destination);
            var invalid = new PixelBuffer([], 16, 16, 64);

            Assert.Throws<ArgumentException>(() => BitmapUtilities.SavePng(invalid, destination));
            CollectionAssert.AreEqual(existingBytes, File.ReadAllBytes(destination));
            CollectionAssert.AreEqual(new[] { destination }, Directory.GetFiles(directory.FullName));

            var replacement = Document(17, 19);
            BitmapUtilities.SavePng(replacement, destination);
            using var file = File.OpenRead(destination);
            var actual = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
                .Frames[0].ToPixelBuffer();
            Assert.AreEqual(replacement.Width, actual.Width);
            Assert.AreEqual(replacement.Height, actual.Height);
            CollectionAssert.AreEqual(replacement.Pixels, actual.Pixels);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [STATestMethod]
    public void SavingPngSucceedsEvenIfTheLastDirectoryCannotBeRemembered()
    {
        var directory = Directory.CreateTempSubdirectory("screen-long-output-");
        try
        {
            var destination = Path.Combine(directory.FullName, "capture.png");
            var blocker = Path.Combine(directory.FullName, "blocker");
            File.WriteAllText(blocker, "Settings parent is a file.");
            var document = Document(16, 16);
            using var controller = new ScrollCaptureController(
                new SelectionRegion(new PixelRect(0, 0, document.Width, document.Height), null),
                new JsonSettingsStore(Path.Combine(blocker, "settings.json")),
                new ScrollCaptureSequence(_ => Task.FromResult(document)));

            var warning = controller.SavePngAndRememberDirectory(document, destination);

            Assert.IsTrue(File.Exists(destination));
            Assert.AreEqual("长截图已保存，无法记住保存目录", warning);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
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
