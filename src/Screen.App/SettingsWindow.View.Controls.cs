using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// Reusable WPF building blocks for the Settings pages. Every row follows the
/// same structure: optional icon tile, label and right-aligned control.
/// </summary>
public partial class SettingsWindow
{
    private T GetResource<T>(string key) where T : class => (T)FindResource(key);

    private Border CreateCard(out StackPanel body)
    {
        body = new StackPanel();
        return new Border { Style = GetResource<Style>("SettingsGroup"), Child = body };
    }

    private Border CreateSettingRow(string title, FrameworkElement control, LucideIconKind? icon = null, string? palette = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (icon.HasValue)
        {
            var tile = new IconTile { Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            tile.SetResourceReference(BackgroundProperty, palette ?? "Tile.Gray");
            tile.Content = title is "默认格式" or "画质"
                ? new TextBlock { Text = title == "画质" ? "HD" : "GIF", FontWeight = FontWeights.Bold }
                : new LucideIconElement { Kind = icon.Value, Width = 18, Height = 18, StrokeThickness = 1.8, Stroke = GetResource<Brush>("Brush.White") };
            grid.Children.Add(tile);
        }
        var label = new TextBlock { Text = title, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Label");
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        System.Windows.Automation.AutomationProperties.SetLabeledBy(control, label);
        Grid.SetColumn(control, 2);
        grid.Children.Add(control);
        return new Border { Style = GetResource<Style>("SettingsRow"), Child = grid };
    }

    private Border CreateDivider(bool withIcon = false) =>
        new() { Style = GetResource<Style>(withIcon ? "SettingsIconDivider" : "SettingsDivider") };

    private TextBlock CreateSectionHeader(string text)
    {
        var title = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 5, 0, 5) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.LabelSecondary");
        return title;
    }

    private Border CreateFootnote(string text)
    {
        var label = new TextBlock { Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap, LineHeight = 17 };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.LabelSecondary");
        return new Border { Child = label, Padding = new Thickness(12, 0, 12, 7) };
    }

    private Expander CreateAdvanced(FrameworkElement content)
    {
        var expander = new Expander { Header = "高级", Content = content, Style = GetResource<Style>("SettingsAdvancedStyle") };
        return expander;
    }

    private Binding FieldBinding(string property) => new(property)
    {
        Source = _viewModel,
        Mode = BindingMode.TwoWay,
        UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
    };

    private ToggleButton CreateBoundToggle(string propertyName)
    {
        var toggle = new ToggleButton { Style = GetResource<Style>("ToggleSwitchStyle") };
        toggle.SetBinding(ToggleButton.IsCheckedProperty, FieldBinding(propertyName));
        return toggle;
    }

    private SegmentedControl CreateBoundSegment(string propertyName, IReadOnlyList<string> labels)
    {
        var segmented = new SegmentedControl { ItemsSource = labels };
        segmented.SetBinding(SegmentedControl.SelectedIndexProperty, FieldBinding(propertyName));
        return segmented;
    }

    private TextBox CreateBoundTextBox(string propertyName, double width = 250)
    {
        var text = new TextBox { Width = width, Style = GetResource<Style>("SettingsFieldText") };
        text.SetBinding(TextBox.TextProperty, FieldBinding(propertyName));
        return text;
    }

    private PasswordBox CreateCredentialPasswordBox(string propertyName, double width = 250, string? password = null)
    {
        var box = new PasswordBox { Width = width, Password = password ?? string.Empty, Style = GetResource<Style>("SettingsFieldPassword") };
        box.PasswordChanged += (_, _) => ApplyCredentialValue(propertyName, box.Password);
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != propertyName)
                return;
            var current = propertyName switch
            {
                nameof(SettingsViewModel.ApiKey) => _viewModel.ApiKey,
                nameof(SettingsViewModel.RacingApiKey) => _viewModel.RacingApiKey,
                nameof(SettingsViewModel.TencentSecretKey) => _viewModel.TencentSecretKey,
                nameof(SettingsViewModel.TencentSecretId) => _viewModel.TencentSecretId,
                _ => box.Password
            };
            if (box.Password != current)
                box.Password = current;
        };
        return box;
    }
    private void ApplyCredentialValue(string propertyName, string value)
    {
        switch (propertyName)
        {
            case nameof(SettingsViewModel.TencentSecretId):
                _viewModel.TencentSecretId = value;
                break;
            case nameof(SettingsViewModel.TencentSecretKey):
                _viewModel.TencentSecretKey = value;
                break;
            case nameof(SettingsViewModel.ApiKey):
                _viewModel.ApiKey = value;
                break;
            case nameof(SettingsViewModel.RacingApiKey):
                _viewModel.RacingApiKey = value;
                break;
        }
    }

    private FrameworkElement CreateApiKeyInput(bool racing)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(CreateCredentialPasswordBox(
            racing ? nameof(SettingsViewModel.RacingApiKey) : nameof(SettingsViewModel.ApiKey),
            width: 174, password: racing ? _viewModel.RacingApiKey : _viewModel.ApiKey));

        var bind = CreateSecondaryButton("绑定到此地址");
        bind.HorizontalAlignment = HorizontalAlignment.Right;
        bind.SetBinding(VisibilityProperty, new Binding(racing
            ? nameof(SettingsViewModel.CanBindRacingApiKey) : nameof(SettingsViewModel.CanBindApiKey))
        {
            Source = _viewModel,
            Converter = new BooleanToVisibilityConverter()
        });
        bind.SetBinding(ToolTipProperty, new Binding(racing
            ? nameof(SettingsViewModel.RacingBaseAddress) : nameof(SettingsViewModel.BaseAddress))
        {
            Source = _viewModel
        });
        bind.Click += (_, _) => _viewModel.BindDraftKey(racing);
        panel.Children.Add(bind);
        return panel;
    }

    private ComboBox CreateBoundComboBox(string propertyName, IReadOnlyList<string> items, double width = 155)
    {
        var combo = new ComboBox { Width = width, Height = 29, ItemsSource = items };
        combo.SetBinding(ComboBox.SelectedIndexProperty, FieldBinding(propertyName));
        return combo;
    }

    private Button CreateSecondaryButton(string text) =>
        new() { Content = text, Style = GetResource<Style>("SettingsFieldButton") };
    private Button CreateHotKeyButton(string propertyName)
    {
        var button = new HotKeyCaptureButton
        {
            Style = GetResource<Style>("SettingsKeyCaptureButton"),
            Content = BuildKeycapPanel(GetHotKeyValue(propertyName)),
            ToolTip = "点击或按 Enter 开始录入，Esc 取消"
        };
        button.ShortcutEntered += (key, modifiers) => CaptureHotKey(button, propertyName, key, modifiers);
        return button;
    }

    private string GetHotKeyValue(string propertyName) => propertyName switch
    {
        nameof(SettingsViewModel.ClickThroughRecoveryHotKey) => _viewModel.ClickThroughRecoveryHotKey,
        nameof(SettingsViewModel.RecordHotKey) => _viewModel.RecordHotKey,
        _ => _viewModel.HotKey
    };

    private StackPanel BuildKeycapPanel(string hotKey)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        foreach (var key in hotKey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            panel.Children.Add(new Border
            {
                Style = GetResource<Style>("KeycapStyle"),
                Margin = new Thickness(2, 0, 2, 0),
                Child = new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
            });
        }
        return panel;
    }
}
