using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Screen.Core;

namespace Screen.App;

internal sealed class CaptureCoordinator(
    IScreenCaptureService captureService,
    WindowSnapService windowSnapService,
    PinWindowManager pinWindowManager,
    IOcrEngine ocrEngine,
    ITranslationProvider translationProvider,
    CredentialService credentialService,
    JsonSettingsStore settingsStore)
{
    private int _capturing;
    private int _overlayWarmed;
    private Action? _closeWarmup;

    /// <summary>True while the screenshot selection overlay is open.</summary>
    public bool IsCapturing => Volatile.Read(ref _capturing) != 0;

    /// <summary>
    /// Pre-initializes the real CaptureOverlayWindow visual tree (CaptureSurface, toolbars,
    /// OCR/Translation cards, DropShadow pixel shaders, fonts, vector icons, and lazy UI
    /// assemblies) in an invisible off-screen tool window so the first hotkey capture renders
    /// immediately.
    /// </summary>
    public void WarmUpOverlayPipeline()
    {
        if (IsCapturing || Interlocked.Exchange(ref _overlayWarmed, 1) != 0)
        {
            return;
        }

        var timer = Stopwatch.StartNew();
        Window? warmup = null;
        DispatcherTimer? failsafe = null;
        try
        {
            if (Application.Current is not null)
            {
                var syntheticFrame = new CaptureFrame(
                    new byte[4],
                    1,
                    1,
                    4,
                    96,
                    96,
                    new PixelRect(-32000, -32000, 1, 1),
                    CaptureColorSpace.Srgb,
                    DateTimeOffset.UtcNow);
                warmup = new CaptureOverlayWindow(
                    syntheticFrame,
                    captureService,
                    windowSnapService,
                    pinWindowManager,
                    ocrEngine,
                    translationProvider,
                    credentialService,
                    settingsStore,
                    recordingMode: false,
                    warmUp: true);
            }
            else
            {
                var panel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        AppleVectorIcons.Create("rect"),
                        new TextBlock
                        {
                            Text = "Flick",
                            FontFamily = new FontFamily("Segoe UI Variable Text, Cascadia Mono, Microsoft YaHei UI"),
                            FontSize = 12
                        }
                    }
                };
                warmup = new Window
                {
                    Width = 1,
                    Height = 1,
                    Left = -32000,
                    Top = -32000,
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Opacity = 0,
                    IsHitTestVisible = false,
                    Content = new SquircleGlassBorder { Child = panel }
                };
                warmup.SourceInitialized += (_, _) =>
                {
                    var handle = new WindowInteropHelper(warmup).Handle;
                    if (handle != 0)
                    {
                        var exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
                        exStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate | NativeMethods.WsExTransparent;
                        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, (nint)exStyle);
                    }
                };
            }

            var closed = false;
            void CloseWarmup()
            {
                if (closed)
                {
                    return;
                }
                closed = true;
                _closeWarmup = null;
                failsafe?.Stop();
                try
                {
                    warmup?.Close();
                }
                catch
                {
                }
                SimpleLogger.LogInfo($"Overlay UI pipeline warmed up in {timer.Elapsed.TotalMilliseconds:F1} ms");
            }

            _closeWarmup = CloseWarmup;
            failsafe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            failsafe.Tick += (_, _) => CloseWarmup();
            warmup.ContentRendered += (_, _) => CloseWarmup();
            warmup.Show();
            warmup.UpdateLayout();
            failsafe.Start();
        }
        catch (Exception exception)
        {
            _closeWarmup = null;
            failsafe?.Stop();
            try
            {
                warmup?.Close();
            }
            catch
            {
            }
            SimpleLogger.LogWarning($"Overlay UI warm-up skipped: {exception.Message}");
        }
    }

    public async Task StartCaptureAsync(string trigger = "manual")
    {
        if (Interlocked.Exchange(ref _capturing, 1) != 0)
        {
            SimpleLogger.LogInfo($"Ignored capture request ({trigger}): capture already in progress.");
            return;
        }

        Interlocked.Exchange(ref _overlayWarmed, 1);
        _closeWarmup?.Invoke();
        try
        {
            var timer = Stopwatch.StartNew();
            NativeMethods.TryGetPhysicalCursorPosition(out var cursor);
            var frame = await captureService.CaptureAtAsync(new PixelPoint(cursor.X, cursor.Y));
            var captureMs = timer.Elapsed.TotalMilliseconds;
            var overlay = new CaptureOverlayWindow(
                frame,
                captureService,
                windowSnapService,
                pinWindowManager,
                ocrEngine,
                translationProvider,
                credentialService,
                settingsStore);
            var buildMs = timer.Elapsed.TotalMilliseconds - captureMs;
            overlay.Closed += (_, _) => Interlocked.Exchange(ref _capturing, 0);
            overlay.Show();
            overlay.Activate();
            var totalMs = timer.Elapsed.TotalMilliseconds;
            var showMs = totalMs - captureMs - buildMs;
            Debug.WriteLine($"Screen capture overlay ready in {totalMs:F1} ms");
            SimpleLogger.LogInfo(
                $"Screen capture overlay ready in {totalMs:F1} ms " +
                $"(trigger: {trigger}, capture: {captureMs:F1} ms, build: {buildMs:F1} ms, show: {showMs:F1} ms)");
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _capturing, 0);
            SimpleLogger.LogError($"Screen capture failed (trigger: {trigger})", exception);
            MessageBox.Show(
                $"无法开始截图：{exception.Message}",
                "Flick",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
