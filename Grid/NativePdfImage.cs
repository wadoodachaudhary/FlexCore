using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Fx.ControlKit.Grid;

/// <summary>Bounded decoding of embedded PNG/JPEG/BMP assets, without filesystem or network image loaders.</summary>
internal sealed record NativePdfImage(int Width, int Height, byte[] Data, string ColorSpace, byte[]? Alpha = null, bool Jpeg = false)
{
    public static NativePdfImage Read(byte[] data)
    {
        if (data.Length > 5 * 1024 * 1024) throw new InvalidDataException("PDF images must be 5 MB or smaller.");
        if (data.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return Png(data);
        if (data.AsSpan().StartsWith(new byte[] { 255, 216, 255 })) return ReadJpeg(data);
        if (data.AsSpan().StartsWith("BM"u8)) return Bitmap(data);
        throw new NotSupportedException("Native PDF images support PNG, JPEG and uncompressed BMP.");
    }

    private static NativePdfImage Bitmap(byte[] data)
    {
        if (data.Length < 54) throw new InvalidDataException("Truncated BMP header.");
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        int I32(int at) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
        ushort U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
        if (U32(2) != data.Length) throw new InvalidDataException("Invalid BMP file size.");
        if (U32(14) != 40 || U32(30) != 0)
            throw new NotSupportedException("Native PDF requires an uncompressed BITMAPINFOHEADER BMP.");
        var width = I32(18);
        var signedHeight = I32(22);
        var height = Math.Abs((long)signedHeight);
        if (height > int.MaxValue) throw new InvalidDataException("Invalid BMP height.");
        ValidateSize(width, (int)height);
        if (U16(26) != 1) throw new InvalidDataException("Invalid BMP planes.");
        var depth = U16(28);
        if (depth is not (1 or 4 or 8 or 24 or 32)) throw new NotSupportedException("Unsupported BMP color depth for native PDF.");
        var colors = U32(46);
        if (depth <= 8 && colors == 0) colors = (uint)(1 << depth);
        if (depth <= 8 ? colors > 1 << depth : colors != 0) throw new InvalidDataException("Invalid BMP palette size.");
        var offset = (long)U32(10);
        var stride = ((long)width * depth + 31) / 32 * 4;
        var pixelBytes = stride * height;
        if (offset < 54 + colors * 4L || offset + pixelBytes > data.Length
            || U32(34) != 0 && U32(34) != pixelBytes)
            throw new InvalidDataException("Invalid BMP pixel length or offset.");
        var rgb = new byte[checked(width * (int)height * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = (int)(offset + (signedHeight < 0 ? y : height - 1 - y) * stride);
            for (var x = 0; x < width; x++)
            {
                var source = row + x * (depth / 8);
                if (depth <= 8)
                {
                    var index = data[row + x * depth / 8] >> (8 - depth - x * depth % 8) & ((1 << depth) - 1);
                    if (index >= colors) throw new InvalidDataException("BMP palette index is out of bounds.");
                    source = 54 + index * 4;
                }
                var target = (y * width + x) * 3;
                // BI_RGB uses BGR; the fourth byte in a 32-bit pixel/palette entry is reserved, not alpha.
                rgb[target] = data[source + 2]; rgb[target + 1] = data[source + 1]; rgb[target + 2] = data[source];
            }
        }
        return new(width, (int)height, rgb, "DeviceRGB");
    }
    private static NativePdfImage ReadJpeg(byte[] data)
    {
        var at = 2;
        while (at < data.Length)
        {
            if (data[at++] != 255) throw new InvalidDataException("Invalid JPEG marker.");
            while (at < data.Length && data[at] == 255) at++;
            if (at >= data.Length) break;
            var marker = data[at++];
            if (marker is 0xD9 or 0xDA) break;
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;
            if (at + 2 > data.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at));
            if (length < 2 || length > data.Length - at) throw new InvalidDataException("Truncated JPEG image.");
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (length < 8 || data[at + 2] != 8) throw new NotSupportedException("Native PDF requires an 8-bit JPEG image.");
                var h = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 3)); var w = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 5)); var components = data[at + 7];
                ValidateSize(w, h);
                if (components is not (1 or 3)) throw new NotSupportedException("Native PDF supports grayscale or RGB JPEG images.");
                if (length < 8 + 3 * components || data.Length < 2 || data[^2] != 255 || data[^1] != 217) throw new InvalidDataException("Truncated JPEG image.");
                return new(w, h, data.ToArray(), components == 1 ? "DeviceGray" : "DeviceRGB", Jpeg: true);
            }
            at += length;
        }
        throw new InvalidDataException("JPEG image has no supported frame.");
    }
    private static NativePdfImage Png(byte[] data)
    {
        int at = 8, width = 0, height = 0, depth = 0, type = -1;
        byte[]? palette = null, transparency = null;
        var haveHeader = false; var haveEnd = false; var idatEnded = false; var sawIdat = false;
        using var compressed = new MemoryStream();
        while (at <= data.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)); at += 4;
            if (length > data.Length - at - 8) throw new InvalidDataException("Truncated PNG image.");
            var kindBytes = data.AsSpan(at, 4); var kind = Encoding.ASCII.GetString(kindBytes); at += 4;
            var chunk = data.AsSpan(at, (int)length);
            if (Crc(data.AsSpan(at - 4, (int)length + 4)) != BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at + (int)length))) throw new InvalidDataException("PNG image checksum failed.");
            if (!haveHeader && kind != "IHDR") throw new InvalidDataException("PNG header is missing.");
            if (sawIdat && kind != "IDAT") idatEnded = true;
            switch (kind)
            {
                case "IHDR":
                    if (haveHeader || length != 13) throw new InvalidDataException("Invalid PNG header.");
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunk)); height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunk[4..])); ValidateSize(width, height);
                    depth = chunk[8]; type = chunk[9];
                    if (chunk[10] != 0 || chunk[11] != 0 || chunk[12] != 0) throw new NotSupportedException("Native PDF requires a non-interlaced PNG image.");
                    if (type is not (0 or 2 or 3 or 4 or 6) || (type is 0 or 3 ? depth is not (1 or 2 or 4 or 8) : depth != 8)) throw new NotSupportedException("Unsupported PNG color depth for native PDF.");
                    haveHeader = true; break;
                case "PLTE":
                    if (sawIdat || palette is not null || length == 0 || length > 768 || length % 3 != 0) throw new InvalidDataException("Invalid PNG palette.");
                    palette = chunk.ToArray(); break;
                case "tRNS":
                    if (sawIdat || transparency is not null || type is 4 or 6 || type == 0 && length != 2 || type == 2 && length != 6 || type == 3 && (palette is null || length > palette.Length / 3)) throw new InvalidDataException("Invalid PNG transparency.");
                    transparency = chunk.ToArray(); break;
                case "IDAT":
                    if (idatEnded) throw new InvalidDataException("PNG image data is not consecutive.");
                    compressed.Write(chunk); sawIdat = true; break;
                case "IEND":
                    if (length != 0 || !sawIdat) throw new InvalidDataException("Invalid PNG end marker.");
                    haveEnd = true; break;
                default:
                    if (kindBytes[0] is >= (byte)'A' and <= (byte)'Z') throw new NotSupportedException("Unsupported critical PNG chunk: " + kind + ".");
                    break;
            }
            at += (int)length + 4;
            if (haveEnd) break;
        }
        if (!haveEnd || at != data.Length || type == 3 && palette is null) throw new InvalidDataException("PNG image is incomplete.");
        var channels = type switch { 0 or 3 => 1, 2 => 3, 4 => 2, _ => 4 };
        var rowBytes = checked((width * channels * depth + 7) / 8); var bytesPerPixel = Math.Max(1, (channels * depth + 7) / 8);
        var expected = checked((rowBytes + 1) * height); var filtered = new byte[expected];
        compressed.Position = 0;
        using (var zlib = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true))
        {
            var read = 0;
            while (read < expected) { var n = zlib.Read(filtered, read, expected - read); if (n == 0) throw new InvalidDataException("PNG decompressed data is truncated."); read += n; }
            if (zlib.ReadByte() != -1) throw new InvalidDataException("PNG decompressed data exceeds its dimensions.");
        }
        var rgb = new byte[checked(width * height * 3)]; var alpha = type is 4 or 6 || transparency is not null ? new byte[checked(width * height)] : null;
        var prior = new byte[rowBytes]; var current = new byte[rowBytes];
        for (var y = 0; y < height; y++)
        {
            var offset = y * (rowBytes + 1); var filter = filtered[offset];
            for (var i = 0; i < rowBytes; i++)
            {
                var a = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0; var b = prior[i]; var c = i >= bytesPerPixel ? prior[i - bytesPerPixel] : 0;
                current[i] = unchecked((byte)(filtered[offset + 1 + i] + (filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, 4 => Paeth(a, b, c), _ => throw new InvalidDataException("Invalid PNG filter.") })));
            }
            for (var x = 0; x < width; x++)
            {
                var target = (y * width + x) * 3; var pixel = x * channels; byte opacity = 255;
                if (type is 0 or 3)
                {
                    var sample = depth == 8 ? current[x] : (current[x * depth / 8] >> (8 - depth - x * depth % 8)) & ((1 << depth) - 1);
                    if (type == 3)
                    {
                        if (sample * 3 + 2 >= palette!.Length) throw new InvalidDataException("PNG palette index is out of bounds.");
                        palette.AsSpan(sample * 3, 3).CopyTo(rgb.AsSpan(target));
                        if (transparency is not null && sample < transparency.Length) opacity = transparency[sample];
                    }
                    else
                    {
                        var gray = (byte)(sample * 255 / ((1 << depth) - 1)); rgb[target] = rgb[target + 1] = rgb[target + 2] = gray;
                        if (transparency is not null && sample == BinaryPrimitives.ReadUInt16BigEndian(transparency)) opacity = 0;
                    }
                }
                else if (type == 4)
                { rgb[target] = rgb[target + 1] = rgb[target + 2] = current[pixel]; opacity = current[pixel + 1]; }
                else
                {
                    current.AsSpan(pixel, 3).CopyTo(rgb.AsSpan(target));
                    if (type == 6) opacity = current[pixel + 3];
                    else if (transparency is not null && current[pixel] == BinaryPrimitives.ReadUInt16BigEndian(transparency) && current[pixel + 1] == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) && current[pixel + 2] == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4))) opacity = 0;
                }
                if (alpha is not null) alpha[y * width + x] = opacity;
            }
            (prior, current) = (current, prior);
        }
        return new(width, height, rgb, "DeviceRGB", alpha);
    }
    private static void ValidateSize(int width, int height)
    { if (width <= 0 || height <= 0 || (long)width * height > 40_000_000) throw new InvalidDataException("PDF image exceeds the supported pixel dimensions."); }
    private static int Paeth(int a, int b, int c)
    { var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var b in bytes) { crc ^= b; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1))); }
        return ~crc;
    }
}
