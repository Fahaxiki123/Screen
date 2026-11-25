using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Screen.Core;

namespace Screen.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly HttpClient? _translationTestClient;
    private readonly ITranslationProvider _translationTestProvider;
    private readonly Dictionary<string, Button> _navigation = [];
    private readonly Dictionary<string, string> _itemGroups = [];
    private readonly Dictionary<string, SettingsNavGroup> _navGroups = [];
    private readonly Dictionary<string, FrameworkElement> _pages = [];

    private CancellationTokenSource? _translationTestOperation;
    private TranslationTestInputs? _testedInputs;
    private Button? _translationTestButton;
    private TextBlock? _translationTestStatus;
    private string _activeNavId = string.Empty;
    private string _activeGroupId = string.Empty;
    private readonly bool _localOcrReady;
    private readonly string _versionLabel;

    // 热键实时注册回调：捕获新快捷键后立即生效，无需等到关闭窗口。
    private readonly Func<string, bool>? _registerCaptureHotKey;
    private readonly Func<string, bool>? _registerRecoveryHotKey;
    private readonly Func<string, bool>? _registerRecordHotKey;

    // 快捷键输入按钮。
    private Button? _captureHotKeyButton;
    private Button? _clickThroughRecoveryHotKeyButton;
    private Button? _recordHotKeyButton;
    private (Button Button, string PropertyName)? _hotKeyEditor;

    // 识别与翻译页按服务商动态显隐的配置分区。
    private StackPanel? _edgeSection;
    private StackPanel? _aiSection;
    private StackPanel? _aiAdvancedSection;
    private StackPanel? _racingRows;

    internal SettingsWindow(
        JsonSettingsStore store,
        ICredentialStore credentials,
        Action<bool> setStartupEnabled,
        Func<string, bool>? registerCaptureHotKey = null,
        Func<string, bool>? registerRecoveryHotKey = null,
        Func<string, bool>? registerRecordHotKey = null,
        Func<bool, bool>? setHotKeyPriority = null,
        ITranslationProvider? translationTestProvider = null)
    {
        InitializeComponent();
        if (translationTestProvider is null)
        {
            _translationTestClient = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            translationTestProvider = new OnlineTranslationProvider(_translationTestClient);
        }
        _translationTestProvider = translationTestProvider;
        Deactivated += (_, _) => EndHotKeyCapture();
        _registerCaptureHotKey = registerCaptureHotKey;
        _registerRecoveryHotKey = registerRecoveryHotKey;
        _registerRecordHotKey = registerRecordHotKey;

        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        _versionLabel = version is null ? "本地构建" : $"v{version.ToString(3)}";
        NavVersion.Text = _versionLabel;

        string modelRoot = Path.Combine(AppContext.BaseDirectory, "models");
        string[] required =
        [
            "v6/PP-OCRv6_det_small.onnx",
            "v6/PP-OCRv6_rec_small.onnx",
            "v6/ppocrv6_small_dict.txt",
            "v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"
        ];
        _localOcrReady = required.All(file => File.Exists(Path.Combine(modelRoot, file)));
        EngineStatus.Text = _localOcrReady ? "引擎就绪" : "模型缺失";
        EngineStatus.ToolTip = _localOcrReady
            ? "本地 PaddleOCR 模型已安装就绪，首次识别时自动载入。"
            : "本地 OCR 模型不完整，可重新安装 Flick 恢复。";
        if (!_localOcrReady)
            EngineDot.Fill = (Brush)FindResource("Brush.Warning");

        DesktopWindowChrome.Attach(this);
        StateChanged += (_, _) =>
        {
            bool maximized = WindowState == WindowState.Maximized;
            WindowFrame.CornerRadiusRadius = maximized ? 0 : 13;
            WindowFrame.Margin = maximized ? new Thickness(0) : new Thickness(16, 12, 16, 20);
            WindowFrame.ShadowLevel = maximized ? 0 : 3;
            WindowFrame.OutlineThickness = maximized ? 0 : 1;
        };

        _viewModel = new SettingsViewModel(store, credentials, setStartupEnabled, setHotKeyPriority);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        BuildNavigation();
        ShowPage("general");
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();

    private void DragTitleBar(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        if (e.ClickCount == 2)
        {
            if (WindowState == WindowState.Maximized)
                SystemCommands.RestoreWindow(this);
            else
                SystemCommands.MaximizeWindow(this);
        }
        else if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void BuildNavigation()
    {
        var basicGroup = AddNavigationGroup("basic", "基础偏好");
        AddNavigationItem("basic", basicGroup, "general", AppleVectorIcons.Create("settings", 16), "通用");
        AddNavigationItem("basic", basicGroup, "shortcuts", AppleVectorIcons.Create("keyboard", 16), "快捷键");

        var modulesGroup = AddNavigationGroup("modules", "功能模块");
        AddNavigationItem("modules", modulesGroup, "recording", AppleVectorIcons.Create("video", 16), "录屏");
        AddNavigationItem("modules", modulesGroup, "translation", AppleVectorIcons.Create("translate", 16), "识别与翻译");
    }

    private SettingsNavGroup AddNavigationGroup(string groupId, string title)
    {
        NavGroupsHost.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            Foreground = GetResource<Brush>("Settings.Brush.TextTertiary"),
            Margin = new Thickness(10, _navGroups.Count == 0 ? 2 : 16, 0, 6)
        });

        var group = new SettingsNavGroup();
        group.HoverEntered += activeGroup =>
        {
            foreach (var other in _navGroups.Values)
            {
                if (!ReferenceEquals(other, activeGroup))
                    other.ClearHover();
            }
        };

        _navGroups.Add(groupId, group);
        NavGroupsHost.Children.Add(group);
        return group;
    }

    private void AddNavigationItem(string groupId, SettingsNavGroup group, string id, FrameworkElement icon, string text)
    {
        var label = new TextBlock { Text = text, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
            Style = (Style)FindResource("SettingsNavButtonStyle")
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => ShowPage(id);
        _navigation.Add(id, button);
        _itemGroups.Add(id, groupId);
        group.AddItem(button);
    }

    private void ShowPage(string id)
    {
        if (string.Equals(id, "ocr", StringComparison.Ordinal))
        {
            id = "translation";
        }
        if (_activeNavId == id)
        {
            return;
        }
        EndHotKeyCapture();

        string previousGroupId = _activeGroupId;
        string nextGroupId = _itemGroups[id];
        bool isInitial = string.IsNullOrEmpty(previousGroupId);
        bool crossGroup = !isInitial && !string.Equals(previousGroupId, nextGroupId, StringComparison.Ordinal);

        _activeNavId = id;
        _activeGroupId = nextGroupId;

        foreach (var (key, button) in _navigation)
        {
            bool active = key == id;
            System.Windows.Controls.Primitives.Selector.SetIsSelected(button, active);
            button.Foreground = GetResource<Brush>(active ? "Settings.Brush.TextPrimary" : "Settings.Brush.NavText");
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            var content = (StackPanel)button.Content;
            var icon = (FrameworkElement)content.Children[0];
            var iconBrush = GetResource<Brush>(active ? "Settings.Brush.Accent" : "Settings.Brush.NavIcon");
            System.Windows.Documents.TextElement.SetForeground(icon, iconBrush);
        }

        if (crossGroup && _navGroups.TryGetValue(previousGroupId, out var oldGroup))
        {
            oldGroup.Deselect();
        }
        _navGroups[nextGroupId].Select(_navigation[id], crossGroup: crossGroup, instant: isInitial);

        if (!_pages.TryGetValue(id, out var page))
        {
            page = id switch
            {
                "shortcuts" => BuildShortcutsPage(),
                "recording" => BuildRecordingPage(),
                "translation" => BuildTranslationPage(),
                _ => BuildGeneralPage()
            };
            _pages[id] = page;
        }

        PageHost.Content = page;
        ApplyPageHeader(id);
        PageScroll.ScrollToTop();
        UpdateTranslationSections();
        AnimatePageGroups(page);
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
            case "translation":
                PageTitle.Text = "识别与翻译";
                PageDescription.Text = "免配置即可使用免费在线翻译；也可按需接入 AI 模型获得更自然的表达。";
                break;
            default:
                PageTitle.Text = "通用";
                PageDescription.Text = "控制 Flick 的启动方式与运行状态。";
                break;
        }
    }

    private void UpdateTranslationSections()
    {
        if (_edgeSection is null || _aiSection is null || _aiAdvancedSection is null || _racingRows is null)
        {
            return;
        }

        var ai = _viewModel.TranslationProviderIndex == 1;
        _edgeSection.Visibility = ai ? Visibility.Collapsed : Visibility.Visible;
        _aiSection.Visibility = ai ? Visibility.Visible : Visibility.Collapsed;
        _aiAdvancedSection.Visibility = _aiSection.Visibility;
        _racingRows.Visibility = _viewModel.EnableRacing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_testedInputs is not null && _testedInputs != _viewModel.BuildTranslationTestInputs())
        {
            ResetTranslationTest();
        }
        if (e.PropertyName is nameof(SettingsViewModel.TranslationProviderIndex) or nameof(SettingsViewModel.EnableRacing))
        {
            UpdateTranslationSections();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        EndHotKeyCapture();
        foreach (var group in _navGroups.Values)
            group.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ResetTranslationTest();
        _translationTestClient?.Dispose();
        _viewModel.Dispose();
    }
}
