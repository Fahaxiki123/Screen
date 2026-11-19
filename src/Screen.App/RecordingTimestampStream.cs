using System.Buffers.Binary;
using System.Text;

namespace Screen.App;

/// <summary>
/// A streaming QuickTime track of uncompressed BGRA or NV12 frames. Unlike rawvideo,
/// each fragment carries the capture clock's decode timestamp and duration.
/// The bundled MOV demuxer supports this format without another dependency.
/// One pending frame supplies the exact duration when the next frame arrives.
/// </summary>
internal sealed class RecordingTimestampStream
{
    internal const uint Timescale = 1_000_000;
    private readonly Stream _output;
    private readonly int _frameBytes;
    private byte[]? _pending;
    private long _pendingTime;
    private uint _sequence;
    private bool _finished;

    public RecordingTimestampStream(Stream output, int width, int height, string pixelFormat = "bgra")
    {
        if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "录制尺寸超出了视频容器支持的范围。");
        }
        if (pixelFormat is not ("bgra" or "nv12")
            || (pixelFormat == "nv12" && ((width & 1) != 0 || (height & 1) != 0)))
        {
            throw new ArgumentException("无效的视频像素格式或尺寸。", nameof(pixelFormat));
        }
        _output = output;
        _frameBytes = pixelFormat == "nv12" ? checked(width * height * 3 / 2) : checked(width * height * 4);
        _output.Write(CreateHeader(width, height, pixelFormat));
    }

    public void WriteFrame(ReadOnlySpan<byte> pixels, double captureSeconds)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (pixels.Length != _frameBytes || !double.IsFinite(captureSeconds) || captureSeconds < 0)
        {
            throw new ArgumentException("无效的录制画面或采集时间。");
        }
        var time = checked((long)Math.Round(captureSeconds * Timescale));
        if (_pending is not null)
        {
            if (time <= _pendingTime)
            {
                throw new ArgumentException("采集时间必须严格递增。");
            }
            FlushPending(time);
        }
        _pending ??= new byte[_frameBytes];
        pixels.CopyTo(_pending);
        _pendingTime = time;
    }

    public void Finish(double endSeconds)
    {
        if (_finished)
        {
            return;
        }
        if (!double.IsFinite(endSeconds) || endSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(endSeconds));
        }
        if (_pending is not null)
        {
            FlushPending(Math.Max(_pendingTime + 1, checked((long)Math.Round(endSeconds * Timescale))));
        }
        _pending = null;
        _finished = true;
    }

    private void FlushPending(long endTime)
    {
        // A single MP4 sample uses a 32-bit duration (over 71 minutes here).
        // Split longer held frames, preserving the complete capture timeline.
        while (_pendingTime < endTime)
        {
            var duration = (uint)Math.Min(uint.MaxValue, endTime - _pendingTime);
            var sequence = ++_sequence;
            byte[] Fragment(int dataOffset) => Box("moof", Join(
                FullBox("mfhd", 0, 0, U32(sequence)),
                Box("traf", Join(
                    FullBox("tfhd", 0, 0x020000, U32(1)),
                    FullBox("tfdt", 1, 0, U64((ulong)_pendingTime)),
                    FullBox("trun", 0, 0x000301, Join(U32(1), U32((uint)dataOffset), U32(duration), U32((uint)_frameBytes)))))));
            var fragment = Fragment(0);
            fragment = Fragment(fragment.Length + 8);
            _output.Write(fragment);
            _output.Write(U32(checked((uint)_frameBytes + 8)));
            _output.Write("mdat"u8);
            _output.Write(_pending!);
            _pendingTime += duration;
        }
    }

    internal static byte[] CreateHeader(int width, int height, string pixelFormat = "bgra")
    {
        var matrix = Join(U32(0x10000), U32(0), U32(0), U32(0), U32(0x10000), U32(0), U32(0), U32(0), U32(0x40000000));
        var mvhd = FullBox("mvhd", 0, 0, Join(new byte[8], U32(Timescale), U32(0), U32(0x10000), U16(0x100), new byte[10], matrix, new byte[24], U32(2)));
        var tkhd = FullBox("tkhd", 0, 3, Join(new byte[8], U32(1), new byte[8], new byte[8], new byte[8], matrix, U32((uint)width << 16), U32((uint)height << 16)));
        var mdhd = FullBox("mdhd", 0, 0, Join(new byte[8], U32(Timescale), U32(0), U16(0x55c4), U16(0)));
        var hdlr = FullBox("hdlr", 0, 0, Join(U32(0), Encoding.ASCII.GetBytes("vide"), new byte[12], Encoding.ASCII.GetBytes("Screen capture\0")));
        var nv12 = pixelFormat == "nv12";
        // NV12 is understood by the bundled MOV demuxer even though FFmpeg's
        // own MOV muxer does not select this fourcc automatically. Keep the GPU,
        // GDI fallback and cursor conversion on the same BT.601 limited matrix.
        var color = nv12 ? Box("colr", Join(Encoding.ASCII.GetBytes("nclc"), U16(1), U16(13), U16(6))) : [];
        var entry = Box(nv12 ? "NV12" : "BGRA", Join(new byte[6], U16(1), new byte[16], U16((ushort)width), U16((ushort)height), U32(72 << 16), U32(72 << 16), U32(0), U16(1), new byte[32], U16(nv12 ? (ushort)12 : (ushort)32), U16(0xffff), color));
        var stbl = Box("stbl", Join(
            FullBox("stsd", 0, 0, Join(U32(1), entry)),
            FullBox("stts", 0, 0, U32(0)), FullBox("stsc", 0, 0, U32(0)),
            FullBox("stsz", 0, 0, new byte[8]), FullBox("stco", 0, 0, U32(0))));
        var dinf = Box("dinf", FullBox("dref", 0, 0, Join(U32(1), FullBox("url ", 0, 1, []))));
        var minf = Box("minf", Join(FullBox("vmhd", 0, 1, new byte[8]), dinf, stbl));
        var trak = Box("trak", Join(tkhd, Box("mdia", Join(mdhd, hdlr, minf))));
        var mvex = Box("mvex", FullBox("trex", 0, 0, Join(U32(1), U32(1), new byte[12])));
        return Join(Box("ftyp", Encoding.ASCII.GetBytes("qt  \0\0\0\0qt  ")), Box("moov", Join(mvhd, trak, mvex)));
    }

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload) =>
        Box(type, Join(U32(((uint)version << 24) | flags), payload));

    private static byte[] Box(string type, byte[] payload) =>
        Join(U32(checked((uint)payload.Length + 8)), Encoding.ASCII.GetBytes(type), payload);

    private static byte[] U16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Join(params byte[][] parts)
    {
        var result = new byte[parts.Sum(part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }
}
