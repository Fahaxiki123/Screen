using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using Screen.Core;

namespace Screen.App;

internal sealed partial class RecordingPreviewWindow
{
    private bool IsGifExport => _exportProfile.Format == RecordingFormat.Gif;

    private async void SaveAsAsync(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _closed || _exportOperation is not null)
            return;
        var extension = IsGifExport ? ".gif" : ".mp4";
        var dialog = new SaveFileDialog
        {
            Title = "保存录屏",
            Filter = IsGifExport ? "GIF 动图 (*.gif)|*.gif" : "MP4 视频 (*.mp4)|*.mp4",
            DefaultExt = extension,
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(_filePath) + (_timeline.IsTrimmed ? "-裁切" : string.Empty) + extension
        };
        if (dialog.ShowDialog(this) == true)
            await DeliverExportAsync(dialog.FileName, clipboard: false);
    }

    private async void CopyToClipboardAsync(object sender, RoutedEventArgs e)
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
        void Report(double value)
        {
            if (!_closed && ReferenceEquals(_exportOperation, operation) && !operation.IsCancellationRequested)
                SetStatus($"正在导出 {value:P0}", persistent: true);
        }
        // GIF palette/encoding is the expensive part; keep progress monotone through the atomic copy.
        var progress = new Progress<double>(value => Report(IsGifExport ? 0.9 + 0.1 * value : value));
        var encodingProgress = new Progress<double>(value => Report(0.9 * value));
        try
        {
            if (IsGifExport && destination is not null)
                ValidateGifDestination(_filePath, destination);
            source = await GetExportSourceAsync(operation.Token, encodingProgress);
            operation.Token.ThrowIfCancellationRequested();
            if (clipboard)
            {
                if (!string.Equals(source, _filePath, StringComparison.OrdinalIgnoreCase))
                {
                    clipboardCopy = RecordingFileStore.ClipboardCopyPath(_filePath, _exportProfile.Format);
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

    private Task<string> GetExportSourceAsync(CancellationToken cancellationToken, IProgress<double> encodingProgress)
    {
        // Format and GIF options belong to this immutable recording session; the cache only needs the range.
        var isTrimmed = _timeline.IsTrimmed;
        if (!IsGifExport && !isTrimmed)
            return Task.FromResult(_filePath);
        var range = isTrimmed
            ? new RecordingTrimRange(_timeline.TrimStart, _timeline.TrimEnd)
            : new RecordingTrimRange(0, _durationSeconds);
        return _exportCache.GetAsync(range, async (selected, lifetime) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
            return await RenderExportAsync(selected, isTrimmed, linked.Token, encodingProgress).ConfigureAwait(false);
        });
    }

    internal static void ValidateGifDestination(string source, string target)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(target), ".gif", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 .gif 文件名；原始 MP4 不能被 GIF 覆盖。", nameof(target));
    }

    internal static ProcessStartInfo BuildGifStartInfo(string ffmpeg, string source, string target, RecordingTrimRange? range,
        int fps, int maxWidth)
    {
        ValidateGifDestination(source, target);
        if (fps is < 10 or > 30)
            throw new ArgumentOutOfRangeException(nameof(fps), "GIF 帧率必须在 10–30 之间。");
        ArgumentOutOfRangeException.ThrowIfNegative(maxWidth);
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        void Add(params string[] args)
        {
            foreach (var arg in args)
                info.ArgumentList.Add(arg);
        }
        Add("-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-progress", "pipe:1", "-nostats");
        var frameTrim = string.Empty;
        if (range is { } selected)
        {
            if (!double.IsFinite(selected.Start) || !double.IsFinite(selected.End) || selected.Start < 0 || selected.End <= selected.Start)
                throw new ArgumentOutOfRangeException(nameof(range), "裁切区间无效。");
            // Retain the preroll frame that may span Start in variable-frame-rate screen recordings.
            Add("-noaccurate_seek", "-ss", selected.Start.ToString("0.######", CultureInfo.InvariantCulture));
            // Don't put -to on the input: the next VFR timestamp can be AFTER End and still
            // define how long the last selected frame is displayed. Stop after sampling the
            // requested frames, before split, so the palette and GIF cover the same interval.
            var frames = Math.Max(1, checked((long)Math.Ceiling((selected.End - selected.Start) * fps - 0.00000001)));
            frameTrim = FormattableString.Invariant($"trim=end_frame={frames},");
        }
        Add("-i", Path.GetFullPath(source), "-map", "0:v:0", "-an");
        var scale = maxWidth == 0 ? string.Empty : FormattableString.Invariant($"scale='min(iw,{maxWidth})':-2:flags=lanczos,");
        // Keep at least the final held sample, even for a one-frame recording shorter than a GIF frame.
        var rate = FormattableString.Invariant($"fps={fps}") + (range.HasValue ? ":start_time=0" : string.Empty) + ":eof_action=pass";
        Add("-vf", $"{rate},{frameTrim}{scale}split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle");
        Add("-c:v", "gif", "-f", "gif", "-loop", "0", Path.GetFullPath(target));
        return info;
    }

    internal static bool IsGifUnavailable(string errors)
    {
        // Older bundled builds may lack the muxer as well as the encoder or one filter.
        // Do not report bad input, lack of disk space, or palette runtime failures as a missing feature.
        return errors.Contains("Unrecognized option 'loop'.", StringComparison.OrdinalIgnoreCase)
            || errors.Contains("Unknown encoder 'gif'", StringComparison.OrdinalIgnoreCase)
            || errors.Contains("Encoder (codec gif) not found", StringComparison.OrdinalIgnoreCase)
            || errors.Contains("Requested output format 'gif' is not known", StringComparison.OrdinalIgnoreCase)
            || new[] { "fps", "trim", "scale", "split", "palettegen", "paletteuse" }.Any(name =>
                errors.Contains($"No such filter: '{name}'", StringComparison.OrdinalIgnoreCase)
                || errors.Contains($"Filter '{name}' not found", StringComparison.OrdinalIgnoreCase));
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

    private async Task<string> RenderExportAsync(RecordingTrimRange range, bool isTrimmed, CancellationToken cancellationToken,
        IProgress<double> progress)
    {
        var root = _exportTempRoot;
        Directory.CreateDirectory(root);
        RecordingStorage.EnsureAvailableSpace(root);
        var tempPath = Path.Combine(root, IsGifExport ? $"gif-preview-{Guid.NewGuid():N}.gif" : $"trim-{Guid.NewGuid():N}.mp4");
        cancellationToken.ThrowIfCancellationRequested();
        var ffmpeg = FfmpegLocator.Resolve();
        var info = IsGifExport
            ? BuildGifStartInfo(ffmpeg, _filePath, tempPath, isTrimmed ? range : null, _exportProfile.GifFrameRate, _exportProfile.GifMaxWidth)
            : BuildTrimStartInfo(ffmpeg, _filePath, tempPath, range, _fps);
        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("无法启动 ffmpeg。");
        var errors = ProcessErrorTail.Attach(process);
        if (info.RedirectStandardOutput)
        {
            var duration = range.End - range.Start;
            const string key = "out_time_us=";
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line && line.StartsWith(key, StringComparison.Ordinal)
                    && long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                    progress.Report(Math.Clamp(microseconds / (duration * 1_000_000d), 0, 1));
            };
            process.BeginOutputReadLine();
        }
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
            {
                if (IsGifExport && IsGifUnavailable(errors.Text))
                    throw new InvalidOperationException("当前 FFmpeg 不支持 GIF，请更新 Screen。原始 MP4 已保留。");
                throw new InvalidOperationException(errors.Text);
            }
            if (IsGifExport && new FileInfo(tempPath).Length == 0)
                throw new IOException("GIF 导出为空，原始 MP4 已保留。");
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
