using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace Screen.App;

/// <summary>Save-as, copy-to-clipboard and trimmed export for the recording editor.</summary>
internal sealed partial class RecordingPreviewWindow
{
    private readonly RecordingExportCache _gifExportCache = new();
    private readonly string _savedPath;
    private bool _originalGifAvailable;

    private async void SaveAs(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _closed)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "保存录屏",
            Filter = "MP4 视频 (*.mp4)|*.mp4|GIF 动图 (*.gif)|*.gif",
            FilterIndex = _exportFormat == RecordingExportFormat.Gif ? 2 : 1,
            DefaultExt = _exportFormat == RecordingExportFormat.Gif ? ".gif" : ".mp4",
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(_savedPath)
                       + (_timeline.IsTrimmed ? "-裁切" : string.Empty)
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
            var destination = dialog.FileName;
            var format = Path.GetExtension(destination).ToLowerInvariant() switch
            {
                ".mp4" => RecordingExportFormat.Mp4,
                ".gif" => RecordingExportFormat.Gif,
                _ => throw new InvalidOperationException("请选择 .mp4 或 .gif 文件扩展名。")
            };
            var token = _exportLifetime.Token;
            // Overwriting the delivered GIF with a trim leaves the full MP4 preview intact.
            if (_timeline.IsTrimmed && string.Equals(destination, _savedPath, StringComparison.OrdinalIgnoreCase))
                _originalGifAvailable = false;
            var source = await GetExportAsync(format);
            if (string.Equals(destination, _filePath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(source, _filePath, StringComparison.OrdinalIgnoreCase))
            {
                // Saving a trim over its source must not change the footage behind the open timeline.
                var original = _filePath;
                _filePath = await _exportCache.GetAsync(new RecordingTrimRange(0, _timeline.Maximum), (_, lifetime) =>
                    Task.Run(() =>
                    {
                        var directory = Directory.CreateDirectory(RecordingTempCleaner.DefaultRoot);
                        var snapshot = Path.Combine(directory.FullName, $"trim-{Guid.NewGuid():N}.mp4");
                        RecordingFileStore.CopyAtomically(original, snapshot, lifetime);
                        return snapshot;
                    }, lifetime));
                token.ThrowIfCancellationRequested();
                var previousDecode = _decodeTask;
                StartDecoderFrom(_positionSeconds, autoPlay: _playing);
                if (previousDecode is not null)
                    await previousDecode;
            }
            await Task.Run(() => RecordingFileStore.CopyAtomically(source, destination, token), token);
            if (!_closed)
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
            MessageBox.Show(this, $"保存失败：{exception.Message}", "Flick", MessageBoxButton.OK, MessageBoxImage.Error);
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
        string? pendingCopy = null;
        try
        {
            var token = _exportLifetime.Token;
            var trimmed = _timeline.IsTrimmed;
            SetStatus(_exportFormat == RecordingExportFormat.Gif ? "正在导出 GIF…" : "正在复制…");
            var source = await GetExportAsync(_exportFormat);
            token.ThrowIfCancellationRequested();
            if (!string.Equals(source, _savedPath, StringComparison.OrdinalIgnoreCase))
            {
                pendingCopy = await Task.Run(() => RecordingFileStore.CreateClipboardCopy(source, _savedPath, trimmed, token), token);
                source = pendingCopy;
            }
            token.ThrowIfCancellationRequested();
            Clipboard.SetFileDropList(new StringCollection { source });
            pendingCopy = null;
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
            MessageBox.Show(this, $"复制失败：{exception.Message}", "Flick", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (pendingCopy != null)
                RecordingFileStore.DeleteFileQuietly(pendingCopy);
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
    private Task<string> GetExportAsync(RecordingExportFormat format)
    {
        var range = new RecordingTrimRange(_timeline.TrimStart, _timeline.TrimEnd);
        if (format == RecordingExportFormat.Gif)
        {
            if (!_timeline.IsTrimmed && _originalGifAvailable && File.Exists(_savedPath))
                return Task.FromResult(_savedPath);
            return _gifExportCache.GetAsync(range, ExportGifAsync);
        }
        if (!_timeline.IsTrimmed)
        {
            return Task.FromResult(_filePath);
        }
        return _exportCache.GetAsync(range, ExportTrimAsync);
    }

    private async Task<string> ExportGifAsync(RecordingTrimRange range, CancellationToken token)
    {
        var directory = Directory.CreateDirectory(RecordingTempCleaner.DefaultRoot);
        var path = Path.Combine(directory.FullName, $"trim-{Guid.NewGuid():N}.gif");
        await RecordingGifExporter.ExportAsync(_filePath, path, _videoWidth, _videoHeight, range, token).ConfigureAwait(false);
        return path;
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
