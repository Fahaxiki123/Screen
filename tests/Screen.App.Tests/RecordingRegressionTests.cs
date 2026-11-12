using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NAudio.Wave;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class RecordingRegressionTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "Screen.Recording.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public void FailedRecordingSaveRetainsVideoAndAudioAndReportsRecoveryDirectory()
    {
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "session")).FullName;
        var source = Path.Combine(scratch, "video.mp4");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(scratch, "audio.raw"), [5, 6]);
        var destination = Path.Combine(_root, "unavailable", "recording.mp4");

        var result = RecordingFileStore.Save(source, destination, scratch);

        Assert.IsFalse(result.Saved);
        Assert.AreEqual(scratch, result.RecoveryDirectory);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Error));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source));
        Assert.IsTrue(File.Exists(Path.Combine(scratch, "audio.raw")));
        Assert.IsFalse(File.Exists(destination));
    }

    [TestMethod]
    public void FailedCommitPreservesExistingDestinationAndRecoveryMaterials()
    {
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "session")).FullName;
        var source = Path.Combine(scratch, "video.mp4");
        var destination = Path.Combine(_root, "recording.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        File.WriteAllBytes(destination, [9, 8, 7]);

        using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = RecordingFileStore.Save(source, destination, scratch);
            Assert.IsFalse(result.Saved);
            Assert.IsTrue(File.Exists(source));
        }

        CollectionAssert.AreEqual(new byte[] { 9, 8, 7 }, File.ReadAllBytes(destination));
        Assert.AreEqual(0, Directory.GetFiles(_root, ".screen-export-*.tmp").Length);
    }

    [TestMethod]
    public void SuccessfulCommitRemovesScratchOnlyAfterPublishingCompletedFile()
    {
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "session")).FullName;
        var source = Path.Combine(scratch, "video.mp4");
        var destination = Path.Combine(_root, "recording.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);

        var result = RecordingFileStore.Save(source, destination, scratch, "audio unavailable");

        Assert.IsTrue(result.Saved);
        Assert.AreEqual("audio unavailable", result.Warning);
        Assert.IsNull(result.Error);
        Assert.IsNull(result.RecoveryDirectory);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
        Assert.IsFalse(Directory.Exists(scratch));
    }

    [TestMethod]
    public void CrossDisplayCaptureKeepsNegativeCoordinatesAndExcludesGaps()
    {
        PixelRect[] monitors = [new(-1600, 120, 1600, 900), new(0, 0, 1920, 1080)];
        Assert.IsTrue(DesktopRegionCapture.FitsOnOneDisplay(new PixelRect(-1500, 150, 500, 200), monitors));
        var region = new PixelRect(-100, 40, 300, 300);
        Assert.IsFalse(DesktopRegionCapture.FitsOnOneDisplay(region, monitors));
        CollectionAssert.AreEqual(new[] { new PixelRect(-100, 120, 100, 220), new PixelRect(0, 40, 200, 300) },
            DesktopRegionCapture.GetVisibleAreas(region, monitors));
        Assert.IsEmpty(DesktopRegionCapture.GetVisibleAreas(new PixelRect(-100, 0, 100, 100), monitors));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ReusedDesktopFrameHasNoStaleCursor(bool useNv12)
    {
        const int width = 4;
        const int height = 4;
        var frames = new ScreenRecorder.FrameBuffer(width, height, useNv12);
        Array.Fill(frames.Desktop, (byte)64);
        if (useNv12)
        {
            frames.Desktop.AsSpan(width * height).Fill(128);
        }
        var clean = frames.Desktop.ToArray();
        var cursor = new ScreenRecorder.CursorBitmap([10, 20, 120, 128], new PixelRect(0, 0, 1, 1));
        var first = frames.GetEncodingFrame(cursor);
        var firstSnapshot = first.ToArray();
        Assert.IsFalse(firstSnapshot.SequenceEqual(clean), "A visible cursor must change the encoded frame.");

        // No fresh desktop readback occurs between these encoding ticks.
        CollectionAssert.AreEqual(firstSnapshot, frames.GetEncodingFrame(cursor), "Repeated blending must not accumulate.");
        var movedCursor = cursor with { Bounds = new PixelRect(2, 2, 1, 1) };
        var moved = frames.GetEncodingFrame(movedCursor);
        Assert.AreSame(first, moved, "Cursor composition should reuse its encoding buffer.");
        Assert.IsFalse(moved.SequenceEqual(firstSnapshot), "The cursor should move to its new position.");
        var fresh = new ScreenRecorder.FrameBuffer(width, height, useNv12);
        clean.CopyTo(fresh.Desktop, 0);
        CollectionAssert.AreEqual(fresh.GetEncodingFrame(movedCursor), moved, "The previous cursor must leave no pixels behind.");

        Assert.AreSame(frames.Desktop, frames.GetEncodingFrame(null));
        CollectionAssert.AreEqual(clean, frames.Desktop, "The cached desktop must stay clean when the cursor disappears.");
    }

    [STATestMethod(UseSTASynchronizationContext = true)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ImmediateStopKeepsItsResultForTheHost(bool hasVideo)
    {
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "session")).FullName;
        var source = Path.Combine(scratch, "video.mp4");
        var destination = Path.Combine(_root, "recording.mp4");
        var stopSignal = new RecordingStopSignal();
        var capture = new FakeRecordingCapture(() =>
        {
            if (!hasVideo)
            {
                throw new InvalidOperationException("No frames captured.");
            }
            File.WriteAllBytes(source, [1, 2, 3]);
        });
        using var controller = new RecordingController(capture, new PixelRect(0, 0, 64, 64),
            destination, scratch, audio: null, border: null, stopSignal);

        // Let the stop finish before the host starts waiting for its result.
        await controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var result = await controller.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await controller.StopAsync();

        Assert.AreEqual(hasVideo, result.Saved);
        Assert.AreEqual(1, capture.FinalizeCalls);
        Assert.IsTrue(stopSignal.IsRequested);
        Assert.IsTrue(capture.WasDisposed);
        Assert.AreEqual(hasVideo, File.Exists(destination));
        if (!hasVideo)
        {
            Assert.AreEqual("No frames captured.", result.Error);
            Assert.AreEqual(scratch, result.RecoveryDirectory);
        }
    }

    [STATestMethod(UseSTASynchronizationContext = true)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopPublishesVideoAndKeepsOriginalAudioIfMuxFails(bool muxFails)
    {
        const int width = 64;
        const int height = 64;
        const int frameRate = 12;
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "session")).FullName;
        var source = Path.Combine(scratch, "video.mp4");
        var audioPath = Path.Combine(scratch, "audio.raw");
        var destination = Path.Combine(_root, "recording.mp4");
        using (var pipeline = new RecordingPipeline(source, width, height, frameRate, 400_000, "nv12"))
        {
            pipeline.WriteVideoFrame(new byte[width * height * 3 / 2]);
            pipeline.FinalizeFile();
        }
        var videoBytes = File.ReadAllBytes(source);
        var audioBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var signal = new RecordingStopSignal();
        var waveIn = new FakeWaveIn();
        using var track = new RecordingAudioTrack(waveIn, audioPath,
            muxFails ? "invalid_test_audio_format" : "s16le", 48000, 2, signal);
        waveIn.Emit(audioBytes);
        using var audio = new RecordingAudioService([track]);
        var capture = new FakeRecordingCapture(() => { }) { EncodedDurationSeconds = 1.0 / frameRate };
        using var controller = new RecordingController(capture, new PixelRect(0, 0, width, height),
            destination, scratch, audio, border: null, signal);

        await controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var result = await controller.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(result.Saved, result.Error);
        Assert.IsTrue(File.Exists(destination));
        if (muxFails)
        {
            StringAssert.Contains(result.Warning ?? string.Empty, "invalid_test_audio_format");
            Assert.AreEqual(scratch, result.RecoveryDirectory);
            CollectionAssert.AreEqual(videoBytes, File.ReadAllBytes(destination));
            CollectionAssert.AreEqual(videoBytes, File.ReadAllBytes(source));
            CollectionAssert.AreEqual(audioBytes, File.ReadAllBytes(audioPath));
        }
        else
        {
            Assert.IsNull(result.Warning);
            Assert.IsNull(result.RecoveryDirectory);
            Assert.IsFalse(Directory.Exists(scratch));
        }
    }

    [TestMethod]
    public async Task ClipboardExportSurvivesPreviewCacheDisposal()
    {
        var cache = new RecordingExportCache();
        var source = Path.Combine(_root, "trim-cache.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        var cached = await cache.GetAsync(new RecordingTrimRange(1, 3), (_, _) => Task.FromResult(source));
        var clipboardPath = RecordingFileStore.CreateClipboardCopy(cached, Path.Combine(_root, "recording.mp4"));

        cache.Dispose();

        Assert.IsFalse(File.Exists(source));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(clipboardPath));
    }

    [TestMethod]
    public async Task OutOfOrderExportsStayAssociatedWithTheirOriginalRanges()
    {
        using var cache = new RecordingExportCache();
        var rangeA = new RecordingTrimRange(0, 2);
        var rangeB = new RecordingTrimRange(4, 6);
        var completionA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<string> Create(RecordingTrimRange range, CancellationToken _)
        {
            calls++;
            return range == rangeA ? completionA.Task : completionB.Task;
        }
        var exportA = cache.GetAsync(rangeA, Create);
        var concurrentA = cache.GetAsync(rangeA, Create);
        var exportB = cache.GetAsync(rangeB, Create);
        var pathA = Path.Combine(_root, "a.mp4");
        var pathB = Path.Combine(_root, "b.mp4");
        File.WriteAllBytes(pathA, [1]);
        File.WriteAllBytes(pathB, [2]);

        completionB.SetResult(pathB);
        Assert.AreEqual(pathB, await exportB);
        completionA.SetResult(pathA);
        Assert.AreEqual(pathA, await exportA);
        Assert.AreEqual(pathA, await concurrentA);
        Assert.AreEqual(pathB, await cache.GetAsync(rangeB, Create));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task FailedExportCanBeRetriedForTheSameRange()
    {
        using var cache = new RecordingExportCache();
        var range = new RecordingTrimRange(1, 2);
        await Assert.ThrowsExactlyAsync<IOException>(() => cache.GetAsync(range,
            (_, _) => Task.FromException<string>(new IOException("disk unavailable"))));
        var path = Path.Combine(_root, "retry.mp4");
        File.WriteAllBytes(path, [3]);

        Assert.AreEqual(path, await cache.GetAsync(range, (_, _) => Task.FromResult(path)));
    }

    [TestMethod]
    public async Task ClosingPreviewCancelsExportAndDeletesALateResult()
    {
        var cache = new RecordingExportCache();
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var capturedToken = CancellationToken.None;
        var export = cache.GetAsync(new RecordingTrimRange(1, 2), (_, token) =>
        {
            capturedToken = token;
            return completion.Task;
        });
        cache.Dispose();
        Assert.IsTrue(capturedToken.IsCancellationRequested);
        var path = Path.Combine(_root, "late.mp4");
        File.WriteAllBytes(path, [3]);
        completion.SetResult(path);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => export);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void StopTimeoutAbortsAndJoinsWriterWithoutFinalizingTheContainer()
    {
        var writer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = false;
        var finalized = false;

        Assert.ThrowsExactly<TimeoutException>(() => RecordingStopCoordinator.JoinAndFinalize(
            writer.Task,
            () => { aborted = true; writer.SetResult(); },
            () => finalized = true,
            TimeSpan.Zero, TimeSpan.Zero));

        Assert.IsTrue(aborted);
        Assert.IsTrue(writer.Task.IsCompleted);
        Assert.IsFalse(finalized);
    }

    [TestMethod]
    public void WriterThatCannotBeJoinedNeverHasItsContainerFinalized()
    {
        var writer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abortCount = 0;
        var finalizeCount = 0;
        try
        {
            Assert.ThrowsExactly<TimeoutException>(() => RecordingStopCoordinator.JoinAndFinalize(
                writer.Task, () => abortCount++, () => finalizeCount++, TimeSpan.Zero, TimeSpan.Zero));
            Assert.AreEqual(1, abortCount);
            Assert.AreEqual(0, finalizeCount);
        }
        finally
        {
            writer.SetResult();
        }
    }

    [TestMethod]
    public void JoinedWriterFinalizesNormallyWithoutAborting()
    {
        var finalized = false;
        RecordingStopCoordinator.JoinAndFinalize(Task.CompletedTask,
            () => Assert.Fail("A completed writer must not be aborted."),
            () => finalized = true, TimeSpan.Zero, TimeSpan.Zero);
        Assert.IsTrue(finalized);
    }

    [TestMethod]
    public void SharedStopBoundaryRejectsAudioWhileVideoIsStillFinalizing()
    {
        var signal = new RecordingStopSignal();
        var capture = new FakeWaveIn();
        var path = Path.Combine(_root, "audio.raw");
        using var track = new RecordingAudioTrack(capture, path, "s16le", 48000, 2, signal);
        capture.Emit([1, 2, 3, 4]);
        signal.Request();
        // A callback can still arrive while the encoder is draining its pipe.
        capture.Emit([5, 6, 7, 8]);
        track.Stop();

        Assert.AreEqual(4L, track.BytesWritten);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void AudioFlushFailureStillFinalizesAndPublishesSilentVideoWithRecoveryMaterials()
    {
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "session")).FullName;
        var source = Path.Combine(scratch, "video.mp4");
        var audioPath = Path.Combine(scratch, "audio.raw");
        var destination = Path.Combine(_root, "recording.mp4");
        File.WriteAllBytes(audioPath, [7, 8]);
        var stream = new FlushFailingStream();
        var capture = new FakeWaveIn();
        using var track = new RecordingAudioTrack(capture, audioPath, "s16le", 48000, 2,
            new RecordingStopSignal(), stream);
        var videoFinalized = false;

        var audioError = RecordingStopCoordinator.FinalizeCapture(track.Stop, () =>
        {
            videoFinalized = true;
            File.WriteAllBytes(source, [1, 2, 3, 4]);
        });
        Assert.IsTrue(videoFinalized);
        Assert.IsInstanceOfType<IOException>(audioError);
        var result = RecordingFileStore.Save(source, destination, scratch,
            audioError.Message, preserveRecovery: true);

        Assert.IsTrue(result.Saved);
        Assert.AreEqual(scratch, result.RecoveryDirectory);
        Assert.IsFalse(string.IsNullOrEmpty(result.Warning));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(destination));
        Assert.IsTrue(File.Exists(source));
        Assert.IsTrue(File.Exists(audioPath));
        Assert.IsTrue(stream.WasDisposed);
    }

    [TestMethod]
    public void AudioDisposeReleasesCaptureEvenWhenFlushFails()
    {
        var stream = new FlushFailingStream();
        var capture = new FakeWaveIn();
        var track = new RecordingAudioTrack(capture, "injected.raw", "s16le", 48000, 2,
            new RecordingStopSignal(), stream);

        track.Dispose();
        track.Dispose();

        Assert.AreEqual(1, stream.FlushCalls);
        Assert.IsTrue(stream.WasDisposed);
        Assert.IsTrue(capture.WasDisposed);
    }

    [TestMethod]
    public void AudioServiceStopsAndDisposesEveryTrackAfterOneFlushFailure()
    {
        var firstCapture = new FakeWaveIn { DisposeError = new IOException("device disconnected") };
        var secondCapture = new FakeWaveIn();
        var firstStream = new FlushFailingStream();
        var secondStream = new MemoryStream();
        var signal = new RecordingStopSignal();
        var first = new RecordingAudioTrack(firstCapture, "first.raw", "s16le", 48000, 2, signal, firstStream);
        var second = new RecordingAudioTrack(secondCapture, "second.raw", "s16le", 48000, 2, signal, secondStream);
        using var service = new RecordingAudioService([first, second]);

        Assert.ThrowsExactly<AggregateException>(service.Stop);
        Assert.AreEqual(1, firstCapture.StopCalls);
        Assert.AreEqual(1, secondCapture.StopCalls);
        Assert.IsFalse(secondStream.CanWrite);
        service.Dispose();

        Assert.IsTrue(firstCapture.WasDisposed);
        Assert.IsTrue(secondCapture.WasDisposed);
        Assert.AreEqual(0, service.Tracks.Count);
    }

    [TestMethod]
    public void VideoFinalizationFailureStillPropagatesAfterAnAudioFailure()
    {
        var videoError = new InvalidOperationException("video cannot be finalized");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => RecordingStopCoordinator.FinalizeCapture(
            () => throw new IOException("audio flush failed"),
            () => throw videoError));

        Assert.AreSame(videoError, error);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SystemCursorsDecodeHotspotTransparencyAndInvertMaskAndCompositeVisibly(bool useNv12)
    {
        var region = new PixelRect(100, 100, 96, 96);
        var cursorPos = new PixelPoint(148, 148);

        foreach (var cursorId in new[] { NativeMethods.IdcIBeam, NativeMethods.IdcCross, NativeMethods.IdcArrow })
        {
            var handle = NativeMethods.LoadCursor(0, cursorId);
            Assert.AreNotEqual(0, handle, $"LoadCursor({cursorId}) failed.");
            var bitmap = ScreenRecorder.RenderCursorHandle(handle, cursorPos, region);
            Assert.IsNotNull(bitmap, $"Cursor {cursorId} must decode into a visible CursorBitmap.");
            var cursor = bitmap.Value;

            if (cursorId is NativeMethods.IdcIBeam or NativeMethods.IdcCross)
            {
                Assert.IsTrue(
                    cursor.Bounds.X < 48 || cursor.Bounds.Y < 48,
                    $"Cursor {cursorId} must offset its bounds by its hotspot, got {cursor.Bounds}.");
            }

            var totalPixels = cursor.Bounds.Width * cursor.Bounds.Height;
            var transparentCount = 0;
            for (var i = 0; i < totalPixels; i++)
            {
                var inverted = cursor.InvertMask is not null && cursor.InvertMask[i];
                var alpha = cursor.Pixels[i * 4 + 3];
                if (!inverted && alpha == 0)
                {
                    transparentCount++;
                }
            }
            Assert.IsGreaterThan(totalPixels / 4, transparentCount,
                $"Cursor {cursorId} must preserve transparent background pixels rather than filling an opaque box.");

            foreach (var backgroundLuma in new byte[] { 24, 232 })
            {
                var buffer = new ScreenRecorder.FrameBuffer(region.Width, region.Height, useNv12);
                Array.Fill(buffer.Desktop, backgroundLuma);
                if (useNv12)
                {
                    buffer.Desktop.AsSpan(region.Width * region.Height).Fill(128);
                }
                var clean = buffer.Desktop.ToArray();
                var composited = buffer.GetEncodingFrame(cursor);
                Assert.IsFalse(
                    clean.AsSpan().SequenceEqual(composited),
                    $"Cursor {cursorId} must be visible over background {backgroundLuma} (useNv12={useNv12}).");
            }
        }
    }

    [TestMethod]
    public void TimelineAlignedAudioDropsPreStartSamplesAndBackfillsSilentLoopbackGaps()
    {
        var freq = System.Diagnostics.Stopwatch.Frequency;
        long nowTicks = 1_000_000;
        var timeline = new RecordingTimeline(() => nowTicks);
        var signal = new RecordingStopSignal();
        var waveIn = new FakeWaveIn();
        var output = new MemoryStream();
        const int sampleRate = 48000;
        const int channels = 2;
        const int blockAlign = 4; // s16le stereo

        using var track = new RecordingAudioTrack(
            waveIn, "loopback.raw", "s16le", sampleRate, channels, signal, output, timeline);

        // Pre-start audio before video loop begins must be ignored.
        waveIn.Emit(new byte[480 * blockAlign]);
        Assert.AreEqual(0L, track.BytesWritten);

        // Start timeline: 2.0s of silence, 5.0s paused, then 0.5s more silence = 2.5s active silence.
        timeline.Start();
        nowTicks += 2 * freq;
        timeline.Pause();
        nowTicks += 5 * freq;
        timeline.Resume();
        nowTicks += freq / 2;

        // Emit 10ms (480 frames) of non-zero sound ending at 2.51s.
        nowTicks += freq / 100;
        var activePacket = Enumerable.Repeat((byte)0x7F, 480 * blockAlign).ToArray();
        waveIn.Emit(activePacket);

        var written = output.ToArray();
        const int expectedSilenceBytes = (int)(2.5 * sampleRate) * blockAlign;
        Assert.AreEqual(expectedSilenceBytes + activePacket.Length, written.Length);
        Assert.IsTrue(written.AsSpan(0, expectedSilenceBytes).IndexOfAnyExcept((byte)0) < 0,
            "Backfilled loopback gap must be pure zero PCM.");
        CollectionAssert.AreEqual(activePacket, written[expectedSilenceBytes..]);
    }

    [STATestMethod(UseSTASynchronizationContext = true)]
    public async Task MidRecordingCaptureFaultPreservesSavedSegmentAndSurfacesInterruptionWarning()
    {
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "session")).FullName;
        var source = Path.Combine(scratch, "video.mp4");
        var destination = Path.Combine(_root, "recording.mp4");
        File.WriteAllBytes(source, [10, 20, 30, 40]);
        var stopSignal = new RecordingStopSignal();
        var capture = new FakeRecordingCapture(() => { })
        {
            EncodedDurationSeconds = 1.5,
            Fault = new InvalidOperationException("DXGI_ERROR_ACCESS_LOST")
        };
        using var controller = new RecordingController(
            capture, new PixelRect(0, 0, 64, 64), destination, scratch, audio: null, border: null, stopSignal);

        await controller.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var result = await controller.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(result.Saved);
        Assert.IsTrue(File.Exists(destination));
        Assert.IsNotNull(result.Warning);
        StringAssert.Contains(result.Warning, "提前中断");
        StringAssert.Contains(result.Warning, "DXGI_ERROR_ACCESS_LOST");
    }

    [TestMethod]
    public void MuxArgumentsIncludeFaststartAndExactEncodedDuration()
    {
        var waveIn = new FakeWaveIn();
        var signal = new RecordingStopSignal();
        using var track = new RecordingAudioTrack(waveIn, "sys.raw", "f32le", 48000, 2, signal, new MemoryStream());
        var startInfo = RecordingController.BuildMuxStartInfo(
            "ffmpeg.exe", "video.mp4", [track], 12.5, "muxed.mp4");
        var args = startInfo.ArgumentList.ToArray();

        var movflagsIndex = Array.IndexOf(args, "-movflags");
        Assert.IsGreaterThanOrEqualTo(0, movflagsIndex, "Mux command must include -movflags.");
        Assert.AreEqual("+faststart", args[movflagsIndex + 1]);
        CollectionAssert.Contains(args, "apad");
        CollectionAssert.Contains(args, "12.5");
    }

    [TestMethod]
    public void RecordingTempCleanerRetainsRecentRecoveryAndRemovesExpiredOrphans()
    {
        var cleanRoot = Directory.CreateDirectory(Path.Combine(_root, "CleanerRoot")).FullName;
        var now = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);

        var recentGuid = Directory.CreateDirectory(Path.Combine(cleanRoot, Guid.NewGuid().ToString("N"))).FullName;
        var recentFile = Path.Combine(recentGuid, "video.mp4");
        File.WriteAllBytes(recentFile, [1]);
        File.SetLastWriteTimeUtc(recentFile, now.AddDays(-2));

        var expiredGuid = Directory.CreateDirectory(Path.Combine(cleanRoot, Guid.NewGuid().ToString("N"))).FullName;
        var expiredFile = Path.Combine(expiredGuid, "video.mp4");
        File.WriteAllBytes(expiredFile, [1]);
        File.SetLastWriteTimeUtc(expiredFile, now.AddDays(-15));
        Directory.SetLastWriteTimeUtc(expiredGuid, now.AddDays(-15));

        var nonGuidFolder = Directory.CreateDirectory(Path.Combine(cleanRoot, "user-notes")).FullName;
        Directory.SetLastWriteTimeUtc(nonGuidFolder, now.AddDays(-30));

        var oldTrim = Path.Combine(cleanRoot, "trim-old.mp4");
        File.WriteAllBytes(oldTrim, [2]);
        File.SetLastWriteTimeUtc(oldTrim, now.AddHours(-2));

        var freshTrim = Path.Combine(cleanRoot, "trim-fresh.mp4");
        File.WriteAllBytes(freshTrim, [3]);
        File.SetLastWriteTimeUtc(freshTrim, now.AddMinutes(-10));

        var removed = RecordingTempCleaner.Clean(cleanRoot, now);

        Assert.AreEqual(2, removed);
        Assert.IsTrue(Directory.Exists(recentGuid));
        Assert.IsFalse(Directory.Exists(expiredGuid));
        Assert.IsTrue(Directory.Exists(nonGuidFolder));
        Assert.IsFalse(File.Exists(oldTrim));
        Assert.IsTrue(File.Exists(freshTrim));

        var (scaledW, scaledH) = RecordingMediaInfo.FitDecodeSize(3840, 2160, 960, 540);
        Assert.AreEqual(960, scaledW);
        Assert.AreEqual(540, scaledH);
        Assert.AreEqual((1920, 1080), RecordingMediaInfo.FitDecodeSize(1920, 1080, 1800, 1000));
    }

    private sealed class FakeRecordingCapture(Action finalize) : IRecordingCapture
    {
        public bool IsPaused { get; private set; }
        public Exception? Fault { get; init; }
        public double ElapsedSeconds => EncodedDurationSeconds;
        public double EncodedDurationSeconds { get; init; }
        public int FinalizeCalls { get; private set; }
        public bool WasDisposed { get; private set; }
        public void Pause() => IsPaused = true;
        public void Resume() => IsPaused = false;
        public void RequestStop() { }
        public void StopAndFinalize()
        {
            FinalizeCalls++;
            finalize();
        }
        public void Dispose() => WasDisposed = true;
    }

    private sealed class FlushFailingStream : MemoryStream
    {
        public int FlushCalls { get; private set; }
        public bool WasDisposed { get; private set; }

        public override void Flush()
        {
            FlushCalls++;
            throw new IOException("simulated audio disk flush failure");
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FakeWaveIn : IWaveIn
    {
        public int StopCalls { get; private set; }
        public bool WasDisposed { get; private set; }
        public Exception? DisposeError { get; init; }
        public WaveFormat WaveFormat { get; set; } = new(48000, 16, 2);
        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;
        public void Emit(byte[] data) => DataAvailable?.Invoke(this, new WaveInEventArgs(data, data.Length));
        public void StartRecording() { }
        public void StopRecording()
        {
            StopCalls++;
            RecordingStopped?.Invoke(this, new StoppedEventArgs());
        }
        public void Dispose()
        {
            WasDisposed = true;
            if (DisposeError is not null)
            {
                throw DisposeError;
            }
        }
    }
}
