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
        if (_translationTestStatus is null || sender is not Button)
        {
            return;
        }

        _translationTestOperation?.Cancel();
        var operation = _translationTestOperation = new CancellationTokenSource();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        ShowConnectionResult("正在连接…", "正在连接…", "Brush.LabelSecondary", busy: true);

        try
        {
            // The smoke test is a fixed English -> Chinese request. A user's
            // same-language preference must not bypass the network and claim
            // a successful connection without contacting the service.
            var profile = _viewModel.BuildTranslationProfile() with { SourceLanguage = "en", TargetLanguage = "简体中文" };
            if (profile.Provider == TranslationProviderKind.OpenAiCompatible)
            {
                if (TranslationEndpointRules.ValidateBaseAddress(profile.BaseAddress) is { } baseAddressError)
                {
                    throw new InvalidOperationException(baseAddressError);
                }
                if (profile.EnableRacing
                    && !string.IsNullOrWhiteSpace(profile.RacingBaseAddress)
                    && TranslationEndpointRules.ValidateBaseAddress(profile.RacingBaseAddress) is { } racingAddressError)
                {
                    throw new InvalidOperationException(racingAddressError);
                }
                if (_viewModel.CanBindApiKey || (profile.EnableRacing && _viewModel.CanBindRacingApiKey))
                {
                    throw new InvalidOperationException("请先将 Key 草稿绑定到当前地址，再测试连接。");
                }
            }
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

            if (result.UsedFallback)
            {
                var reason = $"回退可用：{result.ProviderName}（首选失败：{result.PrimaryFailure}）";
                ShowConnectionResult("首选未连接", reason, "Brush.Warning");
            }
            else
            {
                ShowConnectionResult("已连接", $"✓ {watch.Elapsed.TotalSeconds:F1} 秒", "Brush.Success", detail: result.ProviderName);
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (operation.IsCancellationRequested)
            {
                SimpleLogger.LogError("Translation connection test ended with an error after cancellation", exception);
            }
            else
            {
                ShowConnectionResult("连接失败", exception.Message, "Brush.DangerOnDark");
            }
        }
        finally
        {
            if (ReferenceEquals(_translationTestOperation, operation))
                _translationTestOperation = null;
            operation.Dispose();
        }
    }

    private void ResetConnectionTest()
    {
        // The old request still owns its token until its finally block. Its result
        // must not repaint the newly edited endpoint or credential as connected.
        _translationTestOperation?.Cancel();
        _translationTestOperation = null;
        ShowConnectionResult("未测试", "", "Brush.LabelSecondary");
    }

    private void ShowConnectionResult(string header, string status, string color, bool busy = false, string? detail = null)
    {
        TranslationConnectionStatus.Text = header;
        TranslationConnectionStatus.SetResourceReference(TextBlock.ForegroundProperty, color);
        TranslationConnectionStatus.ToolTip = detail ?? status;
        if (_translationTestStatus is null || _testConnection is null || _testProgress is null)
            return;
        _translationTestStatus.Text = status;
        _translationTestStatus.Visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _translationTestStatus.ToolTip = detail ?? status;
        _translationTestStatus.SetResourceReference(TextBlock.ForegroundProperty, color);
        _testConnection.IsEnabled = !busy;
        _testConnection.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _testConnection.Content = status.Length == 0 ? "测试连接" : "重新测试";
        _testProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        var spin = (RotateTransform)_testProgress.RenderTransform;
        spin.BeginAnimation(RotateTransform.AngleProperty, busy && SystemParameters.ClientAreaAnimation
            ? new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever }
            : null);
    }
}
