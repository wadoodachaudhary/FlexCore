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

    /// <summary>Solid fill, 6-digit hex without '#'. Null leaves the cell unfilled.</summary>
    public string? FillHex { get; set; }

    /// <summary>Thin outside border, 6-digit hex without '#'. Null means no border.</summary>
    public string? BorderHex { get; set; }

    /// <summary>Excel number-format code, e.g. "#,##0.00" or "m/d/yyyy".</summary>
    public string? NumberFormat { get; set; }

    public XlsxAlign Align { get; set; } = XlsxAlign.Left;
}

/// <summary>A single worksheet: a grid of cells plus per-column widths.</summary>
public sealed class XlsxSheet
{
    public string Name { get; set; } = "Sheet1";

    /// <summary>Row-major cells. Ragged rows are allowed.</summary>
    public List<List<XlsxCell>> Rows { get; } = new();

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
/// workbook, one worksheet, a shared-string table and a style table.
/// </summary>
public static class XlsxWriter
{
    // Excel serialises dates as days since 1899-12-30 (the "1900 system", including
    // its deliberate 1900 leap-year bug, which DateTime.ToOADate already reproduces).
    private const double OaDateFloor = -657434d;

    public static byte[] Write(XlsxSheet sheet)
    {
        using var buffer = new MemoryStream();
        Write(buffer, sheet);
        return buffer.ToArray();
    }

    public static void Write(Stream destination, XlsxSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(sheet);

        var strings = new SharedStrings();
        var styles = new StyleTable();

        // Resolve every cell to a (style index, rendered value) pair up front so the
        // style and shared-string tables are complete before either part is written.
        var resolved = new List<List<ResolvedCell?>>(sheet.Rows.Count);
        foreach (var row in sheet.Rows)
        {
            var line = new List<ResolvedCell?>(row.Count);
            foreach (var cell in row)
                line.Add(cell is null ? null : Resolve(cell, strings, styles));
            resolved.Add(line);
        }

        // leaveOpen: callers pass their own stream (FilerControl hands us a file
        // stream it still owns) and must be free to keep using it afterwards.
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        WriteEntry(zip, "[Content_Types].xml", ContentTypes);
        WriteEntry(zip, "_rels/.rels", RootRels);
        WriteEntry(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
        WriteEntry(zip, "xl/workbook.xml", BuildWorkbook(sheet.Name));
        WriteEntry(zip, "xl/styles.xml", styles.ToXml());
        WriteEntry(zip, "xl/sharedStrings.xml", strings.ToXml());
        WriteEntry(zip, "xl/worksheets/sheet1.xml", BuildSheet(sheet, resolved));
    }

    private readonly record struct ResolvedCell(int StyleIndex, string? Type, string? Content, string? Formula);

    private static ResolvedCell Resolve(XlsxCell cell, SharedStrings strings, StyleTable styles)
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

        var styleIndex = styles.GetIndex(cell.Bold, cell.FillHex, cell.BorderHex, numberFormat, cell.Align);

        if (!string.IsNullOrWhiteSpace(cell.Formula))
            return new ResolvedCell(styleIndex, null, null, cell.Formula);

        switch (value)
        {
            case null:
            case DBNull:
                return new ResolvedCell(styleIndex, null, null, null);

            case bool b:
                return new ResolvedCell(styleIndex, "b", b ? "1" : "0", null);

            case string s:
                return new ResolvedCell(styleIndex, "s", strings.GetIndex(s).ToString(CultureInfo.InvariantCulture), null);

            case DateTime dt:
                return new ResolvedCell(styleIndex, null, Serial(dt.ToOADate()), null);
            case DateOnly d:
                return new ResolvedCell(styleIndex, null, Serial(d.ToDateTime(TimeOnly.MinValue).ToOADate()), null);
            case DateTimeOffset dto:
                return new ResolvedCell(styleIndex, null, Serial(dto.DateTime.ToOADate()), null);
            case TimeOnly t:
                return new ResolvedCell(styleIndex, null, Serial(t.ToTimeSpan().TotalDays), null);
            case TimeSpan ts:
                return new ResolvedCell(styleIndex, null, Serial(ts.TotalDays), null);

            case byte or sbyte or short or ushort or int or uint or long:
                return new ResolvedCell(styleIndex, null,
                    Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), null);
            case ulong u when u <= long.MaxValue:
                return new ResolvedCell(styleIndex, null, ((long)u).ToString(CultureInfo.InvariantCulture), null);
            case ulong u:
                return new ResolvedCell(styleIndex, null, Serial((double)u), null);
            case decimal m:
                return new ResolvedCell(styleIndex, null, m.ToString(CultureInfo.InvariantCulture), null);

            // Non-finite doubles have no XLSX numeric representation; ClosedXML's
            // behaviour here was to fall back to text, so keep that.
            case double dbl when double.IsNaN(dbl) || double.IsInfinity(dbl):
                return new ResolvedCell(styleIndex, "s",
                    strings.GetIndex(dbl.ToString(CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture), null);
            case double dbl:
                return new ResolvedCell(styleIndex, null, Serial(dbl), null);
            case float f when float.IsNaN(f) || float.IsInfinity(f):
                return new ResolvedCell(styleIndex, "s",
                    strings.GetIndex(f.ToString(CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture), null);
            case float f:
                return new ResolvedCell(styleIndex, null, Serial(f), null);

            default:
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                return new ResolvedCell(styleIndex, "s", strings.GetIndex(text).ToString(CultureInfo.InvariantCulture), null);
        }
    }

    private static string Serial(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "0";
        if (value < OaDateFloor)
            value = OaDateFloor;
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string BuildSheet(XlsxSheet sheet, List<List<ResolvedCell?>> resolved)
    {
        var sb = new StringBuilder(1024);
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");

        var columnCount = resolved.Count == 0 ? 0 : resolved.Max(r => r.Count);
        if (columnCount > 0)
        {
            sb.Append("<cols>");
            for (var c = 0; c < columnCount; c++)
            {
                var width = sheet.ColumnWidths.TryGetValue(c, out var explicitWidth)
                    ? explicitWidth
                    : MeasureColumn(resolved, c);
                sb.Append(CultureInfo.InvariantCulture,
                    $"""<col min="{c + 1}" max="{c + 1}" width="{width.ToString("0.##", CultureInfo.InvariantCulture)}" customWidth="1"/>""");
            }
            sb.Append("</cols>");
        }

        sb.Append("<sheetData>");
        for (var r = 0; r < resolved.Count; r++)
        {
            var row = resolved[r];
            sb.Append(CultureInfo.InvariantCulture, $"""<row r="{r + 1}">""");
            for (var c = 0; c < row.Count; c++)
            {
                var cell = row[c];
                if (cell is null)
                    continue;

                var reference = $"{ColumnName(c)}{r + 1}";
                var style = cell.Value.StyleIndex;

                // An empty, unstyled cell carries no information — omit it entirely.
                if (cell.Value.Content is null && cell.Value.Formula is null && style == 0)
                    continue;

                sb.Append(CultureInfo.InvariantCulture, $"""<c r="{reference}" s="{style}" """);
                if (cell.Value.Type is not null)
                    sb.Append(CultureInfo.InvariantCulture, $"""t="{cell.Value.Type}" """);
                sb.Append('>');

                if (cell.Value.Formula is not null)
                    sb.Append("<f>").Append(Escape(cell.Value.Formula)).Append("</f>");
                else if (cell.Value.Content is not null)
                    sb.Append("<v>").Append(Escape(cell.Value.Content)).Append("</v>");

                sb.Append("</c>");
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    // Content-based sizing, standing in for ClosedXML's AdjustToContents. Shared
    // strings are the only content whose display length we know cheaply; numbers are
    // close enough at their serialised length.
    private static double MeasureColumn(List<List<ResolvedCell?>> resolved, int column)
    {
        var widest = 0;
        foreach (var row in resolved)
        {
            if (column >= row.Count || row[column] is not { } cell)
                continue;
            var length = cell.Formula?.Length ?? cell.Content?.Length ?? 0;
            if (length > widest)
                widest = length;
        }
        return Math.Clamp(widest + 2, 8.43, 80);
    }

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

    private static string Escape(string value) =>
        new XmlDocument().CreateTextNode(value).OuterXml;

    private static void WriteEntry(ZipArchive zip, string path, string xml)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(xml);
        stream.Write(bytes, 0, bytes.Length);
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

        public string ToXml()
        {
            var sb = new StringBuilder();
            sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
            sb.Append(CultureInfo.InvariantCulture,
                $"""<sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="{_total}" uniqueCount="{_values.Count}">""");
            foreach (var value in _values)
                sb.Append("<si><t xml:space=\"preserve\">").Append(Escape(value)).Append("</t></si>");
            sb.Append("</sst>");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Builds fonts/fills/borders/numFmts and the cellXfs combinations that reference
    /// them, deduplicating each so a 50k-row export emits a handful of styles rather
    /// than one per cell.
    /// </summary>
    private sealed class StyleTable
    {
        private readonly record struct Key(bool Bold, string? Fill, string? Border, string? NumberFormat, XlsxAlign Align);

        private readonly Dictionary<Key, int> _xfIndex = new();
        private readonly List<Key> _xfs = new();
        private readonly List<string> _fills = new();
        private readonly List<string> _borders = new();
        private readonly List<string> _numberFormats = new();

        public StyleTable()
        {
            // cellXfs[0] must be the default style.
            _xfs.Add(default);
            _xfIndex[default] = 0;
        }

        public int GetIndex(bool bold, string? fill, string? border, string? numberFormat, XlsxAlign align)
        {
            fill = Normalize(fill);
            border = Normalize(border);
            numberFormat = string.IsNullOrWhiteSpace(numberFormat) ? null : numberFormat.Trim();

            var key = new Key(bold, fill, border, numberFormat, align);
            if (_xfIndex.TryGetValue(key, out var existing))
                return existing;

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

            sb.Append("""<fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>""");

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
                var fontId = xf.Bold ? 1 : 0;
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
