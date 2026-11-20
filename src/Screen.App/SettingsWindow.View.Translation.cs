using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Screen.App;

public partial class SettingsWindow
{
    private StackPanel? _tencentPrimaryHost;
    private StackPanel? _tencentAdvancedHost;
    private StackPanel? _tencentDisclosure;
    private Button? _tencentDisclosureButton;
    private TextBlock? _tencentSummary;
    private Button? _testConnection;
    private Ellipse? _testProgress;
    private bool _showTencentCredentials;

    private FrameworkElement BuildTranslationPage()
    {
        var page = new StackPanel();
        var serviceGroup = CreateCard(out var services);
        page.Children.Add(serviceGroup);
        services.Children.Add(CreateSettingRow("翻译服务", CreateBoundSegment(
            nameof(SettingsViewModel.TranslationServiceIndex), ["AI 翻译", "腾讯", "免费"])));

        _aiSection = new StackPanel();
        services.Children.Add(_aiSection);
        AddTranslationRow(_aiSection, "接口地址", CreateBoundTextBox(nameof(SettingsViewModel.BaseAddress)));
        AddTranslationRow(_aiSection, "模型", CreateBoundTextBox(nameof(SettingsViewModel.Model)));
        AddTranslationRow(_aiSection, "API Key", CreateApiKeyInput(racing: false));

        _tencentPrimaryHost = new StackPanel();
        services.Children.Add(_tencentPrimaryHost);
        // Move this one editor between the provider group and Advanced. A draft secret
        // never gets duplicated into a second password box or lost when changing providers.
        _tencentSection = new StackPanel();
        AddTranslationRow(_tencentSection, "SecretId", CreateBoundTextBox(nameof(SettingsViewModel.TencentSecretId)));
        AddTranslationRow(_tencentSection, "SecretKey", CreateCredentialPasswordBox(
            nameof(SettingsViewModel.TencentSecretKey), password: _viewModel.TencentSecretKey));
        AddTranslationRow(_tencentSection, "地域", CreateBoundComboBox(
            nameof(SettingsViewModel.TencentRegionIndex), SettingsViewModel.TranslationRegions()));

        _memorySection = new StackPanel();
        _memorySection.Children.Add(CreateFootnote("MyMemory 免费服务，无需配置，每天有额度限制。"));
        services.Children.Add(_memorySection);
        AddTranslationRow(services, "连接", CreateTranslationTestControl());
        page.Children.Add(CreateFootnote("密钥只保存在这台电脑上。"));

        var outputGroup = CreateCard(out var output);
        page.Children.Add(outputGroup);
        output.Children.Add(CreateSettingRow("翻译成", CreateBoundComboBox(
            nameof(SettingsViewModel.TargetLanguageIndex), SettingsViewModel.TranslationLanguages())));
        AddTranslationRow(output, "显示方式", CreateBoundSegment(nameof(SettingsViewModel.TranslationDisplayModeIndex), ["卡片", "覆盖原文"]));

        var advanced = new StackPanel();
        advanced.Children.Add(CreateSettingRow("文字识别", CreateBoundComboBox(
            nameof(SettingsViewModel.OcrModeIndex), ["智能（本机优先）", "云端优先", "仅本机"])));
        AddTranslationRow(advanced, "截图翻译时增强识别", CreateBoundToggle(nameof(SettingsViewModel.UseCloudForTranslation)));
        AddTranslationRow(advanced, "原文语言", CreateBoundComboBox(
            nameof(SettingsViewModel.SourceLanguageIndex), SettingsViewModel.SourceLanguageNames()));

        _tencentDisclosure = new StackPanel();
        _tencentSummary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        _tencentSummary.SetResourceReference(TextBlock.ForegroundProperty, "Brush.LabelSecondary");
        _tencentDisclosureButton = CreateSecondaryButton("设置…");
        _tencentDisclosureButton.Click += (_, _) =>
        {
            _showTencentCredentials = !_showTencentCredentials;
            UpdateTranslationSections();
        };
        var disclosureActions = new StackPanel { Orientation = Orientation.Horizontal };
        disclosureActions.Children.Add(_tencentSummary);
        disclosureActions.Children.Add(_tencentDisclosureButton);
        AddTranslationRow(_tencentDisclosure, "腾讯云密钥", disclosureActions);
        advanced.Children.Add(_tencentDisclosure);
        _tencentAdvancedHost = new StackPanel();
        advanced.Children.Add(_tencentAdvancedHost);

        AddTranslationRow(advanced, "同时请求第二个模型", CreateBoundToggle(nameof(SettingsViewModel.EnableRacing)));
        _racingRows = new StackPanel();
        AddTranslationRow(_racingRows, "第二模型地址", CreateBoundTextBox(nameof(SettingsViewModel.RacingBaseAddress)));
        AddTranslationRow(_racingRows, "第二模型", CreateBoundTextBox(nameof(SettingsViewModel.RacingModel)));
        AddTranslationRow(_racingRows, "第二模型 API Key", CreateApiKeyInput(racing: true));
        advanced.Children.Add(_racingRows);
        AddTranslationRow(advanced, "AI 超时后改用腾讯", CreateBoundToggle(nameof(SettingsViewModel.EnableFastFallbackRace)));
        var delay = new StackPanel { Orientation = Orientation.Horizontal };
        delay.Children.Add(CreateBoundTextBox(nameof(SettingsViewModel.FastFallbackDelayText), 68));
        delay.Children.Add(new TextBlock { Text = "秒", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
        _fastFallbackRow = CreateSettingRow("等待时间（2–30 秒）", delay);
        advanced.Children.Add(_fastFallbackRow);
        AddTranslationRow(advanced, "失败时改用免费服务", CreateBoundToggle(nameof(SettingsViewModel.EnableMyMemoryFallback)));
        var email = CreateBoundTextBox(nameof(SettingsViewModel.MyMemoryContactEmail));
        var emailHost = new Grid();
        emailHost.Children.Add(email);
        var placeholder = new TextBlock
        {
            Text = "可选",
            Margin = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        placeholder.SetResourceReference(TextBlock.ForegroundProperty, "Brush.LabelTertiary");
        void RefreshPlaceholder() => placeholder.Visibility = email.Text.Length == 0 && !email.IsKeyboardFocusWithin
            ? Visibility.Visible : Visibility.Collapsed;
        email.TextChanged += (_, _) => RefreshPlaceholder();
        email.IsKeyboardFocusWithinChanged += (_, _) => RefreshPlaceholder();
        RefreshPlaceholder();
        emailHost.Children.Add(placeholder);
        System.Windows.Automation.AutomationProperties.SetHelpText(email, "可选");
        AddTranslationRow(advanced, "MyMemory 联系邮箱", emailHost);
        advanced.Children.Add(CreateFootnote("AI 翻译失败时，如已配置腾讯密钥会先尝试腾讯；直接选择免费服务时原文始终发送给 MyMemory。"));
        page.Children.Add(CreateAdvanced(advanced));
        UpdateTranslationSections();
        return page;
    }

    private void AddTranslationRow(Panel section, string title, FrameworkElement input)
    {
        section.Children.Add(CreateDivider());
        section.Children.Add(CreateSettingRow(title, input));
    }

    private FrameworkElement CreateTranslationTestControl()
    {
        _testConnection = CreateSecondaryButton("测试连接");
        _testConnection.Click += TestTranslationAsync;
        _testProgress = new Ellipse
        {
            Width = 14,
            Height = 14,
            StrokeThickness = 2,
            StrokeDashArray = [13, 6],
            RenderTransform = new RotateTransform(),
            RenderTransformOrigin = new Point(0.5, 0.5),
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(6, 0, 0, 0)
        };
        _testProgress.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        _translationTestStatus = new TextBlock
        {
            MaxWidth = 190,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_testConnection);
        panel.Children.Add(_testProgress);
        panel.Children.Add(_translationTestStatus);
        return panel;
    }

    private void UpdateTranslationSections()
    {
        if (_tencentSection is null || _aiSection is null || _memorySection is null || _tencentPrimaryHost is null || _tencentAdvancedHost is null)
            return;
        var tencent = _viewModel.TranslationProviderIndex == 0;
        _aiSection.Visibility = _viewModel.TranslationProviderIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        _memorySection.Visibility = _viewModel.TranslationProviderIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        var host = tencent ? _tencentPrimaryHost : _tencentAdvancedHost;
        if (!ReferenceEquals(_tencentSection.Parent, host))
        {
            (_tencentSection.Parent as Panel)?.Children.Remove(_tencentSection);
            host.Children.Add(_tencentSection);
        }
        _tencentAdvancedHost.Visibility = !tencent && _showTencentCredentials ? Visibility.Visible : Visibility.Collapsed;
        _tencentDisclosure!.Visibility = tencent ? Visibility.Collapsed : Visibility.Visible;
        _tencentDisclosureButton!.Content = _showTencentCredentials ? "收起" : "设置…";
        _tencentSummary!.Text = !string.IsNullOrWhiteSpace(_viewModel.TencentSecretId) && !string.IsNullOrWhiteSpace(_viewModel.TencentSecretKey)
            ? "已配置" : "未配置";
        _racingRows!.Visibility = _viewModel.EnableRacing ? Visibility.Visible : Visibility.Collapsed;
        _fastFallbackRow!.Visibility = _viewModel.EnableFastFallbackRace ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.TranslationProviderIndex) or nameof(SettingsViewModel.EnableRacing)
            or nameof(SettingsViewModel.EnableFastFallbackRace) or nameof(SettingsViewModel.TencentSecretId) or nameof(SettingsViewModel.TencentSecretKey))
            UpdateTranslationSections();
        // Persisted, unrelated settings should not invalidate a completed network test.
        if (e.PropertyName is nameof(SettingsViewModel.TranslationProviderIndex) or nameof(SettingsViewModel.BaseAddress)
            or nameof(SettingsViewModel.Model) or nameof(SettingsViewModel.ApiKey) or nameof(SettingsViewModel.CanBindApiKey)
            or nameof(SettingsViewModel.TencentSecretId) or nameof(SettingsViewModel.TencentSecretKey) or nameof(SettingsViewModel.TencentRegionIndex)
            or nameof(SettingsViewModel.EnableRacing) or nameof(SettingsViewModel.RacingBaseAddress) or nameof(SettingsViewModel.RacingModel)
            or nameof(SettingsViewModel.RacingApiKey) or nameof(SettingsViewModel.CanBindRacingApiKey)
            or nameof(SettingsViewModel.EnableFastFallbackRace) or nameof(SettingsViewModel.FastFallbackDelayText)
            or nameof(SettingsViewModel.EnableMyMemoryFallback) or nameof(SettingsViewModel.MyMemoryContactEmail))
            ResetConnectionTest();
    }
}
