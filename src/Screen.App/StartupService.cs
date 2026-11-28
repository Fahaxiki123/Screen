using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Screen.App;

internal sealed class StartupService
{
    internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string StartupApprovedRunPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string ValueName = "Screen";
    internal const string TaskNamePrefix = "Flick-AutoStart";
    internal const string StartupArgument = "--startup";

    private readonly IStartupBackend _backend;
    private readonly Func<string?> _getExecutablePath;
    private readonly Lock _sync = new();

    public StartupService()
        : this(new WindowsStartupBackend(), () => Environment.ProcessPath)
    {
    }

    internal StartupService(IStartupBackend backend, Func<string?> getExecutablePath)
    {
        _backend = backend;
        _getExecutablePath = getExecutablePath;
    }

    public void SetEnabled(bool enabled)
    {
        lock (_sync)
        {
            SetEnabledCore(enabled);
        }
    }

    private void SetEnabledCore(bool enabled)
    {
        if (!enabled)
        {
            TryDeleteScheduledTask();
            _backend.DeleteRegistryRunValue();
            return;
        }

        var executable = _getExecutablePath()
                         ?? throw new InvalidOperationException("Cannot determine the executable path.");
        try
        {
            _backend.RegisterLogonTask(executable, StartupArgument);
            // Remove the legacy HKCU\Run entry once the parallel logon task is active so
            // Windows Explorer does not launch a second redundant --startup process later.
            _backend.DeleteRegistryRunValueIfPointsToScreen();
            SimpleLogger.LogInfo($"Startup enabled via Task Scheduler logon task ({executable}).");
        }
        catch (Exception exception)
        {
            SimpleLogger.LogWarning($"Task Scheduler logon task registration failed, falling back to Run key: {exception.Message}");
            _backend.SetRegistryRunValue(FormatRunCommand(executable));
        }
    }

    /// <summary>
    /// Upgrades an existing HKCU\Run startup registration to a parallel logon scheduled task
    /// (or refreshes a stale task path after moving/reinstalling) without overriding an
    /// explicit disable made by the user in Windows Task Manager.
    /// </summary>
    public void SyncWithSettings(bool startWithWindows)
    {
        if (!startWithWindows)
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var executable = _getExecutablePath();
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return;
                }

                var hasLegacyRun = _backend.HasScreenRegistryRunValue();
                if (hasLegacyRun && _backend.IsRegistryRunDisabledByUser())
                {
                    SimpleLogger.LogInfo("Skipping startup migration because Flick is disabled in Windows Startup settings.");
                    return;
                }

                var currentTask = _backend.GetLogonTask();
                var taskMatches = currentTask is { Enabled: true }
                                  && PathsEqual(currentTask.ExecutablePath, executable)
                                  && HasStartupArgument(currentTask.Arguments)
                                  && currentTask.Priority <= NormalTaskPriority;
                if (taskMatches && !hasLegacyRun)
                {
                    return;
                }

                // If the user explicitly disabled the scheduled task in Task Scheduler while
                // no legacy Run entry remains, respect that choice instead of re-enabling it.
                if (currentTask is { Enabled: false } && !hasLegacyRun)
                {
                    return;
                }

                SetEnabledCore(true);
            }
            catch (Exception exception)
            {
                SimpleLogger.LogWarning($"Startup registration sync skipped: {exception.Message}");
            }
        }
    }

    internal static string FormatRunCommand(string executable) => $"\"{executable}\" {StartupArgument}";

    internal static bool IsScreenRunCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var trimmed = command.Trim();
        string path;
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote <= 1)
            {
                return false;
            }
            path = trimmed[1..endQuote];
        }
        else
        {
            var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex >= 0)
            {
                var end = exeIndex + 4;
                if (end == trimmed.Length || char.IsWhiteSpace(trimmed[end]))
                {
                    path = trimmed[..end];
                }
                else
                {
                    var space = trimmed.IndexOf(' ');
                    path = space > 0 ? trimmed[..space] : trimmed;
                }
            }
            else
            {
                var space = trimmed.IndexOf(' ');
                path = space > 0 ? trimmed[..space] : trimmed;
            }
        }

        return string.Equals(Path.GetFileName(path), "Screen.exe", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsStartupApprovedDisabled(byte[]? state) => state is { Length: > 0 } && (state[0] & 1) == 1;

    private void TryDeleteScheduledTask()
    {
        try
        {
            _backend.DeleteLogonTask();
        }
        catch (Exception exception)
        {
            SimpleLogger.LogWarning($"Unable to remove Task Scheduler logon task: {exception.Message}");
        }
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }
        try
        {
            return string.Equals(
                Path.GetFullPath(left.Trim().Trim('"')),
                Path.GetFullPath(right.Trim().Trim('"')),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return string.Equals(left.Trim().Trim('"'), right.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool HasStartupArgument(string? arguments) =>
        !string.IsNullOrWhiteSpace(arguments)
        && arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(token => string.Equals(token, StartupArgument, StringComparison.OrdinalIgnoreCase));

    internal const int NormalTaskPriority = 4;

    internal sealed record ScheduledStartupTaskInfo(
        bool Enabled,
        string ExecutablePath,
        string Arguments,
        int Priority = NormalTaskPriority);

    internal interface IStartupBackend
    {
        ScheduledStartupTaskInfo? GetLogonTask();
        void RegisterLogonTask(string executablePath, string arguments);
        void DeleteLogonTask();
        bool HasScreenRegistryRunValue();
        bool IsRegistryRunDisabledByUser();
        void SetRegistryRunValue(string command);
        void DeleteRegistryRunValue();
        void DeleteRegistryRunValueIfPointsToScreen();
    }

    private sealed class WindowsStartupBackend : IStartupBackend
    {
        private const int TaskTriggerLogon = 9;
        private const int TaskActionExec = 0;
        private const int TaskLogonInteractiveToken = 3;
        private const int TaskRunLevelLua = 0;
        private const int TaskCreateOrUpdate = 6;
        private const int TaskInstancesIgnoreNew = 2;
        private const int FileNotFoundHResult = unchecked((int)0x80070002);
        private const int PathNotFoundHResult = unchecked((int)0x80070003);

        public ScheduledStartupTaskInfo? GetLogonTask()
        {
            var (_, taskName) = GetCurrentUserIdentity();
            return WithRootFolder(rootFolder =>
            {
                dynamic? task = null;
                try
                {
                    task = rootFolder.GetTask(taskName);
                    bool enabled = task.Enabled;
                    int priority = (int)task.Definition.Settings.Priority;
                    string path = string.Empty;
                    string arguments = string.Empty;
                    foreach (dynamic action in task.Definition.Actions)
                    {
                        try
                        {
                            if ((int)action.Type == TaskActionExec)
                            {
                                path = (string?)action.Path ?? string.Empty;
                                arguments = (string?)action.Arguments ?? string.Empty;
                                break;
                            }
                        }
                        finally
                        {
                            ReleaseComObject(action);
                        }
                    }
                    return new ScheduledStartupTaskInfo(enabled, path, arguments, priority);
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (COMException exception) when (exception.ErrorCode is FileNotFoundHResult or PathNotFoundHResult)
                {
                    return null;
                }
                finally
                {
                    ReleaseComObject(task);
                }
            });
        }

        public void RegisterLogonTask(string executablePath, string arguments)
        {
            var (userName, taskName) = GetCurrentUserIdentity();
            WithScheduler((scheduler, rootFolder) =>
            {
                dynamic? definition = null;
                dynamic? trigger = null;
                dynamic? action = null;
                dynamic? registered = null;
                try
                {
                    definition = scheduler.NewTask(0);
                    definition.RegistrationInfo.Description = "Start Flick in the system tray when signing in to Windows.";
                    definition.RegistrationInfo.Author = "Flick";

                    trigger = definition.Triggers.Create(TaskTriggerLogon);
                    trigger.UserId = userName;
                    trigger.Enabled = true;

                    action = definition.Actions.Create(TaskActionExec);
                    action.Path = executablePath;
                    action.Arguments = arguments;
                    var workingDirectory = Path.GetDirectoryName(executablePath);
                    if (!string.IsNullOrWhiteSpace(workingDirectory))
                    {
                        action.WorkingDirectory = workingDirectory;
                    }

                    definition.Principal.UserId = userName;
                    definition.Principal.LogonType = TaskLogonInteractiveToken;
                    definition.Principal.RunLevel = TaskRunLevelLua;

                    var settings = definition.Settings;
                    settings.Enabled = true;
                    settings.Hidden = false;
                    settings.DisallowStartIfOnBatteries = false;
                    settings.StopIfGoingOnBatteries = false;
                    // Default Task Scheduler tasks terminate after 72 hours (PT72H) and run at
                    // Below Normal CPU/I/O priority (7). Keep Flick alive indefinitely at Normal priority.
                    settings.ExecutionTimeLimit = "PT0S";
                    settings.AllowHardTerminate = false;
                    settings.StartWhenAvailable = false;
                    settings.MultipleInstances = TaskInstancesIgnoreNew;
                    settings.Priority = NormalTaskPriority;

                    registered = rootFolder.RegisterTaskDefinition(
                        taskName,
                        definition,
                        TaskCreateOrUpdate,
                        null,
                        null,
                        TaskLogonInteractiveToken);
                    return true;
                }
                finally
                {
                    ReleaseComObject(registered);
                    ReleaseComObject(action);
                    ReleaseComObject(trigger);
                    ReleaseComObject(definition);
                }
            });
        }

        public void DeleteLogonTask()
        {
            var (_, taskName) = GetCurrentUserIdentity();
            WithRootFolder(rootFolder =>
            {
                try
                {
                    rootFolder.DeleteTask(taskName, 0);
                }
                catch (FileNotFoundException)
                {
                }
                catch (COMException exception) when (exception.ErrorCode is FileNotFoundHResult or PathNotFoundHResult)
                {
                }
                return true;
            });
        }

        public bool HasScreenRegistryRunValue()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: false);
            return IsScreenRunCommand(key?.GetValue(ValueName) as string);
        }

        public bool IsRegistryRunDisabledByUser()
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedRunPath, writable: false);
            return IsStartupApprovedDisabled(key?.GetValue(ValueName) as byte[]);
        }

        public void SetRegistryRunValue(string command)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunPath, writable: true);
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }

        public void DeleteRegistryRunValue()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        public void DeleteRegistryRunValueIfPointsToScreen()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath, writable: true);
            if (key is null)
            {
                return;
            }
            if (IsScreenRunCommand(key.GetValue(ValueName) as string))
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }

        private static (string UserName, string TaskName) GetCurrentUserIdentity()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var userName = identity.Name;
            var sid = identity.User?.Value;
            var taskName = string.IsNullOrWhiteSpace(sid) ? TaskNamePrefix : $"{TaskNamePrefix}-{sid}";
            return (userName, taskName);
        }

        private static T WithRootFolder<T>(Func<dynamic, T> action) =>
            WithScheduler((_, rootFolder) => action(rootFolder));

        private static T WithScheduler<T>(Func<dynamic, dynamic, T> action)
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service")
                              ?? throw new InvalidOperationException("Windows Task Scheduler COM service is unavailable.");
            dynamic? scheduler = null;
            dynamic? rootFolder = null;
            try
            {
                scheduler = Activator.CreateInstance(serviceType)
                            ?? throw new InvalidOperationException("Failed to create Windows Task Scheduler service instance.");
                scheduler.Connect();
                rootFolder = scheduler.GetFolder("\\");
                return action(scheduler, rootFolder);
            }
            finally
            {
                ReleaseComObject(rootFolder);
                ReleaseComObject(scheduler);
            }
        }

        private static void ReleaseComObject(object? value)
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }
    }
}
