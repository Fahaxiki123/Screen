using System.Diagnostics;

namespace Screen.App;

/// <summary>Owns a cancellable, bounded final remux without blocking the dispatcher.</summary>
internal static class RecordingProcessRunner
{
    internal static TimeSpan FinalizationTimeout(long inputBytes) => TimeSpan.FromSeconds(
        Math.Clamp(120 + Math.Max(0, inputBytes) / (10.0 * 1024 * 1024), 120, 1800));

    public static void Run(ProcessStartInfo startInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        var errors = ProcessErrorTail.Attach(process);
        using var cancellation = cancellationToken.Register(() => Kill(process));
        if (!process.WaitForExit(timeout))
        {
            Kill(process);
            process.WaitForExit(TimeSpan.FromSeconds(5));
            throw new TimeoutException($"录屏文件整理超过 {timeout.TotalMinutes:0.#} 分钟，原始素材已保留。");
        }
        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg 退出码 {process.ExitCode}：{errors.Text}");
        }
    }

    private static void Kill(Process process)
    {
        try
        { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
