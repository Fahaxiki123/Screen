using System.Diagnostics;
using System.Runtime.InteropServices;
using Screen.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace Screen.App;

/// <summary>
/// Screen recording loop: DXGI Desktop Duplication capture on a dedicated
/// thread, GPU crop + BGRA→NV12 conversion via the D3D11 video processor,
/// cursor compositing into the NV12 buffer, and CFR delivery into the
/// <see cref="RecordingPipeline"/>.  Paused ticks drop frames entirely so the
/// output timeline has no frozen segments (see <see cref="PauseClock"/>).
/// </summary>
internal sealed class ScreenRecorder : IDisposable
{
    private const uint CursorShowing = 1;

    private readonly PixelRect _region;
    private readonly int _frameRate;
    private readonly bool _showCursor;
    private readonly string _filePath;
    private readonly RecordingProfile _profile;
    private readonly CancellationTokenSource _cts = new();
    private readonly PauseClock _clock = new();
    private readonly Task _loop;
    private readonly RecordingStopSignal _stopSignal;
    private readonly object _lifecycleGate = new();
    private volatile RecordingPipeline? _pipeline;
    private bool _disposed;
    private long _framesWritten;
    private bool _useNv12 = true;
    private int _frameByteSize;

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

    private byte[]? _lastNv12;
    // Rotating pair of preallocated frame buffers: the capture loop never
    // allocates per frame, and the buffer referenced by _lastNv12 is never
    // the one ConvertFrame is refilling.
    private byte[]? _frameBufferA;
    private byte[]? _frameBufferB;
    private bool _fillBufferB;
    private volatile bool _paused;
    private volatile bool _stopRequested;
    private Exception? _fault;

    public ScreenRecorder(string filePath, PixelRect region, RecordingProfile profile, RecordingStopSignal? stopSignal = null)
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
        _loop = Task.Run(Run);
    }

    public bool IsPaused => _paused;
    public bool IsRunning => !_loop.IsCompleted;
    public Exception? Fault => _fault;

    /// <summary>Recorded duration so far, excluding paused time.</summary>
    public double ElapsedSeconds => _clock.ElapsedSeconds;
    public double EncodedDurationSeconds => Interlocked.Read(ref _framesWritten) / (double)_frameRate;

    public void Pause()
    {
        if (_paused)
        {
            return;
        }
        _paused = true;
        _clock.Pause();
    }

    public void Resume()
    {
        if (!_paused)
        {
            return;
        }
        _paused = false;
        _clock.Resume();
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
        _clock.Pause();
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
                throw new InvalidOperationException("没有捕获到可保存的视频帧。", _fault);
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
            CreateSession();
            CreateProcessorChain();
            _cts.Token.ThrowIfCancellationRequested();
            _pipeline = new RecordingPipeline(
                _filePath,
                _region.Width,
                _region.Height,
                _frameRate,
                _profile.VideoBitrate,
                _useNv12 ? "nv12" : "bgra");
            RunLoop();
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

    private void RunLoop()
    {
        var frameIntervalTicks = Stopwatch.Frequency / (double)_frameRate;
        var stopwatch = Stopwatch.StartNew();
        long frameIndex = 0;

        while (!_cts.IsCancellationRequested && !_stopSignal.IsRequested)
        {
            // CFR pacing: each output frame owns a fixed slot on the wall
            // clock.  Falling behind never bursts — the slot is simply
            // written as soon as possible.
            var targetTicks = (long)((frameIndex + 1) * frameIntervalTicks);
            var waitTicks = targetTicks - stopwatch.ElapsedTicks;
            if (waitTicks > 0)
            {
                var waitMs = (int)(waitTicks * 1000 / Stopwatch.Frequency);
                if (waitMs > 0 && _cts.Token.WaitHandle.WaitOne(Math.Min(waitMs, 200)))
                {
                    break;
                }
                continue;
            }

            frameIndex++;
            if (_paused)
            {
                // Paused ticks produce no output frames at all; the encoder
                // timeline simply advances when Resume() starts writing again.
                continue;
            }

            var fresh = TryAcquireDesktopFrame();
            var frame = fresh ? ConvertFrame() : _lastNv12;
            if (frame is null)
            {
                continue;
            }
            if (_showCursor)
            {
                CompositeCursor(frame);
            }
            if (_stopSignal.IsRequested)
            {
                break;
            }
            _pipeline!.WriteVideoFrame(frame);
            Interlocked.Increment(ref _framesWritten);
            _lastNv12 = frame;
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
    private byte[] ConvertFrame()
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

        // Allocated once per session (sized by the active pixel format) and
        // rotated thereafter; see the field declarations above.
        _frameBufferA ??= new byte[_frameByteSize];
        _frameBufferB ??= new byte[_frameByteSize];
        var frame = _fillBufferB ? _frameBufferB : _frameBufferA;
        _fillBufferB = !_fillBufferB;
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
        return frame;
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
        _frameByteSize = _useNv12
            ? checked(_region.Width * _region.Height * 3 / 2)
            : checked(_region.Width * _region.Height * 4);

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
    // Cursor compositing (CPU, into the NV12 buffer)
    // ------------------------------------------------------------------

    private void CompositeCursor(byte[] frame)
    {
        if (!TryCaptureCursorBitmap(out var pixels, out var width, out var height, out var left, out var top))
        {
            return;
        }
        var regionWidth = _region.Width;
        var regionHeight = _region.Height;
        if (_useNv12)
        {
            CompositeCursorNv12(frame, pixels, width, height, left, top, regionWidth, regionHeight);
        }
        else
        {
            CompositeCursorBgra(frame, pixels, width, height, left, top, regionWidth, regionHeight);
        }
    }

    private static void CompositeCursorBgra(
        byte[] frame, byte[] pixels, int width, int height, int left, int top, int regionWidth, int regionHeight)
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
                var source = (row * width + column) * 4;
                var alpha = pixels[source + 3];
                if (alpha == 0)
                {
                    continue;
                }
                var dest = (py * regionWidth + px) * 4;
                var invAlpha = 255 - alpha;
                frame[dest] = (byte)Math.Min(255, pixels[source] + frame[dest] * invAlpha / 255);
                frame[dest + 1] = (byte)Math.Min(255, pixels[source + 1] + frame[dest + 1] * invAlpha / 255);
                frame[dest + 2] = (byte)Math.Min(255, pixels[source + 2] + frame[dest + 2] * invAlpha / 255);
                frame[dest + 3] = 255;
            }
        }
    }

    private static void CompositeCursorNv12(
        byte[] nv12, byte[] pixels, int width, int height, int left, int top, int regionWidth, int regionHeight)
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
                var source = (row * width + column) * 4;
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

    private bool TryCaptureCursorBitmap(out byte[] pixels, out int width, out int height, out int left, out int top)
    {
        pixels = [];
        width = height = 0;
        left = top = 0;
        var info = new NativeMethods.CursorInfo
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.CursorInfo>()
        };
        if (!NativeMethods.GetCursorInfo(ref info) || info.Cursor == 0 || info.Flags != CursorShowing)
        {
            return false;
        }
        left = info.ScreenPosition.X - _region.X;
        top = info.ScreenPosition.Y - _region.Y;
        width = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCursorWidth));
        height = Math.Max(1, NativeMethods.GetSystemMetrics(NativeMethods.SmCursorHeight));
        if (left + width <= 0 || top + height <= 0 || left >= _region.Width || top >= _region.Height)
        {
            return false;
        }

        var screenDc = NativeMethods.GetDC(0);
        if (screenDc == 0)
        {
            return false;
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
                return false;
            }
            previousObject = NativeMethods.SelectObject(memDc, dib);
            unsafe
            {
                new Span<byte>((void*)bits, width * height * 4).Clear();
            }
            if (!NativeMethods.DrawIconEx(memDc, 0, 0, info.Cursor, width, height, 0, 0, NativeMethods.DiNormal))
            {
                return false;
            }
            pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return true;
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
