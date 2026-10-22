using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Fx.ControlKit.Grid;

/// <summary>Portable metrics and Unicode mapping for the unchanged bundled OFL TrueType fonts.</summary>
internal sealed class NativePdfFont
{
    private static readonly ConcurrentDictionary<string, Lazy<NativePdfFont>> Fonts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private readonly ushort[] _glyphs = new ushort[65536];
    private readonly ushort[] _advances;
    private readonly Dictionary<uint, int> _kerning = new();

    public byte[] Data { get; }
    public string PostScriptName { get; }
    public int UnitsPerEm { get; }
    public double Ascent { get; }
    public double Descent { get; }
    public double CapHeight { get; }
    public double[] BoundingBox { get; }
    public double ItalicAngle { get; }
    public int Flags { get; }

    public static NativePdfFont Resolve(string family, bool bold = false, bool italic = false)
    {
        var normalized = (family ?? "").Trim().Trim('\'', '"').ToLowerInvariant();
        var font = normalized switch
        {
            "arial" or "helvetica" or "liberation sans" or "liberationsans" or "sans-serif" => "LiberationSans",
            "times new roman" or "times" or "times-roman" or "liberation serif" or "liberationserif" or "serif" => "LiberationSerif",
            "courier new" or "courier" or "liberation mono" or "liberationmono" or "monospace" => "LiberationMono",
            _ => throw new NotSupportedException($"Native PDF does not have an embedded font for '{family}'. Use Arial, Times New Roman, Courier New, or their Liberation equivalents.")
        };
        var style = bold ? italic ? "BoldItalic" : "Bold" : italic ? "Italic" : "Regular";
        var file = font + "-" + style + ".ttf";
        return Fonts.GetOrAdd(file, static key => new Lazy<NativePdfFont>(() => new NativePdfFont(key))).Value;
    }

    public ushort Glyph(char character)
    {
        if (char.IsSurrogate(character) || char.IsControl(character))
            throw new NotSupportedException($"Native PDF cannot render character U+{(int)character:X4} as a standalone glyph.");
        var category = CharUnicodeInfo.GetUnicodeCategory(character);
        if (category is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
            || character is >= '\u0590' and <= '\u1cff' or >= '\ufb1d' and <= '\ufdff' or >= '\ufe70' and <= '\ufeff')
            throw new NotSupportedException($"Native PDF does not support the bidirectional or complex-script shaping required by character U+{(int)character:X4}.");
        var glyph = _glyphs[character];
        if (glyph == 0)
            throw new NotSupportedException($"The embedded PDF font {PostScriptName} does not contain character U+{(int)character:X4}.");
        return glyph;
    }

    /// <summary>Horizontal advance in PDF font units (1000 units per em).</summary>
    public double Width(char character) => Scale(_advances[Glyph(character)]);

    public double Kerning(char left, char right) => Scale(_kerning.GetValueOrDefault(((uint)Glyph(left) << 16) | Glyph(right)));

    private NativePdfFont(string file)
    {
        using var resource = typeof(NativePdfFont).Assembly.GetManifestResourceStream("Fx.PdfFonts." + file)
            ?? throw new InvalidOperationException("Missing embedded PDF font: " + file);
        if (resource.Length is < 12 or > 8 * 1024 * 1024) throw InvalidFont();
        using var data = new MemoryStream();
        resource.CopyTo(data);
        Data = data.ToArray();
        if (U32(0) != 0x00010000) throw InvalidFont();
        var tableCount = U16(4);
        if (tableCount is 0 or > 128) throw InvalidFont();
        Check(12, tableCount * 16);
        for (var index = 0; index < tableCount; index++)
        {
            var at = 12 + index * 16;
            var name = Encoding.ASCII.GetString(Data, at, 4);
            var offset = ToOffset(U32(at + 8));
            var length = ToOffset(U32(at + 12));
            Check(offset, length);
            if (!_tables.TryAdd(name, (offset, length))) throw InvalidFont();
        }
        var head = Table("head", 54);
        UnitsPerEm = U16(head + 18);
        if (UnitsPerEm is < 16 or > 16384) throw InvalidFont();
        BoundingBox = [Scale(I16(head + 36)), Scale(I16(head + 38)), Scale(I16(head + 40)), Scale(I16(head + 42))];
        var maxp = Table("maxp", 6);
        var glyphCount = U16(maxp + 4);
        if (glyphCount == 0) throw InvalidFont();
        var hhea = Table("hhea", 36);
        Ascent = Scale(I16(hhea + 4));
        Descent = Scale(I16(hhea + 6));
        var metricCount = U16(hhea + 34);
        if (metricCount == 0 || metricCount > glyphCount) throw InvalidFont();
        var hmtx = Table("hmtx", metricCount * 4 + (glyphCount - metricCount) * 2);
        _advances = new ushort[glyphCount];
        for (var index = 0; index < glyphCount; index++)
            _advances[index] = U16(hmtx + Math.Min(index, metricCount - 1) * 4);
        var post = Table("post", 16);
        ItalicAngle = I32(post + 4) / 65536d;
        var fixedPitch = U32(post + 12) != 0;
        Flags = 32 | (fixedPitch ? 1 : 0) | (file.StartsWith("LiberationSerif", StringComparison.Ordinal) ? 2 : 0) | (ItalicAngle != 0 ? 64 : 0);
        CapHeight = Ascent;
        if (_tables.TryGetValue("OS/2", out var os2) && os2.Length >= 90 && U16(os2.Offset) >= 2)
            CapHeight = Scale(I16(os2.Offset + 88));
        PostScriptName = ReadPostScriptName() ?? Path.GetFileNameWithoutExtension(file);
        LoadCmap(glyphCount);
        LoadKerning(glyphCount);
    }

    // OpenType horizontal format-0 pairs, as provided by the bundled fonts.
    // Keep bounds checks local to each subtable; unsupported layouts are explicit.
    private void LoadKerning(int glyphCount)
    {
        if (!_tables.TryGetValue("kern", out var table)) return;
        if (table.Length < 4 || U16(table.Offset) != 0) throw InvalidFont();
        var at = table.Offset + 4;
        for (var index = 0; index < U16(table.Offset + 2); index++)
        {
            if (at > table.Offset + table.Length - 6) throw InvalidFont();
            var length = U16(at + 2);
            var coverage = U16(at + 4);
            if (length < 6 || at > table.Offset + table.Length - length) throw InvalidFont();
            if (U16(at) != 0 || (coverage & ~8) != 1 || length < 14)
                throw new NotSupportedException("Unsupported embedded PDF font kerning table.");
            var count = U16(at + 6);
            if (14 + count * 6 > length) throw InvalidFont();
            for (var pair = 0; pair < count; pair++)
            {
                var offset = at + 14 + pair * 6;
                var left = U16(offset); var right = U16(offset + 2);
                if (left >= glyphCount || right >= glyphCount) throw InvalidFont();
                var key = ((uint)left << 16) | right;
                var value = I16(offset + 4);
                _kerning[key] = (coverage & 8) != 0 ? value : _kerning.GetValueOrDefault(key) + value;
            }
            at += length;
        }
    }

    private string? ReadPostScriptName()
    {
        if (!_tables.TryGetValue("name", out var table) || table.Length < 6) return null;
        var count = U16(table.Offset + 2);
        var stringOffset = U16(table.Offset + 4);
        if (6 + count * 12 > table.Length || stringOffset > table.Length) throw InvalidFont();
        string? fallback = null;
        for (var index = 0; index < count; index++)
        {
            var at = table.Offset + 6 + index * 12;
            if (U16(at + 6) != 6) continue;
            var platform = U16(at);
            var length = U16(at + 8);
            var offset = U16(at + 10);
            if (stringOffset + offset + length > table.Length) throw InvalidFont();
            var bytes = Data.AsSpan(table.Offset + stringOffset + offset, length);
            var text = platform is 0 or 3 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.ASCII.GetString(bytes);
            if (text.Length is 0 or > 127 || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) continue;
            if (platform == 3) return text;
            fallback ??= text;
        }
        return fallback;
    }

    private void LoadCmap(int glyphCount)
    {
        var cmap = Table("cmap", 4);
        var count = U16(cmap + 2);
        var tableLength = _tables["cmap"].Length;
        if (count == 0 || count > 128 || 4 + count * 8 > tableLength) throw InvalidFont();
        var selected = 0;
        var rank = -1;
        for (var index = 0; index < count; index++)
        {
            var at = cmap + 4 + index * 8;
            var platform = U16(at);
            var encoding = U16(at + 2);
            var relative = ToOffset(U32(at + 4));
            if (relative > tableLength - 2) throw InvalidFont();
            var format = U16(cmap + relative);
            if (platform != 0 && !(platform == 3 && encoding is 1 or 10)) continue;
            var candidateRank = format switch { 12 => 4, 4 => 2, _ => -1 };
            if (platform == 3 && candidateRank >= 0) candidateRank++;
            if (candidateRank <= rank) continue;
            selected = cmap + relative;
            rank = candidateRank;
        }
        if (rank < 0) throw InvalidFont();
        if (U16(selected) == 12) LoadCmap12(selected, cmap + tableLength, glyphCount);
        else LoadCmap4(selected, cmap + tableLength, glyphCount);
    }

    private void LoadCmap4(int start, int tableEnd, int glyphCount)
    {
        var length = U16(start + 2);
        if (length < 16 || start > tableEnd - length) throw InvalidFont();
        var segments = U16(start + 6) / 2;
        if (segments == 0 || 16 + segments * 8 > length) throw InvalidFont();
        var ends = start + 14;
        var starts = ends + segments * 2 + 2;
        var deltas = starts + segments * 2;
        var ranges = deltas + segments * 2;
        var previousEnd = -1;
        for (var index = 0; index < segments; index++)
        {
            var first = U16(starts + index * 2);
            var last = U16(ends + index * 2);
            if (last < first || first <= previousEnd) throw InvalidFont();
            previousEnd = last;
            var delta = I16(deltas + index * 2);
            var range = U16(ranges + index * 2);
            for (var code = (int)first; code <= last; code++)
            {
                var glyph = (code + delta) & 0xffff;
                if (range != 0)
                {
                    var at = ranges + index * 2 + range + (code - first) * 2;
                    if (at < start || at > start + length - 2) throw InvalidFont();
                    glyph = U16(at);
                    if (glyph != 0) glyph = (glyph + delta) & 0xffff;
                }
                if (glyph >= glyphCount) throw InvalidFont();
                _glyphs[code] = (ushort)glyph;
            }
        }
    }

    private void LoadCmap12(int start, int tableEnd, int glyphCount)
    {
        Check(start, 16);
        var length = ToOffset(U32(start + 4));
        var count = ToOffset(U32(start + 12));
        if (length < 16 || start > tableEnd - length || count > 65536 || 16L + count * 12L > length) throw InvalidFont();
        long previousEnd = -1;
        for (var index = 0; index < count; index++)
        {
            var at = start + 16 + index * 12;
            var first = U32(at);
            var last = U32(at + 4);
            var initialGlyph = U32(at + 8);
            if (last < first || first <= previousEnd || last > 0x10ffff || (long)initialGlyph + last - first >= glyphCount) throw InvalidFont();
            previousEnd = last;
            for (var code = (long)first; code <= Math.Min(last, 65535u); code++)
                _glyphs[code] = (ushort)(initialGlyph + code - first);
        }
    }

    private int Table(string name, int minimum)
    {
        if (!_tables.TryGetValue(name, out var table) || table.Length < minimum) throw InvalidFont();
        return table.Offset;
    }
    private static int ToOffset(uint value) => value <= int.MaxValue ? (int)value : throw InvalidFont();
    private void Check(int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > Data.Length - length) throw InvalidFont();
    }
    private ushort U16(int offset) { Check(offset, 2); return BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(offset, 2)); }
    private short I16(int offset) { Check(offset, 2); return BinaryPrimitives.ReadInt16BigEndian(Data.AsSpan(offset, 2)); }
    private uint U32(int offset) { Check(offset, 4); return BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(offset, 4)); }
    private int I32(int offset) { Check(offset, 4); return BinaryPrimitives.ReadInt32BigEndian(Data.AsSpan(offset, 4)); }
    private double Scale(int value) => value * 1000d / UnitsPerEm;
    private static InvalidDataException InvalidFont() => new("The embedded native PDF TrueType font is malformed or unsupported.");
}
