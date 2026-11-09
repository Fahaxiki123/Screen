using System.Runtime.InteropServices;
using Screen.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace Screen.App;

internal sealed class DxgiScreenCaptureService : IScreenCaptureService
{
    private readonly object _gate = new();
    private CaptureSession? _session;

    public Task<CaptureFrame> CaptureAtAsync(PixelPoint screenPoint, CancellationToken cancellationToken = default) =>
        Task.Run(() => Capture(screenPoint, cancellationToken), cancellationToken);

    private CaptureFrame Capture(PixelPoint point, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_session is null || !_session.Contains(point))
            {
                ResetSession();
                _session = CreateSession(point);
            }

            try
            {
                return _session.Capture(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                ResetSession();
                _session = CreateSession(point);
                return _session.Capture(cancellationToken);
            }
        }
    }

    private static CaptureSession CreateSession(PixelPoint point)
    {
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
                        var description = output.Description;
                        var bounds = new PixelRect(
                            description.DesktopCoordinates.Left,
                            description.DesktopCoordinates.Top,
                            description.DesktopCoordinates.Right - description.DesktopCoordinates.Left,
                            description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top);
                        if (point.X >= bounds.X && point.X < bounds.Right
                            && point.Y >= bounds.Y && point.Y < bounds.Bottom)
                        {
                            var session = new CaptureSession(adapter, output, bounds, description.Monitor);
                            keepAdapter = true;
                            keepOutput = true;
                            return session;
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

        throw new InvalidOperationException("No DXGI output contains the requested screen point.");
    }

    private void ResetSession()
    {
        _session?.Dispose();
        _session = null;
    }

    internal void Reset()
    {
        lock (_gate)
        {
            ResetSession();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            ResetSession();
        }
    }

    private sealed class CaptureSession : IDisposable
    {
        private readonly IDXGIAdapter1 _adapter;
        private readonly IDXGIOutput _output;
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutputDuplication _duplication;
        private readonly double _dpiX;
        private readonly double _dpiY;
        private readonly CaptureColorSpace _colorSpace;
        private ID3D11Texture2D? _staging;
        private PixelBuffer? _lastFrame;

        public CaptureSession(IDXGIAdapter1 adapter, IDXGIOutput output, PixelRect bounds, nint monitor)
        {
            _adapter = adapter;
            _output = output;
            Bounds = bounds;
            var featureLevels = new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            };
            ID3D11Device? device = null;
            ID3D11DeviceContext? context = null;
            IDXGIOutputDuplication? duplication = null;
            try
            {
                D3D11CreateDevice(
                    adapter,
                    DriverType.Unknown,
                    DeviceCreationFlags.BgraSupport,
                    featureLevels,
                    out device,
                    out context).CheckError();
                using var output1 = output.QueryInterface<IDXGIOutput1>();
                duplication = output1.DuplicateOutput(device);
                _device = device;
                _context = context;
                _duplication = duplication;
            }
            catch
            {
                duplication?.Dispose();
                context?.Dispose();
                device?.Dispose();
                throw;
            }

            if (NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out var dpiY) == 0)
            {
                _dpiX = dpiX;
                _dpiY = dpiY;
            }
            else
            {
                _dpiX = _dpiY = 96;
            }

            _colorSpace = CaptureColorSpace.Srgb;
            try
            {
                using var output6 = output.QueryInterface<IDXGIOutput6>();
                _colorSpace = output6.Description1.ColorSpace switch
                {
                    ColorSpaceType.RgbFullG2084NoneP2020 => CaptureColorSpace.Hdr10,
                    ColorSpaceType.RgbFullG10NoneP709 => CaptureColorSpace.ScRgb,
                    _ => CaptureColorSpace.Srgb
                };
            }
            catch
            {
                // Virtual and older outputs may not expose advanced color information.
            }
        }

        public PixelRect Bounds { get; }
        public bool Contains(PixelPoint point) =>
            point.X >= Bounds.X && point.X < Bounds.Right && point.Y >= Bounds.Y && point.Y < Bounds.Bottom;

        public CaptureFrame Capture(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquire = _duplication.AcquireNextFrame(120, out _, out var desktopResource);
            if (acquire.Failure)
            {
                if (acquire == Vortice.DXGI.ResultCode.WaitTimeout && _lastFrame is not null)
                {
                    return MakeFrame(_lastFrame);
                }
                acquire.CheckError();
            }

            try
            {
                using (desktopResource)
                using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
                {
                    var description = desktopTexture.Description;
                    if (description.Format is not Format.B8G8R8A8_UNorm
                        and not Format.B8G8R8A8_UNorm_SRgb)
                    {
                        throw new NotSupportedException($"DXGI output format {description.Format} requires tone mapping.");
                    }
                    if (description.Width != Bounds.Width || description.Height != Bounds.Height)
                    {
                        throw new NotSupportedException("Rotated DXGI outputs use the GDI fallback.");
                    }

                    EnsureStaging(description);
                    _context.CopyResource(_staging!, desktopTexture);
                    _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
                    try
                    {
                        var width = checked((int)description.Width);
                        var height = checked((int)description.Height);
                        var stride = checked(width * 4);
                        var pixels = GC.AllocateUninitializedArray<byte>(checked(stride * height));
                        for (var y = 0; y < height; y++)
                        {
                            Marshal.Copy(mapped.DataPointer + y * checked((int)mapped.RowPitch), pixels, y * stride, stride);
                        }
                        _lastFrame = new PixelBuffer(pixels, width, height, stride);
                        return MakeFrame(_lastFrame);
                    }
                    finally
                    {
                        _context.Unmap(_staging!, 0);
                    }
                }
            }
            finally
            {
                _duplication.ReleaseFrame();
            }
        }

        private void EnsureStaging(Texture2DDescription source)
        {
            if (_staging is not null)
            {
                var current = _staging.Description;
                if (current.Width == source.Width && current.Height == source.Height && current.Format == source.Format)
                {
                    return;
                }
                _staging.Dispose();
            }
            _staging = _device.CreateTexture2D(new Texture2DDescription(
                source.Format,
                source.Width,
                source.Height,
                1,
                1,
                BindFlags.None,
                ResourceUsage.Staging,
                CpuAccessFlags.Read,
                1,
                0,
                ResourceOptionFlags.None));
        }

        private CaptureFrame MakeFrame(PixelBuffer pixels) => new(
            pixels.Pixels,
            pixels.Width,
            pixels.Height,
            pixels.Stride,
            _dpiX,
            _dpiY,
            Bounds,
            _colorSpace,
            DateTimeOffset.UtcNow);

        public void Dispose()
        {
            _staging?.Dispose();
            _duplication.Dispose();
            _context.Dispose();
            _device.Dispose();
            _output.Dispose();
            _adapter.Dispose();
        }
    }
}
