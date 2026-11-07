using System.Buffers.Binary;
using System.Globalization;

namespace Fx.ControlKit.Reports.NativeCrystal;

public static partial class NativeCrystalRptConverter
{
    private static ReportChartSpatialStyle ReadSpatialDrawing(ReadOnlySpan<byte> drawing, ReportChartSpatialStyle style)
    {
        if (!drawing.StartsWith("TIFFGRAPH"u8)) return style;
        var gaugeIntervals = 0;
        var colors = new SortedDictionary<int,string>();
        for (var at=256; at<=drawing.Length-4;)
        {
            var tag=BinaryPrimitives.ReadUInt16LittleEndian(drawing[at..]);
            var length=BinaryPrimitives.ReadUInt16LittleEndian(drawing[(at+2)..]); at+=4;
            if (length>drawing.Length-at) break;
            var p=drawing.Slice(at,length); at+=length;
            if (length<4) continue;
            var index=BinaryPrimitives.ReadInt16LittleEndian(p); var destination=BinaryPrimitives.ReadUInt16LittleEndian(p[2..]);
            if(tag==9001 && length==16 && destination==2132 && index is >=0 and <256)
                colors[index]=$"#{p[11]:x2}{p[13]:x2}{p[15]:x2}";
            if (style.Family == 12 && tag == 6024 && destination == 3028 && length == 56)
                gaugeIntervals = BinaryPrimitives.ReadUInt16LittleEndian(p[24..]) + 1;
            if (style.Family == 12 && tag == 10027 && destination == 12173 && length >= 8)
            {
                var start = BinaryPrimitives.ReadUInt16LittleEndian(p[4..]);
                var end = BinaryPrimitives.ReadUInt16LittleEndian(p[6..]);
                if (end > start && end <= 360 && end - start != 180) style = style with { GaugeArc = new(start, end, gaugeIntervals is >= 2 and <= 40 ? gaugeIntervals : 8) };
            }
            if (style.Family == 11 && tag == 6024 && length == 56 && destination is 3028 or 3029)
            {
                var scale = new ReportChartScale(BinaryPrimitives.ReadDoubleLittleEndian(p[6..]),
                    BinaryPrimitives.ReadDoubleLittleEndian(p[14..]), BinaryPrimitives.ReadUInt16LittleEndian(p[24..]) + 1);
                if (scale.IsValid) style = destination == 3028 ? style with { PrimaryScale = scale } : style with { SecondaryScale = scale };
            }
            if(tag==6012 && length==16 && destination==2099)
            {
                var size=new double[3];
                for(var i=0;i<3;i++) size[i]=BinaryPrimitives.ReadInt32LittleEndian(p[(4+i*4)..])/4d;
                if(size.All(n=>n>0)) style=style with { Volume=string.Join(' ',size.Select(n=>n.ToString("R",CultureInfo.InvariantCulture))) };
            }
            if(tag==6013 && length==100 && destination==2098)
            {
                var matrix=new double[12];
                for(var i=0;i<12;i++) matrix[i]=BinaryPrimitives.ReadDoubleLittleEndian(p[(4+i*8)..]);
                if(matrix.All(double.IsFinite))style=style with { Camera=string.Join(' ',matrix.Select(n=>n.ToString("R",CultureInfo.InvariantCulture))) };
            }
        }
        return style with { Palette=string.Join(' ',colors.TakeWhile((v,i)=>v.Key==i).Select(v=>v.Value)), Frame=ReadPlotFrame(drawing) };
    }
    private static ReportBottomPieLayout? ReadPerspectivePieLayout(ReadOnlySpan<byte> drawing, bool showBottomLegend)
    {
        if (ReadPlotFrame(drawing) is null) return null;
        var legend = showBottomLegend ? ReadPlotFrame(drawing, 2033) : null;
        if (showBottomLegend && legend is null) return null;
        var tilt = 0; var depth = -1; var rotation = 0; var clockwise = true;
        var horizontalInset = 0; var verticalInset = 0; var labelFont = 0; var legendFont = 0;
        var slices = new SortedDictionary<int, ReportPieSliceStyle>();
        var labels = new SortedDictionary<int, char>();
        var offsets = new SortedDictionary<int, ReportPieLabelOffset>();
        var transparent = false;
        for (var at = 256; at <= drawing.Length - 4;)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(drawing[at..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(drawing[(at + 2)..]);
            at += 4;
            if (length > drawing.Length - at) return null;
            var p = drawing.Slice(at, length);
            at += length;
            if (length < 4) continue;
            var index = Short(p, 0); var destination = Short(p, 2);
            if (tag == 10017 && destination == 3034 && length == 24 && Short(p, 12) == 338 && Short(p, 18) == 1)
                offsets[Short(p, 4)] = new(Short(p, 14), Short(p, 16));
            if (tag == 9001 && destination == 2015 && index == -1 && length == 16)
                transparent = Short(p, 6) == -2;
            if (tag == 6062 && destination == 2070 && length == 28)
            {
                tilt = Short(p, 22) != 0 ? Short(p, 4) : 0;
                rotation = Short(p, 6); depth = Short(p, 8); clockwise = Short(p, 14) != 0;
                if (Short(p, 26) != 5) return null; // External labels drawn beside the pie.
            }
            if (tag == 1039 && destination == 2143 && length >= 14)
            {
                horizontalInset = Short(p, 10) + Short(p, 12);
                verticalInset = Short(p, 6) + Short(p, 8);
            }
            if (tag == 9017 && index == -1 && length >= 10)
            {
                if (destination == 2020) legendFont = Short(p, 8);
                if (destination == 2024) labelFont = Short(p, 8);
            }
            if (tag == 6026 && destination == 3032 && length >= 8 && index is >= 0 and < 256)
            {
                var labelBits = Short(p, 4);
                labels[index] = (labelBits & 2) == 0 ? '0' : Short(p, 6) == 0 ? '1' : '2';
                if (length >= 24)
                {
                    var explode = Math.Clamp((int)Short(p, 22), 0, 100);
                    var format = (labelBits & 1) == 0 ? "" : Short(p, 14) == 5 ? "C0" : "G";
                    if (explode > 0 || format.Length > 0) slices[index] = new(explode, format);
                }
            }
        }
        var flags = new string(labels.TakeWhile((entry, i) => entry.Key == i).Select(entry => entry.Value).ToArray());
        var result = new ReportBottomPieLayout(tilt, depth, rotation, clockwise, horizontalInset, verticalInset, labelFont, legendFont, flags, legend)
        {
            Slices = slices.Count > 0 ? slices : null,
            LabelOffsets = offsets.Count > 0 ? offsets : null,
            TransparentBackground = transparent
        };
        return result.IsValid ? result : null;
        static int Short(ReadOnlySpan<byte> p, int offset) => BinaryPrimitives.ReadInt16LittleEndian(p[offset..]);
    }

    private static ReportChartPrintAxis? ReadRetainedStockAxis(ReadOnlySpan<byte> drawing, ReportChartSpatialStyle style)
    {
        if (!drawing.StartsWith("TIFFGRAPH"u8)) return null;
        for (var at = 256; at <= drawing.Length - 4;)
        {
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(drawing[at..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(drawing[(at + 2)..]); at += 4;
            if (length > drawing.Length - at) break;
            var p = drawing.Slice(at, length); at += length;
            if (tag != 6024 || length != 56 || BinaryPrimitives.ReadUInt16LittleEndian(p[2..]) != 3028) continue;
            var min = BinaryPrimitives.ReadDoubleLittleEndian(p[6..]);
            var max = BinaryPrimitives.ReadDoubleLittleEndian(p[14..]);
            var intervals = BinaryPrimitives.ReadUInt16LittleEndian(p[24..]) + 1;
            if (double.IsFinite(min) && double.IsFinite(max) && min < max && intervals is >= 1 and <= 64)
                return new(min, max, intervals, "C0", style.CategoryTitle, style.ValueTitle, true);
        }
        return null;
    }
}
