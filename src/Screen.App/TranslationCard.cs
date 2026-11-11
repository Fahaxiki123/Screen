using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Lucide.NET;

namespace Screen.App;

internal sealed class TranslationCard : Grid
{
    private readonly TextBox _text;
    private readonly TextBlock _status;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _sourceLanguage;
    private readonly TextBlock _targetLanguage;
    private readonly TextBlock _provider;
    private readonly Button _retranslate;
    private readonly Button _copy;
    private readonly Border _card;
    private readonly Polygon _pointer;
    private bool _hasTranslation;

    public event EventHandler? RetranslateRequested;
    public event EventHandler<string>? TargetLanguageChanged;

    public string TargetLanguage { get; private set; } = "简体中文";

    private static Brush Token(string key) => (Brush)Application.Current.FindResource(key);

    public TranslationCard()
    {
        Width = 450;

        // Top bar: language switch takes center stage, AI provider badge + close on the right
        _sourceLanguage = new TextBlock
        {
            Text = "自动识别",
            FontSize = 13,
            Foreground = Token("Brush.TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };

        _targetLanguage = new TextBlock
        {
            Text = TargetLanguage,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Token("Brush.TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var targetContent = new StackPanel { Orientation = Orientation.Horizontal };
        targetContent.Children.Add(_targetLanguage);
        targetContent.Children.Add(new LucideIconElement
        {
            Kind = LucideIconKind.ChevronDown,
            Width = 13,
            Height = 13,
            Stroke = Token("Brush.AccentText"),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var targetButton = new Button
        {
            Content = targetContent,
            Height = 28,
            Margin = new Thickness(0),
            Padding = new Thickness(10, 0, 9, 0),
            Background = Token("Brush.AccentSubtleDark"),
            BorderBrush = Token("Brush.AccentSubtleDarkBorder"),
            BorderThickness = new Thickness(1),
            ToolTip = "选择目标语言"
        };
        targetButton.SetResourceReference(StyleProperty, "CardSecondaryButtonStyle");
        targetButton.Click += (_, _) => ShowTargetLanguageMenu(targetButton);

        var langGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        langGroup.Children.Add(_sourceLanguage);
        langGroup.Children.Add(new LucideIconElement
        {
            Kind = LucideIconKind.ArrowRight,
            Width = 14,
            Height = 14,
            Stroke = Token("Brush.TextTertiary"),
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        langGroup.Children.Add(targetButton);

        _provider = new TextBlock
        {
            Text = "✦ AI 自然翻译",
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = Token("Brush.AccentText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var providerPill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1E, 0x38, 0xBD, 0xF8)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0x38, 0xBD, 0xF8)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _provider
        };

        var close = new Button
        {
            Content = new LucideIconElement
            {
                Kind = LucideIconKind.X,
                Width = 16,
                Height = 16,
                Stroke = Token("Brush.TextSecondary"),
                StrokeThickness = 1.9
            },
            ToolTip = "关闭"
        };
        close.SetResourceReference(StyleProperty, "CardIconButtonStyle");
        close.Click += (_, _) => Visibility = Visibility.Collapsed;

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.Children.Add(langGroup);
        Grid.SetColumn(providerPill, 1);
        headerGrid.Children.Add(providerPill);
        Grid.SetColumn(close, 2);
        headerGrid.Children.Add(close);

        var headerBar = new Border
        {
            Padding = new Thickness(18, 12, 12, 11),
            BorderBrush = Token("Brush.BorderSubtle"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerGrid
        };

        // Frameless reading surface: text breathes directly inside the aurora card
        _text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 186,
            MaxHeight = 330,
            IsReadOnly = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
            FontSize = 15.5,
            Foreground = Token("Brush.TextPrimary"),
            CaretBrush = Token("Brush.TextPrimary"),
            SelectionBrush = Token("Brush.Accent"),
            SelectionOpacity = 0.28,
            Padding = new Thickness(20, 16, 16, 16)
        };
        _text.Resources[typeof(ScrollBar)] = Application.Current.FindResource("DarkScrollBarStyle");
        TextBlock.SetLineHeight(_text, 26);
        TextOptions.SetTextFormattingMode(_text, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(_text, TextRenderingMode.ClearType);

        // Quiet bottom action bar with live status dot + ghost retry + luminous copy CTA
        _statusDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Token("Brush.Success"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        _status = new TextBlock
        {
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Token("Brush.TextTertiary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        statusGroup.Children.Add(_statusDot);
        statusGroup.Children.Add(_status);

        _retranslate = new Button
        {
            Content = ButtonContent("重新翻译", LucideIconKind.RotateCw, 14),
            Height = 32,
            MinWidth = 88,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(10, 0, 10, 0),
            Background = Brushes.Transparent,
            Foreground = Token("Brush.TextSecondary"),
            BorderThickness = new Thickness(0),
            FontSize = 12.5
        };
        _retranslate.SetResourceReference(StyleProperty, "CardSecondaryButtonStyle");
        _retranslate.Click += (_, _) => RetranslateRequested?.Invoke(this, EventArgs.Empty);

        _copy = new Button
        {
            Content = ButtonContent("复制译文", LucideIconKind.Copy, 14),
            Height = 32,
            MinWidth = 98,
            Margin = new Thickness(0),
            Padding = new Thickness(12, 0, 14, 0)
        };
        _copy.SetResourceReference(StyleProperty, "CardPrimaryButtonStyle");
        _copy.Click += (_, _) =>
        {
            if (_hasTranslation && !string.IsNullOrWhiteSpace(_text.Text))
            {
                try
                {
                    BitmapUtilities.SetClipboardTextWithRetry(_text.Text);
                    SetStatus("译文已复制");
                }
                catch (Exception exception)
                {
                    SetStatus(exception.Message);
                }
            }
        };

        var actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        actionButtons.Children.Add(_retranslate);
        actionButtons.Children.Add(_copy);

        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerGrid.Children.Add(statusGroup);
        Grid.SetColumn(actionButtons, 1);
        footerGrid.Children.Add(actionButtons);

        var footerBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x52, 0x06, 0x0C, 0x18)),
            BorderBrush = Token("Brush.BorderSubtle"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 15, 15),
            Padding = new Thickness(18, 9, 12, 9),
            Child = footerGrid
        };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(headerBar);
        Grid.SetRow(_text, 1);
        content.Children.Add(_text);
        Grid.SetRow(footerBar, 2);
        content.Children.Add(footerBar);

        // Subtle aurora glow bloom at top center of the card
        var auroraGlow = new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.0),
                Center = new Point(0.5, 0.0),
                RadiusX = 0.72,
                RadiusY = 0.35,
                GradientStops = new GradientStopCollection
                {
                    new(Color.FromArgb(0x2E, 0x38, 0xBD, 0xF8), 0),
                    new(Color.FromArgb(0x00, 0x38, 0xBD, 0xF8), 1)
                }
            },
            Child = content
        };

        _card = new Border
        {
            Background = Token("Brush.AuroraCard"),
            BorderBrush = Token("Brush.GlassRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(0, 0, 0, 10),
            Effect = (System.Windows.Media.Effects.DropShadowEffect)Application.Current.FindResource("Shadow.Floating"),
            Child = auroraGlow
        };
        _pointer = new Polygon
        {
            Points = new PointCollection { new(0, 0), new(18, 0), new(9, 10) },
            Fill = Token("Brush.Surface"),
            Stroke = Token("Brush.Border"),
            StrokeThickness = 1,
            Width = 18,
            Height = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 1)
        };
        Children.Add(_card);
        Children.Add(_pointer);
    }

    public void Begin(string targetLanguage)
    {
        TargetLanguage = targetLanguage;
        _targetLanguage.Text = targetLanguage;
        _sourceLanguage.Text = "自动识别";
        _hasTranslation = false;
        _copy.IsEnabled = false;
        _retranslate.IsEnabled = false;
        _text.Text = "正在识别选区文字…";
        _text.ScrollToHome();
        _text.Foreground = Token("Brush.TextSecondary");
        _statusDot.Fill = Token("Brush.Accent");
        SetStatus("正在准备翻译");
        Visibility = Visibility.Visible;
    }

    public void SetAvailableSize(double availableWidth, double availableHeight)
    {
        Width = Math.Min(Math.Clamp(availableWidth * 0.34, 430, 470), Math.Max(340, availableWidth - 32));
        var bodyHeight = Math.Clamp(availableHeight - 170, 100, 330);
        _text.MinHeight = Math.Min(186, bodyHeight);
        _text.MaxHeight = bodyHeight;
    }

    public void SetText(string text)
    {
        _text.Foreground = Token("Brush.TextPrimary");
        _text.Text = text;
        _hasTranslation = !string.IsNullOrWhiteSpace(text);
    }

    public void SetStatus(string status)
    {
        _status.Text = status;
        _status.ToolTip = status;
    }

    public void SetSourceLanguage(string languageTag)
    {
        var language = languageTag.Split('-')[0].ToLowerInvariant();
        _sourceLanguage.Text = language switch
        {
            "zh" => "中文",
            "en" => "英语",
            "ja" => "日语",
            "ko" => "韩语",
            "fr" => "法语",
            "es" => "西班牙语",
            "pt" => "葡萄牙语",
            "de" => "德语",
            _ => "自动识别"
        };
    }

    public void SetProvider(string providerName)
    {
        _provider.Text = string.IsNullOrWhiteSpace(providerName) ? "✦ AI 翻译" : $"✦ {providerName}";
        _provider.ToolTip = providerName;
    }

    public void Complete()
    {
        _retranslate.IsEnabled = true;
        _copy.IsEnabled = _hasTranslation;
        _statusDot.Fill = _hasTranslation ? Token("Brush.Success") : Token("Brush.Warning");
        if (!_hasTranslation)
        {
            _text.Text = "未识别到可翻译文字";
        }
    }

    public void SetError(string message)
    {
        _text.Foreground = Token("Brush.DangerOnDark");
        _text.Text = message;
        _hasTranslation = false;
        _copy.IsEnabled = false;
        _retranslate.IsEnabled = true;
        _statusDot.Fill = Token("Brush.Danger");
        SetStatus("请求失败，请检查接口配置");
    }

    private void ShowTargetLanguageMenu(Button target)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            Background = Token("Brush.SurfaceRaised"),
            Foreground = Token("Brush.TextPrimary"),
            BorderBrush = Token("Brush.Border"),
            BorderThickness = new Thickness(1)
        };
        foreach (var language in new[] { "简体中文", "繁體中文", "English", "日本語", "한국어", "Français", "Español", "Português", "Deutsch" })
        {
            var item = new MenuItem { Header = language, IsCheckable = true, IsChecked = language == TargetLanguage };
            item.Click += (_, _) =>
            {
                if (TargetLanguage == language)
                {
                    return;
                }
                TargetLanguage = language;
                _targetLanguage.Text = language;
                TargetLanguageChanged?.Invoke(this, language);
            };
            menu.Items.Add(item);
        }
        target.ContextMenu = menu;
        menu.IsOpen = true;
    }

    public void SetPointer(CalloutPointerSide side, double offset)
    {
        _pointer.Visibility = side == CalloutPointerSide.None ? Visibility.Collapsed : Visibility.Visible;
        _card.Margin = side switch
        {
            CalloutPointerSide.Top => new Thickness(0, 10, 0, 0),
            CalloutPointerSide.Right => new Thickness(0, 0, 10, 0),
            CalloutPointerSide.Bottom => new Thickness(0, 0, 0, 10),
            CalloutPointerSide.Left => new Thickness(10, 0, 0, 0),
            _ => new Thickness(0)
        };
        var horizontal = side is CalloutPointerSide.Top or CalloutPointerSide.Bottom;
        _pointer.Width = horizontal ? 18 : 10;
        _pointer.Height = horizontal ? 10 : 18;
        _pointer.HorizontalAlignment = side switch
        {
            CalloutPointerSide.Left => HorizontalAlignment.Left,
            CalloutPointerSide.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left
        };
        _pointer.VerticalAlignment = side switch
        {
            CalloutPointerSide.Top => VerticalAlignment.Top,
            CalloutPointerSide.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Top
        };
        _pointer.Margin = horizontal
            ? new Thickness(Math.Max(12, offset - 9), side == CalloutPointerSide.Top ? 1 : 0, 0, side == CalloutPointerSide.Bottom ? 1 : 0)
            : new Thickness(side == CalloutPointerSide.Left ? 1 : 0, Math.Max(12, offset - 9), side == CalloutPointerSide.Right ? 1 : 0, 0);
        _pointer.Points = side switch
        {
            CalloutPointerSide.Top => new PointCollection { new(0, 10), new(18, 10), new(9, 0) },
            CalloutPointerSide.Right => new PointCollection { new(0, 0), new(0, 18), new(10, 9) },
            CalloutPointerSide.Bottom => new PointCollection { new(0, 0), new(18, 0), new(9, 10) },
            CalloutPointerSide.Left => new PointCollection { new(10, 0), new(10, 18), new(0, 9) },
            _ => _pointer.Points
        };
    }

    private static StackPanel ButtonContent(string text, LucideIconKind icon, double iconSize = 16)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var iconElement = new LucideIconElement
        {
            Kind = icon,
            Width = iconSize,
            Height = iconSize,
            StrokeThickness = 1.9,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        iconElement.SetBinding(LucideIconElement.StrokeProperty, new Binding(nameof(Control.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
        });
        row.Children.Add(iconElement);
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}
