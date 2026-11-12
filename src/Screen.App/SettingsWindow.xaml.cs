using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly HttpClient _translationTestClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<string, Button> _navigation = [];
    private readonly Dictionary<string, FrameworkElement> _pages = [];

    private CancellationTokenSource? _translationTestOperation;
    private TextBlock? _translationTestStatus;
    private string _activeNavId = string.Empty;

    // Optional live registration: when provided, a newly captured hotkey is
    // registered immediately instead of waiting for this window to close.
    private readonly Func<string, bool>? _registerCaptureHotKey;
    private readonly Func<string, bool>? _registerRecoveryHotKey;
    private readonly Func<string, bool>? _registerRecordHotKey;

    // Hot-key input controls.
    private Button? _captureHotKeyButton;
    private Button? _clickThroughRecoveryHotKeyButton;
    private Button? _recordHotKeyButton;

    // Translation page sections that show/hide with the selected provider.
    private StackPanel? _tencentSection;
    private StackPanel? _aiSection;
    private StackPanel? _memorySection;
    private StackPanel? _racingRows;
    private FrameworkElement? _fastFallbackRow;

    internal SettingsWindow(
        JsonSettingsStore store,
        CredentialService credentials,
        StartupService startup,
        Func<string, bool>? registerCaptureHotKey = null,
        Func<string, bool>? registerRecoveryHotKey = null,
        Func<string, bool>? registerRecordHotKey = null,
        Func<bool, bool>? setHotKeyPriority = null)
    {
        InitializeComponent();
        Background = (Brush)FindResource("Brush.WindowBackdrop");
        _registerCaptureHotKey = registerCaptureHotKey;
        _registerRecoveryHotKey = registerRecoveryHotKey;
        _registerRecordHotKey = registerRecordHotKey;
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        NavVersion.Text = version is null ? "本地构建" : $"v{version.ToString(3)} · 本地构建";
        SourceInitialized += (_, _) =>
        {
            // Match the system title bar to the navy content.
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var dark = 1;
            _ = NativeMethods.DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            ApplyTitleBarColor(35, "Brush.WindowBackground"); // DWMWA_CAPTION_COLOR
            ApplyTitleBarColor(36, "Brush.TextPrimary");      // DWMWA_TEXT_COLOR
            ApplyTitleBarColor(34, "Brush.Border");           // DWMWA_BORDER_COLOR

            void ApplyTitleBarColor(int attribute, string resourceKey)
            {
                var color = ((SolidColorBrush)FindResource(resourceKey)).Color;
                // DWM expects a COLORREF (0x00BBGGRR), not WPF's ARGB value.
                var colorRef = color.R | (color.G << 8) | (color.B << 16);
                _ = NativeMethods.DwmSetWindowAttribute(handle, attribute, ref colorRef, sizeof(int));
            }
        };

        _viewModel = new SettingsViewModel(store, credentials, startup.SetEnabled, setHotKeyPriority);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        BuildNavigation();
        ShowPage("general");
    }

    private void BuildNavigation()
    {
        NavHost.Children.Clear();
        _navigation.Clear();

        AddNavigationSection("基础设置");
        AddNavigationItem("general", LucideIconKind.Settings2, "通用");
        AddNavigationItem("shortcuts", LucideIconKind.Keyboard, "快捷键");
        AddNavigationItem("recording", LucideIconKind.Video, "录屏");
        AddNavigationSection("文字与翻译");
        AddNavigationItem("ocr", LucideIconKind.ScanText, "文字识别");
        AddNavigationItem("translation", LucideIconKind.Languages, "翻译接口");
    }

    private void AddNavigationSection(string title)
    {
        NavHost.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(12, 14, 0, 9),
            FontSize = (double)FindResource("Settings.FontSize.Section"),
            FontWeight = GetFontWeight("Settings.FontWeight.Section"),
            Foreground = GetResource<Brush>("Settings.Brush.TextTertiary")
        });
    }

    private void AddNavigationItem(string id, LucideIconKind icon, string text)
    {
        var iconElement = new LucideIconElement
        {
            Kind = icon,
            Width = 18,
            Height = 18,
            StrokeThickness = 1.8,
            Stroke = (Brush)FindResource("Settings.Brush.TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var label = new TextBlock
        {
            Text = text,
            FontSize = (double)FindResource("Settings.FontSize.Nav"),
            FontWeight = GetFontWeight("Settings.FontWeight.Nav"),
            Foreground = (Brush)FindResource("Settings.Brush.TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var indicator = new Border
        {
            Width = 3,
            Height = 18,
            CornerRadius = new CornerRadius(1.5),
            Margin = new Thickness(-8, 0, 8, 0),
            Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(indicator);
        content.Children.Add(iconElement);
        content.Children.Add(new Border { Width = 11 });
        content.Children.Add(label);

        var button = new Button
        {
            Tag = id,
            Content = content,
            Style = (Style)FindResource("SettingsNavButtonStyle"),
            ToolTip = text
        };
        // Hover lives in code (not the template) so the ACTIVE page can opt
        // out and keep its selection pill steady.
        button.MouseEnter += (_, _) =>
        {
            if (id != _activeNavId)
            {
                button.Background = (Brush)FindResource("Settings.Brush.NavHover");
            }
        };
        button.MouseLeave += (_, _) =>
        {
            if (id != _activeNavId)
            {
                button.Background = Brushes.Transparent;
            }
        };
        button.Click += (_, _) => ShowPage(id);
        _navigation[id] = button;
        NavHost.Children.Add(button);
    }

    private void ShowPage(string id)
    {
        _activeNavId = id;
        foreach (var (key, button) in _navigation)
        {
            var isActive = key == id;
            button.Background = isActive
                ? new SolidColorBrush(Color.FromArgb(0x22, 0x38, 0xBD, 0xF8))
                : Brushes.Transparent;
            button.BorderBrush = isActive
                ? new SolidColorBrush(Color.FromArgb(0x40, 0x7D, 0xD3, 0xFC))
                : Brushes.Transparent;

            if (button.Content is StackPanel content
                && content.Children.Count == 4
                && content.Children[0] is Border indicator
                && content.Children[1] is LucideIconElement icon
                && content.Children[3] is TextBlock label)
            {
                indicator.Background = isActive
                    ? (Brush)FindResource("Settings.Brush.AccentText")
                    : Brushes.Transparent;
                icon.Stroke = isActive
                    ? (Brush)FindResource("Settings.Brush.AccentText")
                    : (Brush)FindResource("Settings.Brush.TextSecondary");
                label.Foreground = (Brush)FindResource(isActive
                    ? "Settings.Brush.TextPrimary"
                    : "Settings.Brush.TextSecondary");
            }
        }

        if (!_pages.TryGetValue(id, out var page))
        {
            page = id switch
            {
                "shortcuts" => BuildShortcutsPage(),
                "recording" => BuildRecordingPage(),
                "ocr" => BuildOcrPage(),
                "translation" => BuildTranslationPage(),
                _ => BuildGeneralPage()
            };
            _pages[id] = page;
        }

        PageHost.Content = page;
        ApplyPageHeader(id);
        UpdateTranslationSections();
    }

    private void ApplyPageHeader(string id)
    {
        switch (id)
        {
            case "shortcuts":
                PageTitle.Text = "快捷键";
                PageDescription.Text = "点击快捷键按钮后直接按下新组合键，设置会自动保存。";
                break;
            case "recording":
                PageTitle.Text = "录屏";
                PageDescription.Text = "录屏的帧率、清晰度与快捷键；每次录制前仍会先选择区域。";
                break;
            case "ocr":
                PageTitle.Text = "文字识别";
                PageDescription.Text = "本地识别优先，困难内容可自动使用云端增强。";
                break;
            case "translation":
                PageTitle.Text = "翻译接口";
                PageDescription.Text = "配置截图翻译使用的服务、密钥和回退策略。";
                break;
            default:
                PageTitle.Text = "通用";
                PageDescription.Text = "控制 Screen 的启动方式与基础行为。";
                break;
        }
    }

    private void UpdateTranslationSections()
    {
        if (_tencentSection is null || _aiSection is null || _memorySection is null)
        {
            return;
        }

        _tencentSection.Visibility = _viewModel.TranslationProviderIndex == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _aiSection.Visibility = _viewModel.TranslationProviderIndex == 2
            ? Visibility.Visible
            : Visibility.Collapsed;
        _memorySection.Visibility = _viewModel.TranslationProviderIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_racingRows is not null)
        {
            _racingRows.Visibility = _viewModel.EnableRacing
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (_fastFallbackRow is not null)
        {
            _fastFallbackRow.Visibility = _viewModel.EnableFastFallbackRace
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.TranslationProviderIndex)
            or nameof(SettingsViewModel.EnableRacing)
            or nameof(SettingsViewModel.EnableFastFallbackRace))
        {
            UpdateTranslationSections();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Cancel without disposing: an in-flight test may still be linking
        // this token (CreateLinkedTokenSource registers on it), and disposing
        // surfaces ObjectDisposedException instead of cancellation.
        _translationTestOperation?.Cancel();
        _translationTestClient.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
    }
}
