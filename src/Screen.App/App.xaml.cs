using System.Windows;
using Forms = System.Windows.Forms;

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
        if (AssemblyPreloadWorker.TryRunFromArgs(e.Args))
        {
            Environment.Exit(0);
            return;
        }

        base.OnStartup(e);
        SimpleLogger.LogInfo(
            $"Screen started (version: {typeof(App).Assembly.GetName().Version}, " +
            $"pid: {Environment.ProcessId}, executable: {Environment.ProcessPath}, " +
            $"runtime: {Environment.Version}, runtime directory: {System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()})");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            SimpleLogger.LogError($"Unhandled process exception (terminating: {args.IsTerminating})", args.ExceptionObject as Exception);

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            ReportDesktopException("Unhandled dispatcher exception", args.Exception);
        };

        try
        {
            _singleInstance = new SingleInstanceCoordinator();
            if (!_singleInstance.TryBecomePrimary())
            {
                var isWindowsStartup = e.Args.Any(argument =>
                    string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
                SingleInstanceCoordinator.NotifyPrimary(requestCapture: !isWindowsStartup);
                Shutdown();
                return;
            }

            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.CatchException, threadScope: true);
            Forms.Application.ThreadException += OnTrayThreadException;
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
            try
            {
                MessageBox.Show(
                    $"Flick 启动失败，即将退出：{exception.Message}",
                    "Flick",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Shutdown(-1);
            }
        }
    }

    private void OnTrayThreadException(object sender, ThreadExceptionEventArgs e) =>
        ReportDesktopException("Unhandled tray window exception", e.Exception);

    private void ReportDesktopException(string source, Exception exception)
    {
        SimpleLogger.LogError(source, exception);
        var errorKey = $"{exception.GetType().Name}:{exception.Message}";
        var now = DateTime.Now;
        if (_shownErrors.Contains(errorKey) && (now - _lastErrorTime).TotalSeconds < 60)
        {
            return;
        }
        _shownErrors.Add(errorKey);
        _lastErrorTime = now;
        MessageBox.Show(
            $"发生未处理的错误，程序将继续运行：{exception.Message}\n\n详细日志已保存到 %LocalAppData%\\Screen\\logs\\",
            "Flick",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Forms.Application.ThreadException -= OnTrayThreadException;
        try
        {
            _readyEvent?.Dispose();
            _host?.Dispose();
        }
        finally
        {
            try
            {
                _singleInstance?.Dispose();
            }
            finally
            {
                base.OnExit(e);
            }
        }
    }
}
