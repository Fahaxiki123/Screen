using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Lucide.NET;

namespace Screen.App;

public partial class SettingsWindow
{
    private FrameworkElement BuildGeneralPage()
    {
        var page = new StackPanel();
        page.Children.Add(CreateCard(out var general));
        general.Children.Add(CreateSettingRow("开机时启动", CreateBoundToggle(nameof(SettingsViewModel.StartWithWindows))));
        general.Children.Add(CreateDivider());
        general.Children.Add(CreateSettingRow("存储位置", CreateSaveFolderPicker()));

        page.Children.Add(CreateSectionHeader("快捷键"));
        page.Children.Add(CreateCard(out var keys));
        _captureHotKeyButton = CreateHotKeyButton(nameof(SettingsViewModel.HotKey));
        keys.Children.Add(CreateSettingRow("截图", _captureHotKeyButton, LucideIconKind.ScanText, "Tile.Blue"));
        keys.Children.Add(CreateDivider(withIcon: true));
        _recordHotKeyButton = CreateHotKeyButton(nameof(SettingsViewModel.RecordHotKey));
        keys.Children.Add(CreateSettingRow("录屏", _recordHotKeyButton, LucideIconKind.Video, "Tile.Red"));
        page.Children.Add(CreateFootnote("点按快捷键，然后直接按下新的组合。"));

        var advanced = new StackPanel();
        _clickThroughRecoveryHotKeyButton = CreateHotKeyButton(nameof(SettingsViewModel.ClickThroughRecoveryHotKey));
        advanced.Children.Add(CreateSettingRow("恢复穿透的贴图", _clickThroughRecoveryHotKeyButton));
        advanced.Children.Add(CreateDivider());
        advanced.Children.Add(CreateSettingRow("快捷键优先交给 Screen", CreateBoundToggle(nameof(SettingsViewModel.PreferScreenHotKeys))));
        page.Children.Add(CreateAdvanced(advanced));
        return page;
    }

    private Button CreateSaveFolderPicker()
    {
        var button = CreateSecondaryButton("选择…");
        button.MaxWidth = 250;
        var label = new TextBlock { MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var contents = new StackPanel { Orientation = Orientation.Horizontal };
        contents.Children.Add(new LucideIconElement { Kind = LucideIconKind.Folder, Width = 15, Height = 15, Margin = new Thickness(0, 0, 6, 0), Stroke = GetResource<System.Windows.Media.Brush>("Brush.LabelSecondary") });
        contents.Children.Add(label);
        button.Content = contents;

        void Refresh()
        {
            var path = _viewModel.SaveDirectory;
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var prefix = Path.TrimEndingDirectorySeparator(pictures) + Path.DirectorySeparatorChar;
            label.Text = path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && pictures.Length != 0
                ? "图片 › " + path[prefix.Length..].Replace(Path.DirectorySeparatorChar.ToString(), " › ")
                : path;
            button.ToolTip = path;
        }
        Refresh();
        _viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SettingsViewModel.SaveDirectory)) Refresh(); };
        button.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择截图存储位置" };
            if (Directory.Exists(_viewModel.SaveDirectory))
                dialog.InitialDirectory = _viewModel.SaveDirectory;
            if (dialog.ShowDialog(this) == true)
                _viewModel.SaveDirectory = dialog.FolderName;
        };
        return button;
    }

    private FrameworkElement BuildRecordingPage()
    {
        var page = new StackPanel();
        page.Children.Add(CreateCard(out var presets));
        presets.Children.Add(CreateSettingRow("默认格式", CreateBoundSegment(nameof(SettingsViewModel.RecordingFormatIndex), ["视频", "GIF"]), LucideIconKind.Images, "Tile.Purple"));
        presets.Children.Add(CreateDivider(withIcon: true));
        presets.Children.Add(CreateSettingRow("画质", CreateQualityPresets(), LucideIconKind.Monitor, "Tile.Indigo"));
        var summary = CreateFootnote(string.Empty);
        ((TextBlock)summary.Child).SetBinding(TextBlock.TextProperty, new Binding(nameof(SettingsViewModel.RecordingSummary)) { Source = _viewModel });
        page.Children.Add(summary);

        page.Children.Add(CreateSectionHeader("录制内容"));
        page.Children.Add(CreateCard(out var inputs));
        inputs.Children.Add(CreateSettingRow("系统声音", CreateBoundToggle(nameof(SettingsViewModel.RecordingSystemAudio)), LucideIconKind.Volume2, "Tile.Pink"));
        inputs.Children.Add(CreateDivider(withIcon: true));
        inputs.Children.Add(CreateSettingRow("麦克风", CreateBoundToggle(nameof(SettingsViewModel.RecordingMicrophone)), LucideIconKind.Mic, "Tile.Orange"));
        inputs.Children.Add(CreateDivider(withIcon: true));
        inputs.Children.Add(CreateSettingRow("录制鼠标光标", CreateBoundToggle(nameof(SettingsViewModel.RecordingShowCursor)), LucideIconKind.MousePointer2, "Tile.Teal"));
        inputs.Children.Add(CreateDivider(withIcon: true));
        inputs.Children.Add(CreateSettingRow("鼠标点击效果", CreateBoundToggle(nameof(SettingsViewModel.RecordingClickEffects)), LucideIconKind.MousePointerClick, "Tile.Teal"));

        var advanced = new StackPanel();
        void Add(string label, FrameworkElement control)
        {
            if (advanced.Children.Count != 0)
                advanced.Children.Add(CreateDivider());
            advanced.Children.Add(CreateSettingRow(label, control));
        }
        Add("帧率", RecordingChoice(nameof(SettingsViewModel.RecordingFrameRateIndex), ["15 帧", "30 帧", "60 帧"], () => $"{_viewModel.Snapshot.Recording.FrameRate} 帧"));
        Add("码率", CreateBoundComboBox(nameof(SettingsViewModel.RecordingQualityIndex), ["4 Mbps", "8 Mbps", "12 Mbps"]));
        Add("GIF 帧率", RecordingChoice(nameof(SettingsViewModel.RecordingGifFrameRateIndex), ["10 帧", "15 帧", "20 帧", "30 帧"], () => $"{_viewModel.Snapshot.Recording.GifFrameRate} 帧"));
        Add("GIF 最大宽度", RecordingChoice(nameof(SettingsViewModel.RecordingGifMaxWidthIndex), ["原始", "1920", "1280", "960"], () => $"{_viewModel.Snapshot.Recording.GifMaxWidth} px"));
        Add("临时文件夹", CreateTempFolderPicker());
        advanced.Children.Add(CreateFootnote("留空使用系统临时文件夹。开录前会检查可用空间；保存失败的恢复素材不会自动删除。"));
        page.Children.Add(CreateAdvanced(advanced));
        return page;
    }

    // Older/hand-edited files may contain a value not offered by today's menu.
    // Show its real value without forcing a nearby selection back into the model.
    private Grid RecordingChoice(string property, IReadOnlyList<string> options, Func<string> original)
    {
        var combo = CreateBoundComboBox(property, options);
        var label = new TextBlock { Margin = new Thickness(10, 0, 25, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Label");
        void Refresh()
        {
            label.Text = original();
            label.Visibility = combo.SelectedIndex < 0 ? Visibility.Visible : Visibility.Collapsed;
            combo.ToolTip = combo.SelectedIndex < 0 ? $"当前值：{label.Text}" : null;
        }
        combo.SelectionChanged += (_, _) => Refresh();
        Refresh();
        return new Grid { Children = { combo, label } };
    }

    private SegmentedControl CreateQualityPresets()
    {
        var segments = new SegmentedControl();
        segments.Items.Add("标准");
        segments.Items.Add("高清");
        var custom = new ListBoxItem { Content = "自定义", IsEnabled = false, Focusable = false };
        var syncing = false;
        void Refresh()
        {
            syncing = true;
            try
            {
                var index = _viewModel.RecordingPresetIndex;
                if (index == 2 && !segments.Items.Contains(custom))
                    segments.Items.Add(custom);
                segments.SelectedIndex = index;
                if (index != 2 && segments.Items.Contains(custom))
                    segments.Items.Remove(custom);
            }
            finally { syncing = false; }
        }
        Refresh();
        segments.SelectionChanged += (_, _) =>
        {
            if (!syncing && segments.SelectedIndex is 0 or 1)
                _viewModel.RecordingPresetIndex = segments.SelectedIndex;
        };
        _viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SettingsViewModel.RecordingPresetIndex)) Refresh(); };
        return segments;
    }

    private StackPanel CreateTempFolderPicker()
    {
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var path = CreateBoundTextBox(nameof(SettingsViewModel.RecordingTempDirectory), 205);
        path.Tag = "系统默认";
        var choose = CreateSecondaryButton("选择…");
        choose.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择录屏临时文件夹" };
            if (Directory.Exists(_viewModel.RecordingTempDirectory))
                dialog.InitialDirectory = _viewModel.RecordingTempDirectory;
            if (dialog.ShowDialog(this) == true)
                _viewModel.RecordingTempDirectory = dialog.FolderName;
        };
        controls.Children.Add(path);
        controls.Children.Add(choose);
        return controls;
    }
}
