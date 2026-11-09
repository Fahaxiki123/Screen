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
    private CaptureOverlayWindow? _recordingSelectionOverlay;
    // Covers the gap between confirming the setup panel and the controller
    // being assigned (delay + countdown + early-fault wait), during which
    // both fields above are null but the flow is already underway.
    private bool _recordingFlowActive;
    private bool _settingsLoadFailed;

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
        if (_recordingController is null && _recordingSelectionOverlay is null && !_recordingFlowActive)
        {
            return false;
        }
        // Tray balloons are hidden behind fullscreen overlays; toast on screen.
        HintToastWindow.Show("录屏流程进行中，请先完成或取消录屏");
        return true;
    }

    public async Task StartRecordingSelectionAsync()
    {
        if (_recordingController is not null || _recordingSelectionOverlay is not null || _recordingFlowActive)
        {
            HintToastWindow.Show("已有录制正在进行");
            return;
        }
        if (_coordinator.IsCapturing)
        {
            HintToastWindow.Show("截图进行中，请先完成或取消截图再录屏");
            return;
        }
        try
        {
            if (!NativeMethods.TryGetPhysicalCursorPosition(out var cursor))
            {
                throw new InvalidOperationException("无法读取鼠标位置。");
            }
            var frame = await _captureService.CaptureAtAsync(new PixelPoint(cursor.X, cursor.Y));
            var overlay = new CaptureOverlayWindow(
                frame,
                _captureService,
                _windowSnapService,
                _pinWindowManager,
                _ocrEngine,
                _translationProvider,
                _credentialService,
                _settingsStore,
                recordingMode: true);
            _recordingSelectionOverlay = overlay;
            overlay.RecordingRegionConfirmed += async (_, selection) =>
            {
                // The persistent border takes over BEFORE the overlay (and
                // its selection chrome) closes — Feishu-style: the outline
                // never disappears from confirm to recording end.
                var border = new RecordingBorderWindow(selection.Region);
                border.Show();
                overlay.Close();
                await StartRecordingWithRegionAsync(
                    selection.Region,
                    selection.ShowCursor,
                    selection.ShowClickEffects,
                    selection.RecordSystemAudio,
                    selection.RecordMicrophone,
                    border);
            };
            overlay.Closed += (_, _) => _recordingSelectionOverlay = null;
            overlay.Show();
            overlay.Activate();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"无法开始录屏：{exception.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Feishu flow after a region is known: 3-2-1 countdown over the region,
    /// then the capture loop; on completion the editor/preview window opens.
    /// <paramref name="persistentBorder"/> is the outline window created at
    /// confirm time; this method takes responsibility for closing it on every
    /// abort path.
    /// </summary>
    public async Task StartRecordingWithRegionAsync(
        PixelRect region,
        bool? showCursor = null,
        bool? showClickEffects = null,
        bool? recordSystemAudio = null,
        bool? recordMicrophone = null,
        RecordingBorderWindow? persistentBorder = null)
    {
        if (_recordingController is not null || _recordingFlowActive)
        {
            persistentBorder?.Close();
            HintToastWindow.Show("已有录制正在进行");
            return;
        }
        _recordingFlowActive = true;
        // Give the selection overlay time to disappear before the countdown
        // takes over; the persistent border keeps the outline visible.
        await Task.Delay(200);
        var countdown = new RecordingCountdownWindow(region);
        countdown.Show();
        await countdown.Completion;

        // Desktop Duplication allows a single session per output; the capture
        // service still owns the one created during region selection, so it
        // must be released before the recorder takes over.
        _captureService.ReleaseSessions();
        var controller = await RecordingController.StartAsync(
            _settingsStore,
            region,
            showCursor,
            showClickEffects,
            recordSystemAudio,
            recordMicrophone,
            persistentBorder);
        if (controller is null)
        {
            _recordingFlowActive = false;
            persistentBorder?.Close();
            return;
        }
        _recordingController = controller;
        controller.Completed += (_, _) =>
        {
            _recordingController = null;
            _recordingFlowActive = false;
            try
            {
                WindowActivation.ShowTopmost(new RecordingPreviewWindow(controller.FilePath));
            }
            catch
            {
                // A preview failure must never swallow the finished file.
                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{controller.FilePath}\"");
                }
                catch
                {
                }
            }
            _notifyIcon.ShowBalloonTip(
                5000,
                "Screen 录制已保存",
                Path.GetFileName(controller.FilePath),
                Forms.ToolTipIcon.Info);
        };
        controller.Failed += (_, message) =>
        {
            _recordingController = null;
            _recordingFlowActive = false;
            MessageBox.Show($"录屏失败：{message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
        };
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
        menu.Items.Add("退出", null, (_, _) => Application.Current.Shutdown());
        return menu;
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
            hotKey => _hotKeyWindow.RegisterRecordHotKey(hotKey));
        window.ShowDialog();

        // Settings are auto-saved while the window is open, so always reload and
        // re-register hot keys after the window closes.
        var previousHotKey = _settings.HotKey;
        var previousRecoveryHotKey = _settings.ClickThroughRecoveryHotKey;
        var previousRecordHotKey = _settings.RecordHotKey;
        _settings = _settingsStore.Load();
        if (!_hotKeyWindow.RegisterCaptureHotKey(_settings.HotKey))
        {
            var failed = _settings.HotKey;
            _settings.HotKey = previousHotKey;
            _settingsStore.Update(latest => latest.HotKey = previousHotKey);
            _hotKeyWindow.RegisterCaptureHotKey(previousHotKey);
            _notifyIcon.ShowBalloonTip(5000, "Screen 快捷键冲突", $"{failed} 已被其他程序占用，已恢复 {previousHotKey}。", Forms.ToolTipIcon.Warning);
        }
        if (!_hotKeyWindow.RegisterClickThroughRecoveryHotKey(_settings.ClickThroughRecoveryHotKey))
        {
            var failed = _settings.ClickThroughRecoveryHotKey;
            _settings.ClickThroughRecoveryHotKey = previousRecoveryHotKey;
            _settingsStore.Update(latest => latest.ClickThroughRecoveryHotKey = previousRecoveryHotKey);
            _hotKeyWindow.RegisterClickThroughRecoveryHotKey(previousRecoveryHotKey);
            _notifyIcon.ShowBalloonTip(
                5000,
                "Screen 快捷键冲突",
                $"{failed} 已被其他程序占用，已恢复 {previousRecoveryHotKey}。",
                Forms.ToolTipIcon.Warning);
        }
        if (!_hotKeyWindow.RegisterRecordHotKey(_settings.RecordHotKey))
        {
            var failed = _settings.RecordHotKey;
            _settings.RecordHotKey = previousRecordHotKey;
            _settingsStore.Update(latest => latest.RecordHotKey = previousRecordHotKey);
            _hotKeyWindow.RegisterRecordHotKey(previousRecordHotKey);
            _notifyIcon.ShowBalloonTip(
                5000,
                "Screen 快捷键冲突",
                $"录屏快捷键 {failed} 已被其他程序占用，已恢复 {previousRecordHotKey}。",
                Forms.ToolTipIcon.Warning);
        }
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
        _recordingController?.StopForShutdown();
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
