using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Interaction handlers for the Settings window: hot-key capture and the
/// translation service smoke test.
/// </summary>
public partial class SettingsWindow
{
    private void CaptureHotKey(Button input, string propertyName, KeyEventArgs e)
    {
        e.Handled = true;

        var isClickThroughRecovery = string.Equals(
            propertyName,
            nameof(SettingsViewModel.ClickThroughRecoveryHotKey),
            StringComparison.Ordinal);
        var isRecord = string.Equals(
            propertyName,
            nameof(SettingsViewModel.RecordHotKey),
            StringComparison.Ordinal);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if (modifiers == ModifierKeys.None && !isFunctionKey)
        {
            _viewModel.SetStatus("可以直接按 F1–F24，其他键请搭配 Ctrl、Shift、Alt 或 Win。", isError: true);
            input.Content = BuildKeycapPanel(GetHotKeyValue(propertyName));
            return;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }
        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }
        parts.Add(key.ToString());

        var value = string.Join('+', parts);
        var previous = GetHotKeyValue(propertyName);
        // Conflict check against the OTHER two hotkeys (there are three now).
        var otherHotKeys = new List<string> { _viewModel.HotKey, _viewModel.ClickThroughRecoveryHotKey, _viewModel.RecordHotKey };
        otherHotKeys.Remove(GetHotKeyValue(propertyName));
        if (otherHotKeys.Any(other => string.Equals(value, other, StringComparison.OrdinalIgnoreCase)))
        {
            _viewModel.SetStatus("该快捷键已分配给另一项功能，请换一个组合。", isError: true);
            input.Content = BuildKeycapPanel(previous);
            return;
        }

        // Register FIRST so an occupied combination is reported here, with a
        // visible message — not silently ignored until the next restart.
        var register = isClickThroughRecovery
            ? _registerRecoveryHotKey
            : isRecord ? _registerRecordHotKey : _registerCaptureHotKey;
        if (register is not null && !register(value))
        {
            _viewModel.SetStatus($"快捷键 {value} 注册失败，已保留 {previous}。冲突时可启用 Screen 优先。", isError: true);
            input.Content = BuildKeycapPanel(previous);
            return;
        }

        if (isClickThroughRecovery)
        {
            _viewModel.ClickThroughRecoveryHotKey = value;
        }
        else if (isRecord)
        {
            _viewModel.RecordHotKey = value;
        }
        else
        {
            _viewModel.HotKey = value;
        }

        input.Content = BuildKeycapPanel(value);
        // Return focus to the window instead of advancing to the next control:
        // focus traversal wraps around to the FIRST sidebar nav button, whose
        // focused state looked like a stuck selection on "通用".
        Focus();
        _viewModel.SetStatus($"新快捷键：{value}（已生效）", isError: false);
    }

    private async void TestTranslationAsync(object sender, RoutedEventArgs e)
    {
        if (_translationTestStatus is null || sender is not Button button)
        {
            return;
        }

        button.IsEnabled = false;
        // Cancel without disposing: the in-flight test may still be linking
        // this token, and disposing it surfaces ObjectDisposedException
        // instead of cancellation (see OcrCard.RecognizeAsync).
        _translationTestOperation?.Cancel();
        var operation = _translationTestOperation = new CancellationTokenSource();
        _translationTestStatus.Foreground = GetResource<Brush>("Settings.Brush.TextTertiary");
        _translationTestStatus.Text = "正在连接…";

        try
        {
            var profile = _viewModel.BuildTranslationProfile();
            var credentials = _viewModel.BuildTranslationCredentials();
            var provider = new OnlineTranslationProvider(_translationTestClient);
            var result = await provider.TranslateAsync(
                "Major Agent upgrades with strong production gains! Flexible reasoning effort: low for simple tasks, high for daily Agent workflows, max for complex tasks.",
                "en-US",
                profile,
                credentials,
                cancellationToken: operation.Token);
            if (operation.IsCancellationRequested)
            {
                // A newer test has begun; a stale success must not overwrite
                // its "正在连接…" status.
                return;
            }

            _translationTestStatus.Foreground = GetResource<Brush>("Settings.Brush.Success");
            _translationTestStatus.Text = result.UsedFallback
                ? $"回退可用：{result.ProviderName}（首选失败：{result.PrimaryFailure}）"
                : $"连接成功：{result.ProviderName}";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            _translationTestStatus.Text = "测试已取消";
        }
        catch (Exception exception) when (!operation.IsCancellationRequested)
        {
            // A stale run faulting after being superseded must not overwrite
            // the newer test's status.
            _translationTestStatus.Foreground = GetResource<Brush>("Settings.Brush.Error");
            _translationTestStatus.Text = exception.Message;
        }
        finally
        {
            if (!operation.IsCancellationRequested)
            {
                // A superseded run must not re-enable the button while the
                // newer test is still in flight.
                button.IsEnabled = true;
            }
        }
    }
}
