using System.Windows;
using System.Windows.Controls;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Builders for the five Settings pages: General, Shortcuts, Recording, OCR,
/// Translation.  Each page is a single card containing grouped rows separated
/// by 1px dividers.
/// </summary>
public partial class SettingsWindow
{
    private FrameworkElement BuildGeneralPage()
    {
        var card = CreateCard(out var body);

        var startWithWindows = CreateBoundToggle(nameof(SettingsViewModel.StartWithWindows));
        body.Children.Add(CreateSettingRow(
            LucideIconKind.Settings2,
            "登录 Windows 后自动启动",
            "通过当前用户启动项运行，可随时在任务管理器中关闭",
            startWithWindows));

        return card;
    }

    private FrameworkElement BuildShortcutsPage()
    {
        var card = CreateCard(out var body);

        _captureHotKeyButton = CreateHotKeyButton(nameof(SettingsViewModel.HotKey));
        body.Children.Add(CreateSettingRow(
            LucideIconKind.ScanLine,
            "截图快捷键",
            "全局唤起区域截图，允许直接使用 F1–F24",
            _captureHotKeyButton));

        body.Children.Add(CreateDivider());

        _clickThroughRecoveryHotKeyButton = CreateHotKeyButton(nameof(SettingsViewModel.ClickThroughRecoveryHotKey));
        body.Children.Add(CreateSettingRow(
            LucideIconKind.Pin,
            "恢复贴图操作",
            "解除全部贴图的鼠标穿透，允许直接使用 F1–F24",
            _clickThroughRecoveryHotKeyButton));

        body.Children.Add(CreateDivider());
        body.Children.Add(CreateSettingRow(
            LucideIconKind.Settings2,
            "快捷键优先交给 Screen",
            "开启后，已设置的组合优先触发 Screen，其他软件可能收不到这些按键",
            CreateBoundToggle(nameof(SettingsViewModel.PreferScreenHotKeys))));

        return card;
    }

    private FrameworkElement BuildRecordingPage()
    {
        var card = CreateCard(out var body);

        var frameRateInput = CreateBoundComboBox(
            nameof(SettingsViewModel.RecordingFrameRateIndex),
            ["15 FPS（文件更小）", "30 FPS（推荐）", "60 FPS（更流畅）"]);
        body.Children.Add(CreateSettingRow(
            LucideIconKind.ScanLine,
            "帧率",
            "每秒采集的画面数，越高越流畅、文件越大",
            frameRateInput));

        body.Children.Add(CreateDivider());

        var qualityInput = CreateBoundComboBox(
            nameof(SettingsViewModel.RecordingQualityIndex),
            ["流畅（4 Mbps）", "标准（8 Mbps）", "高清（12 Mbps）"]);
        body.Children.Add(CreateSettingRow(
            LucideIconKind.Settings2,
            "清晰度",
            "编码目标码率，实际体积随画面复杂度变化",
            qualityInput));

        body.Children.Add(CreateDivider());

        var showCursor = CreateBoundToggle(nameof(SettingsViewModel.RecordingShowCursor));
        body.Children.Add(CreateSettingRow(
            LucideIconKind.MousePointer2,
            "录制鼠标光标",
            "默认值；开录前的面板上还可以临时切换",
            showCursor));

        body.Children.Add(CreateDivider());

        var systemAudio = CreateBoundToggle(nameof(SettingsViewModel.RecordingSystemAudio));
        body.Children.Add(CreateSettingRow(
            LucideIconKind.Video,
            "录制系统声音",
            "采集扬声器正在播放的声音（WASAPI 环回）",
            systemAudio));

        body.Children.Add(CreateDivider());

        var microphone = CreateBoundToggle(nameof(SettingsViewModel.RecordingMicrophone));
        body.Children.Add(CreateSettingRow(
            LucideIconKind.ScanText,
            "录制麦克风",
            "与系统声音自动混音；无设备时自动跳过",
            microphone));

        body.Children.Add(CreateDivider());

        var clickEffects = CreateBoundToggle(nameof(SettingsViewModel.RecordingClickEffects));
        body.Children.Add(CreateSettingRow(
            LucideIconKind.ScanLine,
            "点击波纹特效",
            "鼠标左键点击时显示扩散圆环，会被录进画面",
            clickEffects));

        body.Children.Add(CreateDivider());

        _recordHotKeyButton = CreateHotKeyButton(nameof(SettingsViewModel.RecordHotKey));
        body.Children.Add(CreateSettingRow(
            LucideIconKind.Video,
            "录屏快捷键",
            "全局唤起录屏选区，允许直接使用 F1–F24",
            _recordHotKeyButton));

        body.Children.Add(CreateFootnote(
            "录完可在编辑窗回放、裁切、复制或保存为 MP4 视频。"));

        return card;
    }

    private FrameworkElement BuildOcrPage()
    {
        var card = CreateCard(out var body);

        var modeInput = CreateBoundComboBox(
            nameof(SettingsViewModel.OcrModeIndex),
            ["智能增强（推荐）", "云端优先", "仅本地识别"]);
        body.Children.Add(CreateSettingRow(
            LucideIconKind.ScanText,
            "识别方式",
            "智能增强优先本地识别，困难内容自动联网增强",
            modeInput));

        body.Children.Add(CreateDivider());

        var useCloudForTranslation = CreateBoundToggle(nameof(SettingsViewModel.UseCloudForTranslation));
        body.Children.Add(CreateSettingRow(
            LucideIconKind.Cloud,
            "截图翻译时增强识别",
            "翻译前使用腾讯云高精度 OCR 重新识别选区",
            useCloudForTranslation));

        body.Children.Add(CreateDivider());

        var configureTencent = CreateSecondaryButton("配置密钥");
        configureTencent.Click += (_, _) =>
        {
            _viewModel.TranslationProviderIndex = 0;
            ShowPage("translation");
        };
        body.Children.Add(CreateSettingRow(
            LucideIconKind.Settings2,
            "腾讯云增强密钥",
            "OCR 增强与云端识别复用翻译接口页保存的密钥",
            configureTencent));

        body.Children.Add(CreateFootnote(
            "敏感截图请选择“仅本地识别”。智能增强和云端优先只上传当前框选区域，不会上传整个屏幕。"));

        return card;
    }

    private FrameworkElement BuildTranslationPage()
    {
        var card = CreateCard(out var body);

        var displayModeInput = CreateBoundComboBox(
            nameof(SettingsViewModel.TranslationDisplayModeIndex),
            ["浮动卡片", "原位覆盖"]);
        body.Children.Add(CreateSettingRow(
            LucideIconKind.ScanText,
            "翻译显示",
            "浮动卡片单独展示译文；原位覆盖在截图选区内替换文字",
            displayModeInput));

        body.Children.Add(CreateDivider());

        var providerInput = CreateBoundComboBox(
            nameof(SettingsViewModel.TranslationProviderIndex),
            ["腾讯机器翻译（快速直译）", "MyMemory 基础翻译（保底）", "AI 自然翻译（推荐）"]);
        body.Children.Add(CreateSettingRow(
            LucideIconKind.Languages,
            "翻译服务",
            "截图翻译默认使用的接口，可配置自动回退",
            providerInput));

        // ---------- Tencent ----------
        _tencentSection = CreateSection(withLeadingDivider: true);
        _tencentSection.Children.Add(CreateSectionHeader("腾讯机器翻译"));
        _tencentSection.Children.Add(CreateSettingRow(
            LucideIconKind.Copy,
            "SecretId",
            "腾讯云访问密钥 ID，仅保存在 Windows 凭据管理器",
            CreateBoundTextBox(nameof(SettingsViewModel.TencentSecretId))));
        _tencentSection.Children.Add(CreateSettingRow(
            LucideIconKind.Pencil,
            "SecretKey",
            "腾讯云访问密钥，不会写入 settings.json",
            CreateCredentialPasswordBox(
                nameof(SettingsViewModel.TencentSecretKey),
                password: _viewModel.TencentSecretKey)));
        _tencentSection.Children.Add(CreateSettingRow(
            LucideIconKind.Cloud,
            "接入地域",
            "通常选择离你最近的可用区即可",
            CreateBoundComboBox(
                nameof(SettingsViewModel.TencentRegionIndex),
                SettingsViewModel.TranslationRegions())));
        body.Children.Add(_tencentSection);

        // ---------- AI / OpenAI-compatible ----------
        _aiSection = CreateSection(withLeadingDivider: true);
        _aiSection.Children.Add(CreateSectionHeader("AI 自然翻译"));
        _aiSection.Children.Add(CreateSettingRow(
            LucideIconKind.Cloud,
            "基础地址",
            "OpenAI 兼容的 /chat/completions 接口地址",
            CreateBoundTextBox(nameof(SettingsViewModel.BaseAddress))));
        _aiSection.Children.Add(CreateSettingRow(
            LucideIconKind.Pencil,
            "模型",
            "建议使用擅长翻译与中文表达的对话模型",
            CreateBoundTextBox(nameof(SettingsViewModel.Model))));
        _aiSection.Children.Add(CreateSettingRow(
            LucideIconKind.Pencil,
            "API Key",
            "仅保存在 Windows 凭据管理器",
            CreateCredentialPasswordBox(
                nameof(SettingsViewModel.ApiKey),
                password: _viewModel.ApiKey)));

        _aiSection.Children.Add(CreateSettingRow(
            LucideIconKind.Settings2,
            "第二模型竞速",
            "两个模型同时翻译，自动采用最先返回的结果",
            CreateBoundToggle(nameof(SettingsViewModel.EnableRacing))));

        _racingRows = new StackPanel();
        _racingRows.Children.Add(CreateSettingRow(
            LucideIconKind.Cloud,
            "竞速模型基础地址",
            "第二个 OpenAI 兼容接口地址",
            CreateBoundTextBox(nameof(SettingsViewModel.RacingBaseAddress))));
        _racingRows.Children.Add(CreateSettingRow(
            LucideIconKind.Pencil,
            "竞速模型名称",
            "例如 Qwen/Qwen2.5-7B-Instruct",
            CreateBoundTextBox(nameof(SettingsViewModel.RacingModel))));
        _racingRows.Children.Add(CreateSettingRow(
            LucideIconKind.Pencil,
            "竞速模型 API Key",
            "仅保存在 Windows 凭据管理器",
            CreateCredentialPasswordBox(
                nameof(SettingsViewModel.RacingApiKey),
                password: _viewModel.RacingApiKey)));
        _aiSection.Children.Add(_racingRows);

        _aiSection.Children.Add(CreateSettingRow(
            LucideIconKind.Download,
            "超时后快速回退",
            "AI 翻译等待超时后自动切换腾讯快速翻译",
            CreateBoundToggle(nameof(SettingsViewModel.EnableFastFallbackRace))));

        _fastFallbackRow = CreateSettingRow(
            LucideIconKind.Settings2,
            "快速回退等待",
            "等待秒数，允许范围 2–30 秒",
            CreateBoundTextBox(nameof(SettingsViewModel.FastFallbackDelayText), 120));
        _aiSection.Children.Add(_fastFallbackRow);
        body.Children.Add(_aiSection);

        // ---------- MyMemory ----------
        _memorySection = CreateSection(withLeadingDivider: true);
        _memorySection.Children.Add(CreateSectionHeader("MyMemory 基础翻译"));
        _memorySection.Children.Add(CreateSettingRow(
            LucideIconKind.Copy,
            "联系邮箱（可选）",
            "随保底请求发送，用于提高匿名额度；留空也能使用",
            CreateBoundTextBox(nameof(SettingsViewModel.MyMemoryContactEmail))));
        body.Children.Add(_memorySection);

        // Fallback applies to both Tencent and AI providers; keep its control
        // visible regardless of which provider-specific section is selected.
        var fallbackSection = CreateSection(withLeadingDivider: true);
        fallbackSection.Children.Add(CreateSectionHeader("失败回退"));
        fallbackSection.Children.Add(CreateSettingRow(
            LucideIconKind.Check,
            "自动回退到 MyMemory",
            "AI 或腾讯翻译失败时，将原文发送给 MyMemory；关闭后不使用该保底服务",
            CreateBoundToggle(nameof(SettingsViewModel.EnableMyMemoryFallback))));
        fallbackSection.Children.Add(CreateFootnote(
            "AI 翻译失败时，如已配置腾讯密钥，会先尝试腾讯翻译。直接选择 MyMemory 时，原文始终发送给 MyMemory。"));
        body.Children.Add(fallbackSection);

        // ---------- Output / test ----------
        var outputSection = CreateSection(withLeadingDivider: true);
        outputSection.Children.Add(CreateSectionHeader("输出与测试"));
        outputSection.Children.Add(CreateSettingRow(
            LucideIconKind.Languages,
            "目标语言",
            "截图翻译默认输出的语言",
            CreateBoundComboBox(
                nameof(SettingsViewModel.TargetLanguageIndex),
                SettingsViewModel.TranslationLanguages())));
        outputSection.Children.Add(CreateSettingRow(
            LucideIconKind.Check,
            "测试当前服务",
            "使用固定文本验证接口连通性，不会修改当前截图",
            CreateTranslationTestControl()));
        body.Children.Add(outputSection);

        body.Children.Add(CreateFootnote(
            "所有密钥均通过 Windows 凭据管理器保存，不会写入 settings.json。"));

        return card;
    }

    private FrameworkElement CreateTranslationTestControl()
    {
        var testButton = CreateSecondaryButton("测试当前服务");
        testButton.Click += TestTranslationAsync;

        _translationTestStatus = new TextBlock
        {
            MaxWidth = 148,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = (double)FindResource("Settings.FontSize.SettingDescription"),
            Foreground = GetResource<System.Windows.Media.Brush>("Settings.Brush.TextTertiary"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(testButton);
        panel.Children.Add(_translationTestStatus);
        return panel;
    }
}
