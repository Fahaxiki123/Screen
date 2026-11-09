using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Screen.App;

/// <summary>
/// One WASAPI capture (system loopback or microphone) streamed as raw PCM
/// into a temp file.  Mixing, resampling and channel layout are all handled
/// by ffmpeg at the final mux, so this layer stays format-agnostic: it only
/// records which raw layout (f32le/s16le, rate, channels) the file holds.
/// </summary>
internal sealed class RecordingAudioTrack : IDisposable
{
    private readonly IWaveIn _capture;
    private readonly Stream _stream;
    private readonly object _gate = new();
    private readonly RecordingStopSignal _stopSignal;
    private volatile bool _paused;
    private long _bytesWritten;
    private bool _stopped;
    private int _disposed;

    internal RecordingAudioTrack(IWaveIn capture, string rawPath, string ffmpegFormat, int sampleRate, int channels, RecordingStopSignal stopSignal, Stream? output = null)
    {
        _capture = capture;
        _stopSignal = stopSignal;
        _stream = output ?? new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        RawPath = rawPath;
        FfmpegFormat = ffmpegFormat;
        SampleRate = sampleRate;
        Channels = channels;
        _capture.DataAvailable += (_, e) =>
        {
            if (_paused || e.BytesRecorded <= 0)
            {
                return;
            }
            lock (_gate)
            {
                if (_stopped || _stopSignal.IsRequested)
                {
                    return;
                }
                _stream.Write(e.Buffer, 0, e.BytesRecorded);
                _bytesWritten += e.BytesRecorded;
            }
        };
    }

    public string RawPath { get; }
    public string FfmpegFormat { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    public bool Paused { set => _paused = value; }

    /// <summary>Attempts to capture the default render device (system audio loopback).</summary>
    public static RecordingAudioTrack? TryCreateSystemLoopback(string tempDir, RecordingStopSignal stopSignal)
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            if (RawFormatFor(capture.WaveFormat) is not { } format)
            {
                capture.Dispose();
                return null;
            }
            var track = new RecordingAudioTrack(
                capture,
                Path.Combine(tempDir, "system-audio.raw"),
                format,
                capture.WaveFormat.SampleRate,
                capture.WaveFormat.Channels,
                stopSignal);
            capture.StartRecording();
            return track;
        }
        catch
        {
            // No render device / format we can persist — recording simply
            // proceeds without this track.
            return null;
        }
    }

    /// <summary>Attempts to capture the default microphone.</summary>
    public static RecordingAudioTrack? TryCreateMicrophone(string tempDir, RecordingStopSignal stopSignal)
    {
        try
        {
            var capture = new WasapiCapture();
            if (RawFormatFor(capture.WaveFormat) is not { } format)
            {
                capture.Dispose();
                return null;
            }
            var track = new RecordingAudioTrack(
                capture,
                Path.Combine(tempDir, "microphone.raw"),
                format,
                capture.WaveFormat.SampleRate,
                capture.WaveFormat.Channels,
                stopSignal);
            capture.StartRecording();
            return track;
        }
        catch
        {
            return null;
        }
    }

    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private static string? RawFormatFor(WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                      || (format is WaveFormatExtensible extensible && extensible.SubFormat == SubtypeIeeeFloat);
        if (isFloat && format.BitsPerSample == 32)
        {
            return "f32le";
        }
        var isPcm = format.Encoding == WaveFormatEncoding.Pcm
                    || (format is WaveFormatExtensible extensible2 && extensible2.SubFormat == SubtypePcm);
        if (isPcm && format.BitsPerSample == 16)
        {
            return "s16le";
        }
        return null;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
        }
        try
        {
            _capture.StopRecording();
        }
        catch
        {
        }
        // _stopped and _gate already exclude every late callback, so no timed
        // sleep is needed to guess when it is safe to close the stream.
        lock (_gate)
        {
            try
            {
                _stream.Flush();
            }
            finally
            {
                _stream.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            Stop();
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Audio track finalization failed during disposal", exception);
        }
        try
        {
            _capture.Dispose();
        }
        catch (Exception exception)
        {
            SimpleLogger.LogError("Audio capture disposal failed", exception);
        }
    }
}

/// <summary>
/// Owns the audio tracks of one recording session.  Zero tracks is a valid
/// state (device missing or both switches off) — the mux then produces a
/// silent-free video-only file.
/// </summary>
internal sealed class RecordingAudioService : IDisposable
{
    private readonly List<RecordingAudioTrack> _tracks = [];

    internal RecordingAudioService(IEnumerable<RecordingAudioTrack>? tracks = null)
    {
        if (tracks is not null)
        {
            _tracks.AddRange(tracks);
        }
    }

    public IReadOnlyList<RecordingAudioTrack> Tracks => _tracks;

    public bool Paused
    {
        set
        {
            foreach (var track in _tracks)
            {
                track.Paused = value;
            }
        }
    }

    public static RecordingAudioService Start(string tempDir, bool systemAudio, bool microphone, RecordingStopSignal stopSignal)
    {
        var service = new RecordingAudioService();
        if (systemAudio && RecordingAudioTrack.TryCreateSystemLoopback(tempDir, stopSignal) is { } loopback)
        {
            service._tracks.Add(loopback);
        }
        if (microphone && RecordingAudioTrack.TryCreateMicrophone(tempDir, stopSignal) is { } mic)
        {
            service._tracks.Add(mic);
        }
        return service;
    }

    public void Stop()
    {
        List<Exception>? errors = null;
        foreach (var track in _tracks)
        {
            try
            {
                track.Stop();
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }
        if (errors is not null)
        {
            throw new AggregateException("一个或多个音轨收尾失败。", errors);
        }
    }

    public void Dispose()
    {
        foreach (var track in _tracks)
        {
            try
            {
                track.Dispose();
            }
            catch (Exception exception)
            {
                SimpleLogger.LogError("Audio track disposal failed; continuing other tracks", exception);
            }
        }
        _tracks.Clear();
    }
}
