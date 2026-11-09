using System.Windows;

namespace Screen.App;

public partial class App : Application
{
    private AppHost? _host;
    private SingleInstanceCoordinator? _singleInstance;
    private EventWaitHandle? _readyEvent;
    private readonly HashSet<string> _shownErrors = new();
    private DateTime _lastErrorTime = DateTime.MinValue;

    /// <summary>Live host for cross-window services (e.g. starting a recording from the capture overlay).</summary>
    internal AppHost? Host { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SimpleLogger.LogInfo(
            $"Screen started (version: {typeof(App).Assembly.GetName().Version}, " +
            $"pid: {Environment.ProcessId}, executable: {Environment.ProcessPath}, " +
            $"runtime: {Environment.Version}, runtime directory: {System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()})");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            SimpleLogger.LogError($"Unhandled process exception (terminating: {args.IsTerminating})", args.ExceptionObject as Exception);

        DispatcherUnhandledException += (_, args) =>
        {
            var errorKey = $"{args.Exception.GetType().Name}:{args.Exception.Message}";
            SimpleLogger.LogError("Unhandled dispatcher exception", args.Exception);

            // Deduplicate: show each unique error at most once per 60 seconds
            var now = DateTime.Now;
            if (_shownErrors.Contains(errorKey) && (now - _lastErrorTime).TotalSeconds < 60)
            {
                args.Handled = true;
                return;
            }

            _shownErrors.Add(errorKey);
            _lastErrorTime = now;

            MessageBox.Show(
                $"发生未处理的错误，程序将继续运行：{args.Exception.Message}\n\n详细日志已保存到 %LocalAppData%\\Screen\\logs\\",
                "Screen",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };
        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.TryBecomePrimary())
        {
            var isWindowsStartup = e.Args.Any(argument =>
                string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
            SingleInstanceCoordinator.NotifyPrimary(requestCapture: !isWindowsStartup);
            Shutdown();
            return;
        }

        try
        {
            _host = new AppHost();
            _host.Start();
            Host = _host;
            _singleInstance.StartListening(() => Dispatcher.BeginInvoke(_host.RequestCapture));
            // The updater must distinguish a ready tray app from a startup error dialog.
            _readyEvent = new EventWaitHandle(true, EventResetMode.ManualReset,
                $@"Local\Screen.NativeScreenshot.Ready.{Environment.ProcessId}");
            SimpleLogger.LogInfo("Screen ready");
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Screen startup failed", exception);
            // The tray icon is the app's only entry point; running on after a
            // failed startup would leave an invisible process without hotkeys
            // that the user can only kill from Task Manager. Fail fast instead.
            MessageBox.Show(
                $"Screen 启动失败，即将退出：{exception.Message}",
                "Screen",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
            return;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _readyEvent?.Dispose();
        _host?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
