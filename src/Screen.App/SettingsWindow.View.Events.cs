using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Interaction handlers for the Settings window: hot-key capture and the
/// translation service smoke test.
/// </summary>
public partial class SettingsWindow
{
    private void OnHotKeyInput(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        // Let WPF handle ordinary Tab traversal when it delivers the keystroke.
        if (IsNavigationTab(key, modifiers) || _hotKeyEditor is not { } editor || editor.Button != sender)
            return;
        e.Handled = true;
        CaptureHotKey(editor.PropertyName, key, modifiers);
    }

    private static bool IsNavigationTab(Key key, ModifierKeys modifiers) =>
        key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift;

    internal bool TryCaptureRegisteredHotKey(Key key, ModifierKeys modifiers)
    {
        if (_hotKeyEditor is not { } editor || !IsActive || !editor.Button.IsVisible || !editor.Button.IsKeyboardFocused ||
            NativeMethods.GetForegroundWindow() != new WindowInteropHelper(this).Handle)
            return false;

        if (IsNavigationTab(key, modifiers))
        {
            // A registered Shift+Tab can arrive solely as WM_HOTKEY.
            editor.Button.MoveFocus(new TraversalRequest(modifiers == ModifierKeys.Shift
                ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        }
        else
            CaptureHotKey(editor.PropertyName, key, modifiers);
        return true;
    }

    private void CaptureHotKey(string propertyName, Key key, ModifierKeys modifiers)
    {
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            EndHotKeyCapture();
            Focus();
            return;
        }

        var isClickThroughRecovery = string.Equals(
            propertyName,
            nameof(SettingsViewModel.ClickThroughRecoveryHotKey),
            StringComparison.Ordinal);
        var isRecord = string.Equals(
            propertyName,
            nameof(SettingsViewModel.RecordHotKey),
            StringComparison.Ordinal);

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        var isFunctionKey = key is >= Key.F1 and <= Key.F24;
        if (modifiers == ModifierKeys.None && !isFunctionKey)
        {
            _viewModel.SetStatus("可以直接按 F1–F24，其他键请搭配 Ctrl、Shift、Alt 或 Win。", isError: true);
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
            return;
        }

        // Register FIRST so an occupied combination is reported here, with a
        // visible message — not silently ignored until the next restart.
        var register = isClickThroughRecovery
            ? _registerRecoveryHotKey
            : isRecord ? _registerRecordHotKey : _registerCaptureHotKey;
        if (register is not null && !register(value))
        {
            _viewModel.SetStatus($"快捷键 {value} 注册失败，已保留 {previous}。冲突时可启用 Flick 优先。", isError: true);
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

        EndHotKeyCapture();
        // Return focus to the window instead of advancing to the next control:
        // focus traversal wraps around to the FIRST sidebar nav button, whose
        // focused state looked like a stuck selection on "通用".
        Focus();
        _viewModel.SetStatus($"新快捷键：{value}（已生效）", isError: false);
    }

    private void ResetTranslationTest()
    {
        var previous = _translationTestOperation;
        _translationTestOperation = null;
        _testedInputs = null;
        if (_translationTestStatus is not null && _translationTestButton is not null)
        {
            _translationTestStatus.Foreground = GetResource<Brush>("Settings.Brush.TextTertiary");
            _translationTestStatus.Text = "未测试";
            _translationTestButton.IsEnabled = true;
        }
        // A cancelled request may still need its token. Its own finally disposes the source.
        previous?.Cancel();
    }

    private async void TestTranslationAsync(object sender, RoutedEventArgs e)
    {
        if (_translationTestStatus is null || sender is not Button button)
        {
            return;
        }

        ResetTranslationTest();
        var operation = _translationTestOperation = new CancellationTokenSource();
        _testedInputs = _viewModel.BuildTranslationTestInputs();
        button.IsEnabled = false;
        _translationTestStatus.Foreground = GetResource<Brush>("Settings.Brush.TextTertiary");
        _translationTestStatus.Text = "正在连接…";

        try
        {
            var profile = _viewModel.BuildTranslationProfile();
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
            var result = await _translationTestProvider.TranslateAsync(
                "Major Agent upgrades with strong production gains! Flexible reasoning effort: low for simple tasks, high for daily Agent workflows, max for complex tasks.",
                "en-US",
                profile,
                credentials,
                cancellationToken: operation.Token);
            if (!ReferenceEquals(_translationTestOperation, operation))
            {
                return;
            }

            _translationTestStatus.Foreground = GetResource<Brush>("Settings.Brush.Success");
            _translationTestStatus.Text = result.UsedFallback
                ? $"回退可用：{result.ProviderName}（首选失败：{result.PrimaryFailure}）"
                : $"连接成功：{result.ProviderName}";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_translationTestOperation, operation))
            {
                _translationTestStatus.Foreground = GetResource<Brush>("Settings.Brush.Error");
                _translationTestStatus.Text = exception.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_translationTestOperation, operation))
            {
                _translationTestOperation = null;
                button.IsEnabled = true;
            }
            operation.Dispose();
        }
    }
}
