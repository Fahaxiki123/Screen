using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Screen.App;

internal sealed partial class TranslationCard : Grid
{
    private static readonly string[] TargetLanguages = ["简体中文", "繁體中文", "English", "日本語", "한국어", "Français", "Español", "Português", "Deutsch"];
    private readonly RotateTransform _rotation = new(0, 0.5, 0.5);
    private string _source = string.Empty;
    private string _translation = string.Empty;
    private string _sourceLanguage = "自动识别";
    private string _service = "AI 翻译";
    private string _placeholder = "正在识别选区文字…";
    private bool _busy;
    private bool _failed;
    private bool _rotating;
    private double? _elapsed;

    public event EventHandler? RetranslateRequested;
    public event EventHandler<string>? TargetLanguageChanged;
    public string TargetLanguage { get; private set; } = "简体中文";

    public TranslationCard()
    {
        InitializeComponent();
        var brush = ((LinearGradientBrush)FindResource("Translation.Ring")).Clone();
        brush.RelativeTransform = _rotation;
        _ring.BorderBrush = brush;
        _tab.ItemsSource = new[] { "原文", "译文" };
        _tab.SelectedIndex = 1;
        _tab.SelectionChanged += (_, _) => ShowBody(animate: true, resetScroll: true);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Motion.PopIn(this, horizontal: true);
            UpdateRing();
        };
        UpdateLanguagePair();
        UpdateProvider();
        ShowBody();
    }

    public void Begin(string targetLanguage)
    {
        _busy = true;
        _failed = false;
        _source = _translation = string.Empty;
        _elapsed = null;
        _placeholder = "正在识别选区文字…";
        TargetLanguage = targetLanguage;
        _sourceLanguage = "自动识别";
        _tab.SelectedIndex = 1;
        _retranslate.IsEnabled = false;
        UpdateLanguagePair();
        UpdateProvider();
        SetStatus("正在准备翻译");
        ShowBody(resetScroll: true);
        Visibility = Visibility.Visible;
        UpdateRing();
    }

    public void SetAvailableSize(double availableWidth, double availableHeight)
    {
        Width = Math.Min(Math.Clamp(availableWidth * 0.32, 320, 392), Math.Max(1, availableWidth - 16));
        var bodyHeight = Math.Clamp(availableHeight - 150, 46, 260);
        _text.MinHeight = Math.Min(98, bodyHeight);
        _text.MaxHeight = bodyHeight;
    }

    public void SetOriginalText(string source)
    {
        _source = source;
        if (_busy && !string.IsNullOrWhiteSpace(source))
            _placeholder = "正在翻译…";
        ShowBody(animate: _tab.SelectedIndex == 0);
    }

    public void SetText(string text)
    {
        var wasEmpty = string.IsNullOrEmpty(_translation);
        _translation = text;
        if (_tab.SelectedIndex != 0)
            ShowBody(animate: wasEmpty);
    }

    private void ShowBody(bool animate = false, bool resetScroll = false)
    {
        var original = _tab.SelectedIndex == 0;
        var value = original ? _source : _translation;
        var available = !string.IsNullOrWhiteSpace(value);
        _copy.IsEnabled = available && (original || !_busy);
        _copy.ToolTip = original ? "拷贝原文" : "拷贝译文";
        _text.SetResourceReference(Control.ForegroundProperty, available ? "Brush.OverlayText" :
            _failed && !original ? "Brush.DangerOnDark" : "Brush.OverlayTextDim");
        if (!available)
            value = original ? (_busy ? "正在识别选区文字…" : "未识别到原文") : _placeholder;

        if (!string.Equals(_text.Text, value, StringComparison.Ordinal))
        {
            // Stream updates must not move the user's selection or their scroll position.
            var start = resetScroll ? 0 : Math.Min(_text.SelectionStart, value.Length);
            var length = resetScroll ? 0 : Math.Min(_text.SelectionLength, value.Length - start);
            var horizontal = resetScroll ? 0 : _text.HorizontalOffset;
            var vertical = resetScroll ? 0 : _text.VerticalOffset;
            _text.Text = value;
            _text.Select(start, length);
            _text.ScrollToHorizontalOffset(horizontal);
            _text.ScrollToVerticalOffset(vertical);
            if (animate && SystemParameters.ClientAreaAnimation)
                _text.BeginAnimation(OpacityProperty, Motion.Tween(0.25, 1, 180));
        }
        if (resetScroll)
            _text.ScrollToHome();
    }

    public void SetStatus(string status)
    {
        _status.Text = status;
        _status.ToolTip = status;
        _status.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
        _provider.ToolTip = status;
        AutomationProperties.SetHelpText(_provider, status);
    }

    public void SetSourceLanguage(string languageTag)
    {
        _sourceLanguage = languageTag.Split('-')[0].ToLowerInvariant() switch
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
        UpdateLanguagePair();
    }

    private void UpdateLanguagePair()
    {
        _languagePair.Text = $"{_sourceLanguage} → {TargetLanguage} ⌄";
        AutomationProperties.SetName(_languageButton, $"翻译成{TargetLanguage}");
    }

    public void SetProvider(string providerName)
    {
        _service = providerName switch
        {
            "" or "AI 自然翻译" => "AI 翻译",
            _ => providerName
        };
        UpdateProvider();
    }

    public void SetElapsed(double seconds)
    {
        _elapsed = seconds;
        UpdateProvider();
    }

    private void UpdateProvider() =>
        _provider.Text = _elapsed is { } duration ? $"{_service} · {duration:F1} 秒" : _service;

    public void Complete()
    {
        _busy = false;
        _retranslate.IsEnabled = true;
        _placeholder = "未识别到可翻译文字";
        // Keep the normal timing details in the provider tooltip; errors and fallback explanations stay visible.
        if (_status.Text.StartsWith("翻译完成", StringComparison.Ordinal))
            _status.Visibility = Visibility.Collapsed;
        ShowBody();
        UpdateRing();
    }

    public void SetError(string message)
    {
        _busy = false;
        _failed = true;
        _translation = string.Empty;
        _placeholder = message;
        _retranslate.IsEnabled = true;
        _tab.SelectedIndex = 1;
        SetStatus("请求失败，请检查接口配置");
        ShowBody(animate: true);
        UpdateRing();
    }

    private void UpdateRing()
    {
        var spinning = _busy && IsVisible && SystemParameters.ClientAreaAnimation;
        if (spinning != _rotating)
        {
            _rotation.BeginAnimation(RotateTransform.AngleProperty, spinning
                ? (DoubleAnimation)FindResource("Translation.Rotation")
                : null);
            _rotating = spinning;
        }
        var target = _busy && IsVisible ? 1d : 0d;
        var previous = _ring.Opacity;
        _ring.BeginAnimation(OpacityProperty, null);
        _ring.Opacity = target;
        if (IsVisible && SystemParameters.ClientAreaAnimation && previous != target)
            _ring.BeginAnimation(OpacityProperty, Motion.Tween(previous, target, 180));
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Visibility = Visibility.Collapsed;

    private void RetryClick(object sender, RoutedEventArgs e) => RetranslateRequested?.Invoke(this, EventArgs.Empty);

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            BitmapUtilities.SetClipboardTextWithRetry(_tab.SelectedIndex == 0 ? _source : _translation);
            HintToastWindow.Success("已拷贝");
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message);
        }
    }

    private void ChooseLanguage(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = _languageButton, Placement = PlacementMode.Bottom };
        menu.SetResourceReference(StyleProperty, "GlassMenuStyle");
        foreach (var language in TargetLanguages)
        {
            var item = new MenuItem { Header = language, IsCheckable = true, IsChecked = language == TargetLanguage };
            item.Click += (_, _) =>
            {
                if (TargetLanguage == language)
                    return;
                TargetLanguage = language;
                UpdateLanguagePair();
                TargetLanguageChanged?.Invoke(this, language);
            };
            menu.Items.Add(item);
        }
        _languageButton.ContextMenu = menu;
        menu.IsOpen = true;
    }
    public void SetPointer(CalloutPointerSide side, double offset)
    {
        _pointer.Visibility = side == CalloutPointerSide.None ? Visibility.Collapsed : Visibility.Visible;
        _chrome.Margin = side switch
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

}
