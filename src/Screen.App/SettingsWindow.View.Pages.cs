using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

public partial class SettingsWindow
{
    private FrameworkElement BuildGeneralPage()
    {
        var page = new StackPanel();
        AddGroup(page, "启动", out var startup);
        AddSettingRow(startup, LucideIconKind.Settings2, "登录 Windows 后自动启动",
            "通过当前用户启动项运行，可随时在任务管理器中关闭",
            CreateBoundToggle(nameof(SettingsViewModel.StartWithWindows)));

        AddGroup(page, "运行状态与版本", out var status);
        AddSettingRow(status, LucideIconKind.Check, "本地 OCR 引擎",
            _localOcrReady
                ? "PP-OCRv6 轻量模型已安装就绪，首次识别时自动载入"
                : "本地 OCR 模型缺失，可重新安装 Flick 恢复",
            CreateStatusBadge(_localOcrReady ? "已就绪" : "模型缺失", _localOcrReady));
        AddSettingRow(status, LucideIconKind.Clock, "Flick 版本",
            "所有设置修改实时生效并自动保存",
            CreateVersionBadge(_versionLabel));
        return page;
    }

    private FrameworkElement BuildShortcutsPage()
    {
        var page = new StackPanel();
        AddGroup(page, "全局快捷键", out var shortcuts);
        _captureHotKeyButton = CreateHotKeyButton(nameof(SettingsViewModel.HotKey));
        AddSettingRow(shortcuts, LucideIconKind.ScanLine, "截图快捷键", "全局唤起区域截图，支持直接使用 F1–F24", _captureHotKeyButton);
        _clickThroughRecoveryHotKeyButton = CreateHotKeyButton(nameof(SettingsViewModel.ClickThroughRecoveryHotKey));
        AddSettingRow(shortcuts, LucideIconKind.Pin, "恢复贴图操作", "解除全部贴图的鼠标穿透，支持直接使用 F1–F24", _clickThroughRecoveryHotKeyButton);

        AddGroup(page, "快捷键冲突", out var priority);
        AddSettingRow(priority, LucideIconKind.Keyboard, "快捷键优先交给 Flick",
            "开启后，已设置的组合优先触发 Flick，其他软件可能收不到这些按键",
            CreateBoundToggle(nameof(SettingsViewModel.PreferScreenHotKeys)));
        return page;
    }

    private FrameworkElement BuildRecordingPage()
    {
        var page = new StackPanel();
        AddGroup(page, "画面质量", out var quality);
        AddSettingRow(quality, LucideIconKind.Video, "帧率", "每秒采集的画面数，越高越流畅，文件越大",
            CreateBoundComboBox(nameof(SettingsViewModel.RecordingFrameRateIndex),
                ["15 FPS（文件更小）", "30 FPS（推荐）", "60 FPS（更流畅）"]));
        AddSettingRow(quality, LucideIconKind.Settings2, "清晰度", "编码目标码率，实际体积随画面复杂度变化",
            CreateBoundComboBox(nameof(SettingsViewModel.RecordingQualityIndex),
                ["流畅（4 Mbps）", "标准（8 Mbps）", "高清（12 Mbps）"]));

        AddGroup(page, "光标与声音", out var inputs);
        AddSettingRow(inputs, LucideIconKind.MousePointer2, "录制鼠标光标", "在录屏画面中显示鼠标指针",
            CreateBoundToggle(nameof(SettingsViewModel.RecordingShowCursor)));
        AddSettingRow(inputs, LucideIconKind.MousePointer2, "点击波纹特效", "鼠标左键点击时显示扩散圆环，可在录制准备栏临时切换",
            CreateBoundToggle(nameof(SettingsViewModel.RecordingClickEffects)));
        AddSettingRow(inputs, LucideIconKind.Volume2, "录制系统声音", "采集扬声器正在播放的声音（WASAPI 环回）",
            CreateBoundToggle(nameof(SettingsViewModel.RecordingSystemAudio)));
        AddSettingRow(inputs, LucideIconKind.Mic, "录制麦克风", "与系统声音自动混音，无设备时自动跳过",
            CreateBoundToggle(nameof(SettingsViewModel.RecordingMicrophone)));

        AddGroup(page, "录屏快捷键", out var shortcuts);
        _recordHotKeyButton = CreateHotKeyButton(nameof(SettingsViewModel.RecordHotKey));
        AddSettingRow(shortcuts, LucideIconKind.Keyboard, "录屏快捷键", "全局唤起录屏选区，支持直接使用 F1–F24", _recordHotKeyButton);
        AddFootnote(shortcuts, "录制后可回放、裁切、复制，或保存为 MP4 视频和 GIF 动图。");
        return page;
    }

    private FrameworkElement BuildTranslationPage()
    {
        var page = new StackPanel();
        AddGroup(page, "翻译偏好", out var preferences);
        AddSettingRow(preferences, LucideIconKind.ScanText, "默认显示方式", "可在翻译卡片和原位悬浮条上随时一键互切",
            CreateBoundComboBox(nameof(SettingsViewModel.TranslationDisplayModeIndex), ["浮动卡片", "原位覆盖"]));
        AddSettingRow(preferences, LucideIconKind.Languages, "目标语言", "截图翻译默认输出的语言",
            CreateBoundComboBox(nameof(SettingsViewModel.TargetLanguageIndex), SettingsViewModel.TranslationLanguages()));
        AddSettingRow(preferences, LucideIconKind.Cloud, "翻译服务", "开箱即可免费使用；追求母语级表达可切换 AI 自然翻译",
            CreateBoundComboBox(nameof(SettingsViewModel.TranslationProviderIndex),
                ["免费在线翻译（推荐 · 免配置）", "AI 自然翻译（填 Key 效果最佳）", "腾讯机器翻译（传统机翻）", "MyMemory（基础兜底）"], width: 242));

        _edgeSection = AddGroup(page, "免费在线翻译", out var edge);
        AddSettingRow(edge, LucideIconKind.Cloud, "零配置在线通道",
            "无需申请 API Key，支持自动识别中英混排与原位批量翻译；可点击下方“测试当前服务”验证网络连通性",
            CreateVersionBadge("免配置在线"));

        _aiSection = new StackPanel();
        page.Children.Add(_aiSection);
        AddGroup(_aiSection, "AI 自然翻译", out var ai);
        AddSettingRow(ai, LucideIconKind.Cloud, "服务商预设", "选择预设自动填入直连地址与推荐模型，仅需粘贴 API Key",
            CreateBoundComboBox(nameof(SettingsViewModel.AiPresetIndex),
                TranslationPresets.All.Select(preset => preset.DisplayName).ToArray(), width: 224));
        AddSettingRow(ai, LucideIconKind.Cloud, "基础地址", "OpenAI 兼容的 /chat/completions 接口地址",
            CreateBoundTextBox(nameof(SettingsViewModel.BaseAddress)));
        AddSettingRow(ai, LucideIconKind.Pencil, "模型", "建议使用响应快、擅长中文表达的对话模型",
            CreateBoundTextBox(nameof(SettingsViewModel.Model)));
        AddSettingRow(ai, LucideIconKind.Pencil, "API Key", "仅保存在 Windows 凭据管理器，更换地址自动隔离", CreateApiKeyInput(racing: false));

        _tencentSection = AddGroup(page, "腾讯机器翻译与云端 OCR 密钥", out var tencent);
        AddSettingRow(tencent, LucideIconKind.Copy, "SecretId", "腾讯云访问密钥 ID，仅保存在 Windows 凭据管理器",
            CreateBoundTextBox(nameof(SettingsViewModel.TencentSecretId)));
        AddSettingRow(tencent, LucideIconKind.Pencil, "SecretKey", "腾讯云访问密钥，仅保存在 Windows 凭据管理器",
            CreateCredentialPasswordBox(nameof(SettingsViewModel.TencentSecretKey), password: _viewModel.TencentSecretKey));
        AddSettingRow(tencent, LucideIconKind.Cloud, "接入地域", "通常选择离你最近的可用区即可",
            CreateBoundComboBox(nameof(SettingsViewModel.TencentRegionIndex), SettingsViewModel.TranslationRegions()));
        AddFootnote(tencent, "注：腾讯云传统机器翻译已停止新购，建议优先使用免费在线翻译或 AI 自然翻译。");

        _memorySection = AddGroup(page, "MyMemory 基础翻译", out var memory);
        AddSettingRow(memory, LucideIconKind.Copy, "联系邮箱（可选）", "随请求发送可提高匿名日额度，留空也能使用",
            CreateBoundTextBox(nameof(SettingsViewModel.MyMemoryContactEmail)));

        AddGroup(page, "连接测试", out var test);
        AddSettingRow(test, LucideIconKind.Check, "测试当前服务", "使用固定文本验证当前翻译服务连通性", CreateTranslationTestControl());

        // 将低频的 OCR 策略、AI 竞速/快退和保底回退折叠收起，保持主界面清爽。
        AddCollapsibleSection(page, "高级选项", "文字识别策略、AI 竞速与失败回退配置", out var advanced);

        AddGroup(advanced, "文字识别（OCR）", out var recognition);
        AddSettingRow(recognition, LucideIconKind.ScanText, "识别方式", "默认已自带本地 PP-OCRv6 模型，配齐腾讯密钥可启用云端增强",
            CreateBoundComboBox(nameof(SettingsViewModel.OcrModeIndex), ["智能增强（推荐）", "云端优先", "仅本地识别"]));
        AddSettingRow(recognition, LucideIconKind.Cloud, "截图翻译时云端增强", "已配腾讯云密钥时，在翻译前叠加腾讯云高精度 OCR",
            CreateBoundToggle(nameof(SettingsViewModel.UseCloudForTranslation)));
        var configureTencent = CreateSecondaryButton("展开密钥");
        configureTencent.Click += (_, _) =>
        {
            if (_tencentSection is { } credentials)
            {
                _tencentSectionForcedVisible = true;
                UpdateTranslationSections();
                PageScroll.UpdateLayout();
                credentials.BringIntoView();
            }
        };
        AddSettingRow(recognition, LucideIconKind.Settings2, "腾讯云增强密钥", "云端 OCR 与腾讯翻译共用下方保存的腾讯云密钥", configureTencent);
        AddFootnote(recognition, "敏感截图请选择“仅本地识别”，绝不上传图像到云端。");

        _aiAdvancedSection = new StackPanel();
        advanced.Children.Add(_aiAdvancedSection);
        AddGroup(_aiAdvancedSection, "AI 竞速与超时回退", out var aiAdvanced);
        AddSettingRow(aiAdvanced, LucideIconKind.Settings2, "第二模型竞速", "两个模型同时翻译，自动采用最先返回的结果",
            CreateBoundToggle(nameof(SettingsViewModel.EnableRacing)));
        _racingRows = AddGroup(_aiAdvancedSection, "竞速模型", out var racing);
        AddSettingRow(racing, LucideIconKind.Cloud, "基础地址", "第二个 OpenAI 兼容接口地址",
            CreateBoundTextBox(nameof(SettingsViewModel.RacingBaseAddress)));
        AddSettingRow(racing, LucideIconKind.Pencil, "模型名称", "例如 Qwen/Qwen2.5-7B-Instruct",
            CreateBoundTextBox(nameof(SettingsViewModel.RacingModel)));
        AddSettingRow(racing, LucideIconKind.Pencil, "API Key", "仅保存在 Windows 凭据管理器", CreateApiKeyInput(racing: true));

        AddGroup(_aiAdvancedSection, "快速回退", out var fast);
        AddSettingRow(fast, LucideIconKind.Download, "超时后快速回退", "AI 翻译等待超时后自动切换腾讯快速翻译",
            CreateBoundToggle(nameof(SettingsViewModel.EnableFastFallbackRace)));
        _fastFallbackRow = AddSettingRow(fast, LucideIconKind.Clock, "快速回退等待", "等待秒数，允许范围 2–30 秒",
            CreateBoundTextBox(nameof(SettingsViewModel.FastFallbackDelayText), 80));

        AddGroup(advanced, "失败自动保底", out var fallback);
        AddSettingRow(fallback, LucideIconKind.Check, "首选失败时自动回退免费翻译", "AI 或腾讯失败时依次尝试免费在线翻译与 MyMemory",
            CreateBoundToggle(nameof(SettingsViewModel.EnableMyMemoryFallback)));
        AddFootnote(fallback, "开启后可在主服务异常或超时时自动保底，避免中断工作流。");

        return page;
    }

    private FrameworkElement CreateTranslationTestControl()
    {
        var testButton = _translationTestButton = CreateSecondaryButton("测试当前服务");
        testButton.HorizontalAlignment = HorizontalAlignment.Right;
        testButton.Click += TestTranslationAsync;
        _translationTestStatus = new TextBlock
        {
            Text = "未测试",
            MaxWidth = 190,
            FontSize = 12,
            Foreground = RowDescriptionBrush,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var panel = new StackPanel();
        panel.Children.Add(testButton);
        panel.Children.Add(_translationTestStatus);
        return panel;
    }
}
