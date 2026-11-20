using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Lucide.NET;
using Screen.Core;

namespace Screen.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly HttpClient _translationTestClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly Dictionary<string, Button> _navigation = [];
    private readonly Dictionary<string, FrameworkElement> _pages = [];

    private CancellationTokenSource? _translationTestOperation;
    private TextBlock? _translationTestStatus;

    // Optional live registration: when provided, a newly captured hotkey is
    // registered immediately instead of waiting for this window to close.
    private readonly Func<string, bool>? _registerCaptureHotKey;
    private readonly Func<string, bool>? _registerRecoveryHotKey;
    private readonly Func<string, bool>? _registerRecordHotKey;

    // Hot-key input controls.
    private Button? _captureHotKeyButton;
    private Button? _clickThroughRecoveryHotKeyButton;
    private Button? _recordHotKeyButton;

    // Translation page sections that show/hide with the selected provider.
    private StackPanel? _tencentSection;
    private StackPanel? _aiSection;
    private StackPanel? _memorySection;
    private StackPanel? _racingRows;
    private FrameworkElement? _fastFallbackRow;

    internal SettingsWindow(
        JsonSettingsStore store,
        ICredentialStore credentials,
        Action<bool> setStartupEnabled,
        Func<string, bool>? registerCaptureHotKey = null,
        Func<string, bool>? registerRecoveryHotKey = null,
        Func<string, bool>? registerRecordHotKey = null,
        Func<bool, bool>? setHotKeyPriority = null,
        Func<string?>? hotKeyPriorityError = null)
    {
        InitializeComponent();
        // WPF's plain .ico URI selects the small tray frame. Keep the full-resolution
        // artwork for this 40-DIP tile, including when the window moves between DPIs.
        using (var icon = Application.GetResourceStream(new Uri("/Screen;component/Assets/Screen.ico", UriKind.Relative)).Stream)
            ProfileIcon.Source = new IconBitmapDecoder(icon, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
                .Frames.MaxBy(frame => frame.PixelWidth);
        _registerCaptureHotKey = registerCaptureHotKey;
        _registerRecoveryHotKey = registerRecoveryHotKey;
        _registerRecordHotKey = registerRecordHotKey;
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        NavVersion.Text = version is null ? "本地构建" : $"版本 {version.ToString(3)}";
        SourceInitialized += (_, _) =>
        {
            TrackWorkArea();
            ApplyBackdrop();
        };

        _viewModel = new SettingsViewModel(store, credentials, setStartupEnabled, setHotKeyPriority, hotKeyPriorityError);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closing += (_, e) =>
        {
            _viewModel.SaveNow();
            _viewModel.PauseAutoSave();
            if (_viewModel.HasUnsavedChanges && !Dispatcher.HasShutdownStarted)
            {
                e.Cancel = MessageBox.Show(this,
                    $"有效的设置项已保存，仍有未保存内容：\n{_viewModel.StatusText}\n\n关闭将放弃未保存的内容。是否继续关闭？",
                    "Screen 设置", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes;
                if (e.Cancel)
                    _viewModel.ResumeAutoSave();
                else
                    _viewModel.DiscardPendingChanges();
            }
        };

        BuildNavigation();
        ShowPage("general");
    }

    private sealed record PageInfo(string Id, string Title, string Summary, LucideIconKind Icon, string Tile);

    private static readonly PageInfo[] _pageDefinitions =
    [
        new("general", "通用", "启动方式、快捷键和存储位置。", LucideIconKind.Settings2, "Tile.Gray"),
        new("recording", "录屏", "录下屏幕的一块区域，保存成视频或 GIF。", LucideIconKind.Video, "Tile.Red"),
        new("translation", "文字与翻译", "截图里的文字，一键提取，随手翻译。", LucideIconKind.Languages, "Tile.Indigo")
    ];

    private void ApplyBackdrop()
    {
        if (HwndSource.FromHwnd(new WindowInteropHelper(this).Handle) is not { CompositionTarget: { } target } source)
            return;
        // The XAML defaults deliberately remain opaque until DWM accepts both requests.
        var dark = 1;
        var mica = 2;
        var enabled = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !SystemParameters.HighContrast
            && NativeMethods.DwmSetWindowAttribute(source.Handle, 20, ref dark, sizeof(int)) >= 0
            && NativeMethods.DwmSetWindowAttribute(source.Handle, 38, ref mica, sizeof(int)) >= 0;
        var chrome = WindowChrome.GetWindowChrome(this);
        if (enabled)
        {
            var glass = (WindowChrome)chrome.CloneCurrentValue();
            glass.GlassFrameThickness = new Thickness(-1);
            WindowChrome.SetWindowChrome(this, glass);
            Sidebar.SetResourceReference(Border.BackgroundProperty, "Settings.MicaSidebar");
            ContentSurface.SetResourceReference(Grid.BackgroundProperty, "Settings.MicaContent");
            SetResourceReference(BackgroundProperty, "Brush.Transparent");
        }
        var brush = (SolidColorBrush)FindResource(enabled ? "Brush.Transparent" : "Brush.Window");
        target.BackgroundColor = brush.Color;
    }

    private void BuildNavigation()
    {
        foreach (var page in _pageDefinitions)
        {
            var icon = new LucideIconElement { Kind = page.Icon, Width = 15, Height = 15, StrokeThickness = 1.7 };
            icon.SetResourceReference(LucideIconElement.StrokeProperty, "Brush.White");
            var tile = new IconTile { Width = 24, Height = 24, Content = icon };
            tile.SetResourceReference(BackgroundProperty, page.Tile);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(tile);
            row.Children.Add(new TextBlock { Text = page.Title, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = row, Style = (Style)FindResource("Settings.Navigation") };
            AutomationProperties.SetName(button, page.Title);
            button.Click += (_, _) => ShowPage(page.Id);
            _navigation.Add(page.Id, button);
            NavHost.Children.Add(button);
        }
    }

    private void ShowPage(string id)
    {
        var info = _pageDefinitions.First(page => page.Id == id);
        foreach (var (key, button) in _navigation)
        {
            var selected = key == id;
            HudActiveButton.SetIsActive(button, selected);
            AutomationProperties.SetItemStatus(button, selected ? "已选中" : string.Empty);
        }

        if (!_pages.TryGetValue(id, out var page))
        {
            page = id switch
            {
                "recording" => BuildRecordingPage(),
                "translation" => BuildTranslationPage(),
                _ => BuildGeneralPage()
            };
            _pages.Add(id, page);
        }

        Caption.Text = PageTitle.Text = info.Title;
        PageDescription.Text = info.Summary;
        HeaderIcon.Kind = info.Icon;
        HeaderTile.SetResourceReference(BackgroundProperty, info.Tile);
        TranslationStatusBadge.Visibility = id == "translation" ? Visibility.Visible : Visibility.Collapsed;
        PageHost.Content = page;
        PageScroll.ScrollToTop();
        UpdateTranslationSections();
    }

    private void MinimizeClick(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaximizeButton is null)
            return;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Kind = maximized ? LucideIconKind.Copy : LucideIconKind.Square;
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
        Shell.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // Cancel without disposing: an in-flight test may still be linking
        // this token (CreateLinkedTokenSource registers on it), and disposing
        // surfaces ObjectDisposedException instead of cancellation.
        _translationTestOperation?.Cancel();
        _translationTestClient.Dispose();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
    }
}
