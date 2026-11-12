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

    private sealed class FakeRecordingCapture(Action finalize) : IRecordingCapture
    {
        public bool IsPaused { get; private set; }
        public Exception? Fault => null;
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
