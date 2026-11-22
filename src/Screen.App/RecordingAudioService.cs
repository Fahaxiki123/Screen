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
    private static readonly byte[] ZeroChunk = new byte[16384];

    private readonly IDisposable _capture;
    private readonly Action _stopCapture;
    private readonly MMDevice? _endpoint;
    private readonly Stream _stream;
    private readonly object _gate = new();
    private readonly RecordingStopSignal _stopSignal;
    private readonly RecordingTimeline? _timeline;
    private readonly int _blockAlign;
    private volatile bool _paused;
    private float _peakLevel;
    private long _bytesWritten;
    private long _framesWritten;
    private bool _stopped;
    private int _disposed;

    internal RecordingAudioTrack(
        IWaveIn capture,
        string rawPath,
        string ffmpegFormat,
        int sampleRate,
        int channels,
        RecordingStopSignal stopSignal,
        Stream? output = null,
        RecordingTimeline? timeline = null)
        : this(capture, capture.StopRecording, null, rawPath, ffmpegFormat, sampleRate, channels, stopSignal, output, timeline)
    {
        capture.DataAvailable += (_, e) => WritePacket(e.Buffer.AsSpan(0, e.BytesRecorded));
    }

    private RecordingAudioTrack(IDisposable capture, Action stopCapture, MMDevice? endpoint,
        string rawPath, string ffmpegFormat, int sampleRate, int channels,
        RecordingStopSignal stopSignal, Stream? output, RecordingTimeline? timeline)
    {
        _capture = capture;
        _stopCapture = stopCapture;
        _endpoint = endpoint;
        _stopSignal = stopSignal;
        _timeline = timeline;
        _stream = output ?? new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        RawPath = rawPath;
        FfmpegFormat = ffmpegFormat;
        SampleRate = sampleRate;
        Channels = channels;
        var bytesPerSample = ffmpegFormat == "f32le" ? 4 : 2;
        _blockAlign = Math.Max(1, channels * bytesPerSample);
    }

    private void WritePacket(ReadOnlySpan<byte> buffer)
    {
        if (_paused || buffer.IsEmpty || (_timeline is not null && !_timeline.IsStarted))
        {
            return;
        }
        lock (_gate)
        {
            if (_stopped || _stopSignal.IsRequested)
                return;

            if (_timeline is { IsStarted: true } activeTimeline && SampleRate > 0)
            {
                var callbackFrames = buffer.Length / _blockAlign;
                var expectedStartFrames = (long)Math.Round(activeTimeline.ElapsedSeconds * SampleRate) - callbackFrames;
                var deficitFrames = expectedStartFrames - _framesWritten;
                // A silent render endpoint can skip callbacks. Preserve that gap in the video timeline.
                if (deficitFrames > SampleRate / 20)
                    WriteSilenceFramesCore(Math.Min(deficitFrames, SampleRate * 3600L));
            }
            _stream.Write(buffer);
            _bytesWritten += buffer.Length;
            _framesWritten += buffer.Length / _blockAlign;
        }
        Volatile.Write(ref _peakLevel, MeasurePeak(buffer, FfmpegFormat));
    }

    private void WriteSilenceFramesCore(long frames)
    {
        var remainingBytes = frames * _blockAlign;
        while (remainingBytes > 0)
        {
            var chunk = (int)Math.Min(remainingBytes, ZeroChunk.Length);
            _stream.Write(ZeroChunk, 0, chunk);
            remainingBytes -= chunk;
        }
        _bytesWritten += frames * _blockAlign;
        _framesWritten += frames;
    }

    public string RawPath { get; }
    public string FfmpegFormat { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    // WASAPI loopback may stop sending packets altogether while the endpoint is silent.
    public float TakeMeterSample() => Interlocked.Exchange(ref _peakLevel, 0);

    public bool Paused
    {
        set
        {
            _paused = value;
            Interlocked.Exchange(ref _peakLevel, 0);
        }
    }

    private static float MeasurePeak(ReadOnlySpan<byte> buffer, string format)
    {
        var peak = 0f;
        if (format == "f32le")
        {
            var step = Math.Max(4, (buffer.Length / 4 / 64) * 4);
            for (var i = 0; i + 3 < buffer.Length; i += step)
            {
                var sample = Math.Abs(BitConverter.ToSingle(buffer.Slice(i, 4)));
                if (sample > peak)
                {
                    peak = sample;
                }
            }
        }
        else if (format == "s16le")
        {
            var step = Math.Max(2, (buffer.Length / 2 / 64) * 2);
            for (var i = 0; i + 1 < buffer.Length; i += step)
            {
                var sample = Math.Abs(BitConverter.ToInt16(buffer.Slice(i, 2)) / 32768f);
                if (sample > peak)
                {
                    peak = sample;
                }
            }
        }
        return Math.Clamp(peak, 0f, 1f);
    }

    public static RecordingAudioTrack? TryCreateSystemLoopback(string tempDir, RecordingStopSignal stopSignal,
        RecordingTimeline? timeline = null) => TryCreate(tempDir, stopSignal, timeline, loopback: true);

    public static RecordingAudioTrack? TryCreateMicrophone(string tempDir, RecordingStopSignal stopSignal,
        RecordingTimeline? timeline = null) => TryCreate(tempDir, stopSignal, timeline, loopback: false);

    private static RecordingAudioTrack? TryCreate(string tempDir, RecordingStopSignal stopSignal, RecordingTimeline? timeline, bool loopback)
    {
        MMDevice? endpoint = null;
        WasapiRecorder? capture = null;
        RecordingAudioTrack? track = null;
        try
        {
            using var devices = new MMDeviceEnumerator();
            endpoint = devices.GetDefaultAudioEndpoint(loopback ? DataFlow.Render : DataFlow.Capture,
                loopback ? Role.Multimedia : Role.Console);
            var builder = new WasapiRecorderBuilder().WithDevice(endpoint).WithPollingSync();
            if (loopback)
                builder.WithLoopbackCapture();
            capture = builder.Build();
            if (RawFormatFor(capture.WaveFormat) is not { } format)
                return null;

            var rawPath = Path.Combine(tempDir, loopback ? "system-audio.raw" : "microphone.raw");
            track = new RecordingAudioTrack(capture, capture.StopRecording, endpoint, rawPath, format,
                capture.WaveFormat.SampleRate, capture.WaveFormat.Channels, stopSignal, null, timeline);
            var recorder = capture;
            capture = null;
            endpoint = null;
            var owner = track;
            recorder.DataAvailable += (buffer, _, _, _) => owner.WritePacket(buffer);
            recorder.StartRecording();
            track = null;
            return owner;
        }
        catch (Exception ex)
        {
            SimpleLogger.LogError(loopback ? "System audio capture unavailable" : "Microphone capture unavailable", ex);
            return null;
        }
        finally
        {
            Release(track);
            Release(capture);
            Release(endpoint);
            if (track is not null)
            {
                try
                {
                    File.Delete(track.RawPath);
                }
                catch (Exception ex)
                {
                    SimpleLogger.LogError("Failed to remove an unstarted audio track", ex);
                }
            }
        }
    }

    private static void Release(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch (Exception ex)
        {
            SimpleLogger.LogError("Audio device cleanup failed", ex);
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
            _stopCapture();
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
        finally
        {
            Release(_endpoint);
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

    public float TakeMeterSample()
    {
        var peak = 0f;
        foreach (var track in _tracks)
            peak = Math.Max(peak, track.TakeMeterSample());
        return peak;
    }

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

    public static RecordingAudioService Start(
        string tempDir,
        bool systemAudio,
        bool microphone,
        RecordingStopSignal stopSignal,
        RecordingTimeline? timeline = null)
    {
        var service = new RecordingAudioService();
        if (systemAudio && RecordingAudioTrack.TryCreateSystemLoopback(tempDir, stopSignal, timeline) is { } loopback)
        {
            service._tracks.Add(loopback);
        }
        if (microphone && RecordingAudioTrack.TryCreateMicrophone(tempDir, stopSignal, timeline) is { } mic)
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
