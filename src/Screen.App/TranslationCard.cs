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

    public event EventHandler? RetranslateRequested;
    public event EventHandler<string>? TargetLanguageChanged;
    public string TargetLanguage { get; private set; } = "简体中文";

    private static Brush Token(string key) => (Brush)Application.Current.FindResource(key);

    public TranslationCard()
    {
        Width = 380;
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

        var close = new Button { Content = AppleVectorIcons.Create("close", 15), ToolTip = "关闭翻译卡片" };
        close.SetResourceReference(StyleProperty, "CardIconButtonStyle");
        close.Click += (_, _) => Visibility = Visibility.Collapsed;
        var dragHint = new TextBlock
        {
            Text = "可拖动",
            ToolTip = "按住顶栏移动卡片",
            FontSize = 11,
            Foreground = Token("Brush.TextTertiary"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 6, 0)
        };
        var headerPanel = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(dragHint, Dock.Right);
        headerPanel.Children.Add(close);
        headerPanel.Children.Add(dragHint);
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
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(header);
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

    public void Begin(string targetLanguage)
    {
        _elapsed.Restart();
        _provider = string.Empty;
        TargetLanguage = targetLanguage;
        _targetLanguage.Text = targetLanguage;
        _sourceLanguage.Text = "自动识别";
        _translation = string.Empty;
        _copy.IsEnabled = false;
        _copy.Content = _copyLabel;
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
        Width = Math.Min(Math.Clamp(availableWidth * 0.34, 372, 450), Math.Max(1, availableWidth - 16));
        var bodyHeight = Math.Clamp(availableHeight - 140, 35, 330);
        _text.MinHeight = Math.Min(82, bodyHeight);
        _text.MaxHeight = bodyHeight;
    }

    public void SetText(string text)
    {
        if (text == _translation)
            return;

        bool firstResult = _translation.Length == 0;
        var selectionStart = firstResult ? 0 : _text.SelectionStart;
        var selectionLength = firstResult ? 0 : _text.SelectionLength;
        var horizontalOffset = _text.HorizontalOffset;
        var verticalOffset = _text.VerticalOffset;
        // Providers send cumulative snapshots. Append the usual delta so an active
        // mouse selection is not rebuilt for every token. A fallback provider may replace the text.
        if (!firstResult && text.StartsWith(_translation, StringComparison.Ordinal)
            && !(_translation[^1] == '\r' && text[_translation.Length] == '\n'))
            _text.AppendText(text[_translation.Length..]);
        else
            _text.Text = text;
        _translation = text;
        if (_text.SelectionStart != selectionStart || _text.SelectionLength != selectionLength)
            _text.Select(selectionStart, selectionLength);
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
            return;
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
            item.Click += (_, _) =>
            {
                if (TargetLanguage == language)
                    return;
                TargetLanguage = language;
                _targetLanguage.Text = language;
                TargetLanguageChanged?.Invoke(this, language);
            };
            menu.Items.Add(item);
        }
        target.ContextMenu = menu;
        menu.IsOpen = true;
    }
}
