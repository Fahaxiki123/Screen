using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Screen.App;

internal sealed class TranslationCard : GlassResultCard
{
    private readonly TextBox _text;
    private readonly TextBox _sourceText;
    private readonly Border _sourcePanel;
    private readonly Button _sourceToggle;
    private readonly Button _inPlaceSwitch;
    private readonly TextBlock _status;
    private readonly Ellipse _statusDot;
    private readonly TextBlock _sourceLanguage;
    private readonly TextBlock _targetLanguage;
    private readonly Button _retranslate;
    private readonly Button _copy;
    private readonly StackPanel _copyLabel;
    private readonly Stopwatch _elapsed = new();
    private string _provider = string.Empty;
    private string _translation = string.Empty;
    private string _originalSourceText = string.Empty;
    private bool _sourceVisible;

    public event EventHandler? RetranslateRequested;
    public event EventHandler<string>? TargetLanguageChanged;
    public event EventHandler? SwitchToInPlaceRequested;
    public string TargetLanguage { get; private set; } = "简体中文";
    public string CurrentSourceText => _sourceText.Text;
    public bool HasSourceLoaded { get; private set; }
    public int SourceRevision { get; private set; } = -1;
    public bool IsSourceEdited => !string.Equals(_sourceText.Text, _originalSourceText, StringComparison.Ordinal);

    public void AcceptSourceEdit() => _originalSourceText = _sourceText.Text;

    private static Brush Token(string key) => (Brush)Application.Current.FindResource(key);

    public TranslationCard()
    {
        Width = 396;
        _sourceLanguage = new TextBlock
        {
            Text = "自动识别",
            FontSize = 13,
            Foreground = Token("Brush.TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        _targetLanguage = new TextBlock { Text = TargetLanguage, VerticalAlignment = VerticalAlignment.Center };
        var targetLabel = new StackPanel { Orientation = Orientation.Horizontal };
        targetLabel.Children.Add(_targetLanguage);
        targetLabel.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M1,1 4,4 7,1"),
            Stroke = Token("Brush.Accent"),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(5, 0, 0, 0)
        });
        var target = new Button
        {
            Content = targetLabel,
            Height = 28,
            Margin = new Thickness(0),
            Padding = new Thickness(9, 0, 9, 0),
            Foreground = Token("Brush.Accent"),
            Background = Token("Brush.AccentSubtle"),
            FontSize = 12.5,
            ToolTip = "选择目标语言"
        };
        target.SetResourceReference(StyleProperty, "CardSecondaryButtonStyle");
        target.Click += (_, _) => ShowTargetLanguageMenu(target);

        var languageGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        languageGroup.Children.Add(_sourceLanguage);
        languageGroup.Children.Add(new TextBlock
        {
            Text = "→",
            Foreground = Token("Brush.TextTertiary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 7, 0)
        });
        languageGroup.Children.Add(target);

        _sourceToggle = new Button
        {
            Content = "原文",
            Height = 26,
            MinWidth = 42,
            Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 11.5,
            Foreground = Token("Brush.TextSecondary"),
            Background = Brushes.Transparent,
            ToolTip = "查看或修改识别原文后重译",
            IsEnabled = false
        };
        _sourceToggle.SetResourceReference(StyleProperty, "CardSecondaryButtonStyle");
        _sourceToggle.Click += (_, _) => SetSourceVisible(!_sourceVisible);

        _inPlaceSwitch = new Button
        {
            Content = "原位",
            Height = 26,
            MinWidth = 42,
            Padding = new Thickness(7, 0, 7, 0),
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 11.5,
            Foreground = Token("Brush.TextSecondary"),
            Background = Brushes.Transparent,
            ToolTip = "切换为原位覆盖翻译"
        };
        _inPlaceSwitch.SetResourceReference(StyleProperty, "CardSecondaryButtonStyle");
        _inPlaceSwitch.Click += (_, _) => SwitchToInPlaceRequested?.Invoke(this, EventArgs.Empty);

        var close = new Button { Content = AppleVectorIcons.Create("close", 15), ToolTip = "关闭翻译卡片" };
        close.SetResourceReference(StyleProperty, "CardIconButtonStyle");
        close.Click += (_, _) => Visibility = Visibility.Collapsed;

        var headerPanel = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_inPlaceSwitch, Dock.Right);
        DockPanel.SetDock(_sourceToggle, Dock.Right);
        headerPanel.Children.Add(close);
        headerPanel.Children.Add(_inPlaceSwitch);
        headerPanel.Children.Add(_sourceToggle);
        headerPanel.Children.Add(languageGroup);
        var header = new Border
        {
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Padding = new Thickness(16, 9, 9, 9),
            BorderBrush = Token("Brush.BorderSubtle"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = headerPanel
        };

        _sourceText = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 12.5,
            Foreground = Token("Brush.TextSecondary"),
            CaretBrush = Token("Brush.Accent"),
            SelectionBrush = Token("Brush.Accent"),
            SelectionOpacity = 0.25,
            MinHeight = 42,
            MaxHeight = 110,
            Padding = new Thickness(8, 6, 8, 6)
        };
        TextBlock.SetLineHeight(_sourceText, 19);
        var sourceBox = new SquircleGlassBorder
        {
            CornerRadiusRadius = 7,
            ShowRim = false,
            ShadowLevel = 0,
            GlassBackground = Token("Brush.SurfaceSunken"),
            Child = _sourceText
        };
        var sourceContent = new StackPanel();
        sourceContent.Children.Add(new TextBlock
        {
            Text = "识别原文（可直接修改后点“重译”）",
            FontSize = 11,
            Foreground = Token("Brush.TextTertiary"),
            Margin = new Thickness(2, 0, 0, 5)
        });
        sourceContent.Children.Add(sourceBox);
        _sourcePanel = new Border
        {
            Padding = new Thickness(16, 10, 16, 8),
            BorderBrush = Token("Brush.BorderSubtle"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Visibility = Visibility.Collapsed,
            Child = sourceContent
        };

        _text = new TextBox
        {
            IsReadOnly = true,
            IsUndoEnabled = false,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FocusVisualStyle = null,
            FontSize = 14,
            Foreground = Token("Brush.TextPrimary"),
            SelectionBrush = Token("Brush.Accent"),
            SelectionOpacity = 0.28,
            MinHeight = 82,
            MaxHeight = 330,
            Margin = new Thickness(16, 14, 16, 12),
            Padding = new Thickness(0)
        };
        TextBlock.SetLineHeight(_text, 23.5);

        _statusDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Token("Brush.Success"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        _status = new TextBlock
        {
            FontSize = 11.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Token("Brush.TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var statusGroup = new DockPanel { Margin = new Thickness(0, 0, 6, 0) };
        statusGroup.Children.Add(_statusDot);
        statusGroup.Children.Add(_status);

        _retranslate = new Button
        {
            Content = "重译",
            Height = 30,
            MinWidth = 45,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(8, 0, 8, 0),
            Background = Token("Brush.SurfaceHover"),
            Foreground = Token("Brush.TextPrimary"),
            FontSize = 12
        };
        _retranslate.SetResourceReference(StyleProperty, "CardSecondaryButtonStyle");
        _retranslate.Click += (_, _) => RetranslateRequested?.Invoke(this, EventArgs.Empty);

        _copyLabel = new StackPanel { Orientation = Orientation.Horizontal };
        _copyLabel.Children.Add(AppleVectorIcons.Create("copy", 14));
        _copyLabel.Children.Add(new TextBlock { Text = "复制译文", Margin = new Thickness(5, 0, 0, 0) });
        _copy = new Button
        {
            Content = _copyLabel,
            Height = 30,
            MinWidth = 84,
            FontSize = 12,
            Margin = new Thickness(0),
            Padding = new Thickness(10, 0, 10, 0),
            IsEnabled = false
        };
        _copy.SetResourceReference(StyleProperty, "CardPrimaryButtonStyle");
        _copy.Click += (_, _) => CopyTranslation();

        var footerPanel = new DockPanel();
        DockPanel.SetDock(_copy, Dock.Right);
        DockPanel.SetDock(_retranslate, Dock.Right);
        footerPanel.Children.Add(_copy);
        footerPanel.Children.Add(_retranslate);
        footerPanel.Children.Add(statusGroup);
        var footer = new Border
        {
            Margin = new Thickness(16, 0, 16, 0),
            Padding = new Thickness(0, 9, 0, 10),
            BorderBrush = Token("Brush.BorderSubtle"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = footerPanel
        };

        var content = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_sourcePanel, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(header);
        content.Children.Add(_sourcePanel);
        content.Children.Add(footer);
        content.Children.Add(_text);
        AttachCard(new SquircleGlassBorder
        {
            CornerRadiusRadius = 13,
            GlassBackground = Token("Brush.AuroraCard"),
            ShadowLevel = 2,
            Child = content
        }, header);
    }

    public void Begin(string targetLanguage, bool preserveSource = false)
    {
        _elapsed.Restart();
        _provider = string.Empty;
        TargetLanguage = targetLanguage;
        _targetLanguage.Text = targetLanguage;
        if (!preserveSource)
        {
            _sourceLanguage.Text = "自动识别";
            _originalSourceText = string.Empty;
            _sourceText.Text = string.Empty;
            HasSourceLoaded = false;
            SourceRevision = -1;
            _sourceToggle.IsEnabled = false;
            SetSourceVisible(false);
        }
        _translation = string.Empty;
        _copy.IsEnabled = false;
        _copy.Content = _copyLabel;
        _retranslate.IsEnabled = false;
        _text.Text = preserveSource ? "正在翻译…" : "正在识别选区文字…";
        _text.ScrollToHome();
        _text.Foreground = Token("Brush.TextSecondary");
        _statusDot.Fill = Token("Brush.Accent");
        SetStatus(preserveSource ? "正在翻译…" : "正在准备翻译");
        Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 直接展示已有译文（例如从原位覆盖秒切回浮动卡片），不重复发起翻译请求。
    /// </summary>
    public void ShowCachedTranslation(
        string targetLanguage,
        string sourceLanguage,
        string sourceText,
        string translatedText,
        string providerName,
        string? detail = null,
        int sourceRevision = -1)
    {
        _elapsed.Reset();
        TargetLanguage = targetLanguage;
        _targetLanguage.Text = targetLanguage;
        SetSourceLanguage(sourceLanguage);
        SetSourceText(sourceText, sourceRevision);
        SetProvider(providerName);
        _translation = string.Empty;
        SetText(translatedText);
        _retranslate.IsEnabled = true;
        _copy.IsEnabled = !string.IsNullOrWhiteSpace(translatedText);
        _copy.Content = _copyLabel;
        _statusDot.Fill = Token(_copy.IsEnabled ? "Brush.Success" : "Brush.Warning");
        SetStatus(detail ?? $"{_provider} · 已复用原位译文", $"{_provider} · 已复用");
        Visibility = Visibility.Visible;
    }

    public void SetSourceText(string text, int sourceRevision = -1)
    {
        _originalSourceText = text ?? string.Empty;
        _sourceText.Text = _originalSourceText;
        HasSourceLoaded = !string.IsNullOrWhiteSpace(_originalSourceText);
        if (sourceRevision >= 0)
        {
            SourceRevision = sourceRevision;
        }
        _sourceToggle.IsEnabled = HasSourceLoaded;
        if (!HasSourceLoaded)
        {
            SetSourceVisible(false);
        }
    }

    private void SetSourceVisible(bool visible)
    {
        _sourceVisible = visible;
        _sourcePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _sourceToggle.Foreground = Token(visible ? "Brush.Accent" : "Brush.TextSecondary");
        _sourceToggle.Background = visible ? Token("Brush.AccentSubtle") : Brushes.Transparent;
    }

    public void SetAvailableSize(double availableWidth, double availableHeight)
    {
        Width = Math.Min(Math.Clamp(availableWidth * 0.34, 388, 460), Math.Max(1, availableWidth - 16));
        var extraSource = _sourceVisible ? 115 : 0;
        var bodyHeight = Math.Clamp(availableHeight - 140 - extraSource, 35, 330);
        _text.MinHeight = Math.Min(82, bodyHeight);
        _text.MaxHeight = bodyHeight;
    }

    public void SetText(string text)
    {
        if (text == _translation)
        {
            return;
        }

        bool firstResult = _translation.Length == 0;
        var selectionStart = firstResult ? 0 : _text.SelectionStart;
        var selectionLength = firstResult ? 0 : _text.SelectionLength;
        var horizontalOffset = _text.HorizontalOffset;
        var verticalOffset = _text.VerticalOffset;
        if (!firstResult && text.StartsWith(_translation, StringComparison.Ordinal)
            && !(_translation[^1] == '\r' && text[_translation.Length] == '\n'))
        {
            _text.AppendText(text[_translation.Length..]);
        }
        else
        {
            _text.Text = text;
        }
        _translation = text;
        if (_text.SelectionStart != selectionStart || _text.SelectionLength != selectionLength)
        {
            _text.Select(selectionStart, selectionLength);
        }
        _text.ScrollToHorizontalOffset(horizontalOffset);
        _text.ScrollToVerticalOffset(verticalOffset);
        if (firstResult)
        {
            _text.Foreground = Token("Brush.TextPrimary");
            GlassMotion.Enter(_text, 4, 1, 200);
        }
    }

    public void SetStatus(string details, string? summary = null)
    {
        _status.Text = summary ?? details;
        _status.ToolTip = details;
    }

    public void SetSourceLanguage(string languageTag)
    {
        _sourceLanguage.Text = languageTag.Split('-')[0].ToLowerInvariant() switch
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

    public void SetProvider(string providerName) =>
        _provider = string.IsNullOrWhiteSpace(providerName) ? "翻译" : providerName;

    public void Complete()
    {
        _elapsed.Stop();
        _retranslate.IsEnabled = true;
        _copy.IsEnabled = !string.IsNullOrWhiteSpace(_translation);
        _statusDot.Fill = Token(_copy.IsEnabled ? "Brush.Success" : "Brush.Warning");
        if (_copy.IsEnabled)
        {
            _status.Text = $"{_provider} · {_elapsed.Elapsed.TotalSeconds:F1}s";
        }
        else
        {
            _text.Text = "未识别到可翻译文字";
        }
    }

    public void SetError(string message)
    {
        _elapsed.Stop();
        _text.Foreground = Token("Brush.Danger");
        _text.Text = message;
        _translation = string.Empty;
        _copy.IsEnabled = false;
        _retranslate.IsEnabled = true;
        _statusDot.Fill = Token("Brush.Danger");
        SetStatus(message, "翻译失败");
    }

    private void CopyTranslation()
    {
        if (string.IsNullOrWhiteSpace(_translation))
        {
            return;
        }
        try
        {
            BitmapUtilities.SetClipboardTextWithRetry(_translation);
            ShowCopied(_copy, _copyLabel);
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, "复制失败");
        }
    }

    private void ShowTargetLanguageMenu(Button target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        foreach (var language in SettingsViewModel.TranslationLanguages())
        {
            var item = new MenuItem { Header = language, IsCheckable = true, IsChecked = language == TargetLanguage };
            item.Click += (_, _) => SelectTargetLanguage(language);
            menu.Items.Add(item);
        }
        target.ContextMenu = menu;
        menu.IsOpen = true;
    }

    internal void SelectTargetLanguage(string language)
    {
        if (TargetLanguage == language)
        {
            return;
        }
        TargetLanguage = language;
        _targetLanguage.Text = language;
        TargetLanguageChanged?.Invoke(this, language);
    }
}
