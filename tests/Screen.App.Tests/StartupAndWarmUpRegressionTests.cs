using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class StartupAndWarmUpRegressionTests
{
    [TestMethod]
    public void StartupEnableRegistersParallelLogonTaskAndRemovesLegacyRunEntry()
    {
        var backend = new FakeStartupBackend
        {
            RegistryRunValue = "\"E:\\Screen\\Screen.exe\" --startup"
        };
        var service = new StartupService(backend, () => @"E:\Screen\Screen.exe");

        service.SetEnabled(true);

        Assert.IsNotNull(backend.Task);
        Assert.IsTrue(backend.Task.Enabled);
        Assert.AreEqual(@"E:\Screen\Screen.exe", backend.Task.ExecutablePath);
        Assert.AreEqual("--startup", backend.Task.Arguments);
        Assert.IsNull(backend.RegistryRunValue);
    }

    [TestMethod]
    public void StartupEnableFallsBackToRunKeyWhenTaskSchedulerFails()
    {
        var backend = new FakeStartupBackend { FailTaskRegistration = true };
        var service = new StartupService(backend, () => @"E:\Screen\Screen.exe");

        service.SetEnabled(true);

        Assert.IsNull(backend.Task);
        Assert.AreEqual("\"E:\\Screen\\Screen.exe\" --startup", backend.RegistryRunValue);
    }

    [TestMethod]
    public void StartupDisableRemovesBothScheduledTaskAndRunKey()
    {
        var backend = new FakeStartupBackend
        {
            Task = new StartupService.ScheduledStartupTaskInfo(true, @"E:\Screen\Screen.exe", "--startup"),
            RegistryRunValue = "\"E:\\Screen\\Screen.exe\" --startup"
        };
        var service = new StartupService(backend, () => @"E:\Screen\Screen.exe");

        service.SetEnabled(false);

        Assert.IsNull(backend.Task);
        Assert.IsNull(backend.RegistryRunValue);
    }

    [TestMethod]
    public void SyncMigratesLegacyRunKeyAndRefreshesStaleTaskPath()
    {
        var backend = new FakeStartupBackend
        {
            RegistryRunValue = "\"E:\\Screen\\Screen.exe\" --startup"
        };
        var executable = @"E:\Screen\Screen.exe";
        var service = new StartupService(backend, () => executable);

        service.SyncWithSettings(startWithWindows: true);
        Assert.AreEqual(1, backend.RegisterTaskCalls);
        Assert.IsNull(backend.RegistryRunValue);
        Assert.AreEqual(@"E:\Screen\Screen.exe", backend.Task?.ExecutablePath);

        // Already migrated and matching: should be a no-op.
        service.SyncWithSettings(startWithWindows: true);
        Assert.AreEqual(1, backend.RegisterTaskCalls);

        // App moved to a new directory: should refresh the scheduled task path.
        executable = @"D:\Apps\Flick\Screen.exe";
        service.SyncWithSettings(startWithWindows: true);
        Assert.AreEqual(2, backend.RegisterTaskCalls);
        Assert.AreEqual(@"D:\Apps\Flick\Screen.exe", backend.Task?.ExecutablePath);
        Assert.AreEqual(StartupService.NormalTaskPriority, backend.Task?.Priority);

        // Existing task with degraded priority (> NormalTaskPriority) should be upgraded.
        backend.Task = new StartupService.ScheduledStartupTaskInfo(true, executable, "--startup", Priority: 7);
        service.SyncWithSettings(startWithWindows: true);
        Assert.AreEqual(3, backend.RegisterTaskCalls);
        Assert.AreEqual(StartupService.NormalTaskPriority, backend.Task?.Priority);
    }

    [TestMethod]
    public void SyncRespectsUserDisableInTaskManagerOrTaskScheduler()
    {
        var disabledInTaskManager = new FakeStartupBackend
        {
            RegistryRunValue = "\"E:\\Screen\\Screen.exe\" --startup",
            RegistryRunDisabledByUser = true
        };
        new StartupService(disabledInTaskManager, () => @"E:\Screen\Screen.exe")
            .SyncWithSettings(startWithWindows: true);
        Assert.AreEqual(0, disabledInTaskManager.RegisterTaskCalls);
        Assert.IsNotNull(disabledInTaskManager.RegistryRunValue);

        var disabledInScheduler = new FakeStartupBackend
        {
            Task = new StartupService.ScheduledStartupTaskInfo(false, @"E:\Screen\Screen.exe", "--startup")
        };
        new StartupService(disabledInScheduler, () => @"E:\Screen\Screen.exe")
            .SyncWithSettings(startWithWindows: true);
        Assert.AreEqual(0, disabledInScheduler.RegisterTaskCalls);
        Assert.IsFalse(disabledInScheduler.Task.Enabled);
    }

    [TestMethod]
    public void RunCommandAndStartupApprovedHelpersParseExpectedFormats()
    {
        Assert.IsTrue(StartupService.IsScreenRunCommand("\"E:\\Screen\\Screen.exe\" --startup"));
        Assert.IsTrue(StartupService.IsScreenRunCommand(@"C:\Program Files\Flick\Screen.exe --startup"));
        Assert.IsFalse(StartupService.IsScreenRunCommand("\"E:\\Other\\App.exe\" --startup"));
        Assert.IsFalse(StartupService.IsScreenRunCommand(null));

        Assert.IsFalse(StartupService.IsStartupApprovedDisabled(null));
        Assert.IsFalse(StartupService.IsStartupApprovedDisabled([0x02, 0x00, 0x00, 0x00]));
        Assert.IsTrue(StartupService.IsStartupApprovedDisabled([0x03, 0x00, 0x00, 0x00]));
    }

    [TestMethod]
    public async Task HybridCaptureWarmUpRunsOnceAndRecoversAfterCancellation()
    {
        var warmUpCalls = 0;
        using var service = new HybridScreenCaptureService(
            new DummyCaptureService(),
            new DummyCaptureService(),
            static () => { },
            Timeout.InfiniteTimeSpan,
            token =>
            {
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref warmUpCalls);
                return true;
            });

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => service.WarmUpAsync(cancelled.Token));
        Assert.AreEqual(0, warmUpCalls);

        await Task.WhenAll(service.WarmUpAsync(), service.WarmUpAsync());
        Assert.AreEqual(1, warmUpCalls);
    }

    [TestMethod]
    public async Task HybridCaptureWarmUpRetriesAfterDeferredSessionAndSchedulesIdleRelease()
    {
        var attempts = 0;
        var released = new ManualResetEventSlim();
        using var service = new HybridScreenCaptureService(
            new DummyCaptureService(),
            new DummyCaptureService(),
            () => released.Set(),
            TimeSpan.FromMilliseconds(40),
            _ => Interlocked.Increment(ref attempts) >= 2);

        // First call simulates lock screen (DXGI session deferred -> returns false).
        await service.WarmUpAsync();
        Assert.AreEqual(1, attempts);
        Assert.IsFalse(released.Wait(TimeSpan.FromMilliseconds(80)));

        // Second call (e.g. after SessionUnlock) establishes live DXGI session -> schedules idle release.
        await service.WarmUpAsync();
        Assert.AreEqual(2, attempts);
        Assert.IsTrue(released.Wait(TimeSpan.FromSeconds(2)));

        // Already warmed up -> third call is a no-op.
        await service.WarmUpAsync();
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task HybridCaptureWarmUpSwallowsNonCancellationErrorsAndSkipsWhenDisposed()
    {
        var calls = 0;
        var service = new HybridScreenCaptureService(
            new DummyCaptureService(),
            new DummyCaptureService(),
            static () => { },
            Timeout.InfiniteTimeSpan,
            _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("Simulated GPU init failure");
            });

        await service.WarmUpAsync();
        Assert.AreEqual(1, calls);

        service.Dispose();
        await service.WarmUpAsync();
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void OverlayWarmUpCompletesWithoutLeavingCaptureActive() => OnSta(() =>
    {
        var coordinator = new CaptureCoordinator(
            new DummyCaptureService(),
            new WindowSnapService(),
            new PinWindowManager(),
            new DummyOcrEngine(),
            new DummyTranslationProvider(),
            new CredentialService(),
            new JsonSettingsStore());

        coordinator.WarmUpOverlayPipeline();
        var drained = Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Assert.AreEqual(DispatcherOperationStatus.Completed, drained.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(coordinator.IsCapturing);

        // Calling a second time is an idempotent no-op.
        coordinator.WarmUpOverlayPipeline();
        Assert.IsFalse(coordinator.IsCapturing);
    });

    [TestMethod]
    public async Task SmartOcrEngineWarmUpDelegatesToLocalEngineAndPaddleWarmUpDegradesGracefully()
    {
        var warmed = 0;
        using var http = new System.Net.Http.HttpClient();
        using var smart = new SmartOcrEngine(
            new CountingOcrEngine(() => Interlocked.Increment(ref warmed)),
            new TencentCloudOcrEngine(http),
            new JsonSettingsStore(),
            new CredentialService());

        await smart.WarmUpAsync();
        Assert.AreEqual(1, warmed);

        var missingRoot = Path.Combine(Path.GetTempPath(), $"flick-missing-ocr-{Guid.NewGuid():N}");
        using var paddle = new PaddleOcrEngine(Timeout.InfiniteTimeSpan, missingRoot);
        await paddle.WarmUpAsync();
        Assert.IsFalse(paddle.IsEngineLoaded);

        paddle.Dispose();
        await paddle.WarmUpAsync();
        Assert.IsFalse(paddle.IsEngineLoaded);
    }

    [TestMethod]
    public async Task HybridCaptureUsesFallbackUntilPrimaryWarmUpCompletes()
    {
        var primaryCalls = 0;
        var fallbackCalls = 0;
        var frame = new CaptureFrame(
            [255, 255, 255, 255],
            1,
            1,
            4,
            96,
            96,
            new PixelRect(0, 0, 1, 1),
            CaptureColorSpace.Srgb,
            DateTimeOffset.UtcNow);
        using var service = new HybridScreenCaptureService(
            new StubCaptureService(() => { Interlocked.Increment(ref primaryCalls); return frame; }),
            new StubCaptureService(() => { Interlocked.Increment(ref fallbackCalls); return frame; }),
            static () => { },
            Timeout.InfiniteTimeSpan,
            warmUpPrimary: _ => true,
            deferPrimaryUntilWarmed: true);

        // Before WarmUpAsync completes, CaptureAtAsync must use the fast GDI fallback
        // without touching the primary DXGI service.
        _ = await service.CaptureAtAsync(new PixelPoint(0, 0));
        Assert.AreEqual(0, primaryCalls);
        Assert.AreEqual(1, fallbackCalls);

        await service.WarmUpAsync();

        // After WarmUpAsync completes, CaptureAtAsync switches to the primary DXGI service.
        _ = await service.CaptureAtAsync(new PixelPoint(0, 0));
        Assert.AreEqual(1, primaryCalls);
        Assert.AreEqual(1, fallbackCalls);
    }

    [TestMethod]
    public void AssemblyPreloadWorkerParsesStagesAndListsExpectedAssemblies()
    {
        Assert.IsFalse(AssemblyPreloadWorker.TryParseStage(["--startup"], out _));
        Assert.IsTrue(AssemblyPreloadWorker.TryParseStage(["--preload-assemblies", "capture"], out var captureStage));
        Assert.AreEqual("capture", captureStage);
        Assert.IsTrue(AssemblyPreloadWorker.TryParseStage(["--preload-assemblies", "ocr"], out var ocrStage));
        Assert.AreEqual("ocr", ocrStage);

        var captureList = AssemblyPreloadWorker.GetAssembliesForStage(captureStage);
        CollectionAssert.Contains(captureList.ToList(), "Vortice.DXGI");
        CollectionAssert.Contains(captureList.ToList(), "Vortice.Mathematics");

        var ocrList = AssemblyPreloadWorker.GetAssembliesForStage(ocrStage);
        CollectionAssert.Contains(ocrList.ToList(), "RapidOcrNet");
        CollectionAssert.Contains(ocrList.ToList(), "Microsoft.ML.OnnxRuntime");
        CollectionAssert.Contains(ocrList.ToList(), "SkiaSharp");
        CollectionAssert.Contains(ocrList.ToList(), "Clipper2Lib");
        CollectionAssert.Contains(ocrList.ToList(), "System.Numerics.Tensors");
        CollectionAssert.Contains(ocrList.ToList(), "Lucide.NET.Core");

        Assert.AreEqual(0, AssemblyPreloadWorker.GetAssembliesForStage("unknown").Count);
    }

    private static void OnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private sealed class FakeStartupBackend : StartupService.IStartupBackend
    {
        public StartupService.ScheduledStartupTaskInfo? Task { get; set; }
        public string? RegistryRunValue { get; set; }
        public bool RegistryRunDisabledByUser { get; set; }
        public bool FailTaskRegistration { get; set; }
        public int RegisterTaskCalls { get; private set; }

        public StartupService.ScheduledStartupTaskInfo? GetLogonTask() => Task;

        public void RegisterLogonTask(string executablePath, string arguments)
        {
            if (FailTaskRegistration)
            {
                throw new UnauthorizedAccessException("Task Scheduler unavailable");
            }
            RegisterTaskCalls++;
            Task = new StartupService.ScheduledStartupTaskInfo(true, executablePath, arguments);
        }

        public void DeleteLogonTask() => Task = null;

        public bool HasScreenRegistryRunValue() => StartupService.IsScreenRunCommand(RegistryRunValue);

        public bool IsRegistryRunDisabledByUser() => RegistryRunDisabledByUser;

        public void SetRegistryRunValue(string command) => RegistryRunValue = command;

        public void DeleteRegistryRunValue() => RegistryRunValue = null;

        public void DeleteRegistryRunValueIfPointsToScreen()
        {
            if (StartupService.IsScreenRunCommand(RegistryRunValue))
            {
                RegistryRunValue = null;
            }
        }
    }

    private sealed class DummyCaptureService : IScreenCaptureService
    {
        public Task<CaptureFrame> CaptureAtAsync(PixelPoint screenPoint, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void ReleaseSessions()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubCaptureService(Func<CaptureFrame> capture) : IScreenCaptureService
    {
        public Task<CaptureFrame> CaptureAtAsync(PixelPoint screenPoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(capture());

        public void ReleaseSessions()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class DummyOcrEngine : IOcrEngine
    {
        public IReadOnlyList<string> AvailableLanguages => [];

        public bool IsLanguageAvailable(string languageTag) => false;

        public Task<OcrDocument> RecognizeAsync(PixelBuffer image, string languageTag, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CountingOcrEngine(Action onWarmUp) : IOcrEngine
    {
        public IReadOnlyList<string> AvailableLanguages => [];

        public bool IsLanguageAvailable(string languageTag) => true;

        public Task<OcrDocument> RecognizeAsync(PixelBuffer image, string languageTag, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task WarmUpAsync(CancellationToken cancellationToken = default)
        {
            onWarmUp();
            return Task.CompletedTask;
        }
    }

    private sealed class DummyTranslationProvider : ITranslationProvider
    {
        public Task<TranslationResult> TranslateAsync(
            string text,
            string sourceLanguage,
            TranslationProfile profile,
            TranslationCredentials credentials,
            IProgress<string>? streamingText = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
