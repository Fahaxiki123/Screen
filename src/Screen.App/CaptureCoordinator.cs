using System.Diagnostics;
using System.Windows;
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

    /// <summary>True while the screenshot selection overlay is open.</summary>
    public bool IsCapturing => Volatile.Read(ref _capturing) != 0;

    public Task<CaptureFrame> CaptureSelectionBackgroundAsync()
    {
        var monitors = DesktopRegionCapture.GetMonitorBounds();
        if (monitors.Length > 1)
            return GdiScreenCaptureService.CaptureDesktopAsync(monitors);

        NativeMethods.TryGetPhysicalCursorPosition(out var cursor);
        return captureService.CaptureAtAsync(new PixelPoint(cursor.X, cursor.Y));
    }

    public async Task StartCaptureAsync()
    {
        if (Interlocked.Exchange(ref _capturing, 1) != 0)
        {
            return;
        }

        try
        {
            var timer = Stopwatch.StartNew();
            var frame = await CaptureSelectionBackgroundAsync();
            var overlay = new CaptureOverlayWindow(
                frame,
                captureService,
                windowSnapService,
                pinWindowManager,
                ocrEngine,
                translationProvider,
                credentialService,
                settingsStore);
            overlay.Closed += (_, _) => Interlocked.Exchange(ref _capturing, 0);
            overlay.Show();
            overlay.Activate();
            Debug.WriteLine($"Screen capture overlay ready in {timer.Elapsed.TotalMilliseconds:F1} ms");
            SimpleLogger.LogInfo($"Screen capture overlay ready in {timer.Elapsed.TotalMilliseconds:F1} ms");
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _capturing, 0);
            SimpleLogger.LogError("Screen capture failed", exception);
            MessageBox.Show(
                $"无法开始截图：{exception.Message}",
                "Screen",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
