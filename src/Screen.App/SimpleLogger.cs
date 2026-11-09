using System.IO;

namespace Screen.App;

/// <summary>
/// Lightweight file logger for diagnostics. Logs to %LocalAppData%\Screen\logs\
/// with automatic daily rotation (keeps last 7 days).
/// </summary>
internal static class SimpleLogger
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Screen",
        "logs");

    private static readonly object Lock = new();

    static SimpleLogger()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            CleanOldLogs();
        }
        catch
        {
            // If we can't create the log directory, logging will silently fail.
        }
    }

    public static void LogError(string message, Exception? exception = null)
    {
        Log("ERROR", message, exception);
    }

    public static void LogWarning(string message)
    {
        Log("WARN", message, null);
    }

    public static void LogInfo(string message)
    {
        Log("INFO", message, null);
    }

    private static void Log(string level, string message, Exception? exception)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logFile = Path.Combine(LogDirectory, $"screen-{DateTime.Now:yyyyMMdd}.log");
            var entry = exception is null
                ? $"[{timestamp}] [{level}] {message}\n"
                : $"[{timestamp}] [{level}] {message}\n{exception}\n";

            lock (Lock)
            {
                File.AppendAllText(logFile, entry);
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }

    private static void CleanOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in Directory.GetFiles(LogDirectory, "screen-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
        }
    }
}
