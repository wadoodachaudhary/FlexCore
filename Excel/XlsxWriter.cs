using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Fx.ControlKit.Excel;

/// <summary>Horizontal alignment for an <see cref="XlsxCell"/>.</summary>
public enum XlsxAlign
{
    Left,
    Center,
    Right
}

/// <summary>
/// One cell in a sheet built by <see cref="XlsxWriter"/>. Either <see cref="Value"/>
/// or <see cref="Formula"/> is written; a formula wins when both are set.
/// </summary>
public sealed class XlsxCell
{
    /// <summary>Typed value. Numerics, dates and bools are written as native XLSX
    /// types; everything else falls back to a shared string.</summary>
    public object? Value { get; set; }

    /// <summary>A1-style formula WITHOUT the leading '='.</summary>
    public string? Formula { get; set; }

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public bool Strikethrough { get; set; }

    /// <summary>Font color, 6-digit hex without '#'. Null uses default font color.</summary>
    public string? FontColorHex { get; set; }

    /// <summary>Solid fill, 6-digit hex without '#'. Null leaves the cell unfilled.</summary>
    public string? FillHex { get; set; }

    /// <summary>Thin outside border, 6-digit hex without '#'. Null means no border.</summary>
    public string? BorderHex { get; set; }

    /// <summary>Excel number-format code, e.g. "#,##0.00" or "m/d/yyyy".</summary>
    public string? NumberFormat { get; set; }

    public XlsxAlign Align { get; set; } = XlsxAlign.Left;

    internal XlsxCell Copy() => (XlsxCell)MemberwiseClone();

    // Back to a new cell's state, for a row source that reuses its cells. Keep in step with the properties above.
    internal void Reset()
    {
        Value = null;
        Formula = null;
        Bold = false;
        Italic = false;
        Strikethrough = false;
        FontColorHex = null;
        FillHex = null;
        BorderHex = null;
        NumberFormat = null;
        Align = XlsxAlign.Left;
    }
}

/// <summary>A single worksheet: a grid of cells plus per-column widths.</summary>
public sealed class XlsxSheet
{
    public string Name { get; set; } = "Sheet1";

    /// <summary>Keep text beyond Excel's cell limit inside the workbook, in linked continuation rows.</summary>
    public bool PreserveOversizedText { get; set; }
    internal Dictionary<string, string> InternalHyperlinks { get; } = new(StringComparer.Ordinal);

    /// <summary>Row-major cells. Ragged rows are allowed.</summary>
    public List<List<XlsxCell>> Rows { get; } = new();

    /// <summary>Rows built on demand instead of held in <see cref="Rows"/>.</summary>
    internal XlsxRowSource? RowSource { get; init; }

    internal int RowCount => RowSource?.Count ?? Rows.Count;

    /// <summary>Row <paramref name="index"/>. From a row source it is valid only until the next call.</summary>
    internal List<XlsxCell> RowAt(int index) => RowSource is null ? Rows[index] : RowSource.Row(index);

    /// <summary>Explicit Excel character widths by zero-based column index. A column
    /// with no entry is sized from its content.</summary>
    public Dictionary<int, double> ColumnWidths { get; } = new();

    public List<XlsxCell> AddRow()
    {
        var row = new List<XlsxCell>();
        Rows.Add(row);
        return row;
    }
}

/// <summary>
/// Rows produced one at a time, so a sheet built from another model never exists as a whole
/// cell grid. The writer reads every row in order once per pass; each pass must get the same
/// rows. A call may reuse the list and the cells it returned last time.
/// </summary>
internal abstract class XlsxRowSource
{
    public abstract int Count { get; }

    public abstract List<XlsxCell> Row(int index);
}

/// <summary>
/// Minimal, dependency-free SpreadsheetML (.xlsx) writer.
///
/// FlexCore only ever *wrote* workbooks on the grid-export and DataTable-export
/// paths — values, bold, a solid fill, a thin border, a number format and a
/// horizontal alignment. That is a small enough slice of the format to emit
/// directly, which removes ClosedXML (and its DocumentFormat.OpenXml chain) from
/// those paths entirely. Reading existing workbooks is a different problem and is
/// deliberately NOT attempted here.
///
/// Output is a normal OPC zip: [Content_Types].xml, the two rels parts, a
/// workbook, worksheets, a shared-string table and a style table. Worksheets and
/// the shared-string table are written straight into their zip entries, so a
/// sheet is never held as one string (.NET caps a string at about 1.07 billion
/// characters, which a wide million-row sheet passes).
/// </summary>
public static partial class XlsxWriter
{
    // Excel serialises dates as days since 1899-12-30 (the "1900 system", including
    // its deliberate 1900 leap-year bug, which DateTime.ToOADate already reproduces).
    private const double OaDateFloor = -657434d;

    public static byte[] Write(XlsxSheet sheet)
    {
        // Not a MemoryStream: that keeps each outgrown buffer until a full collection and
        // then copies itself once more in ToArray.
        using var buffer = new ChunkedBuffer();
        Write(buffer, sheet);
        return buffer.ToArray();
    }

    /// <summary>Writes the workbook. The sheet is read more than once and must not change until this returns.</summary>
    public static void Write(Stream destination, XlsxSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(sheet);

        var strings = new SharedStrings();
        var styles = new StyleTable();
        var sheets = sheet.PreserveOversizedText ? PreserveLongText(sheet) : [sheet];

        // First pass: register every style and shared string and size the content-sized
        // columns, so styles.xml and sharedStrings.xml are complete before any worksheet.
        // Nothing per cell is kept; the second pass resolves each cell again as it writes.
        var others = new Queue<int>();
        var widths = new double[sheets.Count][];
        for (var index = 0; index < sheets.Count; index++)
            widths[index] = Measure(sheets[index], strings, styles, others);

        // leaveOpen: callers pass their own stream (FilerControl hands us a file
        // stream it still owns) and must be free to keep using it afterwards.
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        WriteEntry(zip, "[Content_Types].xml", BuildContentTypes(sheets.Count));
        WriteEntry(zip, "_rels/.rels", RootRels);
        WriteEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRels(sheets.Count));
        WriteEntry(zip, "xl/workbook.xml", BuildWorkbook(sheets));
        WriteEntry(zip, "xl/styles.xml", styles.ToXml());
        using (var writer = OpenEntry(zip, "xl/sharedStrings.xml"))
            strings.Write(writer);
        for (var index = 0; index < sheets.Count; index++)
        {
            using var writer = OpenEntry(zip, $"xl/worksheets/sheet{index + 1}.xml");
            WriteSheet(writer, sheets[index], widths[index], strings, styles, others);
        }
        if (others.Count > 0)
            throw SheetChanged();
    }

    private static InvalidOperationException SheetChanged() => new("The sheet changed while it was being written.");

    // Longest rendered value: a negative decimal with 29 digits and a point is 31 characters.
    private const int ValueLength = 64;

    /// <summary>
    /// Resolves a cell to its style index, cell type and rendered value. The value is
    /// written into <paramref name="content"/>; the result is its length, or -1 when the
    /// cell carries no value. A formula cell reports its formula instead.
    /// </summary>
    /// <remarks>
    /// The first pass (<paramref name="register"/>) adds styles and shared strings; the
    /// second only looks them up, so both passes give every cell the same indices. Text
    /// from an arbitrary object's ToString is resolved once: its shared-string index is
    /// queued in the first pass and taken back in the same cell order in the second.
    /// Rendered values are digits, signs, points and exponents only, so they need no
    /// XML escaping.
    /// </remarks>
    private static int Resolve(XlsxCell cell, SharedStrings strings, StyleTable styles, Queue<int> others, bool register,
        Span<char> content, out int styleIndex, out string? type, out string? formula)
    {
        var value = cell.Value;
        var numberFormat = cell.NumberFormat;

        // A date with no explicit format would otherwise render as a raw serial number.
        if (string.IsNullOrWhiteSpace(numberFormat))
        {
            numberFormat = value switch
            {
                DateTime or DateOnly or DateTimeOffset => "m/d/yyyy",
                TimeOnly or TimeSpan => "h:mm:ss",
                _ => null
            };
        }

        styleIndex = styles.GetIndex(
            cell.Bold,
            cell.Italic,
            cell.Strikethrough,
            cell.FontColorHex,
            cell.FillHex,
            cell.BorderHex,
            numberFormat,
            cell.Align);
        type = null;
        formula = null;

        if (!string.IsNullOrWhiteSpace(cell.Formula))
        {
            formula = cell.Formula;
            return -1;
        }

        switch (value)
        {
            case null:
            case DBNull:
                return -1;

            case bool b:
                type = "b";
                content[0] = b ? '1' : '0';
                return 1;

            case string s:
                type = "s";
                return Format(register ? strings.GetIndex(s) : strings.IndexOf(s), content);

            case DateTime dt:
                return Serial(dt.ToOADate(), content);
            case DateOnly d:
                return Serial(d.ToDateTime(TimeOnly.MinValue).ToOADate(), content);
            case DateTimeOffset dto:
                return Serial(dto.DateTime.ToOADate(), content);
            case TimeOnly t:
                return Serial(t.ToTimeSpan().TotalDays, content);
            case TimeSpan ts:
                return Serial(ts.TotalDays, content);

            case byte or sbyte or short or ushort or int or uint or long:
                return Format(Convert.ToInt64(value, CultureInfo.InvariantCulture), content);
            case ulong u when u <= long.MaxValue:
                return Format((long)u, content);
            case ulong u:
                return Serial((double)u, content);
            case decimal m:
                return Format(m, content);

            // Non-finite doubles have no XLSX numeric representation; ClosedXML's
            // behaviour here was to fall back to text, so keep that.
            case double dbl when double.IsNaN(dbl) || double.IsInfinity(dbl):
                type = "s";
                return Format(SharedIndex(dbl.ToString(CultureInfo.InvariantCulture), strings, register), content);
            case double dbl:
                return Serial(dbl, content);
            case float f when float.IsNaN(f) || float.IsInfinity(f):
                type = "s";
                return Format(SharedIndex(f.ToString(CultureInfo.InvariantCulture), strings, register), content);
            case float f:
                return Serial(f, content);

            default:
                type = "s";
                int index;
                if (register)
                {
                    index = strings.GetIndex(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                    others.Enqueue(index);
                }
                else if (!others.TryDequeue(out index))
                {
                    throw SheetChanged();
                }
                return Format(index, content);
        }
    }

    private static int SharedIndex(string text, SharedStrings strings, bool register) =>
        register ? strings.GetIndex(text) : strings.IndexOf(text);

    private static int Format<T>(T value, Span<char> content) where T : ISpanFormattable =>
        value.TryFormat(content, out var written, default, CultureInfo.InvariantCulture)
            ? written
            : throw new InvalidOperationException("Cell value exceeds the render buffer.");

    private static int Serial(double value, Span<char> content)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            content[0] = '0';
            return 1;
        }
        if (value < OaDateFloor)
            value = OaDateFloor;
        return value.TryFormat(content, out var written, "R", CultureInfo.InvariantCulture)
            ? written
            : throw new InvalidOperationException("Cell value exceeds the render buffer.");
    }

    // Registers the sheet's styles and shared strings and returns its column widths.
    private static double[] Measure(XlsxSheet sheet, SharedStrings strings, StyleTable styles, Queue<int> others)
    {
        var widest = new List<int>();
        Span<char> content = stackalloc char[ValueLength];
        for (var r = 0; r < sheet.RowCount; r++)
        {
            var row = sheet.RowAt(r);
            while (widest.Count < row.Count)
                widest.Add(0);
            for (var c = 0; c < row.Count; c++)
            {
                if (row[c] is not { } cell)
                    continue;
                var length = Resolve(cell, strings, styles, others, register: true, content, out _, out _, out var formula);
                length = formula?.Length ?? Math.Max(length, 0);
                if (length > widest[c])
                    widest[c] = length;
            }
        }

        var widths = new double[widest.Count];
        for (var c = 0; c < widths.Length; c++)
            widths[c] = sheet.ColumnWidths.TryGetValue(c, out var explicitWidth) ? explicitWidth : MeasureColumn(widest[c]);
        return widths;
    }

    private static void WriteSheet(TextWriter writer, XlsxSheet sheet, double[] widths, SharedStrings strings, StyleTable styles,
        Queue<int> others)
    {
        writer.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        writer.Write("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");

        if (widths.Length > 0)
        {
            writer.Write("<cols>");
            for (var c = 0; c < widths.Length; c++)
            {
                writer.Write(string.Create(CultureInfo.InvariantCulture,
                    $"""<col min="{c + 1}" max="{c + 1}" width="{widths[c].ToString("0.##", CultureInfo.InvariantCulture)}" customWidth="1"/>"""));
            }
            writer.Write("</cols>");
        }

        writer.Write("<sheetData>");
        var columns = new string?[widths.Length];
        Span<char> content = stackalloc char[ValueLength];
        Span<char> number = stackalloc char[16];
        Span<char> styleText = stackalloc char[16];
        for (var r = 0; r < sheet.RowCount; r++)
        {
            var row = sheet.RowAt(r);
            var rowNumber = number[..Format(r + 1, number)];
            writer.Write("<row r=\"");
            writer.Write(rowNumber);
            writer.Write("\">");
            for (var c = 0; c < row.Count; c++)
            {
                if (row[c] is not { } cell)
                    continue;

                var length = Resolve(cell, strings, styles, others, register: false, content, out var style, out var type, out var formula);

                // An empty, unstyled cell carries no information — omit it entirely.
                if (length < 0 && formula is null && style == 0)
                    continue;

                writer.Write("<c r=\"");
                writer.Write(columns[c] ??= ColumnName(c));
                writer.Write(rowNumber);
                writer.Write("\" s=\"");
                writer.Write(styleText[..Format(style, styleText)]);
                writer.Write("\" ");
                if (type is not null)
                {
                    writer.Write("t=\"");
                    writer.Write(type);
                    writer.Write("\" ");
                }
                writer.Write('>');

                if (formula is not null)
                {
                    writer.Write("<f>");
                    WriteEscaped(writer, formula);
                    writer.Write("</f>");
                }
                else if (length >= 0)
                {
                    writer.Write("<v>");
                    writer.Write(content[..length]);
                    writer.Write("</v>");
                }

                writer.Write("</c>");
            }
            writer.Write("</row>");
        }
        writer.Write("</sheetData>");
        if (sheet.InternalHyperlinks.Count > 0)
        {
            writer.Write("<hyperlinks>");
            foreach (var (cell, target) in sheet.InternalHyperlinks)
            {
                writer.Write("<hyperlink ref=\"");
                WriteEscaped(writer, cell);
                writer.Write("\" location=\"");
                WriteEscaped(writer, target);
                writer.Write("\"/>");
            }
            writer.Write("</hyperlinks>");
        }
        writer.Write("</worksheet>");
    }

    // Content-based sizing, standing in for ClosedXML's AdjustToContents. Shared
    // strings are the only content whose display length we know cheaply; numbers are
    // close enough at their serialised length.
    private static double MeasureColumn(int widest) => Math.Clamp(widest + 2, 8.43, 80);

    private static string BuildWorkbook(string sheetName) =>
        $"""
         <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
         <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{Escape(SanitizeSheetName(sheetName))}" sheetId="1" r:id="rId1"/></sheets></workbook>
         """;

    /// <summary>Excel rejects []:*?/\ in a sheet name and caps it at 31 characters.</summary>
    public static string SanitizeSheetName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Sheet1";
        var cleaned = new string(name.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray()).Trim();
        if (cleaned.Length == 0)
            return "Sheet1";
        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }

    private static string ColumnName(int zeroBased)
    {
        var name = "";
        var index = zeroBased + 1;
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            name = (char)('A' + remainder) + name;
            index = (index - 1) / 26;
        }
        return name;
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        WriteEscaped(writer, value);
        return writer.ToString();
    }

    private static void WriteEscaped(TextWriter writer, string value)
    {
        var run = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            var entity = ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                _ => null
            };
            if (entity is null)
            {
                // A surrogate pair is one character outside the Basic Multilingual Plane
                // (emoji, CJK Extension B). IsXmlChar rejects each half on its own, so the
                // pair is kept together and only lone surrogates are dropped.
                if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                    continue;
                }
                if (XmlConvert.IsXmlChar(ch))
                    continue;
            }
            writer.Write(value.AsSpan(run, i - run));
            if (entity is not null)
                writer.Write(entity);
            run = i + 1;
        }
        writer.Write(value.AsSpan(run));
    }

    // 16K chars keeps the writer's char and byte buffers off the large-object heap.
    private static StreamWriter OpenEntry(ZipArchive zip, string path) =>
        new(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), Utf8NoMark, 1 << 14);

    // Encoding.UTF8.GetBytes never writes a byte-order mark; a StreamWriter would.
    private static readonly UTF8Encoding Utf8NoMark = new(encoderShouldEmitUTF8Identifier: false);

    private static void WriteEntry(ZipArchive zip, string path, string xml)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(xml);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteSpreadsheetText(TextWriter writer, string value)
    {
        // ST_Xstring: preserve CR/control characters and literal _xHHHH_ text through Excel's decoder.
        var run = 0;
        Span<char> code = stackalloc char[4];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '_' && i + 6 < value.Length && value[i + 1] == 'x' && value[i + 6] == '_'
                && value.AsSpan(i + 2, 4).IndexOfAnyExcept("0123456789ABCDEFabcdef") < 0)
            {
                writer.Write(value.AsSpan(run, i - run));
                writer.Write("_x005F_");
            }
            else if (char.IsHighSurrogate(ch) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }
            else if (ch == '\r' || !XmlConvert.IsXmlChar(ch))
            {
                writer.Write(value.AsSpan(run, i - run));
                ((int)ch).TryFormat(code, out _, "X4", CultureInfo.InvariantCulture);
                writer.Write("_x");
                writer.Write(code);
                writer.Write('_');
            }
            else
            {
                var entity = ch switch
                {
                    '&' => "&amp;",
                    '<' => "&lt;",
                    '>' => "&gt;",
                    '"' => "&quot;",
                    '\'' => "&apos;",
                    _ => null
                };
                if (entity is null)
                    continue;
                writer.Write(value.AsSpan(run, i - run));
                writer.Write(entity);
            }
            run = i + 1;
        }
        writer.Write(value.AsSpan(run));
    }

    private const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/><Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/></Types>
        """;

    private const string RootRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
        """;

    private const string WorkbookRels = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/><Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/></Relationships>
        """;

    /// <summary>Deduplicated shared-string table.</summary>
    private sealed class SharedStrings
    {
        private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
        private readonly List<string> _values = new();
        private int _total;

        public int GetIndex(string value)
        {
            _total++;
            if (_index.TryGetValue(value, out var existing))
                return existing;
            var next = _values.Count;
            _index[value] = next;
            _values.Add(value);
            return next;
        }

        /// <summary>Index of a string already registered, without counting another use.</summary>
        public int IndexOf(string value) => _index.TryGetValue(value, out var index) ? index : throw SheetChanged();

        public void Write(TextWriter writer)
        {
            writer.Write("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
            writer.Write(string.Create(CultureInfo.InvariantCulture,
                $"""<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{_total}" uniqueCount="{_values.Count}">"""));
            foreach (var value in _values)
            {
                writer.Write("<si><t xml:space=\"preserve\">");
                WriteSpreadsheetText(writer, value);
                writer.Write("</t></si>");
            }
            writer.Write("</sst>");
        }
    }

    /// <summary>
    /// Builds fonts/fills/borders/numFmts and the cellXfs combinations that reference
    /// them, deduplicating each so a 50k-row export emits a handful of styles rather
    /// than one per cell.
    /// </summary>
    private sealed class StyleTable
    {
        private readonly record struct FontKey(bool Bold, bool Italic, bool Strikethrough, string? Color);
        private readonly record struct Key(FontKey Font, string? Fill, string? Border, string? NumberFormat, XlsxAlign Align);

        private readonly record struct RawKey(bool Bold, bool Italic, bool Strikethrough, string? FontColor, string? Fill,
            string? Border, string? NumberFormat, XlsxAlign Align);

        private readonly Dictionary<Key, int> _xfIndex = new();
        private const int RawCacheLimit = 4096;
        private readonly Dictionary<RawKey, int> _rawIndex = new();
        private readonly List<Key> _xfs = new();
        private readonly List<FontKey> _fonts = new();
        private readonly List<string> _fills = new();
        private readonly List<string> _borders = new();
        private readonly List<string> _numberFormats = new();

        public StyleTable()
        {
            // fonts[0] and cellXfs[0] must be the default style.
            _fonts.Add(default);
            _xfs.Add(default);
            _xfIndex[default] = 0;
        }

        public int GetIndex(
            bool bold,
            bool italic,
            bool strikethrough,
            string? fontColor,
            string? fill,
            string? border,
            string? numberFormat,
            XlsxAlign align)
        {
            // The same inputs always normalise to the same key, so they map to the same index.
            // Bounded: callers passing endless spellings of a few styles fall back to Register.
            var raw = new RawKey(bold, italic, strikethrough, fontColor, fill, border, numberFormat, align);
            if (_rawIndex.TryGetValue(raw, out var known))
                return known;
            var index = Register(bold, italic, strikethrough, fontColor, fill, border, numberFormat, align);
            if (_rawIndex.Count < RawCacheLimit)
                _rawIndex[raw] = index;
            return index;
        }

        private int Register(
            bool bold,
            bool italic,
            bool strikethrough,
            string? fontColor,
            string? fill,
            string? border,
            string? numberFormat,
            XlsxAlign align)
        {
            fontColor = Normalize(fontColor);
            fill = Normalize(fill);
            border = Normalize(border);
            numberFormat = string.IsNullOrWhiteSpace(numberFormat) ? null : numberFormat.Trim();

            var font = new FontKey(bold, italic, strikethrough, fontColor);
            var key = new Key(font, fill, border, numberFormat, align);
            if (_xfIndex.TryGetValue(key, out var existing))
                return existing;

            if (!_fonts.Contains(font)) _fonts.Add(font);
            if (fill is not null && !_fills.Contains(fill)) _fills.Add(fill);
            if (border is not null && !_borders.Contains(border)) _borders.Add(border);
            if (numberFormat is not null && !_numberFormats.Contains(numberFormat)) _numberFormats.Add(numberFormat);

            var index = _xfs.Count;
            _xfs.Add(key);
            _xfIndex[key] = index;
            return index;
        }

        private static string? Normalize(string? hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return null;
            var value = hex.Trim().TrimStart('#');
            return value.Length == 6 ? value.ToUpperInvariant() : null;
        }

        public string ToXml()
        {
            var sb = new StringBuilder();
            sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
            sb.Append("""<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");

            if (_numberFormats.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"""<numFmts count="{_numberFormats.Count}">""");
                for (var i = 0; i < _numberFormats.Count; i++)
                    sb.Append(CultureInfo.InvariantCulture,
                        $"""<numFmt numFmtId="{164 + i}" formatCode="{Escape(_numberFormats[i])}"/>""");
                sb.Append("</numFmts>");
            }

            sb.Append(CultureInfo.InvariantCulture, $"""<fonts count="{_fonts.Count}">""");
            foreach (var font in _fonts)
            {
                sb.Append("<font>");
                if (font.Bold)
                    sb.Append("<b/>");
                if (font.Italic)
                    sb.Append("<i/>");
                if (font.Strikethrough)
                    sb.Append("<strike/>");
                sb.Append("""<sz val="11"/>""");
                if (font.Color is not null)
                    sb.Append(CultureInfo.InvariantCulture, $"""<color rgb="FF{font.Color}"/>""");
                sb.Append("""<name val="Calibri"/></font>""");
            }
            sb.Append("</fonts>");

            // Excel requires fills[0]=none and fills[1]=gray125; custom fills follow.
            sb.Append(CultureInfo.InvariantCulture, $"""<fills count="{_fills.Count + 2}">""");
            sb.Append("""<fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>""");
            foreach (var fill in _fills)
                sb.Append(CultureInfo.InvariantCulture,
                    $"""<fill><patternFill patternType="solid"><fgColor rgb="FF{fill}"/><bgColor indexed="64"/></patternFill></fill>""");
            sb.Append("</fills>");

            sb.Append(CultureInfo.InvariantCulture, $"""<borders count="{_borders.Count + 1}">""");
            sb.Append("<border><left/><right/><top/><bottom/><diagonal/></border>");
            foreach (var border in _borders)
            {
                var side = $"""<color rgb="FF{border}"/>""";
                sb.Append(CultureInfo.InvariantCulture,
                    $"""<border><left style="thin">{side}</left><right style="thin">{side}</right><top style="thin">{side}</top><bottom style="thin">{side}</bottom><diagonal/></border>""");
            }
            sb.Append("</borders>");

            sb.Append("""<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>""");

            sb.Append(CultureInfo.InvariantCulture, $"""<cellXfs count="{_xfs.Count}">""");
            foreach (var xf in _xfs)
            {
                var fontId = _fonts.IndexOf(xf.Font);
                var fillId = xf.Fill is null ? 0 : _fills.IndexOf(xf.Fill) + 2;
                var borderId = xf.Border is null ? 0 : _borders.IndexOf(xf.Border) + 1;
                var numFmtId = xf.NumberFormat is null ? 0 : 164 + _numberFormats.IndexOf(xf.NumberFormat);
                var alignment = xf.Align switch
                {
                    XlsxAlign.Center => "center",
                    XlsxAlign.Right => "right",
                    _ => null
                };

                sb.Append(CultureInfo.InvariantCulture,
                    $"""<xf numFmtId="{numFmtId}" fontId="{fontId}" fillId="{fillId}" borderId="{borderId}" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyNumberFormat="1" """);
                if (alignment is null)
                {
                    sb.Append("/>");
                }
                else
                {
                    sb.Append(CultureInfo.InvariantCulture,
                        $"""applyAlignment="1"><alignment horizontal="{alignment}"/></xf>""");
                }
            }
            sb.Append("</cellXfs>");

            sb.Append("""<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>""");
            sb.Append("</styleSheet>");
            return sb.ToString();
        }
    }
}
