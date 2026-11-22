using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

/// <summary>
/// 验证设置侧边栏分组导航的融合动效：同组平滑平移、跨组原地淡入淡出及鼠标悬停磁吸追踪。
/// </summary>
[TestClass]
public sealed class SettingsNavGroupTests
{
    [TestMethod]
    public void SameGroupSlideMovesSmoothlyWithoutScalingAndSettlesExactly()
    {
        var motion = new SettingsNavTrackMotion();
        var first = new Rect(0, 0, 188, 36);
        var second = new Rect(0, 40, 188, 36);

        motion.SnapSelection(first);
        Assert.AreEqual(first, motion.SelectionBounds);
        Assert.AreEqual(1d, motion.SelectionOpacity, 0.0001);
        Assert.AreEqual(1d, motion.SelectionScale, 0.0001);

        motion.SlideSelectionTo(second);
        Assert.IsFalse(motion.Advance(1.0 / 60.0));
        Assert.IsTrue(motion.SelectionBounds.Y > 0 && motion.SelectionBounds.Y < second.Y);
        Assert.AreEqual(1d, motion.SelectionOpacity, 0.0001);
        Assert.AreEqual(1d, motion.SelectionScale, 0.0001);

        bool settled = false;
        for (int i = 0; i < 180 && !settled; i++)
        {
            settled = motion.Advance(1.0 / 60.0);
        }

        Assert.IsTrue(settled);
        Assert.AreEqual(second, motion.SelectionBounds);
        Assert.AreEqual(1d, motion.SelectionOpacity, 0.0001);
        Assert.AreEqual(1d, motion.SelectionScale, 0.0001);
    }

    [TestMethod]
    public void CrossGroupFadeOutAndFadeInNeverSlideAcrossGroupHeaders()
    {
        var oldGroup = new SettingsNavTrackMotion();
        var newGroup = new SettingsNavTrackMotion();
        var oldRect = new Rect(0, 40, 188, 36);
        var newRect = new Rect(0, 80, 188, 36);

        oldGroup.SnapSelection(oldRect);
        oldGroup.FadeOutSelection();
        newGroup.FadeInSelection(newRect);

        Assert.AreEqual(0d, newGroup.SelectionOpacity, 0.0001);
        Assert.AreEqual(newRect.Y, newGroup.SelectionBounds.Y, 0.0001);

        oldGroup.Advance(0.05);
        newGroup.Advance(0.05);

        // 旧组原地淡出、绝不发生垂直位移；新组直接定位目标位置并原地淡入。
        Assert.AreEqual(oldRect.Y, oldGroup.SelectionBounds.Y, 0.0001);
        Assert.IsTrue(oldGroup.SelectionOpacity < 0.9 && oldGroup.SelectionOpacity > 0);
        Assert.AreEqual(newRect.Y, newGroup.SelectionBounds.Y, 0.0001);
        Assert.IsTrue(newGroup.SelectionOpacity > 0.2 && newGroup.SelectionOpacity < 1);

        for (int i = 0; i < 120; i++)
        {
            oldGroup.Advance(1.0 / 60.0);
            newGroup.Advance(1.0 / 60.0);
        }

        Assert.AreEqual(0d, oldGroup.SelectionOpacity, 0.0001);
        Assert.AreEqual(1d, newGroup.SelectionOpacity, 0.0001);
        Assert.AreEqual(1d, newGroup.SelectionScale, 0.0001);
    }

    [TestMethod]
    public void HoverTrackSnapsOnFirstEntryAndGlidesWithinSameGroup()
    {
        var motion = new SettingsNavTrackMotion();
        var item1 = new Rect(0, 0, 188, 36);
        var item2 = new Rect(0, 40, 188, 36);

        // 初次进入分组时直接落位并淡入，不从 0 位置飞入。
        motion.HoverAt(item2);
        Assert.AreEqual(item2.Y, motion.HoverBounds.Y, 0.0001);
        for (int i = 0; i < 30; i++)
        {
            motion.Advance(1.0 / 60.0);
        }

        Assert.IsTrue(motion.HoverOpacity > 0.95);

        // 同分组内移动到另一项时平滑磁吸跟随。
        motion.HoverAt(item1);
        Assert.IsFalse(motion.Advance(1.0 / 60.0));
        Assert.IsTrue(motion.HoverBounds.Y < item2.Y && motion.HoverBounds.Y > item1.Y);

        for (int i = 0; i < 120; i++)
        {
            motion.Advance(1.0 / 60.0);
        }

        Assert.AreEqual(item1.Y, motion.HoverBounds.Y, 0.01);

        // 离开分组后柔和淡出归零。
        motion.HideHover();
        for (int i = 0; i < 60; i++)
        {
            motion.Advance(1.0 / 60.0);
        }

        Assert.AreEqual(0d, motion.HoverOpacity, 0.0001);
    }

    [STATestMethod]
    public void BrandIconResourcesAndKeyboardVectorIconAreAvailable()
    {
        var iconStream = Application.GetResourceStream(new Uri("/Screen;component/Assets/Screen.png", UriKind.Relative));
        Assert.IsNotNull(iconStream, "Embedded Flick PNG icon resource must exist.");
        iconStream.Stream.Dispose();

        using var trayIcon = ScreenIconFactory.CreateTrayIcon();
        Assert.IsTrue(trayIcon.Width > 0 && trayIcon.Height > 0);

        var keyboardIcon = AppleVectorIcons.Create("keyboard", 16);
        Assert.AreEqual(16d, keyboardIcon.Width);
        Assert.AreEqual(16d, keyboardIcon.Height);
    }

    [STATestMethod]
    public void SettingsWindowOpensSwitchesAcrossGroupsAndClosesCleanly()
    {
        var tempDir = Directory.CreateTempSubdirectory("flick-settings-ui-test-");
        try
        {
            EnsureThemeResources();
            var store = new JsonSettingsStore(Path.Combine(tempDir.FullName, "settings.json"));
            var credentials = new InMemoryCredentialStore();
            var window = new SettingsWindow(store, credentials, _ => { });
            window.Left = -30000;
            window.Top = -30000;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;

            try
            {
                window.Show();
                PumpDispatcher(window, 120);
                Assert.AreEqual("Flick 设置", window.Title);

                var snapshotDir = Environment.GetEnvironmentVariable("FLICK_QA_SNAPSHOT_DIR");
                if (!string.IsNullOrWhiteSpace(snapshotDir))
                {
                    CaptureWindow(window, Path.Combine(snapshotDir, "settings_general.png"));
                }

                var buttons = FindVisualChildren<Button>(window).ToList();
                var shortcutsBtn = buttons.First(b => string.Equals(System.Windows.Automation.AutomationProperties.GetName(b), "快捷键", StringComparison.Ordinal));
                var recordingBtn = buttons.First(b => string.Equals(System.Windows.Automation.AutomationProperties.GetName(b), "录屏", StringComparison.Ordinal));

                // 同分组内切换：通用 -> 快捷键
                shortcutsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher(window, 180);

                // 跨分组切换：快捷键 -> 录屏
                recordingBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher(window, 220);

                if (!string.IsNullOrWhiteSpace(snapshotDir))
                {
                    CaptureWindow(window, Path.Combine(snapshotDir, "settings_recording.png"));
                }
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static void PumpDispatcher(Window window, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }

    private static void CaptureWindow(Window window, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
                yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static void EnsureThemeResources()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Resources.Contains("AppleSwitchStyle"))
        {
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/Screen;component/Themes/Tokens.xaml", UriKind.Relative)));
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/Screen;component/Themes/GraphiteControls.xaml", UriKind.Relative)));
        }
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, StoredCredential> _values = [];
        public string? Read(string targetName) => ReadCredential(targetName)?.Secret;
        public StoredCredential? ReadCredential(string targetName) => _values.GetValueOrDefault(targetName);
        public void Write(string targetName, string secret, string? endpointScope = null) => _values[targetName] = new StoredCredential(secret, endpointScope);
        public void Delete(string targetName) => _values.Remove(targetName);
    }
}
