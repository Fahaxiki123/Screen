using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

internal enum ScrollCaptureFinishAction
{
    Copy,
    Edit,
    Save
}

internal sealed class ScrollCaptureController : IDisposable
{
    private readonly SelectionRegion _selection;
    private readonly JsonSettingsStore _settings;
    private readonly Func<ScrollCaptureFinishAction, PixelBuffer, (ScrollCaptureFinishAction Action, string? Warning)> _writeOutput;
    private readonly ScrollCaptureBottomDetector _bottomDetector = new();
    private ScrollCaptureSequence? _sequence;
    private (ScrollFrameCapture Tail, StitchResult Result, ScrollCaptureFinishAction Action)? _pendingOutput;
    private ScrollCaptureControlWindow? _control;
    private RegionBorderWindow? _border;
    private ScrollCapturePreviewWindow? _preview;
    private LowLevelKeyboardHook? _keyboard;
    private DispatcherTimer? _timer;
    private ScrollCaptureMode _mode = ScrollCaptureMode.Manual;
    private bool _busy;
    private bool _ended;
    private bool _finishing;
    private Task? _captureOperation;
    private Task? _previewOperation;
    private int _previewFrameCount;
    private string? _captureStatus;

    public ScrollCaptureController(
        IScreenCaptureService capture,
        SelectionRegion selection,
        JsonSettingsStore settings)
        : this(selection, settings, new ScrollCaptureSequence(async token =>
        {
            var center = new PixelPoint(
                selection.Bounds.X + selection.Bounds.Width / 2.0,
                selection.Bounds.Y + selection.Bounds.Height / 2.0);
            // Crop on the capture continuation's pool thread, not the UI thread.
            var frame = await capture.CaptureAtAsync(center, token).ConfigureAwait(false);
            return frame.Crop(selection.Bounds);
        }))
    {
    }

    internal ScrollCaptureController(
        SelectionRegion selection,
        JsonSettingsStore settings,
        ScrollCaptureSequence sequence,
        Func<ScrollCaptureFinishAction, PixelBuffer, (ScrollCaptureFinishAction Action, string? Warning)>? writeOutput = null)
    {
        _selection = selection;
        _settings = settings;
        _sequence = sequence;
        _writeOutput = writeOutput ?? WriteOutput;
    }

    public event EventHandler? Completed;
    public event EventHandler? Cancelled;

    public async Task StartAsync()
    {
        if (_sequence is not { } sequence)
        {
            return;
        }
        try
        {
            var center = new NativeMethods.NativePoint(
                _selection.Bounds.X + _selection.Bounds.Width / 2,
                _selection.Bounds.Y + _selection.Bounds.Height / 2);
            var keyboardTarget = _selection.TargetWindow?.Handle ?? NativeMethods.WindowFromPoint(center);
            if (keyboardTarget != 0)
            {
                keyboardTarget = NativeMethods.GetAncestor(keyboardTarget, NativeMethods.GaRoot);
            }
            _border = new RegionBorderWindow(_selection.Bounds);
            _border.Show();
            _preview = new ScrollCapturePreviewWindow(_selection.Bounds);
            _preview.Show();
            _control = new ScrollCaptureControlWindow(_selection.Bounds);
            _control.AutomaticChanged += SetAutomatic;
            _control.FinishRequested += async (_, _) => await FinishAsync(ScrollCaptureFinishAction.Copy);
            _control.EditRequested += async (_, _) => await FinishAsync(ScrollCaptureFinishAction.Edit);
            _control.SaveRequested += async (_, _) => await FinishAsync(ScrollCaptureFinishAction.Save);
            _control.RetryRequested += async (_, _) => await RetryOutputAsync();
            _control.CancelRequested += (_, _) => Cancel();
            _control.Closed += (_, _) => Cancel();
            _control.Show();
            _keyboard = new LowLevelKeyboardHook(keyboardTarget);
            _keyboard.EnterPressed += (_, _) => Application.Current.Dispatcher.BeginInvoke(
                new Action(async () =>
                {
                    if (_sequence is not null)
                    {
                        await FinishAsync(ScrollCaptureFinishAction.Copy);
                    }
                }));
            _keyboard.EscapePressed += (_, _) => Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_sequence is not null)
                {
                    Cancel();
                }
            }));

            WindowActivation.Activate(keyboardTarget);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _timer.Tick += OnTimerTickAsync;
            _captureOperation = CaptureChangedFrameAsync(sequence);
            await _captureOperation;
            if (_ended || _finishing || _sequence is null)
            {
                return;
            }
            _timer.Start();
            _preview?.SetStatus("手动滚动选区 · Enter 完成 · Esc 取消");
        }
        catch (OperationCanceledException) when (_ended)
        {
        }
        catch (Exception exception)
        {
            _ended = true;
            Cleanup();
            MessageBox.Show($"无法开始长截图：{exception.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
            Cancelled?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetAutomatic(bool enabled)
    {
        if (_ended || _finishing || _sequence is null)
        {
            return;
        }
        _mode = enabled ? ScrollCaptureMode.Automatic : ScrollCaptureMode.Manual;
        _bottomDetector.Reset();
        _timer?.Interval = TimeSpan.FromMilliseconds(enabled ? 420 : 80);
        _control?.SetAutomatic(enabled);
        if (enabled && _selection.TargetWindow is { Handle: not 0 } target)
        {
            WindowActivation.Activate(target.Handle);
        }
        _preview?.SetStatus(enabled
            ? "自动滚动中 · 再点一次停止 · Enter 完成"
            : "手动滚动选区 · Enter 完成 · Esc 取消");
    }

    private async void OnTimerTickAsync(object? sender, EventArgs e)
    {
        if (_busy || _ended || _finishing || _sequence is not { } sequence)
        {
            return;
        }
        _busy = true;
        try
        {
            // Cover the automatic wheel delay as well as capture with one
            // operation, so completion can drain it before sampling the tail.
            var operation = CaptureTickAsync(sequence);
            _captureOperation = operation;
            var outcome = await operation;
            if (!_ended && !_finishing && _sequence is not null && _mode == ScrollCaptureMode.Automatic
                && _bottomDetector.Observe(outcome.Outcome))
            {
                await FinishAsync(ScrollCaptureFinishAction.Copy);
            }
        }
        catch (OperationCanceledException) when (_ended)
        {
        }
        catch (Exception exception)
        {
            _mode = ScrollCaptureMode.Manual;
            _bottomDetector.Reset();
            _control?.SetAutomatic(false);
            _preview?.SetStatus($"本次捕获失败，已切回手动模式：{exception.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<ScrollFrameCapture> CaptureTickAsync(ScrollCaptureSequence sequence)
    {
        if (_mode == ScrollCaptureMode.Automatic)
        {
            SendScroll();
            await Task.Delay(180, sequence.CancellationToken);
        }
        return await CaptureChangedFrameAsync(sequence);
    }

    private void SendScroll()
    {
        var x = _selection.Bounds.X + _selection.Bounds.Width / 2;
        var y = _selection.Bounds.Y + _selection.Bounds.Height / 2;
        var lParam = (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        var wheelDelta = unchecked((ushort)(short)-120);
        var wParam = (nuint)((uint)wheelDelta << 16);
        if (_selection.TargetWindow is { Handle: not 0 } target)
        {
            NativeMethods.PostMessageW(target.Handle, NativeMethods.WmMouseWheel, wParam, lParam);
        }
        else
        {
            var handle = NativeMethods.WindowFromPoint(new NativeMethods.NativePoint(x, y));
            if (handle != 0)
            {
                NativeMethods.PostMessageW(handle, NativeMethods.WmMouseWheel, wParam, lParam);
            }
        }
    }

    private async Task<ScrollFrameCapture> CaptureChangedFrameAsync(ScrollCaptureSequence sequence)
    {
        var outcome = await sequence.CaptureAsync();
        if (_ended)
        {
            return outcome;
        }
        if (outcome.Outcome == ScrollFrameOutcome.Added)
        {
            _captureStatus = null;
            RequestPreviewUpdate(sequence);
        }
        else if (outcome.Outcome == ScrollFrameOutcome.Rejected)
        {
            if (_mode == ScrollCaptureMode.Automatic)
            {
                SetAutomatic(false);
            }
            _captureStatus = CaptureFailureMessage(outcome);
            _preview?.SetStatus(_captureStatus);
        }
        return outcome;
    }

    private static string CaptureFailureMessage(ScrollFrameCapture outcome) =>
        outcome.FailureReason?.Contains("memory limit", StringComparison.OrdinalIgnoreCase) == true
            ? "长截图已达到大小上限 · 请回到上一位置完成截图"
            : outcome.FailureReason?.Contains("moved upward", StringComparison.OrdinalIgnoreCase) == true
                ? "向上滚动的画面已忽略 · 请回到上次位置后继续向下"
                : "本屏未能可靠对齐 · 请稍向上回到上次位置，再继续向下";

    private void RequestPreviewUpdate(ScrollCaptureSequence sequence)
    {
        if (_ended || _finishing || sequence.FrameCount == 0
            || _previewOperation is { IsCompleted: false })
        {
            return;
        }
        _previewOperation = UpdatePreviewAsync(sequence);
    }

    private async Task UpdatePreviewAsync(ScrollCaptureSequence sequence)
    {
        try
        {
            // New frames coalesce into the next preview update while capture
            // continues matching each accepted viewport.
            while (!_ended && !_finishing && _sequence is not null && _previewFrameCount != sequence.FrameCount)
            {
                var preview = await sequence.UpdatePreviewAsync();
                if (_ended || _finishing || _sequence is null)
                {
                    return;
                }
                _previewFrameCount = preview.FrameCount;
                _preview?.Update(preview.Image, preview.Width, preview.Height, preview.FrameCount);
                if (_captureStatus is not null)
                {
                    _preview?.SetStatus(_captureStatus);
                }
                _control?.BringToFrontNoActivate();
                await Task.Delay(120, sequence.CancellationToken);
            }
        }
        catch (OperationCanceledException) when (_sequence is null)
        {
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Scroll capture preview failed", exception);
            if (!_ended && !_finishing)
            {
                _preview?.SetStatus("预览暂未更新 · 已采集的画面仍保留");
            }
        }
    }

    internal async Task FinishAsync(ScrollCaptureFinishAction action)
    {
        if (_ended || _finishing)
        {
            return;
        }
        _finishing = true;
        string? warning;
        try
        {
            if (_sequence is { } sequence)
            {
                _timer?.Stop();
                _preview?.SetStatus("正在采集最后一屏…");
                if (_captureOperation is not null)
                {
                    await _captureOperation;
                }
                var tail = await sequence.FlushAsync();
                if (_ended)
                {
                    return;
                }
                // Export only validated content and disclose an omitted tail.
                _preview?.SetStatus("正在拼接原尺寸长截图…");
                var result = await sequence.ComposeAsync();
                if (_ended)
                {
                    return;
                }
                _pendingOutput = (tail, result, action);
                StopAcquisition();
                _control?.ShowOutputActions();
                _preview?.SetStatus("已拼接 · 可复制、保存或编辑");
            }

            if (_pendingOutput is not { } output)
            {
                return;
            }
            _pendingOutput = (output.Tail, output.Result, action);
            var (deliveredAction, outputWarning) = _writeOutput(action, output.Result.Image);
            if (_ended)
            {
                return;
            }
            warning = CompletionWarning(output.Tail, output.Result, deliveredAction);
            if (outputWarning is not null)
            {
                warning = warning is null ? outputWarning : $"{warning} · {outputWarning}";
            }
            _ended = true;
            _pendingOutput = null;
            Cleanup();
        }
        catch (OperationCanceledException) when (_ended)
        {
            return;
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Scroll capture completion failed", exception);
            if (_pendingOutput is not null)
            {
                _preview?.SetStatus("输出失败 · 已保留拼接结果");
                if (_control is { } owner)
                {
                    MessageBox.Show(owner, $"长截图输出失败：{exception.Message}\n已保留拼接结果，可重试复制、保存或编辑。\n回到控制条后，Enter 重试，Esc 取消。",
                        "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
                    if (!_ended)
                    {
                        owner.FocusOutputAction(action);
                    }
                }
            }
            else if (!_ended)
            {
                _preview?.SetStatus("本次完成失败，已保留采集画面 · 请重试");
            }
            return;
        }
        finally
        {
            _finishing = false;
            if (!_ended && _sequence is not null)
            {
                _mode = ScrollCaptureMode.Manual;
                _bottomDetector.Reset();
                _control?.SetAutomatic(false);
                _timer?.Interval = TimeSpan.FromMilliseconds(80);
                _timer?.Start();
            }
        }
        Completed?.Invoke(this, EventArgs.Empty);
        if (!string.IsNullOrEmpty(warning))
        {
            HintToastWindow.Show(warning);
        }
    }

    internal Task RetryOutputAsync() => _pendingOutput is { } output ? FinishAsync(output.Action) : Task.CompletedTask;

    internal static string? CompletionWarning(
        ScrollFrameCapture tail, StitchResult result, ScrollCaptureFinishAction action)
    {
        if (result.IsComplete && tail.Outcome is not (ScrollFrameOutcome.Rejected or ScrollFrameOutcome.Unstable))
        {
            return null;
        }
        var detail = !result.IsComplete || tail.Outcome == ScrollFrameOutcome.Rejected
            ? "末屏未能对齐，仅包含已可靠拼接的部分"
            : "页面仍有变化，已结束采集，请检查末尾";
        return action == ScrollCaptureFinishAction.Copy ? $"已复制 · {detail}" : detail;
    }

    private (ScrollCaptureFinishAction Action, string? Warning) WriteOutput(ScrollCaptureFinishAction action, PixelBuffer image)
    {
        switch (action)
        {
            case ScrollCaptureFinishAction.Edit:
                new LongScreenshotEditorWindow(image).Show();
                return (action, null);
            case ScrollCaptureFinishAction.Save:
                return SaveResultAs(image);
            default:
                BitmapUtilities.CopyToClipboard(image);
                return (action, null);
        }
    }

    private (ScrollCaptureFinishAction Action, string? Warning) SaveResultAs(PixelBuffer image)
    {
        string? saveDirectory = null;
        try
        {
            saveDirectory = _settings.Load().SaveDirectory;
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Could not read the last screenshot directory", exception);
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存长截图",
            Filter = "PNG 图片 (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            InitialDirectory = Directory.Exists(saveDirectory)
                ? saveDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            FileName = $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png"
        };
        var confirmed = _control is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (confirmed != true)
        {
            // Keep the existing clipboard fallback when the user closes Save As.
            BitmapUtilities.CopyToClipboard(image);
            return (ScrollCaptureFinishAction.Copy, null);
        }
        return (ScrollCaptureFinishAction.Save, SavePngAndRememberDirectory(image, dialog.FileName));
    }

    internal string? SavePngAndRememberDirectory(PixelBuffer image, string path)
    {
        BitmapUtilities.SavePng(image, path);
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            _settings.Update(latest => latest.SaveDirectory = directory ?? latest.SaveDirectory);
            return null;
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Long screenshot saved but the last screenshot directory could not be updated", exception);
            return "长截图已保存，无法记住保存目录";
        }
    }

    private void Cancel()
    {
        if (_ended)
        {
            return;
        }
        _ended = true;
        _pendingOutput = null;
        Cleanup();
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Safety net for callers holding the controller: disposing cancels an
    /// active session, which unregisters the global keyboard hook and closes
    /// every helper window. No-op once the session already ended.
    /// </summary>
    public void Dispose() => Cancel();

    private void Cleanup()
    {
        StopAcquisition();
        CloseHelperWindow(ref _control);
        CloseHelperWindow(ref _preview);
    }

    private void StopAcquisition()
    {
        _timer?.Stop();
        _timer = null;
        // The low-level hook swallows Enter/Esc globally while installed, so it
        // must go first, and one failing step must never skip the rest.
        try
        {
            _keyboard?.Dispose();
        }
        catch (Exception)
        {
        }
        _keyboard = null;
        CloseHelperWindow(ref _border);
        var sequence = _sequence;
        _sequence = null;
        sequence?.Cancel();
    }

    private static void CloseHelperWindow<T>(ref T? window) where T : Window
    {
        try
        {
            window?.Close();
        }
        catch (Exception)
        {
        }
        window = null;
    }

}

internal sealed class ScrollCapturePreviewWindow : Window
{
    private readonly Image _image;
    private readonly TextBlock _status;
    private readonly PixelRect _region;
    private nint _handle;

    public ScrollCapturePreviewWindow(PixelRect region)
    {
        _region = region;
        Title = "Screen 长截图预览";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Width = 220;
        Height = 150;
        AllowsTransparency = true;
        Background = GlassTheme.Brush("Brush.Transparent");

        var root = new DockPanel();
        _status = new TextBlock
        {
            Text = "总览 · 1 帧",
            Height = 38,
            Padding = new Thickness(12, 9, 12, 0),
            FontSize = 12,
            Foreground = GlassTheme.Brush("Brush.Label"),
            FontWeight = FontWeights.SemiBold
        };
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(8)
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        root.Children.Add(_image);
        Content = new GlassPanel { Variant = GlassVariant.Solid, Content = root };

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(
                _handle,
                NativeMethods.GwlExStyle,
                (nint)(style | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate));
            NativeMethods.SetWindowDisplayAffinity(_handle, NativeMethods.WdaExcludeFromCapture);
            Reposition();
        };
    }

    public void Update(BitmapSource preview, int originalWidth, int originalHeight, int frames)
    {
        _image.Source = preview;
        _status.Text = $"总览 · {frames} 帧 · {originalWidth} × {originalHeight}";
        Height = Math.Clamp(54 + Width * originalHeight / Math.Max(1.0, originalWidth), 138, 520);
        Reposition();
    }

    public void SetStatus(string text) => _status.Text = text;

    private void Reposition()
    {
        if (_handle == 0)
        {
            return;
        }
        var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
            _region.X + _region.Width / 2,
            _region.Y + _region.Height / 2));
        var width = (int)Math.Round(Width * dpi / 96.0);
        var height = (int)Math.Round(Height * dpi / 96.0);
        var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
        var rightX = _region.Right + 14;
        var leftX = _region.X - width - 14;
        var x = rightX + width <= screen.Right
            ? rightX
            : leftX >= screen.Left ? leftX : Math.Max(screen.Left + 8, _region.Right - width - 8);
        var y = Math.Clamp(_region.Y, screen.Top + 8, Math.Max(screen.Top + 8, screen.Bottom - height - 8));
        NativeMethods.SetWindowPos(
            _handle,
            new nint(-1),
            x,
            y,
            width,
            height,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }
}

/// <summary>
/// Floating controls shown during scroll capture: status,
/// auto-scroll toggle, and the finish/edit/save/cancel actions. Replaces the
/// separate control windows without obscuring the capture region.
/// </summary>
internal sealed class ScrollCaptureControlWindow : Window
{
    private readonly Button _autoButton;
    private readonly Button _editButton;
    private readonly Button _saveButton;
    private readonly Button _finishButton;
    private readonly TextBlock _autoLabel;
    private bool _enabled;
    private bool _positioned;
    private bool _outputActions;
    private nint _handle;

    public ScrollCaptureControlWindow(PixelRect region)
    {
        Title = "Screen 长截图";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        AllowsTransparency = true;
        Background = GlassTheme.Brush("Brush.Transparent");
        // Fit the toolbar to its content: a fixed width clipped the action
        // buttons whenever the status line grew. Height stays fixed.
        SizeToContent = SizeToContent.Width;
        MinWidth = 260;
        MaxWidth = 580;
        Height = 48;
        Opacity = 0;

        _autoLabel = new TextBlock
        {
            Text = "自动滚动",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        _autoButton = new Button { Content = _autoLabel, Width = 86, ToolTip = "切换自动滚动" };
        _autoButton.SetResourceReference(StyleProperty, "HudIconButtonStyle");
        _autoButton.Click += (_, _) =>
        {
            _enabled = !_enabled;
            ApplyAutomaticVisual();
            AutomaticChanged?.Invoke(_enabled);
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_autoButton);
        row.Children.Add(ControlSeparator());
        _editButton = IconAction(LucideIconKind.Pencil, "完成并编辑", () => EditRequested?.Invoke(this, EventArgs.Empty));
        _saveButton = IconAction(LucideIconKind.Download, "完成并保存", () => SaveRequested?.Invoke(this, EventArgs.Empty));
        row.Children.Add(_editButton);
        row.Children.Add(_saveButton);
        row.Children.Add(IconAction(
            LucideIconKind.X, "取消长截图",
            () => CancelRequested?.Invoke(this, EventArgs.Empty)));
        _finishButton = IconAction(
            LucideIconKind.Check, "完成并复制",
            () => FinishRequested?.Invoke(this, EventArgs.Empty));
        _finishButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        _finishButton.Width = _finishButton.MinWidth = 36;
        _finishButton.Height = 36;
        _finishButton.Margin = new Thickness(3, 1, 1, 1);
        _finishButton.Padding = new Thickness(0);
        row.Children.Add(_finishButton);

        Content = new GlassPanel { Variant = GlassVariant.Solid, Padding = new Thickness(5, 4, 5, 4), Content = row };

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, (nint)(style | NativeMethods.WsExNoActivate));
            NativeMethods.SetWindowDisplayAffinity(_handle, NativeMethods.WdaExcludeFromCapture);
        };
        // Position once layout has produced a real width (SizeToContent means
        // Width is NaN at SourceInitialized time). Hidden until then so the
        // toolbar never flashes at the default location.
        ContentRendered += (_, _) =>
        {
            if (_positioned)
            {
                return;
            }
            _positioned = true;
            var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
                region.X + region.Width / 2,
                region.Y + region.Height / 2));
            var width = (int)Math.Round(ActualWidth * dpi / 96.0);
            var height = (int)Math.Round(ActualHeight * dpi / 96.0);
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            var x = Math.Clamp(region.X + (region.Width - width) / 2, screen.Left + 8, Math.Max(screen.Left + 8, screen.Right - width - 8));
            var y = region.Bottom + height + 10 <= screen.Bottom
                ? region.Bottom + 8
                : Math.Max(screen.Top + 8, region.Bottom - height - 8);
            NativeMethods.SetWindowPos(
                _handle,
                new nint(-1),
                x,
                y,
                width,
                height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
            Opacity = 1;
        };
    }

    /// <summary>
    /// Let icons follow the active and disabled states of their buttons.
    /// </summary>
    private static void BindIconStrokeToForeground(LucideIconElement icon) =>
        icon.SetBinding(
            LucideIconElement.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Button.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor,
                    typeof(Button),
                    1)
            });

    private static Border ControlSeparator() => new()
    {
        Width = 1,
        Height = 20,
        Background = GlassTheme.Brush("Brush.Separator"),
        Margin = new Thickness(6, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static Button IconAction(LucideIconKind icon, string tooltip, Action action)
    {
        var iconElement = new LucideIconElement
        {
            Kind = icon,
            Width = 19,
            Height = 19,
            StrokeThickness = 1.75
        };
        BindIconStrokeToForeground(iconElement);
        var button = new Button
        {
            Content = iconElement,
            ToolTip = tooltip
        };
        button.SetResourceReference(StyleProperty, "HudIconButtonStyle");
        button.Click += (_, _) => action();
        return button;
    }

    private void ApplyAutomaticVisual()
    {
        _autoLabel.Text = _enabled ? "停止自动" : "自动滚动";
        HudActiveButton.SetIsActive(_autoButton, _enabled);
        _autoButton.SetResourceReference(Control.ForegroundProperty, _enabled ? "Brush.AccentText" : "Brush.OverlayIcon");
    }

    public void SetAutomatic(bool enabled)
    {
        _enabled = enabled;
        ApplyAutomaticVisual();
    }

    public void ShowOutputActions()
    {
        _outputActions = true;
        SetAutomatic(false);
        _autoLabel.Text = "已拼接";
        _autoButton.IsEnabled = false;
        if (_handle != 0)
        {
            var style = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, (nint)(style & ~NativeMethods.WsExNoActivate));
            NativeMethods.SetWindowPos(_handle, 0, 0, 0, 0, 0,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder
                | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged);
        }
    }

    public void FocusOutputAction(ScrollCaptureFinishAction action)
    {
        Activate();
        var button = action switch
        {
            ScrollCaptureFinishAction.Edit => _editButton,
            ScrollCaptureFinishAction.Save => _saveButton,
            _ => _finishButton
        };
        button.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_outputActions || e.Key is not (Key.Enter or Key.Escape))
        {
            return;
        }

        e.Handled = true;
        if (e.IsRepeat || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            RetryRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public void BringToFrontNoActivate()
    {
        if (_handle == 0)
        {
            return;
        }
        NativeMethods.SetWindowPos(
            _handle,
            new nint(-1),
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove
            | NativeMethods.SwpNoSize
            | NativeMethods.SwpNoActivate
            | NativeMethods.SwpShowWindow);
    }

    public event EventHandler? FinishRequested;
    public event EventHandler? EditRequested;
    public event EventHandler? SaveRequested;
    public event EventHandler? RetryRequested;
    public event EventHandler? CancelRequested;
    public event Action<bool>? AutomaticChanged;
}

/// <summary>
/// Full-virtual-screen focus layer shown during scroll capture: dims
/// everything outside the selected region (restoring the capture overlay's
/// mask), draws an accent border around the region, passes all input through
/// to the underlying window, and stays out of the captured frames.
/// </summary>
internal sealed class RegionBorderWindow : Window
{
    public RegionBorderWindow(PixelRect region)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = GlassTheme.Brush("Brush.Transparent");
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, (nint)(style | NativeMethods.WsExTransparent | NativeMethods.WsExNoActivate));
            NativeMethods.SetWindowDisplayAffinity(handle, NativeMethods.WdaExcludeFromCapture);
            var dpi = NativeMethods.GetDpiAtPoint(new NativeMethods.NativePoint(
                region.X + region.Width / 2,
                region.Y + region.Height / 2));
            var screen = System.Windows.Forms.SystemInformation.VirtualScreen;
            // DIP coordinates inside this window; physical placement via
            // SetWindowPos below.
            var dip = 96.0 / dpi;
            var windowWidth = screen.Width * dip;
            var windowHeight = screen.Height * dip;
            Width = windowWidth;
            Height = windowHeight;
            var hole = new Rect(
                (region.X - screen.X) * dip,
                (region.Y - screen.Y) * dip,
                region.Width * dip,
                region.Height * dip);

            var maskGeometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
            maskGeometry.Children.Add(new RectangleGeometry(new Rect(0, 0, windowWidth, windowHeight)));
            maskGeometry.Children.Add(new RectangleGeometry(hole));
            var root = new Grid();
            root.Children.Add(new System.Windows.Shapes.Path
            {
                Data = maskGeometry,
                Fill = GlassTheme.Brush("Brush.Dim")
            });
            root.Children.Add(new Border
            {
                BorderBrush = GlassTheme.Brush("Brush.Accent"),
                BorderThickness = new Thickness(2),
                Width = hole.Width,
                Height = hole.Height,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(hole.X, hole.Y, 0, 0)
            });
            Content = root;

            NativeMethods.SetWindowPos(
                handle,
                new nint(-1),
                screen.X,
                screen.Y,
                screen.Width,
                screen.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        };
    }
}
