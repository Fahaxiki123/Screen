using System.Net;
using System.Text;
using System.Text.Json;

namespace Screen.Core.Tests;

[TestClass]
public sealed class TranslationLanguageAndChunkRegressionTests
{
    [TestMethod]
    [DataRow("Synchronization", "同步")]
    [DataRow("Acknowledgements", "致谢")]
    public async Task ValidShortAiCompletionWinsDefaultFastRaceWithoutFallbackAsync(string source, string translation)
    {
        var hosts = new List<string>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            hosts.Add(request.RequestUri!.Host);
            return Task.FromResult(Sse(translation));
        }));
        var result = await new OnlineTranslationProvider(client).TranslateAsync(
            source, "en-US", NaturalProfile() with { EnableFastFallbackRace = true },
            new TranslationCredentials("offline-ai", "offline-id", "offline-secret"));

        Assert.AreEqual(translation, result.Text);
        Assert.IsFalse(result.UsedFallback);
        CollectionAssert.AreEqual(new[] { "ai.test" }, hosts);
    }

    [TestMethod]
    public async Task ValidShortCompletionAlsoWinsDualAiRaceAsync()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Sse("同步"))));
        var result = await new OnlineTranslationProvider(client).TranslateAsync(
            "Synchronization", "en-US", NaturalProfile() with
            {
                EnableRacing = true,
                RacingBaseAddress = "https://race.test/v1",
                RacingModel = "secondary"
            }, new TranslationCredentials("offline-ai", RacingApiKey: "offline-secondary"));
        Assert.AreEqual("同步", result.Text);
        Assert.IsFalse(result.UsedFallback);
    }

    [TestMethod]
    [DataRow("はい", "ja-JP")]
    [DataRow("あ", "ja-JP")]
    [DataRow("확인", "ko-KR")]
    [DataRow("한", "ko-KR")]
    [DataRow("你好", "zh-CN")]
    [DataRow("Hello world", "en-US")]
    [DataRow("Save changes", "en-US")]
    [DataRow("Open file in new tab", "en-US")]
    [DataRow("Save SAVE save", "und")]
    [DataRow("Ouvrir dans un nouvel onglet", "und")]
    [DataRow("Datei in einem neuen Tab öffnen", "und")]
    [DataRow("保存 changes", "und")]
    [DataRow("Bonjour le monde", "und")]
    [DataRow("Guten Morgen", "und")]
    [DataRow("Settings", "und")]
    [DataRow("你好 world", "und")]
    [DataRow("はい 확인", "und")]
    [DataRow("123 $4.50", "und")]
    public void SourceLanguageDetectionRetainsUncertainty(string text, string expected) =>
        Assert.AreEqual(expected, OcrQualityEvaluator.DetectLanguage(text));

    [TestMethod]
    public async Task LongEnglishParagraphUsesAutomaticMyMemorySourceAcrossChunksAsync()
    {
        var text = string.Join(" ", Enumerable.Repeat(
            "Please save your changes before you close the window and open the file again.", 8));
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            StringAssert.Contains(Uri.UnescapeDataString(request.RequestUri!.Query), "langpair=en|zh-CN");
            requests++;
            return Task.FromResult(MyMemory("请保存更改。"));
        }));
        await new OnlineTranslationProvider(client).TranslateAsync(text, OcrQualityEvaluator.DetectLanguage(text),
            new AppSettings().Translation with { Provider = TranslationProviderKind.MyMemory, TargetLanguage = "简体中文" },
            new TranslationCredentials());
        Assert.IsTrue(requests >= 2);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnknownSourceNeverSendsAnInvalidMyMemoryRequestAsync(bool fallback)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(MyMemory("unexpected"));
        }));
        var profile = new AppSettings().Translation with
        {
            Provider = fallback ? TranslationProviderKind.Tencent : TranslationProviderKind.MyMemory,
            TargetLanguage = "English"
        };
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            new OnlineTranslationProvider(client).TranslateAsync(
                "Bonjour le monde", "und", profile, new TranslationCredentials()));
        StringAssert.Contains(error.Message, "无法可靠判断源语言");
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public async Task UserSourceOverrideAllowsMyMemoryWithoutGuessingAsync()
    {
        string? query = null;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            query = Uri.UnescapeDataString(request.RequestUri!.Query);
            return Task.FromResult(MyMemory("Hello world"));
        }));
        var result = await new MyMemoryTranslationProvider(client).TranslateAsync(
            "Bonjour le monde", "und", new AppSettings().Translation with
            { SourceLanguage = "fr", TargetLanguage = "English" }, new TranslationCredentials());
        Assert.AreEqual("Hello world", result.Text);
        StringAssert.Contains(query, "langpair=fr|en");
    }

    [TestMethod]
    public async Task OnlyExplicitSameSourceReturnsOriginalWithoutNetworkAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(MyMemory("unexpected"));
        }));
        var provider = new MyMemoryTranslationProvider(client);
        var profile = new AppSettings().Translation with { TargetLanguage = "English" };
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            provider.TranslateAsync("Hello world", "en-US", profile, new TranslationCredentials()));
        StringAssert.Contains(error.Message, "手动确认源语言");

        var original = await provider.TranslateAsync("Hello world", "und",
            profile with { SourceLanguage = "en-US" }, new TranslationCredentials());
        Assert.AreEqual("Hello world", original.Text);
        StringAssert.Contains(original.ProviderName, "无需翻译");
        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public async Task TraditionalAndSimplifiedChineseRemainDifferentLanguageTargetsAsync()
    {
        string? query = null;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            query = Uri.UnescapeDataString(request.RequestUri!.Query);
            return Task.FromResult(MyMemory("设置"));
        }));
        var result = await new MyMemoryTranslationProvider(client).TranslateAsync("設定", "zh-CN",
            new AppSettings().Translation with { SourceLanguage = "zh-Hant", TargetLanguage = "简体中文" },
            new TranslationCredentials());
        Assert.AreEqual("设置", result.Text);
        StringAssert.Contains(query, "langpair=zh-TW|zh-CN");
    }

    [TestMethod]
    [DataRow("auto", "auto")]
    [DataRow("fr", "fr")]
    [DataRow("zh-CN", "zh")]
    public async Task TencentUsesItsOwnDetectionUnlessUserOverridesSourceAsync(string configured, string expected)
    {
        string? source = null;
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            source = body.RootElement.GetProperty("Source").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"Response\":{\"TargetText\":\"结果\"}}") };
        }));
        await new TencentTranslationProvider(client).TranslateAsync("Bonjour", "en-US",
            new AppSettings().Translation with { SourceLanguage = configured },
            new TranslationCredentials(TencentSecretId: "offline-id", TencentSecretKey: "offline-secret"));
        Assert.AreEqual(expected, source);
    }

    [TestMethod]
    public void AiPromptHonorsExplicitSourceAndKeepsAutomaticDetection()
    {
        StringAssert.Contains(OpenAiTranslationProvider.BuildSystemPrompt("English", "fr"), "source language: fr");
        StringAssert.Contains(OpenAiTranslationProvider.BuildSystemPrompt("English"), "Infer the source language");
    }

    [TestMethod]
    public async Task MyMemoryChunkedEnglishTranslationKeepsSentenceSpacingAndByteLimitAsync()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            var query = request.RequestUri!.Query;
            var encodedText = query[(query.IndexOf("q=", StringComparison.Ordinal) + 2)..query.IndexOf("&langpair", StringComparison.Ordinal)];
            Assert.IsTrue(Encoding.UTF8.GetByteCount(Uri.UnescapeDataString(encodedText)) <= 450);
            return Task.FromResult(MyMemory(++requests == 1 ? "This is the first part." : "This is the second part."));
        }));
        var result = await new MyMemoryTranslationProvider(client).TranslateAsync(new string('中', 180), "zh-CN",
            new AppSettings().Translation with { TargetLanguage = "English" }, new TranslationCredentials());
        Assert.AreEqual(2, requests);
        Assert.AreEqual("This is the first part. This is the second part.", result.Text);
    }

    [TestMethod]
    public void ChunkBoundariesPreserveParagraphsAndDoNotInventParagraphsInsideSentences()
    {
        var paragraphs = TranslationChunkLayout.ByCharacters("first\r\n\r\n  second", 12);
        Assert.HasCount(2, paragraphs);
        var output = new StringBuilder();
        TranslationChunkLayout.Append(output, paragraphs[0], "First.", "English");
        TranslationChunkLayout.Append(output, paragraphs[1], "Second.", "English");
        Assert.AreEqual("First.\r\n\r\n  Second.", output.ToString());

        var sentence = TranslationChunkLayout.ByCharacters("alpha beta gamma", 10);
        Assert.HasCount(2, sentence);
        output.Clear();
        TranslationChunkLayout.Append(output, sentence[0], "one", "English");
        TranslationChunkLayout.Append(output, sentence[1], "two", "English");
        Assert.AreEqual("one two", output.ToString());
        Assert.HasCount(1, TranslationChunkLayout.ByUtf8Bytes("one\n\ntwo\n\nthree", 450),
            "Short paragraphs must not multiply the number of HTTP requests.");
    }

    [TestMethod]
    [DataRow("English", "Hello", ",", "Hello,")]
    [DataRow("English", "(", "text)", "(text)")]
    [DataRow("English", "One,", "two", "One, two")]
    [DataRow("简体中文", "第一段", "第二段", "第一段第二段")]
    [DataRow("简体中文", "Visual", "Studio", "Visual Studio")]
    public void ChunkJoinUsesActualPunctuationAndWritingSystem(string target, string left, string right, string expected)
    {
        var output = new StringBuilder(left);
        TranslationChunkLayout.Append(output, new TranslationChunk("source", " ", TranslationChunkBoundary.Word), right, target);
        Assert.AreEqual(expected, output.ToString());
    }

    [TestMethod]
    public void ForcedSplitInsideAWordDoesNotInventASpace()
    {
        var chunks = TranslationChunkLayout.ByCharacters("abcdefghijkl", 6);
        var output = new StringBuilder();
        foreach (var chunk in chunks)
            TranslationChunkLayout.Append(output, chunk, chunk.Text, "English");
        Assert.AreEqual("abcdefghijkl", output.ToString());
    }

    [TestMethod]
    public void ProtectionMarkersRemainWholeAndGloballyMappedAcrossChunks()
    {
        var source = string.Join(' ', Enumerable.Range(0, 90).Select(index => $"https://example.test/item/{index}"));
        var protection = TranslationTextProtector.Protect(source);
        var chunks = TranslationChunkLayout.ByUtf8Bytes(protection.Text, 450);
        Assert.IsTrue(chunks.Count > 1);
        var output = new StringBuilder();
        foreach (var chunk in chunks)
        {
            Assert.IsTrue(Encoding.UTF8.GetByteCount(chunk.Text) <= 450);
            TranslationChunkLayout.Append(output, chunk, chunk.Text, "English", protection.Restore);
        }
        Assert.AreEqual(source, protection.Restore(output.ToString()));
    }

    private static TranslationProfile NaturalProfile() => new AppSettings().Translation with
    {
        Provider = TranslationProviderKind.OpenAiCompatible,
        BaseAddress = "https://ai.test/v1",
        Model = "offline",
        EnableFastFallbackRace = false,
        EnableMyMemoryFallback = false
    };

    private static HttpResponseMessage Sse(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: " + JsonSerializer.Serialize(new
        { choices = new[] { new { delta = new { content = text }, finish_reason = "stop" } } })
            + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
    };

    private static HttpResponseMessage MyMemory(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { responseStatus = 200, responseData = new { translatedText = text } }))
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
