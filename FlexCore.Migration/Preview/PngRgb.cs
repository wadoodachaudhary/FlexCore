using System.IO.Compression;

namespace Fx.ControlKit.Preview;

/// <summary>Encodes RGB24 pixels as a non-interlaced PNG. Used for preview frames.</summary>
internal static class PngRgb
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (rgb.Length < width * height * 3) throw new ArgumentException("RGB buffer is shorter than the image.", nameof(rgb));

        var raw = new byte[height * (1 + width * 3)];
        var source = 0;
        var destination = 0;
        for (var y = 0; y < height; y++)
        {
            raw[destination++] = 0;
            rgb.Slice(source, width * 3).CopyTo(raw.AsSpan(destination));
            source += width * 3;
            destination += width * 3;
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);

        var ihdr = new byte[13];
        WriteInt32(ihdr, 0, width);
        WriteInt32(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        using var output = new MemoryStream(compressed.Length > int.MaxValue ? 0 : (int)compressed.Length + 64);
        output.Write(Signature);
        WriteChunk(output, "IHDR"u8, ihdr);
        WriteChunk(output, "IDAT"u8, compressed.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    public static (int Width, int Height, byte[] Rgb) Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < 8 || !png[..8].SequenceEqual(Signature))
            throw new InvalidDataException("Not a PNG.");
        var offset = 8;
        var width = 0;
        var height = 0;
        using var idat = new MemoryStream();
        while (offset + 8 <= png.Length)
        {
            var length = ReadInt32(png, offset);
            if (length < 0 || offset + 12 + length > png.Length) throw new InvalidDataException("Broken PNG chunk.");
            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = ReadInt32(data, 0);
                height = ReadInt32(data, 4);
                if (data[8] != 8 || data[9] != 2) throw new InvalidDataException("Only 8-bit RGB PNGs are decoded.");
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }
            offset += 12 + length;
        }
        if (width < 1 || height < 1) throw new InvalidDataException("PNG has no image.");
        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        var raw = new byte[height * (1 + width * 3)];
        var read = 0;
        while (read < raw.Length)
        {
            var n = zlib.Read(raw, read, raw.Length - read);
            if (n == 0) break;
            read += n;
        }
        if (read < raw.Length) throw new InvalidDataException("PNG image data ended early.");
        var rgb = new byte[width * height * 3];
        var source = 0;
        var destination = 0;
        for (var y = 0; y < height; y++)
        {
            if (raw[source++] != 0) throw new InvalidDataException("Only filter 0 is decoded.");
            raw.AsSpan(source, width * 3).CopyTo(rgb.AsSpan(destination));
            source += width * 3;
            destination += width * 3;
        }
        return (width, height, rgb);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteInt32(length, 0, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);
        var crcSource = new byte[type.Length + data.Length];
        type.CopyTo(crcSource);
        data.CopyTo(crcSource.AsSpan(type.Length));
        Span<byte> crc = stackalloc byte[4];
        WriteUInt32(crc, 0, Crc(crcSource));
        output.Write(crc);
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteInt32(Span<byte> buffer, int offset, int value) => WriteUInt32(buffer, offset, (uint)value);

    private static void WriteUInt32(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static int ReadInt32(ReadOnlySpan<byte> buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];
}
