using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Screen.App;

/// <summary>
/// Pre-loads lazy assemblies inside a short-lived isolated child process so
/// real-time antivirus image scanning on Windows never acquires the main tray
/// process's loader lock (<c>LdrpLoaderLock</c>) or stalls the UI/hotkey thread.
/// </summary>
internal static class AssemblyPreloadWorker
{
    internal const string WorkerArgument = "--preload-assemblies";
    internal const string CaptureStage = "capture";
    internal const string OcrStage = "ocr";

    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] CaptureAndOverlayAssemblies =
    [
        "Vortice.DXGI",
        "Vortice.Direct3D11",
        "Vortice.DirectX",
        "Vortice.Mathematics",
        "SharpGen.Runtime"
    ];

    private static readonly string[] OcrAssemblies =
    [
        "RapidOcrNet",
        "Microsoft.ML.OnnxRuntime",
        "SkiaSharp",
        "Clipper2Lib",
        "System.Numerics.Tensors",
        "Lucide.NET.Core",
        "UIAutomationClient",
        "UIAutomationClientSideProviders"
    ];

    public static bool TryRunFromArgs(string[] args)
    {
        if (!TryParseStage(args, out var stage))
        {
            return false;
        }

        // Watchdog guarantees a helper process can never linger even if a scan hangs.
        using var watchdog = new Timer(static _ => Environment.Exit(0), null, WorkerTimeout, Timeout.InfiniteTimeSpan);
        LoadStageAssemblies(stage);
        return true;
    }

    internal static bool TryParseStage(string[] args, out string stage)
    {
        stage = string.Empty;
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], WorkerArgument, StringComparison.OrdinalIgnoreCase))
            {
                stage = index + 1 < args.Length ? args[index + 1] : string.Empty;
                return true;
            }
        }
        return false;
    }

    internal static IReadOnlyList<string> GetAssembliesForStage(string stage) => stage.ToLowerInvariant() switch
    {
        CaptureStage => CaptureAndOverlayAssemblies,
        OcrStage => OcrAssemblies,
        _ => []
    };

    public static async Task<bool> RunStageAsync(string stage, string label, CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)
            || !string.Equals(Path.GetFileName(executable), "Screen.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var timer = Stopwatch.StartNew();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add(WorkerArgument);
        startInfo.ArgumentList.Add(stage);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        try
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception)
            {
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(WorkerTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            SimpleLogger.LogInfo(
                $"Preloaded {label} assemblies out-of-process in {timer.Elapsed.TotalMilliseconds:F1} ms (exit: {process.ExitCode})");
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            SimpleLogger.LogWarning($"Out-of-process {label} assembly preload timed out.");
            return false;
        }
        catch (Exception exception)
        {
            TryTerminate(process);
            SimpleLogger.LogWarning($"Out-of-process {label} assembly preload skipped: {exception.Message}");
            return false;
        }
    }

    private static void LoadStageAssemblies(string stage)
    {
        var assemblies = GetAssembliesForStage(stage);
        if (assemblies.Count == 0)
        {
            return;
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 6)
        };
        Parallel.ForEach(assemblies, options, static name =>
        {
            try
            {
                Assembly.Load(name);
            }
            catch (Exception)
            {
            }
        });
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
