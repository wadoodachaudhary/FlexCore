namespace Fx.ControlKit.Preview;

/// <summary>Reads an X Window Dump (<c>xwd</c>) image into RGB24.</summary>
internal static class XwdImage
{
    public static (int Width, int Height, byte[] Rgb) Decode(ReadOnlySpan<byte> dump)
    {
        if (dump.Length < 100) throw new InvalidDataException("XWD dump is too small.");
        var headerIsBigEndian = ReadU32(dump, 4, bigEndian: true) == 7;
        var headerIsLittleEndian = ReadU32(dump, 4, bigEndian: false) == 7;
        if (!headerIsBigEndian && !headerIsLittleEndian) throw new InvalidDataException("XWD version is not 7.");
        var bigEndian = headerIsBigEndian;

        var headerSize = (int)ReadU32(dump, 0, bigEndian);
        var format = ReadU32(dump, 8, bigEndian);
        var width = (int)ReadU32(dump, 16, bigEndian);
        var height = (int)ReadU32(dump, 20, bigEndian);
        var byteOrder = ReadU32(dump, 28, bigEndian);
        var bitsPerPixel = (int)ReadU32(dump, 44, bigEndian);
        var bytesPerLine = (int)ReadU32(dump, 48, bigEndian);
        var redMask = ReadU32(dump, 56, bigEndian);
        var greenMask = ReadU32(dump, 60, bigEndian);
        var blueMask = ReadU32(dump, 64, bigEndian);
        var colorCount = (int)ReadU32(dump, 76, bigEndian);
        if (format != 2) throw new InvalidDataException("Only ZPixmap XWD dumps are supported.");
        if (width < 1 || height < 1 || width > 8192 || height > 8192)
            throw new InvalidDataException("XWD dimensions are invalid.");
        if (bitsPerPixel is not (24 or 32))
            throw new InvalidDataException("Only 24-bit and 32-bit XWD dumps are supported.");
        if (bytesPerLine < width * (bitsPerPixel / 8) || colorCount < 0 || colorCount > 1_000_000)
            throw new InvalidDataException("XWD layout is invalid.");
        var pixelOffset = headerSize + colorCount * 12;
        if (headerSize < 100 || pixelOffset < 0 || pixelOffset > dump.Length)
            throw new InvalidDataException("XWD header size is invalid.");
        var needed = pixelOffset + bytesPerLine * (height - 1) + width * (bitsPerPixel / 8);
        if (needed > dump.Length) throw new InvalidDataException("XWD pixel data is truncated.");

        var rgb = new byte[width * height * 3];
        var redShift = Shift(redMask);
        var greenShift = Shift(greenMask);
        var blueShift = Shift(blueMask);
        var redBits = BitCount(redMask);
        var greenBits = BitCount(greenMask);
        var blueBits = BitCount(blueMask);
        var pixelsLittleEndian = byteOrder == 0;
        var destination = 0;
        for (var y = 0; y < height; y++)
        {
            var row = pixelOffset + y * bytesPerLine;
            for (var x = 0; x < width; x++)
            {
                uint pixel;
                if (bitsPerPixel == 32)
                {
                    pixel = ReadU32(dump, row + x * 4, bigEndian: !pixelsLittleEndian);
                }
                else
                {
                    var start = row + x * 3;
                    pixel = pixelsLittleEndian
                        ? dump[start] | ((uint)dump[start + 1] << 8) | ((uint)dump[start + 2] << 16)
                        : ((uint)dump[start] << 16) | ((uint)dump[start + 1] << 8) | dump[start + 2];
                }
                rgb[destination++] = Scale(pixel, redMask, redShift, redBits);
                rgb[destination++] = Scale(pixel, greenMask, greenShift, greenBits);
                rgb[destination++] = Scale(pixel, blueMask, blueShift, blueBits);
            }
        }
        return (width, height, rgb);
    }

    private static byte Scale(uint pixel, uint mask, int shift, int bits)
    {
        if (mask == 0 || bits == 0) return 0;
        var value = (pixel & mask) >> shift;
        if (bits >= 8) return (byte)(value >> (bits - 8));
        return (byte)(value * 255 / ((1 << bits) - 1));
    }

    private static int Shift(uint mask)
    {
        if (mask == 0) return 0;
        var shift = 0;
        while ((mask & 1) == 0)
        {
            mask >>= 1;
            shift++;
        }
        return shift;
    }

    private static int BitCount(uint mask)
    {
        var count = 0;
        while (mask != 0)
        {
            count += (int)(mask & 1);
            mask >>= 1;
        }
        return count;
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        if ((uint)offset > (uint)(data.Length - 4)) throw new InvalidDataException("XWD read is out of range.");
        return bigEndian
            ? ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3]
            : data[offset] | ((uint)data[offset + 1] << 8) | ((uint)data[offset + 2] << 16) | ((uint)data[offset + 3] << 24);
    }
}
