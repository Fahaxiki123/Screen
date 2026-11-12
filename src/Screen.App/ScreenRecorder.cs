using System.Diagnostics;
using System.Runtime.InteropServices;
using Screen.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace Screen.App;

internal interface IRecordingCapture : IDisposable
{
    bool IsPaused { get; }
    Exception? Fault { get; }
    double ElapsedSeconds { get; }
    double EncodedDurationSeconds { get; }
    void Pause();
    void Resume();
    void RequestStop();
    void StopAndFinalize();
}

/// <summary>
/// Screen recording loop: DXGI Desktop Duplication capture on a dedicated
/// thread, GPU crop + BGRA→NV12 conversion via the D3D11 video processor,
/// cursor compositing into the NV12 buffer, and CFR delivery into the
/// <see cref="RecordingPipeline"/>.  Paused ticks drop frames entirely so the
/// output timeline has no frozen segments (see <see cref="PauseClock"/>).
/// </summary>
internal sealed class ScreenRecorder : IRecordingCapture
{
    private const uint CursorShowing = 1;

    private readonly PixelRect _region;
    private readonly int _frameRate;
    private readonly bool _showCursor;
    private readonly string _filePath;
    private readonly RecordingProfile _profile;
    private readonly CancellationTokenSource _cts = new();
    private readonly RecordingTimeline _timeline;
    private readonly Task _loop;
    private readonly RecordingStopSignal _stopSignal;
    private readonly object _lifecycleGate = new();
    private volatile RecordingPipeline? _pipeline;
    private bool _disposed;
    private long _framesWritten;
    private bool _useNv12 = true;

    // GPU resources owned by the loop thread; created in Run, disposed there.
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput? _output;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _desktopStaging;
    private ID3D11Texture2D? _bgraTexture;
    private ID3D11Texture2D? _nv12Texture;
    private ID3D11Texture2D? _nv12Staging;
    private ID3D11VideoProcessorEnumerator? _enumerator;
    private ID3D11VideoProcessor? _processor;
    private ID3D11VideoProcessorOutputView? _outputView;
    private ID3D11RenderTargetView? _renderTarget;
    private ID3D11VideoDevice? _videoDevice;
    private ID3D11VideoContext? _videoContext;

    private FrameBuffer? _frames;
    private volatile bool _paused;
    private volatile bool _stopRequested;
    private volatile Exception? _fault;

    public ScreenRecorder(
        string filePath,
        PixelRect region,
        RecordingProfile profile,
        RecordingStopSignal? stopSignal = null,
        RecordingTimeline? timeline = null)
    {
        if (region.Width % 2 != 0 || region.Height % 2 != 0)
        {
            throw new ArgumentException("录制区域的宽和高都必须是偶数。");
        }
        _region = region;
        _frameRate = Math.Clamp(profile.FrameRate, 1, 60);
        _showCursor = profile.ShowCursor;
        _filePath = filePath;
        _profile = profile;
        _stopSignal = stopSignal ?? new RecordingStopSignal();
        _timeline = timeline ?? new RecordingTimeline();
        _loop = Task.Run(Run);
    }

    public bool IsPaused => _paused;
    public bool IsRunning => !_loop.IsCompleted;
    public Exception? Fault => _fault;

    /// <summary>Recorded duration so far, excluding paused time.</summary>
    public double ElapsedSeconds => _timeline.ElapsedSeconds;
    public double EncodedDurationSeconds => Interlocked.Read(ref _framesWritten) / (double)_frameRate;

    public void Pause()
    {
        if (_paused)
        {
            return;
        }
        _paused = true;
        _timeline.Pause();
    }

    public void Resume()
    {
        if (!_paused)
        {
            return;
        }
        _paused = false;
        _timeline.Resume();
    }

    /// <summary>
    /// Stops the capture loop, waits for it to exit completely, and only
    /// then finalizes the encoded file — the loop must never race the
    /// container teardown (the project's long-screenshot stitch race taught
    /// this ordering the hard way).
    /// </summary>
    public void RequestStop()
    {
        _stopSignal.Request();
        if (_stopRequested)
        {
            return;
        }
        _stopRequested = true;
        _timeline.Pause();
        _cts.Cancel();
    }

    public void StopAndFinalize()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequestStop();
            RecordingStopCoordinator.JoinAndFinalize(
                _loop,
                () => _pipeline?.Abort(),
                () => _pipeline?.FinalizeFile(),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(5));
            if (_pipeline is null || EncodedDurationSeconds <= 0)
            {
                throw new InvalidOperationException(_fault?.Message ?? "没有捕获到可保存的视频帧。", _fault);
            }
        }
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            RequestStop();
            _pipeline?.Abort();
            if (RecordingStopCoordinator.WaitForCompletion(_loop, TimeSpan.FromSeconds(5)))
            {
                ReleaseEncoder();
            }
            else
            {
                // A wedged GPU/pipe worker still owns these objects. Do not
                // close its stream/CTS concurrently; release them after exit.
                _ = _loop.ContinueWith(_ => ReleaseEncoder(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }

    private void ReleaseEncoder()
    {
        _pipeline?.Dispose();
        _cts.Dispose();
    }

    private void Run()
    {
        try
        {
            var monitors = DesktopRegionCapture.GetMonitorBounds();
            using var desktop = DesktopRegionCapture.FitsOnOneDisplay(_region, monitors)
                ? null
                : new DesktopRegionCapture(_region, monitors);
            if (desktop is null)
            {
                CreateSession();
                CreateProcessorChain();
            }
            else
            {
                _useNv12 = false;
            }
            _cts.Token.ThrowIfCancellationRequested();
            _pipeline = new RecordingPipeline(
                _filePath,
                _region.Width,
                _region.Height,
                _frameRate,
                _profile.VideoBitrate,
                _useNv12 ? "nv12" : "bgra");
            RunLoop(desktop);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _fault = exception;
        }
        finally
        {
            DisposeGpu();
        }
    }

    private void RunLoop(DesktopRegionCapture? desktop)
    {
        var frameIntervalTicks = Stopwatch.Frequency / (double)_frameRate;
        var maxCatchUpDuplicates = Math.Max(1, _frameRate / 4);
        long slotDebt = 0;
        _timeline.Start();

        while (!_cts.IsCancellationRequested && !_stopSignal.IsRequested)
        {
            if (_paused)
            {
                if (_cts.Token.WaitHandle.WaitOne(25))
                {
                    break;
                }
                continue;
            }

            var nextSlotIndex = Interlocked.Read(ref _framesWritten) + slotDebt;
            var targetSeconds = nextSlotIndex / (double)_frameRate;
            var waitSeconds = targetSeconds - _timeline.ElapsedSeconds;
            if (waitSeconds > 0)
            {
                var waitMs = (int)(waitSeconds * 1000);
                if (waitMs > 0 && _cts.Token.WaitHandle.WaitOne(Math.Min(waitMs, 200)))
                {
                    break;
                }
                continue;
            }

            if (desktop is not null || TryAcquireDesktopFrame())
            {
                _frames ??= new FrameBuffer(_region.Width, _region.Height, _useNv12);
                if (desktop is null)
                {
                    ConvertFrame(_frames.Desktop);
                }
                else
                {
                    desktop.ReadInto(_frames.Desktop);
                }
            }
            if (_frames is null)
            {
                if (_cts.Token.WaitHandle.WaitOne(4))
                {
                    break;
                }
                continue;
            }
            var cursor = _showCursor ? CaptureCursorBitmap() : null;
            var frame = _frames.GetEncodingFrame(cursor);
            if (_stopSignal.IsRequested)
            {
                break;
            }

            var writeStart = Stopwatch.GetTimestamp();
            _pipeline!.WriteVideoFrame(frame);
            var writeTicks = Stopwatch.GetTimestamp() - writeStart;
            var written = Interlocked.Increment(ref _framesWritten);

            // Keep CFR frame count aligned with unpaused recording time across
            // capture/scheduling jitter, while avoiding extra writes when the
            // encoder pipe itself is already the bottleneck.
            var targetFrames = (long)Math.Floor(_timeline.ElapsedSeconds * _frameRate) + 1 - slotDebt;
            if (written < targetFrames)
            {
                if (writeTicks < frameIntervalTicks * 0.8)
                {
                    var duplicates = (int)Math.Min(targetFrames - written, maxCatchUpDuplicates);
                    for (var i = 0; i < duplicates && !_stopSignal.IsRequested && !_cts.IsCancellationRequested && !_paused; i++)
                    {
                        var dupStart = Stopwatch.GetTimestamp();
                        _pipeline.WriteVideoFrame(frame);
                        written = Interlocked.Increment(ref _framesWritten);
                        if (Stopwatch.GetTimestamp() - dupStart >= frameIntervalTicks * 0.8)
                        {
                            break;
                        }
                    }
                }
                if (written < targetFrames)
                {
                    slotDebt += targetFrames - written;
                }
            }
        }
    }

    private bool TryAcquireDesktopFrame()
    {
        var acquire = _duplication!.AcquireNextFrame(8, out _, out var desktopResource);
        if (acquire == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            return false;
        }
        acquire.CheckError();
        try
        {
            using (desktopResource)
            using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
            {
                var description = desktopTexture.Description;
                if (description.Format is not Format.B8G8R8A8_UNorm and not Format.B8G8R8A8_UNorm_SRgb)
                {
                    throw new NotSupportedException("当前显示输出不是 sRGB，暂不支持录屏。");
                }
                EnsureDesktopStaging(description);
                _context!.CopyResource(_desktopStaging!, desktopTexture);
                _context.CopyResource(_bgraTexture!, _desktopStaging!);
            }
        }
        finally
        {
            _duplication.ReleaseFrame();
        }
        return true;
    }

    /// <summary>GPU crop (source rect) + BGRA→NV12 in one video-processor pass, then read back.</summary>
    private void ConvertFrame(byte[] frame)
    {
        using var inputView = _videoDevice!.CreateVideoProcessorInputView(
            _bgraTexture!,
            _enumerator!,
            new VideoProcessorInputViewDescription
            {
                ViewDimension = VideoProcessorInputViewDimension.Texture2D
            });
        var streams = new[]
        {
            new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputSurface = inputView
            }
        };
        _videoContext!.VideoProcessorBlt(_processor!, _outputView!, 0, streams);

        _context!.CopyResource(_nv12Staging!, _nv12Texture!);
        _context!.Map(_nv12Staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            var width = _region.Width;
            var height = _region.Height;
            var rowPitch = checked((int)mapped.RowPitch);
            if (_useNv12)
            {
                // Y plane: one byte per pixel.
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(mapped.DataPointer + y * rowPitch, frame, y * width, width);
                }
                // Interleaved UV plane: half height, one UV pair per two pixels.
                var uvDest = height * width;
                for (var y = 0; y < height / 2; y++)
                {
                    Marshal.Copy(mapped.DataPointer + (height + y) * rowPitch, frame, uvDest + y * width, width);
                }
            }
            else
            {
                var rowBytes = width * 4;
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(mapped.DataPointer + y * rowPitch, frame, y * rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            _context.Unmap(_nv12Staging!, 0);
        }
    }

    // ------------------------------------------------------------------
    // Session / GPU setup
    // ------------------------------------------------------------------

    private void CreateSession()
    {
        var centerX = _region.X + _region.Width / 2;
        var centerY = _region.Y + _region.Height / 2;
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            if (factory.EnumAdapters1(adapterIndex, out var adapter).Failure)
            {
                break;
            }
            var keepAdapter = false;
            try
            {
                for (uint outputIndex = 0; ; outputIndex++)
                {
                    if (adapter.EnumOutputs(outputIndex, out var output).Failure)
                    {
                        break;
                    }
                    var keepOutput = false;
                    try
                    {
                        var bounds = OutputBounds(output);
                        if (centerX >= bounds.X && centerX < bounds.Right
                            && centerY >= bounds.Y && centerY < bounds.Bottom)
                        {
                            if (_region.X < bounds.X || _region.Right > bounds.Right
                                || _region.Y < bounds.Y || _region.Bottom > bounds.Bottom)
                            {
                                throw new NotSupportedException("录制区域超出了单个显示器的范围。");
                            }
                            RejectHighDynamicRange(output);
                            CreateDeviceAndDuplication(adapter, output, bounds);
                            keepAdapter = true;
                            keepOutput = true;
                            return;
                        }
                    }
                    finally
                    {
                        if (!keepOutput)
                        {
                            output.Dispose();
                        }
                    }
                }
            }
            finally
            {
                if (!keepAdapter)
                {
                    adapter.Dispose();
                }
            }
        }
        throw new NotSupportedException("未找到包含录制区域的显示输出。");
    }

    private static PixelRect OutputBounds(IDXGIOutput output)
    {
        var description = output.Description;
        return new PixelRect(
            description.DesktopCoordinates.Left,
            description.DesktopCoordinates.Top,
            description.DesktopCoordinates.Right - description.DesktopCoordinates.Left,
            description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top);
    }

    private static void RejectHighDynamicRange(IDXGIOutput output)
    {
        try
        {
            using var output6 = output.QueryInterface<IDXGIOutput6>();
            if (output6.Description1.ColorSpace is ColorSpaceType.RgbFullG2084NoneP2020 or ColorSpaceType.RgbFullG10NoneP709)
            {
                throw new NotSupportedException("该显示器开启了 HDR，录屏暂不支持，请切换为 SDR 后重试。");
            }
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch
        {
            // Virtual outputs may not expose color space info; assume sRGB.
        }
    }

    private void CreateDeviceAndDuplication(IDXGIAdapter1 adapter, IDXGIOutput output, PixelRect outputBounds)
    {
        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0
        };
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            featureLevels,
            out device,
            out context).CheckError();
        try
        {
            using var output1 = output.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(device);
        }
        catch
        {
            context.Dispose();
            device.Dispose();
            throw new NotSupportedException("桌面复制初始化失败（可能已有其他程序正在独占捕获该显示器）。");
        }
        _adapter = adapter;
        _output = output;
        _device = device;
        _context = context;

        _bgraTexture = device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)outputBounds.Width,
            (uint)outputBounds.Height,
            1, 1,
            BindFlags.RenderTarget,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1, 0,
            ResourceOptionFlags.None));
    }

    private bool TryCreateOutputChain(Format format)
    {
        try
        {
            _nv12Texture = _device!.CreateTexture2D(new Texture2DDescription(
                format,
                (uint)_region.Width,
                (uint)_region.Height,
                1, 1,
                BindFlags.RenderTarget,
                ResourceUsage.Default,
                CpuAccessFlags.None,
                1, 0,
                ResourceOptionFlags.None));
            _nv12Staging = _device.CreateTexture2D(new Texture2DDescription(
                format,
                (uint)_region.Width,
                (uint)_region.Height,
                1, 1,
                BindFlags.None,
                ResourceUsage.Staging,
                CpuAccessFlags.Read,
                1, 0,
                ResourceOptionFlags.None));
            // Planar formats need an explicit RTV description; the default
            // (null-desc) path only works for non-planar formats.
            _renderTarget = format == Format.NV12
                ? _device.CreateRenderTargetView(
                    _nv12Texture,
                    new RenderTargetViewDescription
                    {
                        ViewDimension = RenderTargetViewDimension.Texture2D,
                        Format = Format.NV12,
                        Texture2D = new Texture2DRenderTargetView { MipSlice = 0 }
                    })
                : _device.CreateRenderTargetView(_nv12Texture);
            return true;
        }
        catch
        {
            DisposeOutputChain();
            return false;
        }
    }

    private void DisposeOutputChain()
    {
        _renderTarget?.Dispose();
        _renderTarget = null;
        _nv12Staging?.Dispose();
        _nv12Staging = null;
        _nv12Texture?.Dispose();
        _nv12Texture = null;
    }

    private void EnsureDesktopStaging(Texture2DDescription source)
    {
        if (_desktopStaging is not null)
        {
            var current = _desktopStaging.Description;
            if (current.Width == source.Width && current.Height == source.Height && current.Format == source.Format)
            {
                return;
            }
            _desktopStaging.Dispose();
        }
        _desktopStaging = _device!.CreateTexture2D(new Texture2DDescription(
            source.Format,
            source.Width,
            source.Height,
            1, 1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            1, 0,
            ResourceOptionFlags.None));
    }

    private void CreateProcessorChain()
    {
        _videoDevice = _device!.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context!.QueryInterface<ID3D11VideoContext>();

        var enumerator = _videoDevice.CreateVideoProcessorEnumerator(new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Vortice.DXGI.Rational((uint)_frameRate, 1),
            InputWidth = (uint)_bgraTexture!.Description.Width,
            InputHeight = (uint)_bgraTexture.Description.Height,
            OutputFrameRate = new Vortice.DXGI.Rational((uint)_frameRate, 1),
            OutputWidth = (uint)_region.Width,
            OutputHeight = (uint)_region.Height,
            Usage = VideoUsage.PlaybackNormal
        });
        _enumerator = enumerator;

        // Prefer GPU-side NV12 output.  Some drivers advertise NV12 support
        // yet refuse NV12 render targets in practice, so probe by actually
        // creating the texture + RTV; on failure fall back to BGRA output
        // and let ffmpeg do the color conversion (slower but universal).
        if (enumerator.CheckVideoProcessorFormat(Format.NV12).HasFlag(VideoProcessorFormatSupport.Output)
            && TryCreateOutputChain(Format.NV12))
        {
            _useNv12 = true;
        }
        else
        {
            DisposeOutputChain();
            if (!TryCreateOutputChain(Format.B8G8R8A8_UNorm))
            {
                throw new NotSupportedException("无法创建视频处理器输出纹理。");
            }
            _useNv12 = false;
        }
        _processor = _videoDevice.CreateVideoProcessor(enumerator, 0);
        _outputView = _videoDevice.CreateVideoProcessorOutputView(
            _nv12Texture!,
            enumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D
            });

        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamAutoProcessingMode(_processor, 0, false);
        // Crop happens here: the source rect is the recording region in
        // desktop coordinates; the dest rect is null = the full (region-sized)
        // output, so no scaling occurs.
        var outputBounds = OutputBounds(_output!);
        _videoContext.VideoProcessorSetStreamSourceRect(
            _processor,
            0,
            true,
            new Vortice.RawRect(
                _region.X - outputBounds.X,
                _region.Y - outputBounds.Y,
                _region.Right - outputBounds.X,
                _region.Bottom - outputBounds.Y));
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, false, null);
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, false, null);
        _videoContext.VideoProcessorSetStreamColorSpace(
            _processor,
            0,
            new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0, Nominal_Range = 1 });
        _videoContext.VideoProcessorSetOutputColorSpace(
            _processor,
            _useNv12
                ? new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 1, YCbCr_xvYCC = 0, Nominal_Range = 0 }
                : new VideoProcessorColorSpace { Usage = 0, RGB_Range = 0, YCbCr_Matrix = 0, YCbCr_xvYCC = 0, Nominal_Range = 0 });
    }

    // ------------------------------------------------------------------
    // Cursor compositing (CPU, into the encoding buffer)
    // ------------------------------------------------------------------

    internal readonly record struct CursorBitmap(byte[] Pixels, PixelRect Bounds, bool[]? InvertMask = null);

    internal sealed class FrameBuffer(int width, int height, bool useNv12)
    {
        public byte[] Desktop { get; } = new byte[useNv12 ? checked(width * height * 3 / 2) : checked(width * height * 4)];
        private byte[]? _withCursor;

        // The encoder writes synchronously. It can reuse the same output buffer
        // on each tick while the clean desktop remains available after timeouts.
        public byte[] GetEncodingFrame(CursorBitmap? cursor)
        {
            if (cursor is not { } bitmap)
            {
                return Desktop;
            }
            _withCursor ??= new byte[Desktop.Length];
            Desktop.AsSpan().CopyTo(_withCursor);
            var bounds = bitmap.Bounds;
            if (useNv12)
            {
                CompositeCursorNv12(_withCursor, bitmap.Pixels, bitmap.InvertMask, bounds.Width, bounds.Height, bounds.X, bounds.Y, width, height);
            }
            else
            {
                CompositeCursorBgra(_withCursor, bitmap.Pixels, bitmap.InvertMask, bounds.Width, bounds.Height, bounds.X, bounds.Y, width, height);
            }
            return _withCursor;
        }
    }

    private static void CompositeCursorBgra(
        byte[] frame,
        byte[] pixels,
        bool[]? invertMask,
        int width,
        int height,
        int left,
        int top,
        int regionWidth,
        int regionHeight)
    {
        for (var row = 0; row < height; row++)
        {
            var py = top + row;
            if ((uint)py >= (uint)regionHeight)
            {
                continue;
            }
            for (var column = 0; column < width; column++)
            {
                var px = left + column;
                if ((uint)px >= (uint)regionWidth)
                {
                    continue;
                }
                var pixelIndex = row * width + column;
                var dest = (py * regionWidth + px) * 4;
                if (invertMask is not null && invertMask[pixelIndex])
                {
                    frame[dest] = (byte)(255 - frame[dest]);
                    frame[dest + 1] = (byte)(255 - frame[dest + 1]);
                    frame[dest + 2] = (byte)(255 - frame[dest + 2]);
                    frame[dest + 3] = 255;
                    continue;
                }
                var source = pixelIndex * 4;
                var alpha = pixels[source + 3];
                if (alpha == 0)
                {
                    continue;
                }
                var invAlpha = 255 - alpha;
                frame[dest] = (byte)Math.Min(255, pixels[source] + frame[dest] * invAlpha / 255);
                frame[dest + 1] = (byte)Math.Min(255, pixels[source + 1] + frame[dest + 1] * invAlpha / 255);
                frame[dest + 2] = (byte)Math.Min(255, pixels[source + 2] + frame[dest + 2] * invAlpha / 255);
                frame[dest + 3] = 255;
            }
        }
    }

    private static void CompositeCursorNv12(
        byte[] nv12,
        byte[] pixels,
        bool[]? invertMask,
        int width,
        int height,
        int left,
        int top,
        int regionWidth,
        int regionHeight)
    {
        for (var row = 0; row < height; row++)
        {
            var py = top + row;
            if ((uint)py >= (uint)regionHeight)
            {
                continue;
            }
            for (var column = 0; column < width; column++)
            {
                var px = left + column;
                if ((uint)px >= (uint)regionWidth)
                {
                    continue;
                }
                var pixelIndex = row * width + column;
                if (invertMask is not null && invertMask[pixelIndex])
                {
                    var invYIndex = py * regionWidth + px;
                    var invUvIndex = regionWidth * regionHeight + (py / 2) * regionWidth + (px & ~1);
                    var (bgR, bgG, bgB) = YuvToRgb(nv12[invYIndex], nv12[invUvIndex], nv12[invUvIndex + 1]);
                    var invR = (byte)(255 - bgR);
                    var invG = (byte)(255 - bgG);
                    var invB = (byte)(255 - bgB);
                    nv12[invYIndex] = RgbToY(invR, invG, invB);
                    nv12[invUvIndex] = RgbToU(invR, invG, invB);
                    nv12[invUvIndex + 1] = RgbToV(invR, invG, invB);
                    continue;
                }
                var source = pixelIndex * 4;
                var alpha = pixels[source + 3];
                if (alpha == 0)
                {
                    continue;
                }
                // DrawIconEx emits premultiplied BGRA; blend over the frame
                // color before converting to YUV.
                var invAlpha = 255 - alpha;
                var yIndex = py * regionWidth + px;
                var uvIndex = regionWidth * regionHeight + (py / 2) * regionWidth + (px & ~1);
                var (backR, backG, backB) = YuvToRgb(nv12[yIndex], nv12[uvIndex], nv12[uvIndex + 1]);
                var b = (byte)Math.Min(255, pixels[source] + backB * invAlpha / 255);
                var g = (byte)Math.Min(255, pixels[source + 1] + backG * invAlpha / 255);
                var r = (byte)Math.Min(255, pixels[source + 2] + backR * invAlpha / 255);
                nv12[yIndex] = RgbToY(r, g, b);
                nv12[uvIndex] = RgbToU(r, g, b);
                nv12[uvIndex + 1] = RgbToV(r, g, b);
            }
        }
    }

    private CursorBitmap? CaptureCursorBitmap()
    {
        var info = new NativeMethods.CursorInfo
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.CursorInfo>()
        };
        if (!NativeMethods.GetCursorInfo(ref info) || info.Cursor == 0 || info.Flags != CursorShowing)
        {
            return null;
        }
        return RenderCursorHandle(
            info.Cursor,
            new PixelPoint(info.ScreenPosition.X, info.ScreenPosition.Y),
            _region);
    }

    internal static CursorBitmap? RenderCursorHandle(nint cursorHandle, PixelPoint screenPosition, PixelRect region)
    {
        if (cursorHandle == 0)
        {
            return null;
        }
        GetCursorDimensionsAndHotspot(cursorHandle, out var width, out var height, out var hotspotX, out var hotspotY);
        var left = (int)Math.Round(screenPosition.X) - hotspotX - region.X;
        var top = (int)Math.Round(screenPosition.Y) - hotspotY - region.Y;
        if (left + width <= 0 || top + height <= 0 || left >= region.Width || top >= region.Height)
        {
            return null;
        }

        var screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0)
        {
            return null;
        }
        var memDc = NativeMethods.CreateCompatibleDC(screenDc);
        nint dib = 0;
        nint previousObject = 0;
        try
        {
            var header = new NativeMethods.BitmapInfoHeader
            {
                BiSize = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                BiWidth = width,
                BiHeight = -height, // top-down
                BiPlanes = 1,
                BiBitCount = 32,
                BiCompression = 0
            };
            dib = NativeMethods.CreateDIBSection(memDc, ref header, 0, out var bits, 0, 0);
            if (dib == 0 || bits == 0)
            {
                return null;
            }
            previousObject = NativeMethods.SelectObject(memDc, dib);
            var byteLength = checked(width * height * 4);
            unsafe
            {
                new Span<byte>((void*)bits, byteLength).Clear();
            }
            if (!NativeMethods.DrawIconEx(memDc, 0, 0, cursorHandle, width, height, 0, 0, NativeMethods.DiNormal))
            {
                return null;
            }
            var onBlack = new byte[byteLength];
            Marshal.Copy(bits, onBlack, 0, byteLength);
            if (HasNonZeroAlpha(onBlack))
            {
                return new CursorBitmap(onBlack, new PixelRect(left, top, width, height));
            }

            // Monochrome / XOR cursors (IDC_IBEAM, IDC_CROSS) and legacy masked
            // cursors leave alpha = 0 across the entire DIB. Render a second pass
            // over opaque white (0x00FFFFFF) so transparent, opaque, and screen-
            // inverting pixels are separated cleanly without opaque bounding boxes.
            unsafe
            {
                var span = new Span<uint>((void*)bits, width * height);
                span.Fill(0x00FFFFFFu);
            }
            if (!NativeMethods.DrawIconEx(memDc, 0, 0, cursorHandle, width, height, 0, 0, NativeMethods.DiNormal))
            {
                return null;
            }
            var onWhite = new byte[byteLength];
            Marshal.Copy(bits, onWhite, 0, byteLength);
            var (decodedPixels, invertMask, hasVisible) = DecodeMaskRenderedCursor(onBlack, onWhite);
            return hasVisible
                ? new CursorBitmap(decodedPixels, new PixelRect(left, top, width, height), invertMask)
                : null;
        }
        finally
        {
            if (previousObject != 0)
            {
                NativeMethods.SelectObject(memDc, previousObject);
            }
            if (dib != 0)
            {
                NativeMethods.DeleteObject(dib);
            }
            NativeMethods.DeleteDC(memDc);
            NativeMethods.ReleaseDC(0, screenDc);
        }
    }

    private static void GetCursorDimensionsAndHotspot(
        nint cursorHandle,
        out int width,
        out int height,
        out int hotspotX,
        out int hotspotY)
    {
        width = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCursorWidth));
        height = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCursorHeight));
        hotspotX = 0;
        hotspotY = 0;

        if (!NativeMethods.GetIconInfo(cursorHandle, out var iconInfo))
        {
            return;
        }
        try
        {
            hotspotX = (int)iconInfo.XHotspot;
            hotspotY = (int)iconInfo.YHotspot;
            var bitmapHandle = iconInfo.ColorBitmap != 0 ? iconInfo.ColorBitmap : iconInfo.MaskBitmap;
            if (bitmapHandle != 0
                && NativeMethods.GetBitmapObject(
                    bitmapHandle,
                    Marshal.SizeOf<NativeMethods.NativeBitmap>(),
                    out var bitmap) > 0
                && bitmap.BmWidth > 0
                && bitmap.BmHeight > 0)
            {
                width = bitmap.BmWidth;
                // Monochrome cursor masks stack the AND and XOR planes vertically.
                height = iconInfo.ColorBitmap == 0
                    ? Math.Max(1, bitmap.BmHeight / 2)
                    : bitmap.BmHeight;
            }
        }
        finally
        {
            if (iconInfo.MaskBitmap != 0)
            {
                NativeMethods.DeleteObject(iconInfo.MaskBitmap);
            }
            if (iconInfo.ColorBitmap != 0)
            {
                NativeMethods.DeleteObject(iconInfo.ColorBitmap);
            }
        }
    }

    private static bool HasNonZeroAlpha(ReadOnlySpan<byte> bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                return true;
            }
        }
        return false;
    }

    internal static (byte[] Pixels, bool[]? InvertMask, bool HasVisiblePixels) DecodeMaskRenderedCursor(
        ReadOnlySpan<byte> onBlack,
        ReadOnlySpan<byte> onWhite)
    {
        var pixelCount = onBlack.Length / 4;
        var pixels = new byte[onBlack.Length];
        bool[]? invertMask = null;
        var hasVisible = false;

        for (var index = 0; index < pixelCount; index++)
        {
            var offset = index * 4;
            var bB = onBlack[offset];
            var gB = onBlack[offset + 1];
            var rB = onBlack[offset + 2];
            var bW = onWhite[offset];
            var gW = onWhite[offset + 1];
            var rW = onWhite[offset + 2];

            // Unchanged over both black and white -> AND=1, XOR=0 (transparent).
            if (bB == 0 && gB == 0 && rB == 0 && bW == 255 && gW == 255 && rW == 255)
            {
                continue;
            }

            // Flipped over both black and white -> AND=1, XOR=1 (screen invert).
            if (bB == 255 && gB == 255 && rB == 255 && bW == 0 && gW == 0 && rW == 0)
            {
                invertMask ??= new bool[pixelCount];
                invertMask[index] = true;
                hasVisible = true;
                continue;
            }

            pixels[offset] = bB;
            pixels[offset + 1] = gB;
            pixels[offset + 2] = rB;
            pixels[offset + 3] = 255;
            hasVisible = true;
        }

        return (pixels, invertMask, hasVisible);
    }

    private static byte RgbToY(byte r, byte g, byte b) =>
        (byte)Math.Clamp(((66 * r + 129 * g + 25 * b + 128) >> 8) + 16, 16, 235);

    private static byte RgbToU(byte r, byte g, byte b) =>
        (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 16, 240);

    private static byte RgbToV(byte r, byte g, byte b) =>
        (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 16, 240);

    private static (byte R, byte G, byte B) YuvToRgb(byte y, byte u, byte v)
    {
        var c = y - 16;
        var d = u - 128;
        var e = v - 128;
        var r = (298 * c + 409 * e + 128) >> 8;
        var g = (298 * c - 100 * d - 208 * e + 128) >> 8;
        var b = (298 * c + 516 * d + 128) >> 8;
        return (
            (byte)Math.Clamp(r, 0, 255),
            (byte)Math.Clamp(g, 0, 255),
            (byte)Math.Clamp(b, 0, 255));
    }

    private void DisposeGpu()
    {
        _outputView?.Dispose();
        _renderTarget?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext?.Dispose();
        _videoDevice?.Dispose();
        _nv12Staging?.Dispose();
        _nv12Texture?.Dispose();
        _bgraTexture?.Dispose();
        _desktopStaging?.Dispose();
        _duplication?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _output?.Dispose();
        _adapter?.Dispose();
    }
}
