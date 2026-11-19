using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;

namespace Screen.App;

internal sealed partial class RecordingPreviewWindow
{
    private async void SaveAs(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _closed || _exportOperation is not null)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "保存录屏",
            Filter = "MP4 视频 (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(_filePath) + (_timeline.IsTrimmed ? "-裁切" : string.Empty) + ".mp4"
        };
        if (dialog.ShowDialog(this) == true)
            await DeliverExportAsync(dialog.FileName, clipboard: false);
    }

    private async void CopyToClipboard(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _closed || _exportOperation is not null)
            return;
        await DeliverExportAsync(null, clipboard: true);
    }

    private async Task DeliverExportAsync(string? destination, bool clipboard)
    {
        using var operation = new CancellationTokenSource();
        _exportOperation = operation;
        _downloadButton.IsEnabled = _copyButton.IsEnabled = false;
        _cancelExportButton.Visibility = Visibility.Visible;
        string? source = null;
        string? clipboardCopy = null;
        var delivered = false;
        SetStatus(_timeline.IsTrimmed ? "正在裁切…" : "正在导出…", persistent: true);
        var progress = new Progress<double>(value =>
        {
            if (!_closed && ReferenceEquals(_exportOperation, operation) && !operation.IsCancellationRequested)
                SetStatus($"正在导出 {value:P0}", persistent: true);
        });
        try
        {
            source = await GetTrimmedOrOriginalAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (clipboard)
            {
                if (!string.Equals(source, _filePath, StringComparison.OrdinalIgnoreCase))
                {
                    clipboardCopy = RecordingFileStore.ClipboardCopyPath(_filePath);
                    await RecordingFileStore.CopyAtomicallyAsync(source, clipboardCopy, operation.Token, progress);
                }
                operation.Token.ThrowIfCancellationRequested();
                Clipboard.SetFileDropList(new StringCollection { clipboardCopy ?? source });
            }
            else
            {
                await RecordingFileStore.CopyAtomicallyAsync(source, destination!, operation.Token, progress);
            }
            delivered = true;
            SetStatus(clipboard ? "已复制，可粘贴到聊天或文件夹" : "已保存");
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested || _closed)
        {
            if (!_closed)
                SetStatus("已取消导出，原文件保留");
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                SetStatus("导出失败");
                MessageBox.Show(this, $"导出失败：{exception.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _exportOperation = null;
            if (!_closed)
            {
                _downloadButton.IsEnabled = _copyButton.IsEnabled = true;
                _cancelExportButton.Visibility = Visibility.Collapsed;
            }
            if (!delivered && clipboardCopy is not null)
                RecordingFileStore.DeleteFileQuietly(clipboardCopy);
            if (_closed && source is not null && !string.Equals(source, _filePath, StringComparison.OrdinalIgnoreCase))
                RecordingFileStore.DeleteFileQuietly(source);
        }
    }

    private Task<string> GetTrimmedOrOriginalAsync(CancellationToken cancellationToken)
    {
        if (!_timeline.IsTrimmed)
            return Task.FromResult(_filePath);
        var range = new RecordingTrimRange(_timeline.TrimStart, _timeline.TrimEnd);
        return _exportCache.GetAsync(range, async (selected, lifetime) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
            return await ExportTrimAsync(selected, linked.Token).ConfigureAwait(false);
        });
    }

    internal static ProcessStartInfo BuildTrimStartInfo(string ffmpeg, string source, string target, RecordingTrimRange range,
        double frameRate = 30)
    {
        if (!double.IsFinite(range.Start) || !double.IsFinite(range.End) || range.Start < 0 || range.End <= range.Start
            || !double.IsFinite(frameRate) || frameRate <= 0 || frameRate > 240)
            throw new ArgumentOutOfRangeException(nameof(range), "裁切区间无效。");
        var duration = range.End - range.Start;
        var frames = checked((long)Math.Ceiling(duration * frameRate - 0.00000001));
        var startInfo = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            // A VFR sample may start before the selected range but remain on
            // screen inside it. Preserve that video preroll and resample it;
            // audio needs its own accurate seek to avoid keeping earlier sound.
            "-an", "-noaccurate_seek", "-ss", range.Start.ToString("0.######", CultureInfo.InvariantCulture), "-i", source,
            "-vn", "-accurate_seek", "-ss", range.Start.ToString("0.######", CultureInfo.InvariantCulture),
            "-t", duration.ToString("0.######", CultureInfo.InvariantCulture), "-i", source,
            "-map", "0:v:0", "-map", "1:a:0?",
            "-frames:v", Math.Max(1, frames).ToString(CultureInfo.InvariantCulture),
            "-r", frameRate.ToString("0.######", CultureInfo.InvariantCulture), "-fps_mode", "cfr",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", target
        })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private async Task<string> ExportTrimAsync(RecordingTrimRange range, CancellationToken cancellationToken)
    {
        var root = _exportTempRoot;
        Directory.CreateDirectory(root);
        RecordingStorage.EnsureAvailableSpace(root);
        var tempPath = Path.Combine(root, $"trim-{Guid.NewGuid():N}.mp4");
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(BuildTrimStartInfo(FfmpegLocator.Resolve(), _filePath, tempPath, range, _fps))
            ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        var errors = ProcessErrorTail.Attach(process);
        using var cancellation = cancellationToken.Register(() =>
        {
            try
            { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(errors.Text);
            return tempPath;
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
            RecordingFileStore.DeleteFileQuietly(tempPath);
            throw;
        }
    }
}
