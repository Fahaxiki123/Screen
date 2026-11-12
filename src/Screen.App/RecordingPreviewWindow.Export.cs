using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace Screen.App;

/// <summary>Save-as, copy-to-clipboard and trimmed export for the recording editor.</summary>
internal sealed partial class RecordingPreviewWindow
{
    private async void SaveAs(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _closed)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "保存录屏",
            Filter = "MP4 视频 (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(_filePath)
                       + (_timeline.IsTrimmed ? "-裁切" : string.Empty) + ".mp4"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }
        SetStatus(_timeline.IsTrimmed ? "正在裁切导出…" : "正在导出…");
        try
        {
            var source = await GetTrimmedOrOriginalAsync();
            if (_closed)
            {
                return;
            }
            RecordingFileStore.CopyAtomically(source, dialog.FileName);
            SetStatus("已保存");
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (Exception exception)
        {
            if (_closed)
            {
                return;
            }
            SetStatus("保存失败");
            MessageBox.Show(this, $"保存失败：{exception.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (sender is Button restore)
            {
                restore.IsEnabled = true;
            }
        }
    }

    private async void CopyToClipboard(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _closed)
        {
            return;
        }
        if (sender is Button button)
        {
            button.IsEnabled = false;
        }
        try
        {
            if (_timeline.IsTrimmed)
            {
                SetStatus("正在裁切…");
            }
            var source = await GetTrimmedOrOriginalAsync();
            if (_closed)
            {
                return;
            }
            if (!string.Equals(source, _filePath, StringComparison.OrdinalIgnoreCase))
            {
                source = RecordingFileStore.CreateClipboardCopy(source, _filePath);
            }
            Clipboard.SetFileDropList(new StringCollection { source });
            SetStatus("已复制，可粘贴到聊天或文件夹");
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (Exception exception)
        {
            if (_closed)
            {
                return;
            }
            SetStatus("复制失败");
            MessageBox.Show(this, $"复制失败：{exception.Message}", "Screen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (sender is Button restore)
            {
                restore.IsEnabled = true;
            }
        }
    }

    /// <summary>
    /// Returns the original file when no trim is set, otherwise re-encodes the
    /// trimmed section once per immutable range. Concurrent requests share a
    /// task, and changing the selection never reassigns an older export result.
    /// </summary>
    private Task<string> GetTrimmedOrOriginalAsync()
    {
        if (!_timeline.IsTrimmed)
        {
            return Task.FromResult(_filePath);
        }
        var range = new RecordingTrimRange(_timeline.TrimStart, _timeline.TrimEnd);
        return _exportCache.GetAsync(range, ExportTrimAsync);
    }

    private async Task<string> ExportTrimAsync(RecordingTrimRange range, CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Screen.Recording");
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, $"trim-{Guid.NewGuid():N}.mp4");
        var startInfo = new ProcessStartInfo(FfmpegLocator.Resolve())
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-i", _filePath,
                     "-ss", range.Start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                     "-to", range.End.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                     "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                     "-c:a", "aac", "-b:a", "192k",
                     tempPath
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        using var cancellation = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        });
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
            {
                var tail = stderr.Length > 200 ? stderr[^200..] : stderr;
                throw new InvalidOperationException(tail.Trim());
            }
            return tempPath;
        }
        catch
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                {
                }
            }
            RecordingFileStore.DeleteFileQuietly(tempPath);
            throw;
        }
    }
}
