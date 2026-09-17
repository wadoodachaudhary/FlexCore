using System.Buffers.Binary;

namespace Fx.ControlKit.Reports.NativeCrystal;

// Extracts a single, full-frame bitmap. This deliberately does not approximate vector playback.
internal static class CrystalMetafileImage
{
    private const int MaxBytes = 5 * 1024 * 1024;
    private const uint SourceCopy = 0x00CC0020;

    public static string Embed(ReadOnlySpan<byte> bytes)
    {
        Limit(bytes);
        if (bytes.Length >= 44 && U32(bytes, 0) == 1 && U32(bytes, 40) == 0x464D4520)
            return ReportObjectVisual.EmbedImage(Emf(bytes));
        if (bytes.Length >= 4 && (U32(bytes, 0) == 0x9AC6CDD7 || U16(bytes, 0) is 1 or 2 && U16(bytes, 2) == 9))
            return ReportObjectVisual.EmbedImage(Wmf(bytes));
        return ReportObjectVisual.EmbedImage(bytes);
    }

    public static string Presentation(ReadOnlySpan<byte> bytes, int aspect)
    {
        var payload = PresentationPayload(bytes, aspect, out var format);
        return format switch
        {
            8 => ReportObjectVisual.EmbedImage(Bitmap(payload)),
            3 => ReportObjectVisual.EmbedImage(Wmf(payload)),
            14 => ReportObjectVisual.EmbedImage(Emf(payload)),
            _ => throw new InvalidDataException("Unsupported OLE presentation clipboard format.")
        };
    }

    internal static ReadOnlySpan<byte> PresentationPayload(ReadOnlySpan<byte> bytes, int aspect, out uint format)
    {
        Limit(bytes);
        Require(bytes.Length >= 40 && U32(bytes, 0) == uint.MaxValue, "Unsupported OLE clipboard format header.");
        Require(U32(bytes, 8) == 4, "Device-specific OLE presentations are not supported.");
        Require(I32(bytes, 12) == aspect, "OLE presentation aspect does not match the picture.");
        format = U32(bytes, 4);
        return Slice(bytes, 40, U32(bytes, 36));
    }

    private static byte[] Bitmap(ReadOnlySpan<byte> dib)
    {
        Limit(dib);
        Require(dib.Length >= 40 && U32(dib, 0) == 40, "Only BITMAPINFOHEADER DIB pictures are supported.");
        var width = I32(dib, 4); var height = Math.Abs((long)I32(dib, 8)); var depth = U16(dib, 14);
        Require(width > 0 && height > 0 && width * height <= 40_000_000 && U16(dib, 12) == 1, "Invalid DIB dimensions or planes.");
        Require(U32(dib, 16) == 0 && depth is 1 or 4 or 8 or 24 or 32, "Compressed/bitfield DIB pictures are not supported.");
        var colors = U32(dib, 32);
        if (depth <= 8 && colors == 0) colors = (uint)(1 << depth);
        Require(depth <= 8 ? colors <= 1 << depth : colors == 0, "Unsupported DIB color table.");
        var offset = 40 + colors * 4;
        var pixelBytes = ((width * (long)depth + 31) / 32) * 4 * height;
        Require(offset + pixelBytes == dib.Length && dib.Length <= MaxBytes - 14, "Invalid DIB pixel length.");
        Require(U32(dib, 20) == 0 || U32(dib, 20) == pixelBytes, "Invalid DIB declared image size.");
        var bmp = new byte[14 + dib.Length]; bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 14 + offset);
        dib.CopyTo(bmp.AsSpan(14));
        return bmp;
    }

    private static byte[] Wmf(ReadOnlySpan<byte> bytes)
    {
        Limit(bytes);
        Require(bytes.Length >= 18, "Truncated WMF header.");
        (int X, int Y, int Width, int Height)? frame = null;
        if (U32(bytes, 0) == 0x9AC6CDD7)
        {
            Require(bytes.Length >= 40, "Truncated placeable WMF header.");
            ushort checksum = 0;
            for (var i = 0; i < 20; i += 2) checksum ^= U16(bytes, i);
            Require(checksum == U16(bytes, 20) && U16(bytes, 14) > 0, "Invalid placeable WMF header.");
            frame = (I16(bytes, 6), I16(bytes, 8), I16(bytes, 10) - I16(bytes, 6), I16(bytes, 12) - I16(bytes, 8));
            bytes = bytes[22..];
        }
        Require(U16(bytes, 0) is 1 or 2 && U16(bytes, 2) == 9 && U16(bytes, 4) is 0x100 or 0x300
            && U32(bytes, 6) * 2L == bytes.Length, "Invalid WMF header or length.");
        var x = frame?.X ?? 0; var y = frame?.Y ?? 0;
        var width = frame?.Width ?? 0; var height = frame?.Height ?? 0;
        byte[]? bitmap = null;
        var anisotropic = false;
        var offset = 18; var records = 0;
        while (offset < bytes.Length)
        {
            Require(++records <= 10000 && bytes.Length - offset >= 6, "Invalid WMF record count or header.");
            var words = U32(bytes, offset);
            Require(words >= 3 && words * 2L <= bytes.Length - offset, "Invalid WMF record length.");
            var record = bytes.Slice(offset, (int)words * 2); offset += record.Length;
            switch (U16(record, 4))
            {
                case 0:
                    Require(record.Length == 6 && offset == bytes.Length && bitmap is not null, "Invalid WMF end record or missing bitmap.");
                    return bitmap!;
                case 0x0103: // MM_ANISOTROPIC with an explicit logical window.
                    Require(bitmap is null && record.Length == 8 && I16(record, 6) == 8, "Unsupported WMF mapping mode.");
                    anisotropic = true;
                    break;
                case 0x020B:
                    Require(bitmap is null && record.Length == 10, "Invalid WMF window origin.");
                    y = I16(record, 6); x = I16(record, 8); break;
                case 0x020C:
                    Require(bitmap is null && record.Length == 10, "Invalid WMF window extent.");
                    height = I16(record, 6); width = I16(record, 8); break;
                case 0x0F43:
                    Require(bitmap is null && record.Length >= 68, "WMF requires exactly one bitmap paint record.");
                    Require(U32(record, 6) == SourceCopy && U16(record, 10) == 0, "Unsupported WMF raster operation or palette.");
                    Require(anisotropic && width > 0 && height > 0 && (!frame.HasValue || frame.Value == (x, y, width, height)), "Unsupported WMF frame mapping.");
                    Require(I16(record, 24) == y && I16(record, 26) == x && I16(record, 20) == height && I16(record, 22) == width,
                        "WMF bitmap does not cover the complete frame.");
                    bitmap = Bitmap(record[28..]);
                    Require(I16(record, 16) == 0 && I16(record, 18) == 0 && I16(record, 14) == I32(record, 32)
                        && I16(record, 12) == Math.Abs((long)I32(record, 36)), "Cropped or mirrored WMF bitmaps are not supported.");
                    break;
                default: throw new InvalidDataException($"WMF record 0x{U16(record, 4):X4} requires vector/state playback, which is not supported.");
            }
        }
        throw new InvalidDataException("Missing WMF end record.");
    }

    private static byte[] Emf(ReadOnlySpan<byte> bytes)
    {
        Limit(bytes);
        Require(bytes.Length >= 88 && U32(bytes, 0) == 1 && U32(bytes, 40) == 0x464D4520
            && U32(bytes, 48) == bytes.Length, "Invalid EMF header or length.");
        var headerSize = U32(bytes, 4);
        Require(headerSize >= 88 && headerSize <= bytes.Length && headerSize % 4 == 0, "Invalid EMF header size.");
        var left = I32(bytes, 8); var top = I32(bytes, 12);
        var width = (long)I32(bytes, 16) - left + 1; var height = (long)I32(bytes, 20) - top + 1;
        Require(width > 0 && height > 0 && width <= int.MaxValue && height <= int.MaxValue, "Invalid EMF bounds.");
        // Frame padding changes the rendered image. Accept only the same physical rectangle, within header rounding.
        Require(FrameMatches(left, I32(bytes, 24), I32(bytes, 72), I32(bytes, 80))
            && FrameMatches(top, I32(bytes, 28), I32(bytes, 76), I32(bytes, 84))
            && FrameMatches(width, (long)I32(bytes, 32) - I32(bytes, 24) + 1, I32(bytes, 72), I32(bytes, 80))
            && FrameMatches(height, (long)I32(bytes, 36) - I32(bytes, 28) + 1, I32(bytes, 76), I32(bytes, 84)),
            "Padded or transformed EMF frames are not supported.");
        byte[]? bitmap = null;
        var offset = (int)headerSize; var records = 1;
        while (offset < bytes.Length)
        {
            Require(++records <= 10000 && bytes.Length - offset >= 8, "Invalid EMF record count or header.");
            var length = U32(bytes, offset + 4);
            Require(length >= 8 && length % 4 == 0 && length <= bytes.Length - offset, "Invalid EMF record length.");
            var record = bytes.Slice(offset, (int)length); offset += record.Length;
            if (U32(record, 0) == 14)
            {
                Require(record.Length >= 20 && offset == bytes.Length && records == U32(bytes, 52) && bitmap is not null,
                    "Invalid EMF end record, count or missing bitmap.");
                return bitmap!;
            }
            Require(U32(record, 0) == 81, $"EMF record {U32(record, 0)} requires vector/state playback, which is not supported.");
            Require(bitmap is null && record.Length >= 80, "EMF requires exactly one bitmap paint record.");
            Require(U32(record, 64) == 0 && U32(record, 68) == SourceCopy, "Unsupported EMF raster operation or palette.");
            Require(I32(record, 24) == left && I32(record, 28) == top && I32(record, 72) == width && I32(record, 76) == height,
                "EMF bitmap does not cover the complete frame.");
            var infoOffset = U32(record, 48); var infoSize = U32(record, 52);
            var bitsOffset = U32(record, 56); var bitsSize = U32(record, 60);
            Require(infoOffset >= 80 && bitsOffset >= 80 && (infoOffset + (long)infoSize <= bitsOffset || bitsOffset + (long)bitsSize <= infoOffset),
                "Overlapping EMF bitmap buffers.");
            var info = Slice(record, infoOffset, infoSize); var bits = Slice(record, bitsOffset, bitsSize);
            Require(info.Length >= 40 && info.Length + (long)bits.Length <= MaxBytes - 14, "Invalid EMF bitmap size.");
            Require(I32(record, 32) == 0 && I32(record, 36) == 0 && I32(record, 40) == I32(info, 4)
                && I32(record, 44) == Math.Abs((long)I32(info, 8)), "Cropped or mirrored EMF bitmaps are not supported.");
            var dib = new byte[info.Length + bits.Length]; info.CopyTo(dib); bits.CopyTo(dib.AsSpan(info.Length));
            bitmap = Bitmap(dib);
            Require(U32(bitmap, 10) - 14 == info.Length, "Invalid EMF bitmap info/pixel boundary.");
        }
        throw new InvalidDataException("Missing EMF end record.");
    }

    private static bool FrameMatches(long pixels, long units, int device, int millimeters) =>
        device > 0 && millimeters > 0 && Math.Abs(pixels * (double)millimeters * 100 / device - units) <= 1;
    private static void Limit(ReadOnlySpan<byte> bytes) => Require(bytes.Length <= MaxBytes, "Picture exceeds the 5 MB limit.");
    private static void Require(bool valid, string message) { if (!valid) throw new InvalidDataException(message); }
    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> bytes, uint offset, uint length)
    {
        Require(offset <= bytes.Length && length <= bytes.Length - (long)offset, "Truncated picture payload.");
        return bytes.Slice((int)offset, (int)length);
    }
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static int I32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]);
    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static short I16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadInt16LittleEndian(bytes[offset..]);
}
