using System.Drawing;
using System.Net.Http;
using System.Windows;
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
    private RecordingController? _recordingController;
    private bool _recordingFlowActive;
    private bool _settingsLoadFailed;
    private bool _exiting;

    public AppHost()
    {
        try
        {
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
        _captureService = new HybridScreenCaptureService();
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
        _notifyIcon.Text = $"Screen — {_settings.HotKey} 截图";
        _notifyIcon.Visible = true;
        _notifyIcon.DoubleClick += (_, _) =>
        {
            if (_pinWindowManager.HasClickThroughWindows)
            {
                RecoverClickThrough(showConfirmation: true);
                return;
            }
            if (!RecordingActive())
            {
                _ = _coordinator.StartCaptureAsync();
            }
        };

        _hotKeyWindow.CaptureRequested += (_, _) =>
        {
            if (!RecordingActive())
            {
                _ = _coordinator.StartCaptureAsync();
            }
        };
        _hotKeyWindow.ClickThroughRecoveryRequested += (_, _) => RecoverClickThrough(showConfirmation: true);
        _hotKeyWindow.RecordRequested += (_, _) => _ = StartRecordingSelectionAsync();
        if (!_hotKeyWindow.TrySetPriority(_settings.PreferScreenHotKeys))
        {
            _notifyIcon.ShowBalloonTip(5000, "Screen 快捷键", "无法启用优先模式，已使用普通快捷键注册。", Forms.ToolTipIcon.Warning);
        }
        if (!_hotKeyWindow.RegisterRecordHotKey(_settings.RecordHotKey))
        {
            _notifyIcon.ShowBalloonTip(
                5000,
                "Screen 快捷键冲突",
                $"录屏快捷键 {_settings.RecordHotKey} 已被其他程序占用，可在设置中重新指定。",
                Forms.ToolTipIcon.Warning);
        }
        if (!_hotKeyWindow.RegisterCaptureHotKey(_settings.HotKey))
        {
            var failed = _settings.HotKey;
            _settings.HotKey = AppSettings.DefaultHotKey;
            _settingsStore.Update(latest => latest.HotKey = _settings.HotKey);
            _hotKeyWindow.RegisterCaptureHotKey(_settings.HotKey);
            _notifyIcon.ShowBalloonTip(5000, "Screen 快捷键冲突", $"{failed} 已被其他程序占用，已回退为 {_settings.HotKey}。", Forms.ToolTipIcon.Warning);
        }
        if (!_hotKeyWindow.RegisterClickThroughRecoveryHotKey(_settings.ClickThroughRecoveryHotKey))
        {
            var failed = _settings.ClickThroughRecoveryHotKey;
            var fallback = string.Equals(
                failed,
                AppSettings.DefaultClickThroughRecoveryHotKey,
                StringComparison.OrdinalIgnoreCase)
                ? "Ctrl+Shift+F1"
                : AppSettings.DefaultClickThroughRecoveryHotKey;
            _settings.ClickThroughRecoveryHotKey = fallback;
            _settingsStore.Update(latest => latest.ClickThroughRecoveryHotKey = fallback);
            var fallbackRegistered = _hotKeyWindow.RegisterClickThroughRecoveryHotKey(fallback);
            _notifyIcon.ShowBalloonTip(5000, "Screen 快捷键冲突", fallbackRegistered
                ? $"恢复贴图穿透快捷键 {failed} 已被占用，已回退为 {fallback}。"
                : $"{failed} 和备用快捷键 {fallback} 均被占用，请从设置中重新指定；仍可从托盘恢复。", Forms.ToolTipIcon.Warning);
        }
        _notifyIcon.Text = $"Screen — {_settings.HotKey} 截图";
        _notifyIcon.ContextMenuStrip = BuildMenu();

        if (_settingsLoadFailed)
        {
            _notifyIcon.ShowBalloonTip(
                6000,
                "Screen 设置加载失败",
                "无法读取配置文件，已使用默认设置。您的更改将正常保存。",
                Forms.ToolTipIcon.Warning);
        }
        else if (Environment.GetCommandLineArgs().Any(argument =>
                string.Equals(argument, "--restarted", StringComparison.OrdinalIgnoreCase)))
        {
            _notifyIcon.ShowBalloonTip(
                4000,
                "Screen 已更新",
                "新版本已启动，快捷键即可使用。",
                Forms.ToolTipIcon.Info);
        }
    }

    public void RequestCapture()
    {
        if (!RecordingActive())
        {
            _ = _coordinator.StartCaptureAsync();
        }
    }

    private bool RecordingActive()
    {
        if (!_recordingFlowActive)
        {
            return false;
        }
        // Tray balloons are hidden behind fullscreen overlays; toast on screen.
        HintToastWindow.Show("录屏流程进行中，请先完成或取消录屏");
        return true;
    }

    public Task StartRecordingSelectionAsync() => RunRecordingAsync(null);

    public Task StartRecordingWithRegionAsync(PixelRect region, RecordingBorderWindow persistentBorder) =>
        RunRecordingAsync(region, persistentBorder);

    private async Task RunRecordingAsync(PixelRect? region, RecordingBorderWindow? persistentBorder = null)
    {
        if (_recordingFlowActive)
        {
            persistentBorder?.Close();
            HintToastWindow.Show("已有录制正在进行");
            return;
        }
        if (region is null && _coordinator.IsCapturing)
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
            RecordingSelection? selection = null;
            if (region is null)
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
                region = selection.Region;
                persistentBorder = new RecordingBorderWindow(region.Value);
                persistentBorder.Show();
                overlay.Close();
            }

            // Let the selection overlay disappear while the border stays visible.
            await Task.Delay(200);
            var countdown = new RecordingCountdownWindow(region.Value);
            countdown.Show();
            if (!await countdown.Completion)
            {
                return;
            }

            // Release the DXGI session used for selection before recording.
            _captureService.ReleaseSessions();
            using var controller = RecordingController.Create(_settingsStore, region.Value, selection, persistentBorder);
            persistentBorder = null; // The controller now owns the border.
            _recordingController = controller;
            controller.ShowControls();
            result = await controller.Completion;
            filePath = controller.FilePath;
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Unable to start recording", exception);
            MessageBox.Show($"无法开始录屏：{exception.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        finally
        {
            _recordingController = null;
            _recordingFlowActive = false;
            overlay?.Close();
            persistentBorder?.Close();
        }

        if (!result.Saved)
        {
            MessageBox.Show($"录屏失败：{result.Error}\n录制素材已保留，可从以下位置恢复：\n{result.RecoveryDirectory}",
                "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (_exiting)
        {
            return;
        }
        if (result.Warning is not null)
        {
            HintToastWindow.Show(result.RecoveryDirectory is null
                ? result.Warning
                : $"{result.Warning}。原始素材保留在：{result.RecoveryDirectory}");
        }
        try
        {
            WindowActivation.ShowTopmost(new RecordingPreviewWindow(filePath));
        }
        catch
        {
            // A preview failure must never swallow the finished file.
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
            interrupted ? "Screen 录制提前中断（已保存片段）" : "Screen 录制已保存",
            Path.GetFileName(filePath),
            interrupted ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        GraphiteToolStripRenderer.ApplyTo(menu);
        menu.Items.Add($"截图  {_settings.HotKey}", null, (_, _) =>
        {
            if (!RecordingActive())
            {
                _ = _coordinator.StartCaptureAsync();
            }
        });
        menu.Items.Add($"录屏  {_settings.RecordHotKey}", null, (_, _) => _ = StartRecordingSelectionAsync());
        menu.Items.Add($"解除全部贴图穿透  {_settings.ClickThroughRecoveryHotKey}", null, (_, _) => RecoverClickThrough(showConfirmation: false));
        menu.Items.Add("关闭全部贴图", null, (_, _) => _pinWindowManager.CloseAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("设置", null, (_, _) => OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => _ = ExitAsync());
        return menu;
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
            HintToastWindow.Show("正在保存当前录屏并退出…");
            try
            {
                await active.StopAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                SimpleLogger.LogError("Failed to finalize recording during exit", exception);
            }
        }
        Application.Current?.Shutdown();
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(
            _settingsStore,
            _credentialService,
            _startupService,
            // Live registration: new hotkeys take effect the moment they are
            // captured in the dialog, and occupied combinations fail loudly
            // right there instead of being silently reverted on close.
            hotKey => _hotKeyWindow.RegisterCaptureHotKey(hotKey),
            hotKey => _hotKeyWindow.RegisterClickThroughRecoveryHotKey(hotKey),
            hotKey => _hotKeyWindow.RegisterRecordHotKey(hotKey),
            _hotKeyWindow.TrySetPriority);
        window.SourceInitialized += (_, _) =>
            _hotKeyWindow.SettingsWindowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            _hotKeyWindow.SettingsWindowHandle = 0;
        }

        // Settings are auto-saved while the window is open, so always reload and
        // re-register hot keys after the window closes.
        _settings = _settingsStore.Load();
        _hotKeyWindow.TrySetPriority(_settings.PreferScreenHotKeys);
        void RefreshShortcut(string description, string key, Func<string, bool> register)
        {
            if (!register(key))
            {
                _notifyIcon.ShowBalloonTip(5000, "Screen 快捷键冲突",
                    $"{description}快捷键 {key} 无法注册，仍可使用托盘菜单。", Forms.ToolTipIcon.Warning);
            }
        }
        RefreshShortcut("截图", _settings.HotKey, _hotKeyWindow.RegisterCaptureHotKey);
        RefreshShortcut("恢复贴图", _settings.ClickThroughRecoveryHotKey, _hotKeyWindow.RegisterClickThroughRecoveryHotKey);
        RefreshShortcut("录屏", _settings.RecordHotKey, _hotKeyWindow.RegisterRecordHotKey);
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.ContextMenuStrip = BuildMenu();
        _notifyIcon.Text = $"Screen — {_settings.HotKey} 截图";
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
            $"按 {_settings.ClickThroughRecoveryHotKey} 可立即恢复贴图操作；也可以双击或右键 Screen 托盘图标恢复。此提示只显示一次。",
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
            _notifyIcon.ShowBalloonTip(2200, "Screen", "已恢复全部贴图的鼠标操作。", Forms.ToolTipIcon.Info);
        }
    }

    public void Dispose()
    {
        _recordingController?.Dispose();
        _recordingController = null;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon?.Dispose();
        _hotKeyWindow.Dispose();
        _pinWindowManager.CloseAll();
        _captureService.Dispose();
        _ocrEngine.Dispose();
        _httpClient.Dispose();
    }
}
