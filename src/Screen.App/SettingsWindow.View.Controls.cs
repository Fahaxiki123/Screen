using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    private static StackPanel AddGroup(Panel page, string title, out StackPanel rows)
    {
        rows = new StackPanel();
        var card = new SquircleGlassBorder
        {
            CornerRadiusRadius = 10,
            ShowRim = false,
            ShadowLevel = 0,
            OutlineBrush = CardOutlineBrush,
            OutlineThickness = 1,
            GlassBackground = Brushes.White,
            Child = rows
        };
        var group = new StackPanel { Tag = card, Margin = new Thickness(0, 0, 0, 18) };
        group.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = RowDescriptionBrush,
            Margin = new Thickness(2, 0, 0, 7)
        });
        group.Children.Add(card);
        page.Children.Add(group);
        return group;
    }

    private static readonly SolidColorBrush RowTitleBrush = FrozenBrush(0xFF, 0x11, 0x18, 0x27);
    private static readonly SolidColorBrush RowDescriptionBrush = FrozenBrush(0xFF, 0x8A, 0x94, 0xA6);
    private static readonly SolidColorBrush IconPlateBrush = FrozenBrush(0xFF, 0xF2, 0xF5, 0xFA);
    private static readonly SolidColorBrush DividerBrush = FrozenBrush(0x0E, 0x0F, 0x17, 0x2A);
    private static readonly SolidColorBrush CardOutlineBrush = FrozenBrush(0x13, 0x0F, 0x17, 0x2A);
    private static readonly SolidColorBrush KeycapOutlineBrush = FrozenBrush(0x1A, 0x0F, 0x17, 0x2A);
    private static readonly SolidColorBrush ReadyBadgeBackground = FrozenBrush(0xFF, 0xDC, 0xFC, 0xE7);
    private static readonly SolidColorBrush ReadyBadgeForeground = FrozenBrush(0xFF, 0x15, 0x80, 0x3D);
    private static readonly SolidColorBrush ReadyBadgeDot = FrozenBrush(0xFF, 0x22, 0xC5, 0x5E);
    private static readonly SolidColorBrush WarningBadgeBackground = FrozenBrush(0xFF, 0xFE, 0xF3, 0xC7);
    private static readonly SolidColorBrush WarningBadgeForeground = FrozenBrush(0xFF, 0xB4, 0x53, 0x09);
    private static readonly SolidColorBrush WarningBadgeDot = FrozenBrush(0xFF, 0xF5, 0x9E, 0x0B);
    private static readonly SolidColorBrush VersionBadgeBackground = FrozenBrush(0xFF, 0xF1, 0xF5, 0xF9);
    private static readonly SolidColorBrush VersionBadgeForeground = FrozenBrush(0xFF, 0x64, 0x74, 0x8B);

    private static SolidColorBrush FrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static void AnimatePageGroups(FrameworkElement page)
    {
        if (page is not StackPanel panel)
            return;
        var index = 0;
        Animate(panel);
        void Animate(StackPanel parent)
        {
            foreach (var child in parent.Children)
            {
                if (child is not StackPanel { Visibility: Visibility.Visible } group)
                    continue;
                if (group.Tag is not SquircleGlassBorder card)
                {
                    Animate(group);
                    continue;
                }
                var delay = TimeSpan.FromMilliseconds(32 * index++);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var rise = new TranslateTransform(0, 3);
                card.RenderTransform = rise;
                card.Opacity = 0.25;
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(180))
                {
                    BeginTime = delay,
                    EasingFunction = ease
                });
                rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(3, 0, TimeSpan.FromMilliseconds(180))
                {
                    BeginTime = delay,
                    EasingFunction = ease
                });
            }
        }
    }

    private FrameworkElement AddSettingRow(Panel rows, LucideIconKind icon, string title, string description, FrameworkElement control)
    {
        var grid = new Grid { MinHeight = 30, Margin = new Thickness(16, 14, 16, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(CreateRowIcon(icon));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 12, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = RowTitleBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = RowDescriptionBrush,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 16
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        AutomationProperties.SetName(control, title);
        Grid.SetColumn(control, 2);
        grid.Children.Add(control);

        var row = new Grid { MinHeight = 58 };
        if (rows.Children.Count > 0)
        {
            row.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(16, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Background = DividerBrush,
                SnapsToDevicePixels = true
            });
        }
        row.Children.Add(grid);
        rows.Children.Add(row);
        return row;
    }

    private FrameworkElement CreateRowIcon(LucideIconKind kind)
    {
        var color = GetResource<Brush>("Settings.Brush.TextSecondary");
        var plate = new SquircleGlassBorder
        {
            Width = 32,
            Height = 32,
            CornerRadiusRadius = 7.5,
            GlassBackground = IconPlateBrush,
            ShowRim = false,
            ShadowLevel = 0,
            Child = new LucideIconElement { Kind = kind, Width = 16, Height = 16 },
            VerticalAlignment = VerticalAlignment.Center
        };
        TextElement.SetForeground(plate, color);
        return plate;
    }

    private static void AddFootnote(Panel rows, string text)
    {
        rows.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = RowDescriptionBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17,
            Margin = new Thickness(16, 0, 16, 14)
        });
    }

    private ToggleButton CreateBoundToggle(string propertyName)
    {
        var toggle = new ToggleButton
        {
            Style = GetResource<Style>("AppleSwitchStyle"),
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

    private TextBox CreateBoundTextBox(string propertyName, double width = 190)
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

    private PasswordBox CreateCredentialPasswordBox(string propertyName, double width = 190, string? password = null)
    {
        var passwordBox = new PasswordBox
        {
            Style = GetResource<Style>("SettingsPasswordBoxStyle"),
            Width = width,
            Password = password ?? string.Empty
        };
        passwordBox.PasswordChanged += (_, _) => ApplyCredentialValue(propertyName, passwordBox.Password);
        if (propertyName is nameof(SettingsViewModel.ApiKey) or nameof(SettingsViewModel.RacingApiKey))
        {
            _viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == propertyName)
                {
                    var key = propertyName == nameof(SettingsViewModel.ApiKey) ? _viewModel.ApiKey : _viewModel.RacingApiKey;
                    if (passwordBox.Password != key)
                    {
                        passwordBox.Password = key;
                    }
                }
            };
        }
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

    private FrameworkElement CreateApiKeyInput(bool racing)
    {
        var panel = new StackPanel();
        panel.Children.Add(CreateCredentialPasswordBox(
            racing ? nameof(SettingsViewModel.RacingApiKey) : nameof(SettingsViewModel.ApiKey),
            password: racing ? _viewModel.RacingApiKey : _viewModel.ApiKey));

        var bind = CreateSecondaryButton("绑定 Key 到当前地址");
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

    private ComboBox CreateBoundComboBox(string propertyName, IReadOnlyList<string> items, double width = 190)
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

    private Button CreateHotKeyButton(string propertyName)
    {
        var button = new Button
        {
            Style = GetResource<Style>("SettingsHotKeyButtonStyle"),
            Content = BuildKeycapPanel(GetHotKeyValue(propertyName)),
            ToolTip = "点击后直接按新的快捷键"
        };
        button.Click += (_, _) =>
        {
            button.Focus();
            if (button.IsKeyboardFocused)
                BeginHotKeyCapture(button, propertyName);
        };
        button.GotKeyboardFocus += (_, _) => BeginHotKeyCapture(button, propertyName);
        button.LostKeyboardFocus += (_, _) =>
        {
            if (_hotKeyEditor?.Button == button)
                EndHotKeyCapture();
        };
        button.PreviewKeyDown += OnHotKeyInput;
        return button;
    }

    private void BeginHotKeyCapture(Button button, string propertyName)
    {
        if (_hotKeyEditor?.Button == button)
            return;
        EndHotKeyCapture();
        button.Content = new TextBlock
        {
            Text = "请按新的快捷键…",
            FontSize = (double)FindResource("Settings.FontSize.Control"),
            FontWeight = GetFontWeight("Settings.FontWeight.SettingTitle"),
            Foreground = GetResource<Brush>("Settings.Brush.AccentText"),
            VerticalAlignment = VerticalAlignment.Center
        };
        _hotKeyEditor = (button, propertyName);
    }

    private void EndHotKeyCapture()
    {
        if (_hotKeyEditor is not { } editor)
            return;
        _hotKeyEditor = null;
        editor.Button.Content = BuildKeycapPanel(GetHotKeyValue(editor.PropertyName));
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

            panel.Children.Add(new SquircleGlassBorder
            {
                CornerRadiusRadius = 5.5,
                ShadowLevel = 1,
                CacheShadows = true,
                ShowRim = false,
                OutlineBrush = KeycapOutlineBrush,
                OutlineThickness = 1,
                GlassBackground = Brushes.White,
                Padding = new Thickness(7, 3, 7, 3),
                Child = new Border
                {
                    BorderBrush = Brushes.White,
                    BorderThickness = new Thickness(0, 1, 0, 0),
                    Child = new TextBlock
                    {
                        Text = keys[index],
                        FontSize = (double)FindResource("Settings.FontSize.Keycap"),
                        FontWeight = GetFontWeight("Settings.FontWeight.Keycap"),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = GetResource<Brush>("Settings.Brush.TextPrimary")
                    }
                }
            });
        }

        return panel;
    }

    /// <summary>创建“通用”页的状态胶囊徽章（如本地 OCR 引擎就绪态）。</summary>
    private static FrameworkElement CreateStatusBadge(string text, bool isSuccess)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 6,
            Height = 6,
            Margin = new Thickness(0, 0, 6, 0),
            Fill = isSuccess ? ReadyBadgeDot : WarningBadgeDot,
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = isSuccess ? ReadyBadgeForeground : WarningBadgeForeground,
            VerticalAlignment = VerticalAlignment.Center
        });
        return new Border
        {
            Background = isSuccess ? ReadyBadgeBackground : WarningBadgeBackground,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            Child = content
        };
    }

    /// <summary>创建可折叠的高级设置分区，默认折叠以保持主页面清爽。</summary>
    private StackPanel AddCollapsibleSection(Panel page, string title, string description, out StackPanel content)
    {
        content = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        var chevron = new TextBlock
        {
            Text = "▸",
            FontSize = 13,
            Foreground = RowDescriptionBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 2, 0)
        };
        var headerGrid = new Grid { MinHeight = 30, Margin = new Thickness(16, 13, 16, 13) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(CreateRowIcon(LucideIconKind.Settings2));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 12, 0) };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = RowTitleBrush
        });
        text.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 12,
            Foreground = RowDescriptionBrush,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(text, 1);
        headerGrid.Children.Add(text);
        Grid.SetColumn(chevron, 2);
        headerGrid.Children.Add(chevron);

        var headerButton = new Button
        {
            Content = headerGrid,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FocusVisualStyle = GetResource<Style>("SettingsFocusVisualStyle")
        };
        headerButton.Template = CreatePlainButtonTemplate();
        AutomationProperties.SetName(headerButton, title);

        var innerContent = content;
        headerButton.Click += (_, _) =>
        {
            var expanded = innerContent.Visibility != Visibility.Visible;
            innerContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = expanded ? "▾" : "▸";
            if (expanded)
            {
                AnimatePageGroups(innerContent);
            }
        };

        var headerCard = new SquircleGlassBorder
        {
            CornerRadiusRadius = 10,
            ShowRim = false,
            ShadowLevel = 0,
            OutlineBrush = CardOutlineBrush,
            OutlineThickness = 1,
            GlassBackground = Brushes.White,
            Child = headerButton
        };

        var container = new StackPanel { Tag = headerCard, Margin = new Thickness(0, 0, 0, 18) };
        container.Children.Add(headerCard);
        container.Children.Add(content);
        page.Children.Add(container);
        return container;
    }

    private static ControlTemplate CreatePlainButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        template.VisualTree = presenter;
        return template;
    }

    /// <summary>创建等宽字体版本号徽章。</summary>
    private FrameworkElement CreateVersionBadge(string versionText) => new Border
    {
        Background = VersionBadgeBackground,
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(9, 3.5, 9, 3.5),
        Child = new TextBlock
        {
            Text = versionText,
            FontSize = 12,
            FontFamily = GetResource<FontFamily>("Font.Numeric"),
            Foreground = VersionBadgeForeground,
            VerticalAlignment = VerticalAlignment.Center
        }
    };
}
