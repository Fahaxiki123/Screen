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
        Width = 460;
        var title = new TextBlock
        {
            Text = "截图翻译",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Token("Brush.TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new Button
        {
            Content = new LucideIconElement
            {
                Kind = LucideIconKind.X,
                Width = 18,
                Height = 18,
                Stroke = Token("Brush.TextSecondary"),
                StrokeThickness = 2
            },
            ToolTip = "关闭"
        };
        close.SetResourceReference(StyleProperty, "CardIconButtonStyle");
        close.Click += (_, _) => Visibility = Visibility.Collapsed;

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        _sourceLanguage = new TextBlock
        {
            Text = "自动识别",
            FontSize = 14,
            Foreground = Token("Brush.TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var sourceChip = new Border
        {
            Background = Token("Brush.SurfaceRaised"),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 6, 11, 6),
            Child = _sourceLanguage
        };
        _targetLanguage = new TextBlock
        {
            Text = TargetLanguage,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        var targetContent = new StackPanel { Orientation = Orientation.Horizontal };
        targetContent.Children.Add(_targetLanguage);
        targetContent.Children.Add(new LucideIconElement
        {
            Kind = LucideIconKind.ChevronDown,
            Width = 14,
            Height = 14,
            Stroke = Token("Brush.TextPrimary"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var targetButton = new Button
        {
            Content = targetContent,
            Height = 32,
            Margin = new Thickness(0),
            Padding = new Thickness(11, 0, 10, 0),
            Background = Token("Brush.SurfaceRaised"),
            ToolTip = "选择目标语言"
        };
        targetButton.SetResourceReference(StyleProperty, "CardSecondaryButtonStyle");
        targetButton.Click += (_, _) => ShowTargetLanguageMenu(targetButton);
        _provider = new TextBlock
        {
            Text = "AI 自然翻译",
            FontSize = 13,
            Foreground = Token("Brush.TextSecondary"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 0, 0, 0)
        };
        var languageRow = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        languageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        languageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        languageRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        languageRow.ColumnDefinitions.Add(new ColumnDefinition());
        languageRow.Children.Add(sourceChip);
        var direction = new LucideIconElement
        {
            Kind = LucideIconKind.ArrowRight,
            Width = 16,
            Height = 16,
            Stroke = Token("Brush.TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(direction, 1);
        languageRow.Children.Add(direction);
        Grid.SetColumn(targetButton, 2);
        languageRow.Children.Add(targetButton);
        Grid.SetColumn(_provider, 3);
        languageRow.Children.Add(_provider);

        _text = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 210,
            MaxHeight = 340,
            IsReadOnly = true,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI Variable Text, Microsoft YaHei UI"),
            FontSize = 17,
            Foreground = Token("Brush.TextPrimary"),
            CaretBrush = Token("Brush.TextPrimary"),
            SelectionBrush = Token("Brush.Accent"),
            SelectionOpacity = 0.28,
            Padding = new Thickness(16, 14, 12, 14)
        };
        _text.Resources[typeof(ScrollBar)] = Application.Current.FindResource("DarkScrollBarStyle");
        TextBlock.SetLineHeight(_text, 27);
        TextOptions.SetTextFormattingMode(_text, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(_text, TextRenderingMode.ClearType);
        var textBorder = new Border
        {
            Background = Token("Brush.SurfaceRaised"),
            BorderBrush = Token("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 16, 0, 0),
            Child = _text
        };

        _status = new TextBlock
        {
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Token("Brush.TextSecondary"),
            Margin = new Thickness(0, 8, 0, 0),
            Height = 16
        };
        _retranslate = new Button
        {
            Content = ButtonContent("重新翻译", LucideIconKind.RotateCw),
            Width = 154,
            Height = 40,
            Margin = new Thickness(0),
            Background = Brushes.Transparent,
            BorderBrush = Token("Brush.Border"),
            BorderThickness = new Thickness(1)
        };
        _retranslate.SetResourceReference(StyleProperty, "CardSecondaryButtonStyle");
        _retranslate.Click += (_, _) => RetranslateRequested?.Invoke(this, EventArgs.Empty);
        _copy = new Button
        {
            Content = ButtonContent("复制译文", LucideIconKind.Copy),
            Width = 154,
            Height = 40,
            Margin = new Thickness(0)
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

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_retranslate);
        Grid.SetColumn(_copy, 2);
        footer.Children.Add(_copy);

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(header);
        Grid.SetRow(languageRow, 1);
        content.Children.Add(languageRow);
        Grid.SetRow(textBorder, 2);
        content.Children.Add(textBorder);
        Grid.SetRow(_status, 3);
        content.Children.Add(_status);
        Grid.SetRow(footer, 4);
        content.Children.Add(footer);

        _card = new Border
        {
            Background = Token("Brush.Surface"),
            BorderBrush = Token("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20, 18, 18, 18),
            Margin = new Thickness(0, 0, 0, 10),
            Effect = (System.Windows.Media.Effects.DropShadowEffect)Application.Current.FindResource("Shadow.Floating"),
            Child = content
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
        SetStatus("正在准备翻译");
        Visibility = Visibility.Visible;
    }

    public void SetAvailableSize(double availableWidth, double availableHeight)
    {
        Width = Math.Min(Math.Clamp(availableWidth * 0.34, 440, 480), Math.Max(340, availableWidth - 32));
        _retranslate.Width = _copy.Width = Math.Min(154, (Width - 64) / 2);
        var bodyHeight = Math.Clamp(availableHeight - 230, 100, 340);
        _text.MinHeight = Math.Min(210, bodyHeight);
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
        _provider.Text = providerName;
        _provider.ToolTip = providerName;
    }

    public void Complete()
    {
        _retranslate.IsEnabled = true;
        _copy.IsEnabled = _hasTranslation;
        if (!_hasTranslation)
        {
            _text.Text = "未识别到可翻译文字";
        }
    }

    public void SetError(string message)
    {
        _text.Foreground = Token("Brush.Danger");
        _text.Text = message;
        _hasTranslation = false;
        _copy.IsEnabled = false;
        _retranslate.IsEnabled = true;
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

    private static StackPanel ButtonContent(string text, LucideIconKind icon)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var iconElement = new LucideIconElement
        {
            Kind = icon,
            Width = 18,
            Height = 18,
            StrokeThickness = 2,
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
