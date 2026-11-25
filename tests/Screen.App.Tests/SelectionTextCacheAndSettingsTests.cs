using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

/// <summary>
/// 验证选区版本化缓存、OCR 纠错直译、卡片与原位联动及合并后「识别与翻译」设置页的行为与安全边界。
/// </summary>
[TestClass]
public sealed class SelectionTextCacheAndSettingsTests
{
    [TestMethod]
    public void SelectionTextCacheRejectsStaleAsyncOcrAfterMaskOrAnnotationEdit()
    {
        var cache = new SelectionTextCache();
        var capturedRevision = cache.Revision;
        var sensitiveDoc = new OcrDocument(
            "en-US",
            "SECRET-TOKEN-12345",
            [new OcrLine("SECRET-TOKEN-12345", new PixelRect(0, 0, 120, 20), [], 0.99)]);

        // 模拟异步 OCR 正在运行期间，用户对选区打了马赛克或修改了标注。
        var nextRevision = cache.Invalidate();
        Assert.AreEqual(capturedRevision + 1, nextRevision);

        // 在途旧结果返回后，必须被版本号拦截丢弃，不能污染缓存。
        Assert.IsFalse(cache.TryStoreCardSource(capturedRevision, sensitiveDoc.Text, sensitiveDoc.LanguageTag, "PP-OCRv6 本地", cloudSatisfied: true));
        Assert.IsFalse(cache.TryStoreImageOcr(capturedRevision, sensitiveDoc, cloudSatisfied: true));
        Assert.IsFalse(cache.TryGetCardSource(requireCloudEnhancement: false, out _));
        Assert.IsFalse(cache.TryGetImageOcr(requireCloudEnhancement: false, out _));

        // 新版本识别结果可正常写入并在同选区内复用，再次改动后立即清空。
        var cleanDoc = new OcrDocument("en-US", "PUBLIC", [new OcrLine("PUBLIC", new PixelRect(0, 0, 60, 20), [], 0.99)]);
        Assert.IsTrue(cache.TryStoreImageOcr(nextRevision, cleanDoc, cloudSatisfied: true));
        Assert.IsTrue(cache.TryGetImageOcr(requireCloudEnhancement: false, out var cached));
        Assert.AreEqual("PUBLIC", cached.Document.Text);

        cache.Invalidate();
        Assert.IsFalse(cache.TryGetImageOcr(requireCloudEnhancement: false, out _));
    }

    [TestMethod]
    public void SelectionTextCacheNeverCachesEmptyImageOcrAndPreservesEditedCardText()
    {
        var cache = new SelectionTextCache();
        var rev = cache.Revision;

        // 首次识别为空时绝不写入图像缓存，重开 OCR 卡可再次识别。
        var emptyDoc = new OcrDocument("en-US", string.Empty, []);
        Assert.IsFalse(cache.TryStoreImageOcr(rev, emptyDoc, cloudSatisfied: true));
        Assert.IsFalse(cache.TryGetImageOcr(requireCloudEnhancement: false, out _));

        // 存入正常图像 OCR 后再存入手工纠错的卡片原文，卡片缓存保留纠错稿且不影响底层带坐标图像 OCR。
        var rawDoc = new OcrDocument("en-US", "He11o", [new OcrLine("He11o", new PixelRect(0, 0, 50, 20), [], 0.9)]);
        Assert.IsTrue(cache.TryStoreImageOcr(rev, rawDoc, cloudSatisfied: true));
        Assert.IsTrue(cache.TryStoreCardSource(rev, "Hello", "en-US", "已编辑文本", cloudSatisfied: true, isEdited: true, isDirectText: false));

        Assert.IsTrue(cache.TryGetCardSource(requireCloudEnhancement: false, out var cardSource));
        Assert.AreEqual("Hello", cardSource.Text);
        Assert.IsTrue(cardSource.IsEdited);
        Assert.IsTrue(cache.TryGetImageOcr(requireCloudEnhancement: false, out var imageOcr));
        Assert.AreEqual("He11o", imageOcr.Document.Text);
    }

    [TestMethod]
    public void SelectionTextCacheRespectsCloudTranslationPolicy()
    {
        var cache = new SelectionTextCache();
        var rev = cache.Revision;
        var localOnlyDoc = new OcrDocument("en-US", "Hello", [new OcrLine("Hello", new PixelRect(0, 0, 50, 20), [], 0.95)])
        {
            UsedOnlineService = false
        };
        cache.TryStoreImageOcr(rev, localOnlyDoc, cloudSatisfied: false);

        // 需要云端翻译增强时，未满足云增强的本地缓存不得被复用。
        Assert.IsFalse(cache.TryGetImageOcr(requireCloudEnhancement: true, out _));
        Assert.IsTrue(cache.TryGetImageOcr(requireCloudEnhancement: false, out _));

        var credentials = new MemoryCredentialStore();
        var settings = new AppSettings { Ocr = new OcrProfile { Mode = OcrProviderMode.Smart, UseCloudForTranslation = true } };
        Assert.IsFalse(SelectionTextCache.RequiresCloudTranslationOcr(settings, credentials));

        credentials.Write(OcrProfile.TencentSecretIdCredentialName, "id");
        credentials.Write(OcrProfile.TencentSecretKeyCredentialName, "key");
        Assert.IsTrue(SelectionTextCache.RequiresCloudTranslationOcr(settings, credentials));

        settings.Ocr = settings.Ocr with { Mode = OcrProviderMode.LocalOnly };
        Assert.IsFalse(SelectionTextCache.RequiresCloudTranslationOcr(settings, credentials));
    }

    [TestMethod]
    public void SelectionTextCacheReusesInPlaceSnapshotByLanguageAndInvalidatesOnChange()
    {
        var cache = new SelectionTextCache();
        var rev = cache.Revision;
        var rendered = new PixelBuffer(new byte[16 * 16 * 4], 16, 16, 16 * 4);
        var snapshot = new CachedInPlaceTranslation(
            rev, "简体中文", "en-US", "Hello", "你好", "免费在线翻译", rendered, false, "原位覆盖完成");

        Assert.IsTrue(cache.TryStoreInPlaceTranslation(rev, snapshot));
        Assert.IsTrue(cache.TryGetInPlaceTranslation("简体中文", out var hit));
        Assert.AreEqual("你好", hit.CombinedText);
        Assert.IsFalse(cache.TryGetInPlaceTranslation("日本語", out _));

        cache.Invalidate();
        Assert.IsFalse(cache.TryGetInPlaceTranslation("简体中文", out _));
    }

    [STATestMethod]
    public async Task OcrCardTranslateButtonEmitsEditedTextWithoutReRunningOcrAsync()
    {
        EnsureThemeResources();
        var engine = new CountingOcrEngine(new OcrDocument(
            "en-US",
            "He11o wor1d",
            [new OcrLine("He11o wor1d", new PixelRect(0, 0, 100, 20), [], 0.92)])
        {
            ProviderName = "PP-OCRv6 本地"
        });
        var pixel = new PixelBuffer(new byte[16 * 16 * 4], 16, 16, 16 * 4);
        using var card = new OcrCard(engine, () => pixel);

        await card.RecognizeAsync();
        Assert.AreEqual(1, engine.Calls);
        Assert.AreEqual("He11o wor1d", card.CurrentText);

        var editButton = FindVisualChildren<Button>(card).First(b => Equals(b.Content, "编辑"));
        var translateButton = FindVisualChildren<Button>(card).First(b => Equals(b.Content, "翻译"));
        var textBox = FindVisualChildren<TextBox>(card).First();

        editButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        textBox.Text = "Hello world";

        OcrTranslateRequest? request = null;
        card.TranslateRequested += (_, args) => request = args;
        translateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.IsNotNull(request);
        Assert.AreEqual("Hello world", request.Text);
        Assert.IsTrue(request.IsEdited);
        Assert.AreEqual(1, engine.Calls, "点击翻译不得再次触发 OCR。");
    }

    [STATestMethod]
    public void TranslationCardSupportsCachedDisplaySourceEditAndInPlaceSwitch()
    {
        EnsureThemeResources();
        var card = new TranslationCard();
        card.ShowCachedTranslation("简体中文", "en-US", "Original line", "已有译文", "免费在线翻译");

        Assert.AreEqual("Original line", card.CurrentSourceText);
        Assert.IsFalse(card.IsSourceEdited);

        var sourceToggle = FindVisualChildren<Button>(card).First(b => Equals(b.Content, "原文"));
        var inPlaceButton = FindVisualChildren<Button>(card).First(b => Equals(b.Content, "原位"));

        sourceToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var sourceBox = FindVisualChildren<TextBox>(card).First(box => !box.IsReadOnly);
        sourceBox.Text = "Edited source line";

        Assert.IsTrue(card.IsSourceEdited);
        Assert.AreEqual("Edited source line", card.CurrentSourceText);

        var switched = false;
        card.SwitchToInPlaceRequested += (_, _) => switched = true;
        inPlaceButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsTrue(switched);

        // 清空原文后 HasSourceLoaded 仍为 true，防止点击重译时偷偷恢复已清空的旧原文。
        sourceBox.Text = "   ";
        Assert.IsTrue(card.HasSourceLoaded);
        Assert.IsTrue(string.IsNullOrWhiteSpace(card.CurrentSourceText));
    }

    [TestMethod]
    public void SafeTranslationLogsNeverLeakEchoedScreenshotText()
    {
        const string sensitive = "TOP_SECRET_BANK_ACCOUNT_998877";
        var echoedException = new System.Net.Http.HttpRequestException(
            $"HTTP 400 Bad Request：Rejected input: {sensitive}",
            null,
            System.Net.HttpStatusCode.BadRequest);

        var errorLog = CaptureOverlayWindow.FormatSafeTranslationErrorLog(
            TranslationProviderKind.OpenAiCompatible,
            echoedException);
        var fallbackLog = CaptureOverlayWindow.FormatSafeFallbackLog(
            TranslationProviderKind.OpenAiCompatible,
            TranslationProviderKind.Edge);

        Assert.IsFalse(errorLog.Contains(sensitive, StringComparison.Ordinal));
        StringAssert.Contains(errorLog, "HTTP 400");
        Assert.IsFalse(fallbackLog.Contains(sensitive, StringComparison.Ordinal));
        StringAssert.Contains(fallbackLog, "OpenAiCompatible");
    }

    [STATestMethod]
    public void OcrCardShowCachedResultReusesTextWithoutCallingEngine()
    {
        EnsureThemeResources();
        var engine = new CountingOcrEngine(new OcrDocument("en-US", "ShouldNotRun", []));
        var pixel = new PixelBuffer(new byte[16 * 16 * 4], 16, 16, 16 * 4);
        using var card = new OcrCard(engine, () => pixel);

        card.ShowCachedResult(
            revision: 3,
            formattedText: "Cached OCR line",
            languageTag: "en-US",
            providerName: "PP-OCRv6 本地",
            imageDocument: null,
            isDirectText: false);

        Assert.AreEqual(0, engine.Calls);
        Assert.AreEqual("Cached OCR line", card.CurrentText);
        var translateButton = FindVisualChildren<Button>(card).First(b => Equals(b.Content, "翻译"));
        Assert.IsTrue(translateButton.IsEnabled);
    }

    [STATestMethod]
    public void SettingsViewModelAiPresetTriggersEndpointKeyIsolation()
    {
        var tempDir = Directory.CreateTempSubdirectory("flick-preset-test-");
        try
        {
            var store = new JsonSettingsStore(Path.Combine(tempDir.FullName, "settings.json"));
            var credentials = new MemoryCredentialStore();
            using var vm = new SettingsViewModel(store, credentials, _ => { });
            vm.TranslationProviderIndex = 1; // AI 自然翻译
            vm.AiPresetIndex = 5; // OpenAI
            vm.ApiKey = "openai-secret-key";
            vm.SaveNow();
            Assert.AreEqual("openai-secret-key", vm.BuildTranslationCredentials().ApiKey);

            // 切换到智谱预设，必须触发域名隔离，绝不把 OpenAI 密钥直接发往智谱地址。
            vm.AiPresetIndex = 1;
            Assert.AreEqual("https://open.bigmodel.cn/api/paas/v4", vm.BaseAddress);
            Assert.AreEqual("glm-4.7-flash", vm.Model);
            Assert.IsTrue(vm.CanBindApiKey);
            Assert.IsNull(vm.BuildTranslationCredentials().ApiKey);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [STATestMethod]
    public void SettingsWindowMergesOcrAndTranslationWithCollapsibleAdvancedSection()
    {
        var tempDir = Directory.CreateTempSubdirectory("flick-merged-settings-");
        try
        {
            EnsureThemeResources();
            var store = new JsonSettingsStore(Path.Combine(tempDir.FullName, "settings.json"));
            var credentials = new MemoryCredentialStore();
            var window = new SettingsWindow(store, credentials, _ => { })
            {
                Left = -30000,
                Top = -30000,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            try
            {
                window.Show();
                PumpDispatcher(window, 120);

                var navNames = FindVisualChildren<Button>(window)
                    .Select(AutomationProperties.GetName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
                CollectionAssert.Contains(navNames, "识别与翻译");
                CollectionAssert.DoesNotContain(navNames, "文字识别");
                CollectionAssert.DoesNotContain(navNames, "翻译接口");

                var navButton = FindVisualChildren<Button>(window)
                    .First(b => string.Equals(AutomationProperties.GetName(b), "识别与翻译", StringComparison.Ordinal));
                navButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher(window, 150);

                var providers = FindVisualChildren<ComboBox>(window)
                    .Single(c => AutomationProperties.GetName(c) == "翻译服务");
                CollectionAssert.AreEqual(new[] { "免费在线翻译（推荐 · 免配置）", "AI 自然翻译（自配 API Key）" },
                    providers.Items.Cast<string>().ToArray());
                Assert.AreEqual(0, providers.SelectedIndex);
                providers.SelectedIndex = 1;
                Assert.AreEqual(TranslationProviderKind.OpenAiCompatible,
                    ((SettingsViewModel)window.DataContext).BuildTranslationProfile().Provider);

                var advancedToggle = FindVisualChildren<Button>(window)
                    .First(b => string.Equals(AutomationProperties.GetName(b), "高级选项", StringComparison.Ordinal));
                var ocrComboBefore = FindVisualChildren<ComboBox>(window)
                    .First(c => string.Equals(AutomationProperties.GetName(c), "识别方式", StringComparison.Ordinal));
                Assert.IsFalse(ocrComboBefore.IsVisible, "高级选项默认应处于折叠状态。");

                advancedToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpDispatcher(window, 120);
                Assert.IsTrue(ocrComboBefore.IsVisible, "点击高级选项后应展开文字识别与回退配置。");
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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }
            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void EnsureThemeResources()
    {
        // Pumping the dispatcher must not run the real tray app's single-instance startup.
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Resources.Contains("AppleSwitchStyle"))
        {
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/Screen;component/Themes/Tokens.xaml", UriKind.Relative)));
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/Screen;component/Themes/GraphiteControls.xaml", UriKind.Relative)));
        }
        if (!app.Resources.Contains("ThemeFocusVisualStyle"))
        {
            app.Resources["ThemeFocusVisualStyle"] = new Style(typeof(Control));
        }
        if (!app.Resources.Contains("DarkScrollBarStyle"))
        {
            app.Resources["DarkScrollBarStyle"] = new Style(typeof(System.Windows.Controls.Primitives.ScrollBar));
        }
        if (!app.Resources.Contains("CardPrimaryButtonStyle"))
        {
            app.Resources["CardPrimaryButtonStyle"] = new Style(typeof(Button));
        }
        if (!app.Resources.Contains("CardSecondaryButtonStyle"))
        {
            app.Resources["CardSecondaryButtonStyle"] = new Style(typeof(Button));
        }
        if (!app.Resources.Contains("CardIconButtonStyle"))
        {
            app.Resources["CardIconButtonStyle"] = new Style(typeof(Button));
        }
    }

    private sealed class CountingOcrEngine(OcrDocument result) : IOcrEngine
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> AvailableLanguages { get; } = ["auto"];
        public bool IsLanguageAvailable(string languageTag) => true;
        public Task<OcrDocument> RecognizeAsync(PixelBuffer image, string languageTag, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class MemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, StoredCredential> _values = [];
        public StoredCredential? ReadCredential(string targetName) => _values.GetValueOrDefault(targetName);
        public void Write(string targetName, string secret, string? endpointScope = null) => _values[targetName] = new StoredCredential(secret, endpointScope);
        public void Delete(string targetName) => _values.Remove(targetName);
    }
}
