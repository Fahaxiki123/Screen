using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Screen.App;
using Screen.Core;

namespace Screen.App.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingsConnectionTestTests
{
    [STATestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ObsoleteCompletionCannotChangeStatusOrUnlockAnotherTest(bool oldFails, bool newFinishesFirst)
    {
        using var ui = new TestWindow();
        var old = ui.Start();
        ui.Model.TranslationProviderIndex = 1;
        Assert.IsTrue(old.Token.IsCancellationRequested);
        Assert.AreEqual("未测试", ui.Status.Text);
        Assert.IsTrue(ui.Button.IsEnabled);
        var current = ui.Start();

        if (newFinishesFirst)
        {
            current.Succeed("新服务");
            ui.Drain();
        }
        if (oldFails)
            old.Reply.SetException(new HttpRequestException("旧配置错误"));
        else
            old.Succeed("旧服务");
        ui.Drain();

        Assert.AreEqual(newFinishesFirst ? "连接成功：新服务" : "正在连接…", ui.Status.Text);
        Assert.AreEqual(newFinishesFirst, ui.Button.IsEnabled);
        if (!newFinishesFirst)
        {
            current.Succeed("新服务");
            ui.Drain();
            Assert.AreEqual("连接成功：新服务", ui.Status.Text);
        }
    }

    [STATestMethod]
    public void RelevantServiceAndFallbackFieldsInvalidateCompletedResults()
    {
        using var ui = new TestWindow();
        var vm = ui.Model;
        vm.TranslationProviderIndex = 1;
        vm.ApiKey = "fake-primary";
        vm.TencentSecretId = "fake-id";
        vm.TencentSecretKey = "fake-key";
        vm.EnableRacing = true;
        vm.RacingBaseAddress = "https://race.example.test/v1";
        vm.RacingModel = "race";
        vm.RacingApiKey = "fake-race";
        var changes = new Action[]
        {
            () => vm.Model = "new-model",
            () => vm.ApiKey = "new-fake-key",
            () => vm.TargetLanguageIndex = 2,
            () => vm.RacingModel = "new-race",
            () => vm.RacingApiKey = "new-fake-race",
            () => vm.FastFallbackDelayText = "4",
            () => vm.TencentRegionIndex = 1,
            () => vm.TencentSecretKey = "other-fake-key",
            () => vm.MyMemoryContactEmail = "fixture@example.test",
            () => vm.EnableMyMemoryFallback = false,
            () => vm.EnableRacing = false,
            () => vm.EnableFastFallbackRace = false,
            () => vm.AiPresetIndex = 2,
            () => vm.BaseAddress = "https://new.example.test/v1"
        };
        foreach (var change in changes)
        {
            vm.BindDraftKey(false);
            ui.Start().Succeed("当前服务");
            ui.Drain();
            Assert.AreEqual("连接成功：当前服务", ui.Status.Text);
            change();
            Assert.AreEqual("未测试", ui.Status.Text);
        }
    }

    [STATestMethod]
    public void BindingTheDraftClearsThePreviousValidationError()
    {
        using var ui = new TestWindow();
        ui.Model.TranslationProviderIndex = 1;
        ui.Model.ApiKey = "fake";
        ui.Model.BaseAddress = "https://test.example.test/v1";
        ui.Click();
        Assert.IsNull(ui.Provider.Last);
        StringAssert.Contains(ui.Status.Text, "绑定");
        ui.Model.BindDraftKey(false);
        Assert.AreEqual("未测试", ui.Status.Text);
        Assert.IsTrue(ui.Button.IsEnabled);
    }

    [STATestMethod]
    public void IrrelevantSettingsDoNotInvalidateASuccessOrCancelAnActiveRequest()
    {
        using var ui = new TestWindow();
        var vm = ui.Model;
        vm.EnableMyMemoryFallback = false;
        var pending = ui.Start();
        vm.OcrModeIndex = 2;
        vm.UseCloudForTranslation = !vm.UseCloudForTranslation;
        vm.TranslationDisplayModeIndex = 1;
        vm.Model = "inactive-ai-model";
        vm.MyMemoryContactEmail = "unused@example.test";
        vm.TencentSecretKey = "unused-test-key";
        Assert.IsFalse(pending.Token.IsCancellationRequested);
        Assert.IsFalse(ui.Button.IsEnabled);
        pending.Succeed("免费服务");
        ui.Drain();
        vm.RecordingClickEffects = !vm.RecordingClickEffects;
        vm.RacingModel = "inactive-alternate-model";
        Assert.AreEqual("连接成功：免费服务", ui.Status.Text);
    }

    [STATestMethod]
    public void DormantTencentAndUnchangedEffectiveDelayKeepAiTestValid()
    {
        using var ui = new TestWindow();
        var vm = ui.Model;
        vm.TranslationProviderIndex = 1;
        vm.EnableFastFallbackRace = true;
        var request = ui.Start();
        vm.TencentRegionIndex = 1;
        vm.TencentSecretId = "test-id-only";
        vm.FastFallbackDelayText = "4";
        Assert.IsFalse(request.Token.IsCancellationRequested);
        vm.TencentSecretKey = "completes-the-pair";
        Assert.IsTrue(request.Token.IsCancellationRequested);
        request.Succeed("旧请求");
        ui.Drain();
        ui.Start().Succeed("当前服务");
        ui.Drain();
        vm.FastFallbackDelayText = "invalid";
        Assert.AreEqual("连接成功：当前服务", ui.Status.Text);
    }

    [STATestMethod]
    public void ClosingDetachesAnOutstandingRequest()
    {
        using var ui = new TestWindow();
        var pending = ui.Start();
        ui.Window.Close();
        Assert.IsTrue(pending.Token.IsCancellationRequested);
        pending.Reply.SetException(new HttpRequestException("旧请求"));
        ui.Drain();
        Assert.AreEqual("未测试", ui.Status.Text);
    }

    private sealed class TestWindow : IDisposable
    {
        private readonly SynchronizationContext? _previous = SynchronizationContext.Current;
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("flick-settings-test-");
        private bool _closed;

        public SettingsWindow Window { get; }
        public SettingsViewModel Model => (SettingsViewModel)Window.DataContext;
        public PendingProvider Provider { get; } = new();
        public Button Button { get; }
        public TextBlock Status { get; }

        public TestWindow()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (!app.Resources.Contains("AppleSwitchStyle"))
            {
                foreach (var theme in new[] { "Tokens", "GraphiteControls" })
                {
                    app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                        new Uri($"/Screen;component/Themes/{theme}.xaml", UriKind.Relative)));
                }
            }
            Window = new SettingsWindow(new JsonSettingsStore(Path.Combine(_directory.FullName, "settings.json")),
                new MemoryCredentials(), _ => { }, translationTestProvider: Provider);
            Window.Closed += (_, _) => _closed = true;
            var method = typeof(SettingsWindow).GetMethod("CreateTranslationTestControl",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var panel = (StackPanel)method.Invoke(Window, null)!;
            Button = (Button)panel.Children[0];
            Status = (TextBlock)panel.Children[1];
        }

        public void Click() => Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        public PendingRequest Start()
        {
            var previous = Provider.Last;
            Click();
            Assert.IsNotNull(Provider.Last);
            Assert.AreNotSame(previous, Provider.Last);
            return Provider.Last;
        }

        public void Drain() => Window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

        public void Dispose()
        {
            if (!_closed)
                Window.Close();
            SynchronizationContext.SetSynchronizationContext(_previous);
            _directory.Delete(recursive: true);
        }
    }

    private sealed class PendingRequest(CancellationToken token)
    {
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<TranslationResult> Reply { get; } = new();

        public void Succeed(string provider) => Reply.SetResult(new TranslationResult("示例", provider));
    }

    private sealed class PendingProvider : ITranslationProvider
    {
        public PendingRequest? Last { get; private set; }

        public Task<TranslationResult> TranslateAsync(string text, string sourceLanguage, TranslationProfile profile,
            TranslationCredentials credentials, IProgress<string>? streamingText = null,
            CancellationToken cancellationToken = default)
        {
            // Deliberately finish even after cancellation to exercise late successes and errors.
            Last = new PendingRequest(cancellationToken);
            return Last.Reply.Task;
        }
    }

    private sealed class MemoryCredentials : ICredentialStore
    {
        private readonly Dictionary<string, StoredCredential> _keys = [];
        public StoredCredential? ReadCredential(string targetName) => _keys.GetValueOrDefault(targetName);
        public void Write(string targetName, string secret, string? endpointScope = null) =>
            _keys[targetName] = new StoredCredential(secret, endpointScope);
        public void Delete(string targetName) => _keys.Remove(targetName);
    }
}
