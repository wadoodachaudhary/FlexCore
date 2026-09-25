using System.Buffers.Binary;
using System.Text;

namespace Fx.ControlKit.Reports.NativeCrystal;

/// <summary>Document summary of an RPT (the OLE "\u0005SummaryInformation" property set Crystal saves as Report Summary Info).</summary>
public sealed record CrystalSummaryInformation(string Title = "", string Subject = "", string Author = "", string Keywords = "", string Comments = "");

/// <summary>Reads the FMTID_SummaryInformation section of an OLE property set stream (MS-OLEPS): title 2, subject 3, author 4, keywords 5, comments 6.</summary>
internal static class CrystalSummaryInformationParser
{
    private const int MaxText = 65_536;
    private static readonly Guid SummaryFormat = new("f29f85e0-4ff9-1068-ab91-08002b27b3d9");

    public static CrystalSummaryInformation Parse(ReadOnlySpan<byte> stream)
    {
        if (stream.Length < 48 || BinaryPrimitives.ReadUInt16LittleEndian(stream) != 0xFFFE)
            throw new InvalidDataException("The RPT summary information stream is not an OLE property set.");
        var sections = BinaryPrimitives.ReadInt32LittleEndian(stream[24..]);
        if (sections is < 1 or > 16 || 28 + sections * 20 > stream.Length)
            throw new InvalidDataException("The RPT summary information stream has an invalid section table.");
        for (var index = 0; index < sections; index++)
        {
            var entry = stream.Slice(28 + index * 20, 20);
            if (new Guid(entry[..16]) != SummaryFormat) continue;
            return ReadSection(stream, BinaryPrimitives.ReadInt32LittleEndian(entry[16..]));
        }
        return new();
    }

    private static CrystalSummaryInformation ReadSection(ReadOnlySpan<byte> stream, int offset)
    {
        if (offset < 0 || offset > stream.Length - 8) throw new InvalidDataException("The RPT summary information section lies outside its stream.");
        var size = BinaryPrimitives.ReadInt32LittleEndian(stream[offset..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(stream[(offset + 4)..]);
        if (size < 8 || size > stream.Length - offset || count < 0 || count > (size - 8) / 8)
            throw new InvalidDataException("The RPT summary information section is malformed.");
        var section = stream.Slice(offset, size);
        var codePage = 1252;
        var values = new Dictionary<int, int>();
        for (var index = 0; index < count; index++)
        {
            var id = BinaryPrimitives.ReadInt32LittleEndian(section[(8 + index * 8)..]);
            var position = BinaryPrimitives.ReadInt32LittleEndian(section[(12 + index * 8)..]);
            if (position < 8 || position > size - 4) throw new InvalidDataException($"RPT summary property {id} lies outside its section.");
            if (id == 1 && BinaryPrimitives.ReadUInt16LittleEndian(section[position..]) == 2 && position <= size - 6)
                codePage = BinaryPrimitives.ReadUInt16LittleEndian(section[(position + 4)..]);
            else values[id] = position;
        }
        string Text(ReadOnlySpan<byte> section, int id)
        {
            if (!values.TryGetValue(id, out var position)) return "";
            var type = BinaryPrimitives.ReadUInt16LittleEndian(section[position..]);
            if (position > size - 8) throw new InvalidDataException($"RPT summary property {id} is truncated.");
            var length = BinaryPrimitives.ReadInt32LittleEndian(section[(position + 4)..]);
            var start = position + 8;
            var bytes = type switch
            {
                0x1E => length,
                0x1F => length is >= 0 and <= int.MaxValue / 2 ? length * 2 : -1,
                _ => throw new InvalidDataException($"RPT summary property {id} has unsupported type 0x{type:X}.")
            };
            if (length < 0 || length > MaxText || bytes > size - start) throw new InvalidDataException($"RPT summary property {id} is truncated.");
            var text = (type == 0x1F ? Encoding.Unicode : TextEncoding(codePage)).GetString(section.Slice(start, bytes));
            var end = text.IndexOf('\0');
            return end < 0 ? text : text[..end];
        }
        return new(Text(section, 2), Text(section, 3), Text(section, 4), Text(section, 5), Text(section, 6));
    }

    // The property set's code page: one .NET has built in (1200, 20127, 28591, 65001, ...) or one of the Windows code pages.
    private static Encoding TextEncoding(int codePage)
    {
        if (CodePagesEncodingProvider.Instance.GetEncoding(codePage) is { } windows) return windows;
        try { return Encoding.GetEncoding(codePage); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        { throw new InvalidDataException($"RPT summary information uses unknown code page {codePage}.", error); }
    }
}
