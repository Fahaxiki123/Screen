using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

/// <summary>
/// UI building methods for CaptureOverlayWindow (toolbars, panels, buttons).
/// </summary>
internal sealed partial class CaptureOverlayWindow
{
    private static Brush FindResourceBrush(string key) => (Brush)Application.Current.FindResource(key);

    private static System.Windows.Media.Effects.DropShadowEffect FindResourceEffect(string key) =>
        (System.Windows.Media.Effects.DropShadowEffect)Application.Current.FindResource(key);
    /// <summary>
    /// Recording setup card uses the shared navy and moon-blue palette,
    /// with a light primary action and quiet format and audio options.
    /// </summary>
    private Border BuildRecordingPanel()
    {
        var recordingDefaults = _settingsStore.Load().Recording;
        _showCursorChoice = recordingDefaults.ShowCursor;
        _recordSystemAudioChoice = recordingDefaults.RecordSystemAudio;
        _recordMicrophoneChoice = recordingDefaults.RecordMicrophone;

        var startButton = new Button
        {
            Content = "开始录制",
            Height = 42,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindResourceBrush("Brush.OnAccent"),
            Padding = new Thickness(0)
        };
        startButton.SetResourceReference(StyleProperty, "CardPrimaryButtonStyle");
        startButton.Click += (_, _) => BeginRecordingFromPanel();

        var formatRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0)
        };
        formatRow.Children.Add(RecordingFormatChip("MP4", isSelected: true, isEnabled: true, "录制为 MP4 视频"));
        formatRow.Children.Add(RecordingFormatChip("GIF", isSelected: false, isEnabled: false, "录后可在编辑窗导出 GIF"));

        var iconRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0)
        };
        _speakerToggleIcon = RecordingToggleIcon(
            _recordSystemAudioChoice ? LucideIconKind.Volume2 : LucideIconKind.VolumeX,
            _recordSystemAudioChoice);
        var speakerButton = RecordingOptionButton(
            _speakerToggleIcon,
            "扬声器",
            _recordSystemAudioChoice ? "已开启：录制系统声音" : "已关闭：不录制系统声音",
            enabled: true);
        StyleRecordingToggle(_speakerToggleIcon, _recordSystemAudioChoice, speakerButton);
        speakerButton.Click += (_, _) =>
        {
            _recordSystemAudioChoice = !_recordSystemAudioChoice;
            speakerButton.ToolTip = _recordSystemAudioChoice ? "已开启：录制系统声音" : "已关闭：不录制系统声音";
            _speakerToggleIcon.Kind = _recordSystemAudioChoice ? LucideIconKind.Volume2 : LucideIconKind.VolumeX;
            StyleRecordingToggle(_speakerToggleIcon, _recordSystemAudioChoice, speakerButton);
        };
        iconRow.Children.Add(speakerButton);

        _micToggleIcon = RecordingToggleIcon(LucideIconKind.Mic, _recordMicrophoneChoice);
        var micButton = RecordingOptionButton(
            _micToggleIcon,
            "麦克风",
            _recordMicrophoneChoice ? "已开启：录制麦克风" : "已关闭：不录制麦克风",
            enabled: true);
        StyleRecordingToggle(_micToggleIcon, _recordMicrophoneChoice, micButton);
        micButton.Click += (_, _) =>
        {
            _recordMicrophoneChoice = !_recordMicrophoneChoice;
            micButton.ToolTip = _recordMicrophoneChoice ? "已开启：录制麦克风" : "已关闭：不录制麦克风";
            StyleRecordingToggle(_micToggleIcon, _recordMicrophoneChoice, micButton);
        };
        iconRow.Children.Add(micButton);

        iconRow.Children.Add(RecordingOptionButton(
            RecordingToggleIcon(LucideIconKind.Video, enabled: false),
            "摄像头",
            "摄像头画中画即将推出",
            enabled: false));

        _cursorToggleIcon = RecordingToggleIcon(LucideIconKind.MousePointer2, _showCursorChoice);
        var cursorButton = RecordingOptionButton(
            _cursorToggleIcon,
            "鼠标设置",
            _showCursorChoice ? "已开启：录制鼠标光标与点击波纹" : "已关闭：不录制鼠标光标与点击波纹",
            enabled: true);
        StyleRecordingToggle(_cursorToggleIcon, _showCursorChoice, cursorButton);
        cursorButton.Click += (_, _) =>
        {
            _showCursorChoice = !_showCursorChoice;
            cursorButton.ToolTip = _showCursorChoice ? "已开启：录制鼠标光标与点击波纹" : "已关闭：不录制鼠标光标与点击波纹";
            StyleRecordingToggle(_cursorToggleIcon, _showCursorChoice, cursorButton);
        };
        iconRow.Children.Add(cursorButton);

        var content = new StackPanel { Width = 276, Margin = new Thickness(20, 18, 20, 14) };
        content.Children.Add(startButton);
        content.Children.Add(formatRow);
        content.Children.Add(new Border
        {
            Height = 1,
            Background = FindResourceBrush("Brush.Border"),
            Margin = new Thickness(0, 14, 0, 2)
        });
        content.Children.Add(iconRow);

        return new Border
        {
            Background = FindResourceBrush("Brush.AuroraCard"),
            CornerRadius = new CornerRadius(14),
            BorderBrush = FindResourceBrush("Brush.GlassRim"),
            BorderThickness = new Thickness(1),
            Effect = FindResourceEffect("Shadow.Floating"),
            Child = content
        };
    }

    private static void StyleRecordingToggle(LucideIconElement icon, bool enabled, Button button)
    {
        icon.Stroke = enabled
            ? FindResourceBrush("Brush.Accent")
            : FindResourceBrush("Brush.TextTertiary");
        icon.Opacity = 1;
        button.Background = enabled ? FindResourceBrush("Brush.AccentSubtleDark") : Brushes.Transparent;
        button.BorderBrush = enabled ? FindResourceBrush("Brush.Accent") : Brushes.Transparent;
        button.BorderThickness = new Thickness(1);
    }

    private static LucideIconElement RecordingToggleIcon(LucideIconKind kind, bool enabled) => new()
    {
        Kind = kind,
        Width = 20,
        Height = 20,
        StrokeThickness = 1.7,
        Stroke = enabled
            ? FindResourceBrush("Brush.Accent")
            : FindResourceBrush("Brush.TextTertiary"),
        HorizontalAlignment = HorizontalAlignment.Center
    };

    private static Button RecordingOptionButton(LucideIconElement icon, string label, string toolTip, bool enabled)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = FindResourceBrush("Brush.TextSecondary"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        };
        var button = new Button
        {
            Content = new StackPanel { Children = { icon, text } },
            Width = 61,
            Height = 54,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            ToolTip = toolTip,
            IsEnabled = enabled,
            Opacity = enabled ? 1 : 0.55
        };
        button.SetResourceReference(StyleProperty, "ToolbarIconButtonDarkStyle");
        return button;
    }

    private static Border RecordingFormatChip(string label, bool isSelected, bool isEnabled, string toolTip)
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = isSelected
                ? FindResourceBrush("Brush.Accent")
                : Brushes.Transparent,
            Stroke = isSelected
                ? null
                : FindResourceBrush("Brush.TextTertiary"),
            StrokeThickness = 1.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = isSelected
                ? FindResourceBrush("Brush.AccentText")
                : FindResourceBrush("Brush.TextTertiary"),
            VerticalAlignment = VerticalAlignment.Center
        };
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 14, 6),
            Margin = new Thickness(0, 0, 10, 0),
            Background = isSelected
                ? FindResourceBrush("Brush.AccentSubtleDark")
                : FindResourceBrush("Brush.SurfaceRaised"),
            BorderBrush = isSelected
                ? FindResourceBrush("Brush.Accent")
                : FindResourceBrush("Brush.Border"),
            BorderThickness = new Thickness(1),
            ToolTip = toolTip,
            Opacity = isEnabled ? 1 : 0.6,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text } }
        };
    }

}
