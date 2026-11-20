using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

internal sealed partial class CaptureOverlayWindow
{
    private RecordingFormat _formatChoice;
    private bool _showClickEffectsChoice;

    private static Brush FindResourceBrush(string key) => (Brush)Application.Current.FindResource(key);

    private WrapPanel BuildRecordingPanel()
    {
        var defaults = _settingsStore.Load().Recording;
        _formatChoice = defaults.Format;
        _showCursorChoice = defaults.ShowCursor;
        _showClickEffectsChoice = defaults.ShowClickEffects;
        _recordSystemAudioChoice = defaults.RecordSystemAudio;
        _recordMicrophoneChoice = defaults.RecordMicrophone;

        var format = new SegmentedControl
        {
            ItemsSource = new[] { FormatLabel(LucideIconKind.Video, "视频"), FormatLabel(LucideIconKind.Images, "GIF") },
            SelectedIndex = _formatChoice == RecordingFormat.Gif ? 1 : 0,
            Height = 34
        };
        AutomationProperties.SetName(format, "录制格式");

        var options = HorizontalRow();
        var speaker = RecordingOption("系统声音", LucideIconKind.Volume2);
        var mic = RecordingOption("麦克风", LucideIconKind.Mic);
        var cursor = RecordingOption("鼠标", LucideIconKind.MousePointer2);
        options.Children.Add(speaker);
        options.Children.Add(mic);
        options.Children.Add(cursor);

        // The buttons display the effective state, while these delegates retain the
        // user's video choices even when the GIF format temporarily disables sound.
        var refreshSpeaker = BindAudio(speaker, "系统声音", LucideIconKind.Volume2, LucideIconKind.VolumeX,
            () => _recordSystemAudioChoice, enabled => _recordSystemAudioChoice = enabled);
        var refreshMic = BindAudio(mic, "麦克风", LucideIconKind.Mic, LucideIconKind.MicOff,
            () => _recordMicrophoneChoice, enabled => _recordMicrophoneChoice = enabled);
        format.SelectionChanged += (_, _) =>
        {
            _formatChoice = format.SelectedIndex == 1 ? RecordingFormat.Gif : RecordingFormat.Mp4;
            refreshSpeaker();
            refreshMic();
        };
        cursor.IsChecked = _showCursorChoice || _showClickEffectsChoice;
        cursor.ToolTip = (_showCursorChoice, _showClickEffectsChoice) switch
        {
            (true, false) => "仅录制鼠标光标",
            (false, true) => "仅录制鼠标点击效果",
            (true, true) => "录制鼠标光标与点击效果",
            _ => "不录鼠标光标和点击效果"
        };
        void SetMouseChoice(bool enabled)
        {
            _showCursorChoice = _showClickEffectsChoice = enabled;
            cursor.ToolTip = enabled ? "录制鼠标光标与点击效果" : "不录鼠标光标和点击效果";
        }
        cursor.Checked += (_, _) => SetMouseChoice(true);
        cursor.Unchecked += (_, _) => SetMouseChoice(false);

        var mark = new Ellipse { Width = 10, Height = 10, Margin = new Thickness(0, 0, 6, 0) };
        mark.SetResourceReference(Shape.FillProperty, "Brush.White");
        var caption = HorizontalRow();
        caption.Children.Add(mark);
        caption.Children.Add(new TextBlock { Text = "录制" });
        var start = new Button { Content = caption, ToolTip = "开始录制　Enter" };
        AutomationProperties.SetName(start, "开始录制");
        start.SetResourceReference(StyleProperty, "RecordButtonStyle");
        start.Click += (_, _) => BeginRecordingFromPanel();

        return new WrapPanel { Children = { ToolbarGroup(format), ToolbarGroup(options), ToolbarGroup(start, last: true) } };
    }

    private static StackPanel FormatLabel(LucideIconKind kind, string text)
    {
        var row = HorizontalRow();
        var icon = HudIcon(kind);
        icon.Width = icon.Height = 15;
        icon.Margin = new Thickness(0, 0, 5, 0);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetName(row, text);
        return row;
    }

    private static ToggleButton RecordingOption(string name, LucideIconKind icon)
    {
        var button = new ToggleButton { Content = HudIcon(icon) };
        button.SetResourceReference(StyleProperty, "RecordingOptionStyle");
        ToolTipService.SetShowOnDisabled(button, true);
        AutomationProperties.SetName(button, name);
        return button;
    }

    private Action BindAudio(ToggleButton button, string label, LucideIconKind on, LucideIconKind off,
        Func<bool> choice, Action<bool> remember)
    {
        void UpdateIcon()
        {
            var active = button.IsChecked == true;
            ((LucideIconElement)button.Content).Kind = active ? on : off;
            button.ToolTip = !button.IsEnabled ? "GIF 不含声音" : active ? $"录制{label}" : $"不录{label}";
        }

        void Refresh()
        {
            button.IsEnabled = _formatChoice != RecordingFormat.Gif;
            button.IsChecked = button.IsEnabled && choice();
            UpdateIcon();
        }

        void Changed(object sender, RoutedEventArgs e)
        {
            if (button.IsEnabled)
                remember(button.IsChecked == true);
            UpdateIcon();
        }
        Refresh();
        button.Checked += Changed;
        button.Unchecked += Changed;
        return Refresh;
    }
}
