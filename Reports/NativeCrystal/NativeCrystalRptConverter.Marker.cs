using System.Buffers.Binary;

namespace Fx.ControlKit.Reports.NativeCrystal;

public static partial class NativeCrystalRptConverter
{
    // A chart drawing whose category label (text object 2018) is a large rotated run is a stamp, not a graph: Crystal
    // prints the label and nothing else (the cheque report's "VOID"). The drawing keeps its fonts in records 9008/1021
    // by index, the label's attributes in 9017/2018 (font index, then its size in 32768ths of the frame height and its
    // rotation in degrees) and the label's colour in 9001/2018.
    private static ReportChartMarker? ReadChartMarker(ReadOnlySpan<byte> drawing, string binding)
    {
        if (binding.Length == 0 || !drawing.StartsWith("TIFFGRAPH"u8)) return null;
        var fonts = new Dictionary<int, string>();
        int? fontIndex = null;
        var size = 0;
        var angle = 0;
        var color = "#000000";
        for (var at = 256; at <= drawing.Length - 4;)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(drawing[at..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(drawing[(at + 2)..]); at += 4;
            if (length > drawing.Length - at) break;
            var p = drawing.Slice(at, length); at += length;
            if (length < 4) continue;
            var index = BinaryPrimitives.ReadInt16LittleEndian(p);
            var destination = BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
            if (tag == 9008 && destination == 1021 && index >= 0 && length >= 6)
            {
                // A null-terminated UTF-16 family name; unused table slots hold stale memory.
                var family = new System.Text.StringBuilder();
                for (var i = 4; i + 1 < length; i += 2)
                {
                    var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(p[i..]);
                    if (c == 0) break;
                    family.Append(c);
                }
                var name = family.ToString().Trim();
                if (name.Length > 0 && name.All(c => c < 127 && !char.IsControl(c))) fonts[index] = name;
            }
            else if (tag == 9017 && destination == 2018 && length >= 12)
            {
                fontIndex = BinaryPrimitives.ReadInt16LittleEndian(p[4..]);
                size = BinaryPrimitives.ReadInt16LittleEndian(p[8..]);
                angle = BinaryPrimitives.ReadInt16LittleEndian(p[10..]);
            }
            else if (tag == 9001 && destination == 2018 && length == 16)
                color = $"#{p[11]:x2}{p[13]:x2}{p[15]:x2}";
        }
        if (fontIndex is null || size <= 0) return null;
        var marker = new ReportChartMarker
        {
            Binding = binding, Font = fonts.GetValueOrDefault(fontIndex.Value, "Arial"), SizeRatio = size / 32768d,
            Angle = angle % 360, Color = color
        };
        return marker.IsValid ? marker : null;
    }
}
