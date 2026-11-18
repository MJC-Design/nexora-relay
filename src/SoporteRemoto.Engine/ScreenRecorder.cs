using System.Buffers.Binary;
using System.Threading.Channels;

namespace SoporteRemoto.Engine;

/// <summary>
/// Graba la pantalla remota recibida (fotogramas JPEG) en un AVI Motion JPEG
/// sin dependencias externas, de modo que la grabación siempre esté disponible.
/// </summary>
public sealed class ScreenRecorder : IAsyncDisposable
{
    private const int Fps = 12;
    private const int MaxQueuedFrames = 240;

    private readonly string _path;
    private readonly FileStream _stream;
    private readonly Channel<byte[]> _frames;
    private readonly Task _writer;
    private readonly List<(uint Offset, uint Size)> _index = new();

    private long _hdrlPos;
    private long _strlPos;
    private long _moviPos;
    private long _indexBase;
    private long _totalFramesPos;
    private long _lengthPos;
    private int _width;
    private int _height;
    private int _frameCount;
    private bool _headerWritten;
    private bool _finished;
    private Exception? _error;

    private ScreenRecorder(string path)
    {
        _path = path;
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(MaxQueuedFrames)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    public static ScreenRecorder Start(string path) => new(path);

    public string Path => _path;
    public int FrameCount => _frameCount;

    public void AddFrame(byte[] jpeg)
    {
        if (_finished || jpeg.Length < 4) return;
        _frames.Writer.TryWrite(jpeg);
    }

    public async Task<int> StopAsync()
    {
        if (_finished) return _frameCount;
        _finished = true;
        _frames.Writer.TryComplete();

        try { await _writer; } catch (Exception ex) { _error ??= ex; }

        try { _stream.Flush(); } catch { }
        try { _stream.Dispose(); } catch { }

        if (_error is not null || _frameCount == 0)
        {
            try { File.Delete(_path); } catch { }
            return 0;
        }

        return _frameCount;
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync())
                WriteFrame(frame);

            if (_headerWritten)
                FinishFile();
        }
        catch (Exception ex)
        {
            _error = ex;
        }
    }

    private void WriteFrame(byte[] jpeg)
    {
        var (width, height) = ReadJpegSize(jpeg);
        if (width <= 0 || height <= 0) return;

        if (!_headerWritten)
        {
            _width = width;
            _height = height;
            WriteHeader();
            _headerWritten = true;
        }

        if (width != _width || height != _height) return;

        var chunk = _stream.Position;
        WriteFourCc("00dc");
        WriteUInt32((uint)jpeg.Length);
        _stream.Write(jpeg, 0, jpeg.Length);
        if ((jpeg.Length & 1) == 1) _stream.WriteByte(0);

        _index.Add(((uint)(chunk - _indexBase), (uint)jpeg.Length));
        _frameCount++;
    }

    private void WriteHeader()
    {
        WriteFourCc("RIFF");
        WriteUInt32(0);
        WriteFourCc("AVI ");

        _hdrlPos = _stream.Position;
        WriteFourCc("LIST");
        WriteUInt32(0);
        WriteFourCc("hdrl");

        WriteFourCc("avih");
        WriteUInt32(56);
        WriteUInt32((uint)(1_000_000 / Fps));
        WriteUInt32((uint)Math.Min(int.MaxValue, (long)_width * _height * 3L * Fps));
        WriteUInt32(0);
        WriteUInt32(0x10);
        _totalFramesPos = _stream.Position;
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(1);
        WriteUInt32((uint)Math.Min(int.MaxValue, (long)_width * _height));
        WriteUInt32((uint)_width);
        WriteUInt32((uint)_height);
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(0);

        _strlPos = _stream.Position;
        WriteFourCc("LIST");
        WriteUInt32(0);
        WriteFourCc("strl");

        WriteFourCc("strh");
        WriteUInt32(56);
        WriteFourCc("vids");
        WriteFourCc("MJPG");
        WriteUInt32(0);
        WriteUInt16(0);
        WriteUInt16(0);
        WriteUInt32(0);
        WriteUInt32(1);
        WriteUInt32(Fps);
        WriteUInt32(0);
        _lengthPos = _stream.Position;
        WriteUInt32(0);
        WriteUInt32((uint)Math.Min(int.MaxValue, (long)_width * _height));
        WriteUInt32(0xFFFFFFFF);
        WriteUInt32(0);
        WriteUInt16(0);
        WriteUInt16(0);
        WriteUInt16(0);
        WriteUInt16(0);

        WriteFourCc("strf");
        WriteUInt32(40);
        WriteUInt32(40);
        WriteUInt32((uint)_width);
        WriteUInt32((uint)_height);
        WriteUInt16(1);
        WriteUInt16(24);
        WriteFourCc("MJPG");
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(0);
        WriteUInt32(0);

        PatchListSize(_strlPos, _stream.Position);
        PatchListSize(_hdrlPos, _stream.Position);

        _moviPos = _stream.Position;
        WriteFourCc("LIST");
        WriteUInt32(0);
        WriteFourCc("movi");
        // idx1 offsets se miden desde el FourCC 'movi'.
        _indexBase = _moviPos + 8;
    }

    private void FinishFile()
    {
        var moviEnd = _stream.Position;

        WriteFourCc("idx1");
        WriteUInt32((uint)(4 + _index.Count * 16));
        foreach (var (offset, size) in _index)
        {
            WriteFourCc("00dc");
            WriteUInt32(0x10);
            WriteUInt32(offset);
            WriteUInt32(size);
        }

        var end = _stream.Position;
        PatchListSize(_moviPos, moviEnd);
        PatchUInt32(_totalFramesPos, (uint)_frameCount);
        PatchUInt32(_lengthPos, (uint)_frameCount);
        PatchUInt32(4, (uint)(end - 8));
    }

    private void PatchListSize(long listPosition, long contentEnd)
    {
        var size = (uint)(contentEnd - (listPosition + 8));
        PatchUInt32(listPosition + 4, size);
    }

    private void PatchUInt32(long position, uint value)
    {
        var here = _stream.Position;
        _stream.Position = position;
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
        _stream.Position = here;
    }

    private void WriteFourCc(string value)
    {
        Span<byte> buffer = stackalloc byte[4];
        for (var i = 0; i < 4; i++) buffer[i] = (byte)value[i];
        _stream.Write(buffer);
    }

    private void WriteUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    private void WriteUInt16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    private static (int Width, int Height) ReadJpegSize(byte[] data)
    {
        var i = 2;
        while (i + 8 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = data[i + 1];
            if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                var height = (data[i + 5] << 8) | data[i + 6];
                var width = (data[i + 7] << 8) | data[i + 8];
                return (width, height);
            }

            if (marker is 0xD8 or 0xD9 or 0x01)
            {
                i += 2;
                continue;
            }

            if (i + 3 >= data.Length) break;
            var length = (data[i + 2] << 8) | data[i + 3];
            if (length < 2) break;
            i += 2 + length;
        }

        return (0, 0);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished) await StopAsync();
        else
        {
            try { _stream.Dispose(); } catch { }
        }
    }
}
