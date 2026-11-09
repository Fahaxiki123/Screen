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
/// same structure: icon plate + two text lines + right-aligned control.
/// </summary>
public partial class SettingsWindow
{
    private T GetResource<T>(string key) where T : class => (T)FindResource(key);

    private FontWeight GetFontWeight(string key) => FindResource(key) switch
    {
        FontWeight weight => weight,
        string text when string.Equals(text, "SemiBold", StringComparison.OrdinalIgnoreCase) => FontWeights.SemiBold,
        string text when string.Equals(text, "Medium", StringComparison.OrdinalIgnoreCase) => FontWeights.Medium,
        string text when string.Equals(text, "Bold", StringComparison.OrdinalIgnoreCase) => FontWeights.Bold,
        _ => FontWeights.Normal
    };

    private Border CreateCard(out StackPanel body)
    {
        body = new StackPanel();
        return new Border
        {
            Style = GetResource<Style>("SettingsCardStyle"),
            // Never stretch to fill the page area — the card hugs its rows
            // and the remaining window space stays background.
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = body
        };
    }

    private Border CreateSettingRow(
        LucideIconKind icon,
        string title,
        string description,
        FrameworkElement control)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(CreateRowIcon(icon));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = (double)FindResource("Settings.FontSize.SettingTitle"),
            FontWeight = GetFontWeight("Settings.FontWeight.SettingTitle"),
            Foreground = GetResource<Brush>("Settings.Brush.TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = (double)FindResource("Settings.FontSize.SettingDescription"),
            FontWeight = GetFontWeight("Settings.FontWeight.SettingDescription"),
            Foreground = GetResource<Brush>("Settings.Brush.TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 4);
        grid.Children.Add(control);

        return new Border
        {
            MinHeight = 64,
            Padding = (Thickness)FindResource("Settings.Spacing.RowPadding"),
            Child = grid
        };
    }

    // A quiet accent icon keeps each setting aligned with the navigation.
    private LucideIconElement CreateRowIcon(LucideIconKind icon)
    {
        return new LucideIconElement
        {
            Kind = icon,
            Width = 19,
            Height = 19,
            StrokeThickness = 1.75,
            Stroke = GetResource<Brush>("Settings.Brush.AccentText"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
    }

    private Border CreateDivider()
    {
        return new Border
        {
            Height = 1,
            Margin = new Thickness(18, 0, 18, 0),
            Background = GetResource<Brush>("Settings.Brush.Divider"),
            SnapsToDevicePixels = true
        };
    }

    private TextBlock CreateSectionHeader(string text)
    {
        return new TextBlock
        {
            Text = text,
            Padding = (Thickness)FindResource("Settings.Spacing.SectionHeaderPadding"),
            FontSize = (double)FindResource("Settings.FontSize.Section"),
            FontWeight = GetFontWeight("Settings.FontWeight.Section"),
            Foreground = GetResource<Brush>("Settings.Brush.TextSecondary")
        };
    }

    private StackPanel CreateSection(bool withLeadingDivider)
    {
        var section = new StackPanel();
        if (withLeadingDivider)
        {
            section.Children.Add(CreateDivider());
        }
        return section;
    }

    private Border CreateFootnote(string text)
    {
        return new Border
        {
            Margin = new Thickness(18, 4, 18, 16),
            Padding = new Thickness(12, 9, 12, 9),
            Background = GetResource<Brush>("Settings.Brush.ControlBackgroundSubtle"),
            CornerRadius = (CornerRadius)FindResource("Settings.CornerRadius.Control"),
            Child = new TextBlock
            {
                Text = text,
                FontSize = (double)FindResource("Settings.FontSize.SettingDescription"),
                Foreground = GetResource<Brush>("Settings.Brush.TextSecondary"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20
            }
        };
    }

    private ToggleButton CreateBoundToggle(string propertyName)
    {
        var toggle = new ToggleButton
        {
            Style = GetResource<Style>("SettingsToggleStyle"),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggle.SetBinding(
            ToggleButton.IsCheckedProperty,
            new Binding(propertyName)
            {
                Source = _viewModel,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        return toggle;
    }

    private TextBox CreateBoundTextBox(string propertyName, double width = 280)
    {
        var textBox = new TextBox
        {
            Style = GetResource<Style>("SettingsTextBoxStyle"),
            Width = width
        };
        textBox.SetBinding(
            TextBox.TextProperty,
            new Binding(propertyName)
            {
                Source = _viewModel,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        return textBox;
    }

    private PasswordBox CreateCredentialPasswordBox(string propertyName, double width = 280, string? password = null)
    {
        var passwordBox = new PasswordBox
        {
            Style = GetResource<Style>("SettingsPasswordBoxStyle"),
            Width = width,
            Password = password ?? string.Empty
        };
        passwordBox.PasswordChanged += (_, _) => ApplyCredentialValue(propertyName, passwordBox.Password);
        return passwordBox;
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

    private ComboBox CreateBoundComboBox(string propertyName, IReadOnlyList<string> items, double width = 280)
    {
        var comboBox = new ComboBox
        {
            Style = GetResource<Style>("SettingsComboBoxStyle"),
            Width = width,
            ItemsSource = items
        };
        comboBox.SetBinding(
            ComboBox.SelectedIndexProperty,
            new Binding(propertyName)
            {
                Source = _viewModel,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        return comboBox;
    }

    private Button CreateSecondaryButton(string text)
    {
        return new Button
        {
            Content = text,
            Style = GetResource<Style>("SettingsSecondaryButtonStyle")
        };
    }

    private Button CreatePrimaryButton(string text)
    {
        return new Button
        {
            Content = text,
            Style = GetResource<Style>("SettingsPrimaryButtonStyle")
        };
    }

    private Button CreateHotKeyButton(string propertyName)
    {
        var button = new Button
        {
            Style = GetResource<Style>("SettingsHotKeyButtonStyle"),
            Content = BuildKeycapPanel(GetHotKeyValue(propertyName)),
            ToolTip = "点击后直接按新的快捷键"
        };
        button.Click += (_, _) => button.Focus();
        button.GotKeyboardFocus += (_, _) =>
        {
            button.Content = new TextBlock
            {
                Text = "请按新的快捷键…",
                FontSize = (double)FindResource("Settings.FontSize.Control"),
                FontWeight = GetFontWeight("Settings.FontWeight.SettingTitle"),
                Foreground = GetResource<Brush>("Settings.Brush.AccentText"),
                VerticalAlignment = VerticalAlignment.Center
            };
        };
        button.LostKeyboardFocus += (_, _) =>
        {
            button.Content = BuildKeycapPanel(GetHotKeyValue(propertyName));
        };
        button.PreviewKeyDown += (_, e) => CaptureHotKey(button, propertyName, e);
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

        var keys = hotKey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < keys.Length; index++)
        {
            if (index > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "+",
                    Margin = new Thickness(6, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = (double)FindResource("Settings.FontSize.Keycap"),
                    Foreground = GetResource<Brush>("Settings.Brush.TextTertiary")
                });
            }

            panel.Children.Add(new Border
            {
                Style = GetResource<Style>("SettingsKeycapStyle"),
                Child = new TextBlock
                {
                    Text = keys[index],
                    FontSize = (double)FindResource("Settings.FontSize.Keycap"),
                    FontWeight = GetFontWeight("Settings.FontWeight.Keycap"),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = GetResource<Brush>("Settings.Brush.TextPrimary")
                }
            });
        }

        return panel;
    }
}
