using System.Numerics;
using System.Text;

namespace Fx.ControlKit.Reports.NativeCrystal;

internal sealed partial class CrystalVectorMetafile
{
    private sealed record MetafileFont(string Family, int Height, int Weight, bool Italic, bool Underline, bool StrikeOut, int Escapement, byte CharSet);

    private static double[]? MatrixValues(Matrix3x2 m) => m.IsIdentity ? null : [m.M11, m.M12, m.M21, m.M22, m.M31, m.M32];
    private static void ValidateMatrix(Matrix3x2 m)
    {
        Require(new[] { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 }.All(n => float.IsFinite(n) && Math.Abs(n) <= 100000000)
            && float.IsFinite(m.GetDeterminant()) && Math.Abs(m.GetDeterminant()) >= 1e-12, "Invalid or singular metafile transform.");
    }
    private void Transform(ReadOnlySpan<byte> bytes, uint mode)
    {
        if (mode == 1) { _context = _context with { World = Matrix3x2.Identity }; return; }
        static float F(ReadOnlySpan<byte> data, int offset) => BitConverter.Int32BitsToSingle(I32(data, offset));
        var value = new Matrix3x2(F(bytes, 0), F(bytes, 4), F(bytes, 8), F(bytes, 12), F(bytes, 16), F(bytes, 20));
        ValidateMatrix(value);
        var world = mode switch
        {
            2 => value * _context.World, 3 => _context.World * value, 4 => value,
            _ => throw new InvalidDataException("Unsupported metafile transform mode.")
        };
        ValidateMatrix(world); _context = _context with { World = world };
    }

    private static DrawingObject Font(ReadOnlySpan<byte> bytes, bool wide)
    {
        Require(wide ? bytes.Length >= 92 : bytes.Length == 50, "Invalid metafile font record.");
        var height = wide ? I32(bytes, 0) : I16(bytes, 0);
        var width = wide ? I32(bytes, 4) : I16(bytes, 2);
        var angle = wide ? I32(bytes, 8) : I16(bytes, 4);
        var orientation = wide ? I32(bytes, 12) : I16(bytes, 6);
        var weight = wide ? I32(bytes, 16) : I16(bytes, 8);
        var flags = wide ? 20 : 10;
        Require(height is < 0 and >= -1000000 && width == 0 && angle == orientation && Math.Abs((long)angle) <= 36000
            && weight is >= 0 and <= 1000, "Unsupported metafile font metrics/orientation.");
        var family = Decode(bytes.Slice(flags + 8, wide ? 64 : 32), wide, 0).Split('\0')[0];
        Require(family.Length > 0 && family.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_'), "Unsupported metafile font family.");
        return new(false, "none", Font: new(family, -height, weight == 0 ? 400 : weight,
            bytes[flags] != 0, bytes[flags + 1] != 0, bytes[flags + 2] != 0, angle, bytes[flags + 3]));
    }

    private static string Decode(ReadOnlySpan<byte> bytes, bool wide, byte charset)
    {
        var codePage = charset switch { 0 => 1252, 161 => 1253, 162 => 1254, 163 => 1258, 177 => 1255,
            178 => 1256, 186 => 1257, 204 => 1251, 222 => 874, 238 => 1250, _ => 0 };
        Require(wide && charset != 2 || !wide && codePage != 0, "Unsupported or device-dependent metafile text charset.");
        try
        {
            return wide ? new UnicodeEncoding(false, false, true).GetString(bytes)
                : CodePagesEncodingProvider.Instance.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!.GetString(bytes);
        }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Invalid metafile text encoding.", error); }
    }

    private void Background(int mode)
    { Require(mode is 1 or 2, "Invalid metafile background mode."); _context = _context with { Transparent = mode == 1 }; }
    private void Alignment(uint alignment)
    {
        Require((alignment & ~30u) == 0 && (alignment & 6) is 0 or 2 or 6 && (alignment & 24) is 0 or 8 or 24,
            "Unsupported metafile text alignment/current-position mode.");
        _context = _context with { TextAlign = (int)alignment };
    }

    private void WmfText(ReadOnlySpan<byte> bytes, bool extended)
    {
        Require(bytes.Length >= (extended ? 8 : 6), "Truncated WMF text.");
        var count = U16(bytes, extended ? 4 : 0);
        Require(count <= 16000, "WMF text exceeds the limit.");
        uint options = extended ? U16(bytes, 6) : 0u;
        Require((options & ~0x2006u) == 0, "Unsupported WMF text options.");
        var stringOffset = extended ? ((options & 6) != 0 ? 16 : 8) : 2;
        var after = stringOffset + ((count + 1) & ~1);
        Require(bytes.Length >= after + (extended ? 0 : 4), "Truncated WMF string.");
        var x = I16(bytes, extended ? 2 : after + 2); var y = I16(bytes, extended ? 0 : after);
        double[]? advances = null; double[]? vertical = null;
        var stride = (options & 0x2000) != 0 ? 4 : 2;
        if (extended && bytes.Length > after)
        {
            Require(bytes.Length == after + count * stride, "Invalid WMF text spacing count.");
            advances = new double[count]; if (stride == 4) vertical = new double[count];
            for (var i = 0; i < count; i++) { advances[i] = I16(bytes, after + i * stride); if (vertical is not null) vertical[i] = I16(bytes, after + i * stride + 2); }
        }
        else Require(bytes.Length == after + (extended ? 0 : 4), "Invalid WMF text length.");
        Require((options & 0x2000) == 0 || vertical is not null, "Two-axis text requires spacing values.");
        Text(bytes.Slice(stringOffset, count), false, x, y, advances, vertical, options,
            (options & 6) == 0 ? null : new double[] { I16(bytes, 8), I16(bytes, 10), I16(bytes, 12), I16(bytes, 14) });
    }

    private void EmfText(ReadOnlySpan<byte> record, bool wide)
    {
        Require(record.Length >= 76 && U32(record, 24) is 1 or 2, "Invalid EMF text record.");
        var options = U32(record, 52);
        Require((options & ~0x2006u) == 0, "Unsupported EMF text options.");
        var count = U32(record, 44); var offset = U32(record, 48); var dx = U32(record, 72);
        Require(count <= 16000 && (count == 0 || offset >= 76 && (!wide || offset % 2 == 0) && offset + count * (wide ? 2L : 1L) <= record.Length),
            "Invalid EMF string bounds.");
        double[]? advances = null; double[]? vertical = null;
        var stride = (options & 0x2000) != 0 ? 8 : 4;
        if (dx != 0)
        {
            Require(dx >= 76 && dx % 4 == 0 && dx + count * (long)stride <= record.Length
                && (dx + count * (long)stride <= offset || dx >= offset + count * (wide ? 2L : 1L)), "Invalid/overlapping EMF text spacing.");
            advances = new double[count]; if (stride == 8) vertical = new double[count];
            for (var i = 0; i < count; i++) { advances[i] = I32(record, (int)dx + i * stride); if (vertical is not null) vertical[i] = I32(record, (int)dx + i * stride + 4); }
        }
        Require((options & 0x2000) == 0 || vertical is not null, "Two-axis text requires spacing values.");
        Text(count == 0 ? ReadOnlySpan<byte>.Empty : record.Slice((int)offset, (int)count * (wide ? 2 : 1)), wide, I32(record, 36), I32(record, 40), advances, vertical, options,
            (options & 6) == 0 ? null : new double[] { I32(record, 56), I32(record, 60), I32(record, 64), I32(record, 68) });
    }

    private void Text(ReadOnlySpan<byte> bytes, bool wide, int x, int y, double[]? advances, double[]? vertical, uint options, double[]? rectangle)
    {
        Require(!_pathOpen, "Metafile glyph outlines inside a path bracket are not supported.");
        var clips = _context.ClipPolygons;
        if (rectangle is { } r)
        {
            Require(r[2] >= r[0] && r[3] >= r[1], "Invalid text rectangle.");
            var polygon = MappedRectangle(r[0], r[1], r[2], r[3]);
            if ((options & 2) != 0)
                _scene.Shapes.Add(new("Polygon", polygon, _context.BackgroundColor, "none", 0, Clip: _context.Clip, ClipPolygons: clips));
            if ((options & 4) != 0) clips = [.. clips ?? [], polygon];
        }
        if (bytes.Length == 0) return;
        var font = _context.Font?.Font ?? throw new InvalidDataException("Metafile text requires an explicit supported font.");
        Require(_context.Transparent || (options & 6) == 6, "Implicit opaque text backgrounds require font-cell metrics and are not supported.");
        var value = Decode(bytes, wide, font.CharSet);
        if (value.Length == 0) return;
        Require((_pointCount += value.Length + 2) <= 200000 && _scene.Shapes.Count < 10000, "Metafile text/shape limit exceeded.");
        var matrix = Matrix3x2.CreateRotation((float)(-font.Escapement * Math.PI / 1800), new Vector2(x, y)) * Mapping();
        ValidateMatrix(matrix);
        var text = new ReportVectorText(value, font.Family, font.Height, font.Weight, font.Italic, font.Underline, font.StrikeOut,
            (_context.TextAlign & 6) switch { 2 => "end", 6 => "middle", _ => "start" },
            (_context.TextAlign & 24) switch { 0 => "text-before-edge", 8 => "text-after-edge", _ => "alphabetic" }, advances, vertical);
        _scene.Shapes.Add(new("Text", [x, y], _context.TextColor, "none", 0, Clip: _context.Clip?.ToArray(), Transform: MatrixValues(matrix), Text: text, ClipPolygons: clips));
    }
}
