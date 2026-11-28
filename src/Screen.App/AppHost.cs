using System.Drawing;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Screen.Core;
using Forms = System.Windows.Forms;

namespace Screen.App;

internal sealed class AppHost : IDisposable
{
    private readonly JsonSettingsStore _settingsStore = new();
    private readonly CredentialService _credentialService = new();
    private readonly StartupService _startupService = new();
    private readonly HotKeyWindow _hotKeyWindow;
    private readonly Forms.NotifyIcon _notifyIcon = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly IScreenCaptureService _captureService;
    private readonly SmartOcrEngine _ocrEngine;
    private readonly PinWindowManager _pinWindowManager;
    private readonly CaptureCoordinator _coordinator;
    private readonly WindowSnapService _windowSnapService = new();
    private readonly ITranslationProvider _translationProvider;
    private AppSettings _settings;
    private Icon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private TrayMenuWindow? _trayMenu;
    private readonly Forms.ContextMenuStrip _trayAnchor = new();
    private readonly CancellationTokenSource _lifetime = new();
    private RecordingController? _recordingController;
    private bool _recordingFlowActive;
    private bool _settingsLoadFailed;
    private bool _sessionEventsAttached;
    private bool _exiting;

    public AppHost()
    {
        try
        {
            if (_settingsStore.TryMigrateLegacyDefaultProvider())
            {
                SimpleLogger.LogInfo("Migrated legacy translation settings.");
            }
            _settings = _settingsStore.Load();
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            // A transient lock on settings.json (antivirus, search indexer)
            // must not take the tray app down. Run on in-memory defaults; all
            // later persistence goes through Update(), which reloads from
            // disk, so this snapshot can never overwrite the user's settings.
            _settings = new AppSettings();
            _settingsLoadFailed = true;
            SimpleLogger.LogWarning($"Settings load failed, using defaults: {exception.Message}");
        }
        _hotKeyWindow = new HotKeyWindow();
        _captureService = new HybridScreenCaptureService(deferPrimaryUntilWarmed: true);
        _pinWindowManager = new PinWindowManager();
        _pinWindowManager.ClickThroughEnabled += (_, _) => ShowFirstClickThroughHint();
        _ocrEngine = new SmartOcrEngine(
            new PaddleOcrEngine(),
            new TencentCloudOcrEngine(_httpClient),
            _settingsStore,
            _credentialService);
        _coordinator = new CaptureCoordinator(
            _captureService,
            _windowSnapService,
            _pinWindowManager,
            _ocrEngine,
            _translationProvider = new OnlineTranslationProvider(_httpClient),
            _credentialService,
            _settingsStore);
    }

    public void Start()
    {
        _ = Task.Run(() =>
        {
            try
            {
                RecordingTempCleaner.Clean(RecordingTempCleaner.DefaultRoot, DateTime.UtcNow);
            }
            catch (Exception exception)
            {
                SimpleLogger.LogWarning($"Recording temp cleanup skipped: {exception.Message}");
            }
        });
        _trayIcon = ScreenIconFactory.CreateTrayIcon();
        _notifyIcon.Icon = _trayIcon;
        _notifyIcon.Text = $"Flick — {_settings.HotKey} 截图";
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += async (_, _) =>
        {
            if (_pinWindowManager.HasClickThroughWindows)
            {
                RecoverClickThrough(showConfirmation: true);
                return;
            }
            if (!RecordingActive())
            {
                await _coordinator.StartCaptureAsync("tray-double-click");
            }
        };

        _hotKeyWindow.CaptureRequested += (_, _) =>
        {
            if (!RecordingActive())
            {
                _ = _coordinator.StartCaptureAsync("hotkey");
            }
        };
        _hotKeyWindow.ClickThroughRecoveryRequested += (_, _) => RecoverClickThrough(showConfirmation: true);
        _hotKeyWindow.RecordRequested += async (_, _) => await StartRecordingSelectionAsync();
        var shortcutsRegistered = RegisterConfiguredShortcuts(showBalloonOnFailure: false);
        if (!shortcutsRegistered)
        {
            _ = RetryInitialShortcutRegistrationAsync(_lifetime.Token);
        }
        if (!_settingsLoadFailed)
        {
            var startWithWindows = _settings.StartWithWindows;
            _ = Task.Run(() =>
            {
                if (!_exiting)
                {
                    _startupService.SyncWithSettings(startWithWindows);
                }
            });
        }
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _sessionEventsAttached = true;
        _ = WarmUpAfterStartupAsync(_lifetime.Token);
        _notifyIcon.Text = $"Flick — {_settings.HotKey} 截图";
        // WinForms delivers shell menu requests (including keyboard activation) through Opening.
        _trayAnchor.Opening += (_, e) =>
        {
            e.Cancel = true;
            Application.Current.Dispatcher.BeginInvoke(new Action(ShowTrayMenu));
        };
        _notifyIcon.ContextMenuStrip = _trayAnchor;

        if (_settingsLoadFailed)
        {
            _notifyIcon.ShowBalloonTip(
                6000,
                "Flick 设置加载失败",
                "无法读取配置文件，已使用默认设置。您的更改将正常保存。",
                Forms.ToolTipIcon.Warning);
        }
        else if (Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, "--restarted", StringComparison.OrdinalIgnoreCase)))
        {
            _notifyIcon.ShowBalloonTip(
                4000,
                "Flick 已更新",
                "新版本已启动，快捷键即可使用。",
                Forms.ToolTipIcon.Info);
        }
    }

    public void RequestCapture()
    {
        if (!RecordingActive())
        {
            _ = _coordinator.StartCaptureAsync("single-instance");
        }
    }

    private bool RecordingActive()
    {
        if (_exiting)
        {
            return true;
        }
        if (!_recordingFlowActive)
        {
            return false;
        }
        // Tray balloons are hidden behind fullscreen overlays; toast on screen.
        HintToastWindow.Show("录屏流程进行中，请先完成或取消录屏");
        return true;
    }

    public Task StartRecordingSelectionAsync() => RunRecordingAsync(null);

    public Task StartRecordingWithRegionAsync(RecordingSelection selection, RecordingBorderWindow persistentBorder) =>
        RunRecordingAsync(selection, persistentBorder);

    private async Task RunRecordingAsync(RecordingSelection? selection, RecordingBorderWindow? persistentBorder = null)
    {
        if (_exiting)
        {
            persistentBorder?.Close();
            return;
        }
        if (_recordingFlowActive)
        {
            persistentBorder?.Close();
            HintToastWindow.Show("已有录制正在进行");
            return;
        }
        if (selection is null && _coordinator.IsCapturing)
        {
            HintToastWindow.Show("截图进行中，请先完成或取消截图再录屏");
            return;
        }

        // Reserve the workflow before capturing the selection background.
        _recordingFlowActive = true;
        CaptureOverlayWindow? overlay = null;
        RecordingSaveResult result;
        string filePath;
        try
        {
            if (selection is null)
            {
                NativeMethods.TryGetPhysicalCursorPosition(out var cursor);
                var frame = DesktopRegionCapture.GetMonitorBounds().Length > 1
                    ? await GdiScreenCaptureService.CaptureDesktopAsync()
                    : await _captureService.CaptureAtAsync(new PixelPoint(cursor.X, cursor.Y));
                overlay = new CaptureOverlayWindow(
                    frame,
                    _captureService,
                    _windowSnapService,
                    _pinWindowManager,
                    _ocrEngine,
                    _translationProvider,
                    _credentialService,
                    _settingsStore,
                    recordingMode: true);
                var confirmation = new TaskCompletionSource<RecordingSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
                overlay.RecordingRegionConfirmed += (_, selected) => confirmation.TrySetResult(selected);
                overlay.Closed += (_, _) =>
                {
                    overlay = null;
                    confirmation.TrySetResult(null);
                };
                overlay.Show();
                overlay?.Activate();
                selection = await confirmation.Task;
                if (selection is null || overlay is null)
                {
                    return;
                }
                persistentBorder = new RecordingBorderWindow(selection.Region);
                persistentBorder.Show();
                overlay.Close();
            }

            // Let the selection overlay disappear while the border stays visible.
            await Task.Delay(200);
            var countdown = new RecordingCountdownWindow(selection.Region);
            countdown.Show();
            if (!await countdown.Completion)
            {
                return;
            }

            // Release the DXGI session used for selection before recording.
            _captureService.ReleaseSessions();
            using var controller = RecordingController.Create(_settingsStore, selection.Region, selection, persistentBorder);
            persistentBorder = null; // The controller now owns the border.
            _recordingController = controller;
            controller.ShowControls();
            result = await controller.Completion;
            filePath = controller.FilePath;
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Unable to start recording", exception);
            MessageBox.Show($"无法开始录屏：{exception.Message}", "Flick", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            _recordingController = null;
            _recordingFlowActive = false;
            overlay?.Close();
            persistentBorder?.Close();
        }

        if (_exiting)
        {
            result.ReleasePreviewSource();
            return;
        }
        if (GetRecordingSaveNotice(result) is { } notice)
        {
            if (!result.Saved)
            {
                MessageBox.Show(notice, "Flick", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            HintToastWindow.Show(notice);
        }
        try
        {
            var preview = new RecordingPreviewWindow(result.TemporaryPreviewSource ?? filePath, selection.Format, filePath);
            preview.Closed += (_, _) => result.ReleasePreviewSource();
            WindowActivation.ShowTopmost(preview);
        }
        catch
        {
            // A preview failure must never swallow the finished file.
            result.ReleasePreviewSource();
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            }
            catch
            {
            }
        }
        var interrupted = result.Warning?.Contains("提前中断", StringComparison.Ordinal) == true;
        _notifyIcon.ShowBalloonTip(
            5000,
            interrupted ? "Flick 录制提前中断（已保存片段）" : "Flick 录制已保存",
            Path.GetFileName(filePath),
            interrupted ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    private void ShowTrayMenu()
    {
        if (_exiting || _trayMenu is { IsVisible: true })
            return;
        var menu = new TrayMenuWindow();
        menu.AddItem("截图", _settings.HotKey, "rect", async () =>
        {
            if (!RecordingActive())
                await _coordinator.StartCaptureAsync("tray-menu");
        }, capturesDesktop: true);
        menu.AddItem("录屏", _settings.RecordHotKey, "video", StartRecordingSelectionAsync, capturesDesktop: true);
        menu.AddItem("解除全部贴图穿透", _settings.ClickThroughRecoveryHotKey, "cursor", () =>
        {
            RecoverClickThrough(showConfirmation: false);
            return Task.CompletedTask;
        });
        menu.AddItem("关闭全部贴图", "", "pin", () =>
        {
            _pinWindowManager.CloseAll();
            return Task.CompletedTask;
        });
        menu.AddSeparator();
        menu.AddItem("设置", "", "settings", () =>
        {
            OpenSettings();
            return Task.CompletedTask;
        });
        menu.AddSeparator();
        menu.AddItem("退出", "", "close", ExitAsync);
        _trayMenu = menu;
        menu.Closed += (_, _) => _trayMenu = null;
        NativeMethods.TryGetPhysicalCursorPosition(out var anchor);
        menu.OpenAt(anchor);
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        if (_recordingController is { } active)
        {
            try
            {
                HintToastWindow.Show("正在保存当前录屏并退出…");
            }
            catch (Exception exception)
            {
                SimpleLogger.LogError("Unable to show the recording exit hint", exception);
            }
            try
            {
                var result = await active.StopAsync();
                if (GetRecordingSaveNotice(result) is { } notice)
                {
                    MessageBox.Show(notice, "Flick", MessageBoxButton.OK,
                        result.Saved ? MessageBoxImage.Warning : MessageBoxImage.Error);
                }
            }
            catch (Exception exception)
            {
                SimpleLogger.LogError("Failed to finalize recording during exit", exception);
                MessageBox.Show($"退出时无法完成录屏保存：{exception.Message}",
                    "Flick", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        Application.Current?.Shutdown();
    }

    internal static string? GetRecordingSaveNotice(RecordingSaveResult result)
    {
        var notice = result.Saved ? result.Warning : $"录屏失败：{result.Error}";
        return result.RecoveryDirectory is { } directory
            ? $"{notice}\n录制素材已保留，可从以下位置恢复：\n{directory}"
            : notice;
    }

    private void OpenSettings()
    {
        if (_exiting)
        {
            return;
        }
        if (_settingsWindow is { } existing)
        {
            existing.Activate();
            return;
        }
        var window = new SettingsWindow(
            _settingsStore,
            _credentialService,
            _startupService.SetEnabled,
            // Live registration: new hotkeys take effect the moment they are
            // captured in the dialog, and occupied combinations fail loudly
            // right there instead of being silently reverted on close.
            hotKey => _hotKeyWindow.RegisterCaptureHotKey(hotKey),
            hotKey => _hotKeyWindow.RegisterClickThroughRecoveryHotKey(hotKey),
            hotKey => _hotKeyWindow.RegisterRecordHotKey(hotKey),
            _hotKeyWindow.TrySetPriority);
        _settingsWindow = window;
        _hotKeyWindow.TryCaptureInEditor = window.TryCaptureRegisteredHotKey;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            _hotKeyWindow.TryCaptureInEditor = null;
            _settingsWindow = null;
        }
        if (_exiting)
        {
            return;
        }

        // Settings are auto-saved while the window is open, so always reload and
        // re-register hot keys after the window closes.
        _settings = _settingsStore.Load();
        RegisterConfiguredShortcuts();
        _notifyIcon.Text = $"Flick — {_settings.HotKey} 截图";
    }

    private bool RegisterConfiguredShortcuts(bool force = false, bool showBalloonOnFailure = true)
    {
        var allRegistered = true;
        if (!_hotKeyWindow.TrySetPriority(_settings.PreferScreenHotKeys))
        {
            allRegistered = false;
            SimpleLogger.LogWarning($"Unable to enable priority hotkey mode (win32 error: {_hotKeyWindow.LastNativeError}).");
            if (showBalloonOnFailure)
            {
                _notifyIcon.ShowBalloonTip(5000, "Flick 快捷键", "无法启用优先模式，已使用普通快捷键注册。", Forms.ToolTipIcon.Warning);
            }
        }
        void RefreshShortcut(string description, string key, Func<string, bool, bool> register)
        {
            if (!register(key, force))
            {
                allRegistered = false;
                if (showBalloonOnFailure)
                {
                    _notifyIcon.ShowBalloonTip(5000, "Flick 快捷键冲突",
                        $"{description}快捷键 {key} 无法注册，仍可使用托盘菜单。", Forms.ToolTipIcon.Warning);
                }
                SimpleLogger.LogWarning(
                    $"{description} shortcut could not be registered: {key} (win32 error: {_hotKeyWindow.LastNativeError})");
            }
        }
        RefreshShortcut("截图", _settings.HotKey, _hotKeyWindow.RegisterCaptureHotKey);
        RefreshShortcut("恢复贴图", _settings.ClickThroughRecoveryHotKey, _hotKeyWindow.RegisterClickThroughRecoveryHotKey);
        RefreshShortcut("录屏", _settings.RecordHotKey, _hotKeyWindow.RegisterRecordHotKey);
        if (allRegistered)
        {
            SimpleLogger.LogInfo(
                $"Shortcuts ready (capture: {_settings.HotKey}, recovery: {_settings.ClickThroughRecoveryHotKey}, " +
                $"record: {_settings.RecordHotKey}, priority: {_settings.PreferScreenHotKeys})");
        }
        return allRegistered;
    }

    private async Task RetryInitialShortcutRegistrationAsync(CancellationToken cancellationToken)
    {
        int[] delaysMs = [2000, 5000, 10000];
        for (var attempt = 0; attempt < delaysMs.Length; attempt++)
        {
            try
            {
                await Task.Delay(delaysMs[attempt], cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_exiting || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var isFinalAttempt = attempt == delaysMs.Length - 1;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            var succeeded = await dispatcher.InvokeAsync(() =>
                _exiting || _settingsWindow is not null || RegisterConfiguredShortcuts(force: false, showBalloonOnFailure: isFinalAttempt));
            if (succeeded)
            {
                SimpleLogger.LogInfo($"Shortcuts recovered on startup retry #{attempt + 1}.");
                return;
            }
        }
    }

    private async Task WarmUpAfterStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Yield briefly so App.OnStartup finishes signaling ready and enters the message loop.
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            if (_exiting || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Stage 0 (instant, signed system assemblies only): warm GDI+ and the real
            // CaptureOverlayWindow immediately so an F4 press right after Windows boot
            // renders in ~100 ms without waiting for unsigned DXGI/Vortice cloud verification.
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = Forms.Screen.FromPoint(new System.Drawing.Point(0, 0));
                using var bitmap = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Black);
            }, cancellationToken).ConfigureAwait(false);
            if (_exiting || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            {
                _ = dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() =>
                    {
                        if (!_exiting && !cancellationToken.IsCancellationRequested)
                        {
                            _coordinator.WarmUpOverlayPipeline();
                        }
                    }));
            }

            // Stage 1: warm OS/AV image caches for unsigned DXGI/Vortice assemblies in an
            // isolated child process so LdrpLoaderLock never stalls the main UI/hotkey thread,
            // then enable the live DXGI primary capture path.
            await AssemblyPreloadWorker.RunStageAsync(
                AssemblyPreloadWorker.CaptureStage,
                "DXGI capture",
                cancellationToken).ConfigureAwait(false);
            if (_exiting || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await _captureService.WarmUpAsync(cancellationToken).ConfigureAwait(false);
            if (_exiting || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Stage 2: warm OS/AV image caches for OCR & Settings icon assemblies in an isolated
            // child process without loading the ~400 MB ONNX models or stalling F4.
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            if (_exiting || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await AssemblyPreloadWorker.RunStageAsync(
                AssemblyPreloadWorker.OcrStage,
                "OCR",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SimpleLogger.LogWarning($"Startup warm-up skipped: {exception.Message}");
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (_exiting || e.Reason is not (SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect))
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(() =>
        {
            if (_exiting)
            {
                return;
            }
            _captureService.ReleaseSessions();
            _hotKeyWindow.RefreshHookIfNeeded();
            RegisterConfiguredShortcuts(force: true, showBalloonOnFailure: false);
            _ = _captureService.WarmUpAsync(_lifetime.Token);
        }));
    }

    private void ShowFirstClickThroughHint()
    {
        if (_settings.HasShownClickThroughHint)
        {
            return;
        }
        _settings.HasShownClickThroughHint = true;
        _settingsStore.Update(latest => latest.HasShownClickThroughHint = true);
        _notifyIcon.ShowBalloonTip(
            7000,
            "贴图已开启鼠标穿透",
            $"按 {_settings.ClickThroughRecoveryHotKey} 可立即恢复贴图操作；也可以双击或右键 Flick 托盘图标恢复。此提示只显示一次。",
            Forms.ToolTipIcon.Info);
    }

    private void RecoverClickThrough(bool showConfirmation)
    {
        if (!_pinWindowManager.HasClickThroughWindows)
        {
            return;
        }
        _pinWindowManager.DisableClickThroughForAll();
        if (showConfirmation)
        {
            _notifyIcon.ShowBalloonTip(2200, "Flick", "已恢复全部贴图的鼠标操作。", Forms.ToolTipIcon.Info);
        }
    }

    public void Dispose()
    {
        _exiting = true;
        _lifetime.Cancel();
        if (_sessionEventsAttached)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _sessionEventsAttached = false;
        }
        _recordingController?.Dispose();
        _recordingController = null;
        _trayMenu?.Close();
        _notifyIcon.ContextMenuStrip = null;
        _trayAnchor.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon?.Dispose();
        _hotKeyWindow.Dispose();
        _pinWindowManager.CloseAll();
        _captureService.Dispose();
        _ocrEngine.Dispose();
        _httpClient.Dispose();
        _lifetime.Dispose();
    }
}
