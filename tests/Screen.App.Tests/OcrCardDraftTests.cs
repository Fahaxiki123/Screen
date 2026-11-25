using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
public sealed class OcrCardDraftTests
{
    [STATestMethod]
    [DataRow(false, "corrected text")]
    [DataRow(true, "")]
    public void FinishingOrClosingAnEditPreservesTheDraftAndImageCoordinates(bool close, string changedText)
    {
        var cache = new SelectionTextCache();
        var document = new OcrDocument("en-US", "raw OCR", [new OcrLine("raw OCR", new PixelRect(8, 4, 50, 10), [])]);
        cache.TryStoreImageOcr(cache.Revision, document, cloudSatisfied: false);
        using var card = CreateCard(cache);
        card.ShowCachedResult(cache.Revision, document.Text, document.LanguageTag, "local", document, false);
        Click(card, "编辑");
        Click(card, "完成编辑");
        Assert.IsFalse(Children<TextBlock>(card).Any(block => block.Text == "已保存到当前选区"));
        Assert.IsFalse(cache.TryGetCardSource(false, out _));
        Click(card, "编辑");
        Children<TextBox>(card).Single().Text = changedText;
        if (close)
            Children<Button>(card).Single(button => Equals(button.ToolTip, "关闭")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else
            Click(card, "完成编辑");

        Assert.IsTrue(Children<TextBlock>(card).Any(block => block.Text ==
            (changedText.Length == 0 ? "原文已清空" : "已保存到当前选区")));
        Assert.IsFalse(cache.TryStoreCardSource(cache.Revision, "automatic OCR", "en-US", "OCR", true));
        Assert.IsTrue(cache.TryGetCardSource(requireCloudEnhancement: true, out var draft));
        Assert.IsTrue(draft.IsEdited);
        Assert.AreEqual(changedText, draft.Text);
        Assert.IsTrue(cache.TryGetImageOcr(false, out var image));
        Assert.AreSame(document, image.Document);

        card.ShowCachedResult(cache.Revision, draft.Text, draft.LanguageTag, draft.ProviderName, null, false, isEdited: true);
        Assert.AreEqual(changedText, card.CurrentText);
        var translate = Children<Button>(card).Single(button => Equals(button.Content, "翻译"));
        Assert.AreEqual(changedText.Length != 0, translate.IsEnabled);
        if (translate.IsEnabled)
        {
            OcrTranslateRequest? request = null;
            card.TranslateRequested += (_, args) => request = args;
            translate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsNotNull(request);
            Assert.IsTrue(request.IsEdited);
            Assert.AreEqual(changedText, request.Text);
            Assert.IsNull(request.Document);
        }
    }

    [STATestMethod]
    public void HidingAnEditorAfterInvalidationCannotCommitToTheNewSelection()
    {
        var cache = new SelectionTextCache();
        using var card = CreateCard(cache);
        card.ShowCachedResult(cache.Revision, "original", "en-US", "OCR", null, true);
        Click(card, "编辑");
        Children<TextBox>(card).Single().Text = "private old edit";
        cache.Invalidate();
        card.Visibility = Visibility.Collapsed;
        Assert.IsFalse(cache.TryGetCardSource(false, out _));
        Assert.IsFalse(Children<TextBlock>(card).Any(block => block.Text == "已保存到当前选区"));

        card.ShowCachedResult(cache.Revision, "new selection", "en-US", "OCR", null, true);
        Assert.AreEqual("new selection", card.CurrentText);
        Assert.IsFalse(cache.TryGetCardSource(false, out _));
    }

    [STATestMethod]
    public void ToolbarReopensTheEditedDraftWithoutReextractingTheImage() => WithOverlay((root, ocr, _) =>
    {
        var card = Children<OcrCard>(root).Single();
        ClickTool(root, "提取文字");
        Click(card, "编辑");
        Children<TextBox>(card).Single().Text = "corrected via toolbar";
        Click(card, "完成编辑");
        ClickTool(root, "提取文字");
        Assert.AreEqual("corrected via toolbar", card.CurrentText);

        Click(card, "编辑");
        Children<TextBox>(card).Single().Clear();
        Children<Button>(card).Single(button => Equals(button.ToolTip, "关闭")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ClickTool(root, "提取文字");
        Assert.AreEqual(string.Empty, card.CurrentText);
        Assert.AreEqual(1, ocr.Calls);
    });

    [STATestMethod]
    [DataRow(false, "revised", false)]
    [DataRow(true, "another correction", true)]
    [DataRow(false, "", false)]
    [DataRow(true, "  ", false)]
    public void TranslationActionSavesEditedSourceBeforeValidationOrProviderFailure(
        bool changeLanguage, string draft, bool providerFails) => WithOverlay((root, ocr, provider) =>
    {
        ClickTool(root, "按当前语种翻译");
        Assert.AreEqual("raw OCR", provider.Inputs.Single());
        var translation = Children<TranslationCard>(root).Single();
        Click(translation, "原文");
        Children<TextBox>(translation).Single(box => !box.IsReadOnly).Text = draft;
        provider.Fail = providerFails;
        if (changeLanguage)
            translation.SelectTargetLanguage("日本語");
        else
            Click(translation, "重译");

        var blank = string.IsNullOrWhiteSpace(draft);
        Assert.AreEqual(blank ? 1 : 2, provider.Inputs.Count);
        if (!blank)
            Assert.AreEqual(draft, provider.Inputs[^1]);
        translation.Visibility = Visibility.Collapsed;
        ClickTool(root, "提取文字");
        Assert.AreEqual(draft, Children<OcrCard>(root).Single().CurrentText);
        provider.Fail = false;
        ClickTool(root, "按当前语种翻译");
        Assert.AreEqual(blank ? 1 : 3, provider.Inputs.Count);
        Assert.AreEqual(draft, translation.CurrentSourceText);
        Assert.AreEqual(1, ocr.Calls);
    });

    [STATestMethod]
    public void RestoringTheInitialTextAfterClearingItReplacesTheBlankDraft() => WithOverlay((root, _, provider) =>
    {
        ClickTool(root, "按当前语种翻译");
        var card = Children<TranslationCard>(root).Single();
        Click(card, "原文");
        var editor = Children<TextBox>(card).Single(box => !box.IsReadOnly);
        editor.Clear();
        Click(card, "重译");
        Assert.AreEqual(1, provider.Inputs.Count);
        editor.Text = "raw OCR";
        Click(card, "重译");
        Assert.AreEqual("raw OCR", provider.Inputs[1]);
        ClickTool(root, "提取文字");
        Assert.AreEqual("raw OCR", Children<OcrCard>(root).Single().CurrentText);
    });

    [STATestMethod]
    public void ReadOnlyRetryAndStaleEditsLeaveTheCurrentSelectionSourceAlone() => WithOverlay((root, ocr, provider) =>
    {
        ClickTool(root, "按当前语种翻译");
        var card = Children<TranslationCard>(root).Single();
        // In-place returns its own paragraph ordering for comparison.
        card.ShowCachedTranslation(card.TargetLanguage, "en-US", "regional order", "translated", "stub",
            sourceRevision: card.SourceRevision);
        Click(card, "重译");
        Assert.AreEqual("regional order", provider.Inputs[^1]);
        ClickTool(root, "提取文字");
        Assert.AreEqual("raw OCR", Children<OcrCard>(root).Single().CurrentText);

        ClickTool(root, "按当前语种翻译");
        Click(card, "原文");
        Children<TextBox>(card).Single(box => !box.IsReadOnly).Text = "old private draft";
        Children<CaptureSurface>(root).Single().InitializeCommittedSelection(new PixelRect(3, 4, 120, 70));
        card.SelectTargetLanguage("日本語");
        Assert.AreEqual("raw OCR", provider.Inputs[^1]);
        Assert.AreEqual(2, ocr.Calls);
    });

    private static void WithOverlay(Action<FrameworkElement, TestOcrEngine, TestTranslationProvider> verify)
    {
        EnsureResources();
        var directory = Directory.CreateTempSubdirectory("flick-ocr-draft-");
        try
        {
            var store = new JsonSettingsStore(Path.Combine(directory.FullName, "settings.json"));
            store.Update(settings => settings.Ocr = new OcrProfile { Mode = OcrProviderMode.LocalOnly });
            var bounds = new PixelRect(0, 0, 160, 80);
            var frame = new CaptureFrame(new byte[160 * 80 * 4], 160, 80, 640, 96, 96, bounds,
                CaptureColorSpace.Srgb, DateTimeOffset.UtcNow);
            using var capture = new DxgiScreenCaptureService();
            var ocr = new TestOcrEngine();
            var provider = new TestTranslationProvider();
            var window = new CaptureOverlayWindow(frame, capture, new WindowSnapService(), new PinWindowManager(),
                ocr, provider, new EmptyCredentials(), store);
            try
            {
                var root = (FrameworkElement)window.Content;
                Children<CaptureSurface>(root).Single().InitializeCommittedSelection(bounds);
                verify(root, ocr, provider);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static OcrCard CreateCard(SelectionTextCache cache)
    {
        EnsureResources();
        var card = new OcrCard(new TestOcrEngine(), () => throw new InvalidOperationException("Unexpected image capture."),
            revisionProvider: () => cache.Revision);
        card.EditCommitted += (revision, text, direct) =>
            cache.TryStoreCardSource(revision, text, "en-US", "已编辑文本", false, isEdited: true, isDirectText: direct);
        return card;
    }

    private static void EnsureResources()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (!app.Resources.Contains("Brush.TextSecondary"))
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/Screen;component/Themes/Tokens.xaml", UriKind.Relative)));
        if (!app.Resources.Contains("DarkScrollBarStyle"))
            app.Resources["DarkScrollBarStyle"] = new Style(typeof(ScrollBar));
        foreach (var key in new[] { "ToolbarIconButtonDarkStyle", "CardPrimaryButtonStyle" })
            if (!app.Resources.Contains(key))
                app.Resources[key] = new Style(typeof(Button));
    }

    private static void Click(FrameworkElement card, string label) =>
        Children<Button>(card).Single(button => Equals(button.Content, label)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void ClickTool(FrameworkElement root, string label) =>
        Children<Button>(root).Single(button => AutomationProperties.GetName(button) == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T control)
                yield return control;
            foreach (var descendant in Children<T>(child))
                yield return descendant;
        }
    }

    private sealed class TestTranslationProvider : ITranslationProvider
    {
        public List<string> Inputs { get; } = [];
        public bool Fail { get; set; }

        public Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, TranslationProfile profile,
            TranslationCredentials credentials, IProgress<string>? streamingText = null, CancellationToken cancellationToken = default)
        {
            Inputs.Add(text);
            return Fail ? Task.FromException<TranslationResult>(new InvalidOperationException("Synthetic translation failure."))
                : Task.FromResult(new TranslationResult("translated", "test"));
        }
    }

    private sealed class EmptyCredentials : ICredentialStore
    {
        public StoredCredential? ReadCredential(string targetName) => null;
        public void Write(string targetName, string secret, string? endpointScope = null) => throw new NotSupportedException();
        public void Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class TestOcrEngine : IOcrEngine
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> AvailableLanguages => ["auto"];
        public bool IsLanguageAvailable(string languageTag) => true;
        public Task<OcrDocument> RecognizeAsync(PixelBuffer image, string languageTag, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new OcrDocument("en-US", "raw OCR", [new OcrLine("raw OCR", new PixelRect(8, 4, 50, 10), [])]));
        }
    }
}
