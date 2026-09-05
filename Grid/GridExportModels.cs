using ClosedXML.Excel;
using Microsoft.JSInterop;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Fx.ControlKit.Grid;

public enum GridExportFormat
{
    Csv,
    Tsv,
    Html,
    Xls,
    Xlsx,
    Pdf,
    Json
}

/// <summary>
/// Character encoding used by CSV and TSV exports. Other export formats ignore
/// this setting. <see cref="GridDelimitedTextEncoding.Utf8NoBom"/> preserves
/// GridControl's historical byte output and remains the default.
/// </summary>
public enum GridDelimitedTextEncoding
{
    /// <summary>UTF-8 without a byte-order mark (the default).</summary>
    Utf8NoBom,

    /// <summary>UTF-8 prefixed with the standard EF BB BF byte-order mark.</summary>
    Utf8WithBom,

    /// <summary>Little-endian UTF-16 prefixed with the FF FE byte-order mark.</summary>
    Utf16LittleEndian,

    /// <summary>7-bit ASCII. Characters outside ASCII are replaced with '?'.</summary>
    Ascii
}

public enum GridPdfOrientation
{
    Portrait,
    Landscape
}

public enum GridPdfPageSize
{
    Letter,
    Legal,
    A4
}

public enum GridPdfColumnLayout
{
    WrapText,
    ClipText,
    FitColumnsToPage
}

public enum GridPdfZoomMode
{
    FitToPage,
    Percent
}

/// <summary>
/// Optional leading decoration rendered for a column's data cells in PDF output.
/// This lets a templated grid column retain a meaningful visual cue when the
/// template itself cannot be serialized into the document.
/// </summary>
public enum GridPrintCellIcon
{
    None,
    IdentityCard
}

public sealed class GridPdfPrintOptions
{
    public GridPdfOrientation Orientation { get; set; } = GridPdfOrientation.Portrait;
    public GridPdfPageSize PageSize { get; set; } = GridPdfPageSize.Letter;
    public GridPdfColumnLayout ColumnLayout { get; set; } = GridPdfColumnLayout.WrapText;
    public double Margin { get; set; } = 24;
    public int MaxWrappedLines { get; set; } = 4;
    public bool IncludeColumnHeaders { get; set; } = true;
    public bool ShowGridLines { get; set; } = true;
    public GridPdfZoomMode ZoomMode { get; set; } = GridPdfZoomMode.FitToPage;
    public int ZoomPercent { get; set; } = 100;
}

public sealed class GridExportColumn
{
    public GridExportColumn(
        string header,
        string? format = null,
        TextAlign textAlign = TextAlign.Left,
        double? width = null,
        GridPrintCellIcon printCellIcon = GridPrintCellIcon.None)
    {
        Header = header;
        Format = format;
        TextAlign = textAlign;
        Width = width;
        PrintCellIcon = printCellIcon;
    }

    public string Header { get; set; }
    /// <summary>
    /// Source field name used to translate safe same-row grid formulas to A1
    /// references during XLSX export. It has no effect on other formats.
    /// </summary>
    public string? Field { get; set; }

    /// <summary>
    /// Optional GridColumn formula metadata. XLSX exports preserve arithmetic
    /// formulas whose field references can all be mapped to exported columns;
    /// unsupported formulas retain the row's already-evaluated value.
    /// </summary>
    public string? Formula { get; set; }

    public string? Format { get; set; }
    public TextAlign TextAlign { get; set; }
    public double? Width { get; set; }
    public GridPrintCellIcon PrintCellIcon { get; set; }
}

public sealed class GridExportRow
{
    public GridExportRow()
    {
    }

    public GridExportRow(IEnumerable<object?> values, bool isBold = false)
    {
        Values.AddRange(values);
        IsBold = isBold;
    }

    public List<object?> Values { get; } = new();

    /// <summary>
    /// Optional native CLR values used only by XLSX output. <see cref="Values"/>
    /// remains the formatted/display representation used by CSV, TSV, HTML,
    /// legacy XLS, JSON and PDF so adding typed spreadsheets does not change
    /// those established exports.
    /// </summary>
    public List<object?> XlsxValues { get; } = new();

    /// <summary>Optional per-cell .NET formats for native XLSX values.</summary>
    public List<string?> XlsxFormats { get; } = new();
    public bool IsBold { get; set; }

    /// <summary>
    /// Allows XLSX output to apply formulas declared by the corresponding
    /// <see cref="GridExportColumn"/>. Set false for aggregate or summary rows.
    /// </summary>
    public bool UseColumnFormulas { get; set; } = true;
}

public sealed class GridExportTable
{
    public string Title { get; set; } = "Export";
    public string SheetName { get; set; } = "Export";
    public bool IncludeHeaderRow { get; set; } = true;
    public List<GridExportColumn> Columns { get; } = new();
    public List<GridExportRow> Rows { get; } = new();
    public HashSet<int> HighlightColumnIndexes { get; } = new();
}

public sealed record GridExportResult(byte[] Bytes, string FileName, string ContentType);

public static class GridExporter
{
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string XlsMime = "application/vnd.ms-excel";
    private const string PdfMime = "application/pdf";
    private const string CsvMime = "text/csv";
    private const string TsvMime = "text/tab-separated-values";
    private const string HtmlMime = "text/html";
    private const string JsonMime = "application/json";

    public static GridExportResult Export(
        GridExportTable table,
        GridExportFormat format,
        string? fileName = null,
        GridPdfPrintOptions? pdfOptions = null)
        => ExportWithEncoding(table, format, GridDelimitedTextEncoding.Utf8NoBom, fileName, pdfOptions);

    /// <summary>
    /// Exports a table with an explicit CSV/TSV encoding. The encoding is
    /// ignored for non-delimited formats.
    /// </summary>
    public static GridExportResult ExportWithEncoding(
        GridExportTable table,
        GridExportFormat format,
        GridDelimitedTextEncoding delimitedEncoding,
        string? fileName = null,
        GridPdfPrintOptions? pdfOptions = null)
    {
        var resolvedName = EnsureExtension(
            string.IsNullOrWhiteSpace(fileName) ? SanitizeFileName(table.Title) : fileName!,
            DefaultExtension(format));

        return format switch
        {
            GridExportFormat.Csv => new GridExportResult(
                BuildDelimited(table, ",", delimitedEncoding),
                resolvedName,
                ResolveDelimitedContentType(CsvMime, delimitedEncoding)),
            GridExportFormat.Tsv => new GridExportResult(
                BuildDelimited(table, "\t", delimitedEncoding),
                resolvedName,
                ResolveDelimitedContentType(TsvMime, delimitedEncoding)),
            GridExportFormat.Html => new GridExportResult(BuildHtml(table, standalone: true), resolvedName, HtmlMime),
            GridExportFormat.Xls => new GridExportResult(BuildHtml(table, standalone: false), resolvedName, XlsMime),
            GridExportFormat.Xlsx => new GridExportResult(BuildXlsx(table), resolvedName, XlsxMime),
            GridExportFormat.Pdf => new GridExportResult(BuildPdf(table, pdfOptions), resolvedName, PdfMime),
            GridExportFormat.Json => new GridExportResult(BuildJson(table), resolvedName, JsonMime),
            _ => new GridExportResult(BuildXlsx(table), resolvedName, XlsxMime)
        };
    }

    public static async Task DownloadAsync(IJSRuntime jsRuntime, GridExportResult export)
    {
        var module = await ImportGridModuleAsync(jsRuntime);
        if (module == null)
            return;

        await module.InvokeVoidAsync("downloadFile", export.FileName, Convert.ToBase64String(export.Bytes), export.ContentType);
    }

    public static async Task<string> SaveAsync(IJSRuntime jsRuntime, GridExportResult export)
    {
        var module = await ImportGridModuleAsync(jsRuntime);
        if (module == null)
            return "unavailable";

        return await module.InvokeAsync<string>("saveFile", export.FileName, Convert.ToBase64String(export.Bytes), export.ContentType);
    }

    public static string DefaultExtension(GridExportFormat format) => format switch
    {
        GridExportFormat.Csv => ".csv",
        GridExportFormat.Tsv => ".tsv",
        GridExportFormat.Html => ".html",
        GridExportFormat.Xls => ".xls",
        GridExportFormat.Xlsx => ".xlsx",
        GridExportFormat.Pdf => ".pdf",
        GridExportFormat.Json => ".json",
        _ => ".xlsx"
    };

    private static async ValueTask<IJSObjectReference?> ImportGridModuleAsync(IJSRuntime jsRuntime)
    {
        try
        {
            var modulePath = FxJsAsset.Versioned($"./_content/{typeof(GridExporter).Assembly.GetName().Name}/grid-control.js");
            return await jsRuntime.InvokeAsync<IJSObjectReference>("import", modulePath);
        }
        catch
        {
            return null;
        }
    }

    private static byte[] BuildXlsx(GridExportTable table)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(SanitizeSheetName(table.SheetName));
        var formulaFields = BuildFormulaFieldMap(table.Columns);
        var rowIndex = 1;

        if (table.IncludeHeaderRow && table.Columns.Count > 0)
        {
            worksheet.Row(rowIndex).Style.Font.Bold = true;
            for (var colIndex = 0; colIndex < table.Columns.Count; colIndex++)
            {
                var cell = worksheet.Cell(rowIndex, colIndex + 1);
                cell.SetValue(table.Columns[colIndex].Header);
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#eeeeee");
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#a6a6a6");
            }
            rowIndex++;
        }

        foreach (var row in table.Rows)
        {
            for (var colIndex = 0; colIndex < Math.Max(table.Columns.Count, row.Values.Count); colIndex++)
            {
                var cell = worksheet.Cell(rowIndex, colIndex + 1);
                var value = colIndex < row.XlsxValues.Count
                    ? row.XlsxValues[colIndex]
                    : colIndex < row.Values.Count
                        ? row.Values[colIndex]
                        : null;
                if (row.UseColumnFormulas
                    && colIndex < table.Columns.Count
                    && TryTranslateSameRowFormula(
                        table.Columns,
                        formulaFields,
                        colIndex,
                        rowIndex,
                        out var formula))
                {
                    cell.FormulaA1 = formula;
                }
                else
                {
                    SetCellValue(cell, value);
                }

                if (row.IsBold)
                    cell.Style.Font.Bold = true;

                if (table.HighlightColumnIndexes.Contains(colIndex))
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#ffffc1");

                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#e6e6e6");

                if (colIndex < table.Columns.Count)
                {
                    var column = table.Columns[colIndex];
                    var sourceFormat = colIndex < row.XlsxFormats.Count
                        ? row.XlsxFormats[colIndex]
                        : column.Format;
                    var numberFormat = ResolveXlsxNumberFormat(sourceFormat, value);
                    if (!string.IsNullOrWhiteSpace(numberFormat))
                        cell.Style.NumberFormat.Format = numberFormat;

                    cell.Style.Alignment.Horizontal = column.TextAlign switch
                    {
                        TextAlign.Center => XLAlignmentHorizontalValues.Center,
                        TextAlign.Right => XLAlignmentHorizontalValues.Right,
                        _ => XLAlignmentHorizontalValues.Left
                    };
                }
            }
            rowIndex++;
        }

        for (var colIndex = 0; colIndex < table.Columns.Count; colIndex++)
        {
            if (table.Columns[colIndex].Width is double width)
                worksheet.Column(colIndex + 1).Width = ConvertPixelsToExcelColumnWidth(width);
            else
                worksheet.Column(colIndex + 1).AdjustToContents();
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static double ConvertPixelsToExcelColumnWidth(double width)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return 8.43;

        // GridControl stores runtime widths as CSS pixels. ClosedXML expects
        // Excel's character-based column width, so passing pixels directly
        // makes normal grid columns become half-page-wide Excel columns.
        var excelWidth = (width - 5) / 7d;
        return Math.Clamp(excelWidth, 4, 80);
    }

    private static string? ResolveXlsxNumberFormat(string? dotNetFormat, object? value)
    {
        if (string.IsNullOrWhiteSpace(dotNetFormat))
            return null;

        var format = dotNetFormat.Trim();
        var isDate = value is DateTime or DateOnly or DateTimeOffset;
        var isTime = value is TimeOnly or TimeSpan;
        var isNumeric = value is byte or sbyte or short or ushort or int or uint
            or long or ulong or float or double or decimal;

        if (!isDate && !isTime && !isNumeric)
            return null;

        if (isDate || isTime)
        {
            if (format.Length == 1)
            {
                return format[0] switch
                {
                    'd' => "m/d/yyyy",
                    'D' => "dddd, mmmm d, yyyy",
                    't' => "h:mm AM/PM",
                    'T' => "h:mm:ss AM/PM",
                    'g' => "m/d/yyyy h:mm",
                    'G' => "m/d/yyyy h:mm:ss",
                    'M' or 'm' => "mmmm d",
                    'Y' or 'y' => "mmmm yyyy",
                    's' => "yyyy-mm-dd hh:mm:ss",
                    'u' => "yyyy-mm-dd hh:mm:ss",
                    'O' or 'o' => "yyyy-mm-dd hh:mm:ss.000",
                    _ => null
                };
            }

            // Most custom .NET date patterns have direct Excel equivalents.
            // Normalize the tokens whose spelling differs. Time-zone tokens
            // have no Excel-cell equivalent and are intentionally omitted.
            return format
                .Replace("tt", "AM/PM", StringComparison.Ordinal)
                .Replace("fff", "000", StringComparison.Ordinal)
                .Replace("ff", "00", StringComparison.Ordinal)
                .Replace("HH", "hh", StringComparison.Ordinal)
                .Replace("H", "h", StringComparison.Ordinal)
                .Replace("MM", "mm", StringComparison.Ordinal)
                .Replace("M", "m", StringComparison.Ordinal)
                .Replace("zzz", string.Empty, StringComparison.Ordinal)
                .Replace("zz", string.Empty, StringComparison.Ordinal)
                .Replace("K", string.Empty, StringComparison.Ordinal)
                .Trim();
        }

        var specifier = format[0];
        var precisionText = format.Length > 1 ? format[1..] : string.Empty;
        var isStandard = precisionText.Length == 0
            || precisionText.All(char.IsDigit);
        if (!isStandard)
            return format;

        var precision = precisionText.Length > 0
            && int.TryParse(precisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                ? Math.Clamp(parsed, 0, 30)
                : 2;
        var decimals = precision == 0 ? string.Empty : "." + new string('0', precision);

        return specifier switch
        {
            'C' or 'c' => $"\"{EscapeExcelFormatLiteral(CultureInfo.CurrentCulture.NumberFormat.CurrencySymbol)}\"#,##0{decimals}",
            'N' or 'n' => $"#,##0{decimals}",
            'F' or 'f' => $"0{decimals}",
            'P' or 'p' => $"0{decimals}%",
            'E' or 'e' => $"0{decimals}E+00",
            'D' or 'd' => new string('0', precisionText.Length == 0 ? 1 : precision),
            'G' or 'g' => "General",
            _ => format
        };
    }

    private static string EscapeExcelFormatLiteral(string value) =>
        value.Replace("\"", "\"\"", StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, int> BuildFormulaFieldMap(
        IReadOnlyList<GridExportColumn> columns)
    {
        var fields = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ambiguousFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < columns.Count; index++)
        {
            var field = columns[index].Field?.Trim();
            if (string.IsNullOrWhiteSpace(field) || ambiguousFields.Contains(field))
                continue;

            if (fields.ContainsKey(field))
            {
                fields.Remove(field);
                ambiguousFields.Add(field);
                continue;
            }

            fields[field] = index;
        }

        return fields;
    }

    /// <summary>
    /// Translates only FlexCore's documented arithmetic formula subset. All
    /// references must identify a unique exported field in the current row.
    /// Function calls, ranges, sheet/external references, string literals,
    /// formula-column dependencies, and circular/self references deliberately
    /// fall back to the evaluated value already held by the export row.
    /// </summary>
    private static bool TryTranslateSameRowFormula(
        IReadOnlyList<GridExportColumn> columns,
        IReadOnlyDictionary<string, int> formulaFields,
        int formulaColumnIndex,
        int rowIndex,
        out string formula)
    {
        formula = string.Empty;
        var source = columns[formulaColumnIndex].Formula?.Trim();
        if (string.IsNullOrWhiteSpace(source))
            return false;

        if (source.StartsWith("=", StringComparison.Ordinal))
            source = source[1..].Trim();

        if (source.Length == 0 || source.Length > 4_096)
            return false;

        var tokens = new List<XlsxFormulaToken>();
        var translated = new StringBuilder(source.Length + 16);
        var position = 0;

        while (position < source.Length)
        {
            var current = source[position];
            if (char.IsWhiteSpace(current))
            {
                position++;
                continue;
            }

            if (current == '[')
            {
                var close = source.IndexOf(']', position + 1);
                if (close < 0 || source.IndexOf('[', position + 1, close - position - 1) >= 0)
                    return false;

                var field = source[(position + 1)..close].Trim();
                if (!TryAppendFormulaReference(
                        columns,
                        formulaFields,
                        formulaColumnIndex,
                        rowIndex,
                        field,
                        translated))
                {
                    return false;
                }

                tokens.Add(new XlsxFormulaToken(XlsxFormulaTokenKind.Operand));
                position = close + 1;
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                var start = position++;
                while (position < source.Length
                       && (char.IsLetterOrDigit(source[position])
                           || source[position] is '_' or '.'))
                {
                    position++;
                }

                var field = source[start..position];
                if (!TryAppendFormulaReference(
                        columns,
                        formulaFields,
                        formulaColumnIndex,
                        rowIndex,
                        field,
                        translated))
                {
                    return false;
                }

                tokens.Add(new XlsxFormulaToken(XlsxFormulaTokenKind.Operand));
                continue;
            }

            if (char.IsDigit(current) || (current == '.' && position + 1 < source.Length && char.IsDigit(source[position + 1])))
            {
                var start = position;
                if (current == '.')
                    position++;

                while (position < source.Length && char.IsDigit(source[position]))
                    position++;

                if (position < source.Length && source[position] == '.')
                {
                    position++;
                    while (position < source.Length && char.IsDigit(source[position]))
                        position++;
                }

                if (position < source.Length && source[position] is 'e' or 'E')
                {
                    position++;
                    if (position < source.Length && source[position] is '+' or '-')
                        position++;
                    var exponentDigits = position;
                    while (position < source.Length && char.IsDigit(source[position]))
                        position++;
                    if (position == exponentDigits)
                        return false;
                }

                translated.Append(source, start, position - start);
                tokens.Add(new XlsxFormulaToken(XlsxFormulaTokenKind.Operand));
                continue;
            }

            var tokenKind = current switch
            {
                '+' => XlsxFormulaTokenKind.Plus,
                '-' => XlsxFormulaTokenKind.Minus,
                '*' => XlsxFormulaTokenKind.Multiply,
                '/' => XlsxFormulaTokenKind.Divide,
                '^' => XlsxFormulaTokenKind.Power,
                '(' => XlsxFormulaTokenKind.LeftParenthesis,
                ')' => XlsxFormulaTokenKind.RightParenthesis,
                _ => XlsxFormulaTokenKind.Invalid
            };

            if (tokenKind == XlsxFormulaTokenKind.Invalid)
                return false;

            translated.Append(current);
            tokens.Add(new XlsxFormulaToken(tokenKind));
            position++;
        }

        if (!XlsxFormulaParser.IsValid(tokens))
            return false;

        formula = translated.ToString();
        return true;
    }

    private static bool TryAppendFormulaReference(
        IReadOnlyList<GridExportColumn> columns,
        IReadOnlyDictionary<string, int> formulaFields,
        int formulaColumnIndex,
        int rowIndex,
        string field,
        StringBuilder translated)
    {
        if (string.IsNullOrWhiteSpace(field)
            || !formulaFields.TryGetValue(field, out var referencedColumnIndex)
            || referencedColumnIndex == formulaColumnIndex
            || !string.IsNullOrWhiteSpace(columns[referencedColumnIndex].Formula))
        {
            return false;
        }

        translated.Append(ToExcelColumnName(referencedColumnIndex + 1));
        translated.Append(rowIndex.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private static string ToExcelColumnName(int oneBasedIndex)
    {
        var result = new StringBuilder(4);
        var index = oneBasedIndex;
        while (index > 0)
        {
            index--;
            result.Insert(0, (char)('A' + (index % 26)));
            index /= 26;
        }

        return result.ToString();
    }

    private enum XlsxFormulaTokenKind
    {
        Invalid,
        Operand,
        Plus,
        Minus,
        Multiply,
        Divide,
        Power,
        LeftParenthesis,
        RightParenthesis
    }

    private readonly record struct XlsxFormulaToken(XlsxFormulaTokenKind Kind);

    private sealed class XlsxFormulaParser
    {
        private readonly IReadOnlyList<XlsxFormulaToken> _tokens;
        private int _position;

        private XlsxFormulaParser(IReadOnlyList<XlsxFormulaToken> tokens)
        {
            _tokens = tokens;
        }

        public static bool IsValid(IReadOnlyList<XlsxFormulaToken> tokens)
        {
            if (tokens.Count == 0)
                return false;

            var parser = new XlsxFormulaParser(tokens);
            return parser.ParseExpression() && parser._position == tokens.Count;
        }

        private bool ParseExpression()
        {
            if (!ParseTerm())
                return false;

            while (Match(XlsxFormulaTokenKind.Plus) || Match(XlsxFormulaTokenKind.Minus))
            {
                if (!ParseTerm())
                    return false;
            }

            return true;
        }

        private bool ParseTerm()
        {
            if (!ParsePower())
                return false;

            while (Match(XlsxFormulaTokenKind.Multiply) || Match(XlsxFormulaTokenKind.Divide))
            {
                if (!ParsePower())
                    return false;
            }

            return true;
        }

        private bool ParsePower()
        {
            if (!ParseUnary())
                return false;

            if (Match(XlsxFormulaTokenKind.Power))
                return ParsePower();

            return true;
        }

        private bool ParseUnary()
        {
            if (Match(XlsxFormulaTokenKind.Plus) || Match(XlsxFormulaTokenKind.Minus))
                return ParseUnary();

            return ParsePrimary();
        }

        private bool ParsePrimary()
        {
            if (Match(XlsxFormulaTokenKind.Operand))
                return true;

            if (!Match(XlsxFormulaTokenKind.LeftParenthesis))
                return false;

            return ParseExpression() && Match(XlsxFormulaTokenKind.RightParenthesis);
        }

        private bool Match(XlsxFormulaTokenKind kind)
        {
            if (_position >= _tokens.Count || _tokens[_position].Kind != kind)
                return false;

            _position++;
            return true;
        }
    }

    private static byte[] BuildDelimited(
        GridExportTable table,
        string delimiter,
        GridDelimitedTextEncoding encoding)
    {
        var sb = new StringBuilder();
        if (table.IncludeHeaderRow && table.Columns.Count > 0)
            sb.AppendLine(string.Join(delimiter, table.Columns.Select(c => EscapeDelimited(c.Header, delimiter))));

        foreach (var row in table.Rows)
            sb.AppendLine(string.Join(delimiter, row.Values.Select(v => EscapeDelimited(ToExportString(v), delimiter))));

        return EncodeDelimitedText(sb.ToString(), encoding);
    }

    private static byte[] EncodeDelimitedText(
        string value,
        GridDelimitedTextEncoding encodingChoice)
    {
        Encoding encoding;
        var includePreamble = false;

        switch (encodingChoice)
        {
            case GridDelimitedTextEncoding.Utf8WithBom:
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
                includePreamble = true;
                break;
            case GridDelimitedTextEncoding.Utf16LittleEndian:
                encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
                includePreamble = true;
                break;
            case GridDelimitedTextEncoding.Ascii:
                encoding = Encoding.ASCII;
                break;
            default:
                // This exactly matches the historical Encoding.UTF8.GetBytes
                // path: UTF-8 bytes without a preamble.
                encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                break;
        }

        var content = encoding.GetBytes(value);
        if (!includePreamble)
            return content;

        var preamble = encoding.GetPreamble();
        if (preamble.Length == 0)
            return content;

        var result = new byte[preamble.Length + content.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(content, 0, result, preamble.Length, content.Length);
        return result;
    }

    private static string ResolveDelimitedContentType(
        string baseContentType,
        GridDelimitedTextEncoding encoding) => encoding switch
        {
            GridDelimitedTextEncoding.Utf8WithBom => $"{baseContentType}; charset=utf-8",
            GridDelimitedTextEncoding.Utf16LittleEndian => $"{baseContentType}; charset=utf-16le",
            GridDelimitedTextEncoding.Ascii => $"{baseContentType}; charset=us-ascii",
            _ => baseContentType
        };

    private static byte[] BuildHtml(GridExportTable table, bool standalone)
    {
        var sb = new StringBuilder();
        if (standalone)
        {
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>")
              .Append(Html(table.Title))
              .Append("</title>");
        }

        sb.Append("<style>table{border-collapse:collapse;font:11px Arial,sans-serif}th,td{border:1px solid #ccc;padding:2px 5px;text-align:left}th{background:#eee;font-weight:bold}.bold td{font-weight:bold}.highlight{background:#ffffc1}</style>");
        if (standalone)
            sb.Append("</head><body>");

        sb.Append("<table><thead><tr>");
        foreach (var column in table.Columns)
            sb.Append("<th>").Append(Html(column.Header)).Append("</th>");
        sb.Append("</tr></thead><tbody>");

        foreach (var row in table.Rows)
        {
            sb.Append(row.IsBold ? "<tr class=\"bold\">" : "<tr>");
            for (var colIndex = 0; colIndex < Math.Max(table.Columns.Count, row.Values.Count); colIndex++)
            {
                var css = table.HighlightColumnIndexes.Contains(colIndex) ? " class=\"highlight\"" : "";
                var align = colIndex < table.Columns.Count && table.Columns[colIndex].TextAlign != TextAlign.Left
                    ? $" style=\"text-align:{(table.Columns[colIndex].TextAlign == TextAlign.Right ? "right" : "center")}\""
                    : "";
                sb.Append("<td").Append(css).Append(align).Append(">")
                  .Append(Html(colIndex < row.Values.Count ? ToExportString(row.Values[colIndex]) : ""))
                  .Append("</td>");
            }
            sb.Append("</tr>");
        }

        sb.Append("</tbody></table>");
        if (standalone)
            sb.Append("</body></html>");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] BuildJson(GridExportTable table)
    {
        var headers = table.Columns.Select(c => c.Header).ToList();
        var rows = table.Rows.Select(row =>
        {
            var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < row.Values.Count; i++)
            {
                var key = i < headers.Count && !string.IsNullOrWhiteSpace(headers[i])
                    ? headers[i]
                    : $"Column{i + 1}";
                dict[key] = row.Values[i];
            }
            return dict;
        });

        return JsonSerializer.SerializeToUtf8Bytes(rows, new JsonSerializerOptions { WriteIndented = true });
    }

    private static byte[] BuildPdf(GridExportTable table, GridPdfPrintOptions? pdfOptions)
    {
        var options = pdfOptions ?? new GridPdfPrintOptions();
        var (pageWidth, pageHeight) = ResolvePdfPageSize(options.PageSize, options.Orientation);
        var margin = Math.Clamp(options.Margin, 12, 72);
        var contentWidth = Math.Max(72, pageWidth - (margin * 2));
        var effectiveColumnLayout = ResolvePdfColumnLayout(options);
        var zoomScale = ResolvePdfZoomScale(options, table, contentWidth);
        var fontSize = 8.5 * zoomScale;
        var headerFontSize = 8.5 * zoomScale;
        var lineHeight = 10.75 * zoomScale;
        var cellPaddingX = 3.25 * zoomScale;
        var cellPaddingY = 2.25 * zoomScale;
        var headerHeight = lineHeight + (cellPaddingY * 2);
        var baseRowHeight = lineHeight + (cellPaddingY * 2);
        var maxWrappedLines = Math.Clamp(options.MaxWrappedLines, 1, 12);

        var pages = new List<string>();
        if (table.Columns.Count == 0)
        {
            pages.Add(string.Empty);
            return PdfDocumentWriter.Write(pageWidth, pageHeight, pages);
        }

        var columnWidths = ResolvePdfColumnWidths(table, contentWidth, fontSize, effectiveColumnLayout, zoomScale);
        var columnSegments = BuildPdfColumnSegments(columnWidths, contentWidth, effectiveColumnLayout);
        var rows = table.Rows.ToList();

        foreach (var segment in columnSegments)
        {
            if (rows.Count == 0)
            {
                var headerOnly = new StringBuilder();
                var y = pageHeight - margin;
                if (options.IncludeColumnHeaders && table.IncludeHeaderRow)
                {
                    DrawPdfRow(
                        headerOnly,
                        table,
                        null,
                        segment.Start,
                        segment.Count,
                        columnWidths,
                        margin,
                        y,
                        headerHeight,
                        isHeader: true,
                        GridPdfColumnLayout.ClipText,
                        headerFontSize,
                        lineHeight,
                        cellPaddingX,
                        cellPaddingY,
                        maxWrappedLines,
                        options.ShowGridLines);
                }
                pages.Add(headerOnly.ToString());
                continue;
            }

            var rowIndex = 0;
            while (rowIndex < rows.Count)
            {
                var sb = new StringBuilder();
                var y = pageHeight - margin;

                if (options.IncludeColumnHeaders && table.IncludeHeaderRow)
                {
                    DrawPdfRow(
                        sb,
                        table,
                        null,
                        segment.Start,
                        segment.Count,
                        columnWidths,
                        margin,
                        y,
                        headerHeight,
                        isHeader: true,
                        GridPdfColumnLayout.ClipText,
                        headerFontSize,
                        lineHeight,
                        cellPaddingX,
                        cellPaddingY,
                        maxWrappedLines,
                        options.ShowGridLines);
                    y -= headerHeight;
                }

                var wroteDataRow = false;
                while (rowIndex < rows.Count)
                {
                    var row = rows[rowIndex];
                    var rowHeight = ComputePdfRowHeight(
                        table,
                        row,
                        segment.Start,
                        segment.Count,
                        columnWidths,
                        effectiveColumnLayout,
                        fontSize,
                        lineHeight,
                        cellPaddingX,
                        cellPaddingY,
                        maxWrappedLines);

                    if (wroteDataRow && y - rowHeight < margin)
                        break;

                    if (!wroteDataRow && y - rowHeight < margin)
                        rowHeight = Math.Max(baseRowHeight, y - margin);

                    DrawPdfRow(
                        sb,
                        table,
                        row,
                        segment.Start,
                        segment.Count,
                        columnWidths,
                        margin,
                        y,
                        rowHeight,
                        isHeader: false,
                        effectiveColumnLayout,
                        fontSize,
                        lineHeight,
                        cellPaddingX,
                        cellPaddingY,
                        maxWrappedLines,
                        options.ShowGridLines);

                    y -= rowHeight;
                    rowIndex++;
                    wroteDataRow = true;
                }

                pages.Add(sb.ToString());
            }
        }

        return PdfDocumentWriter.Write(pageWidth, pageHeight, pages.Count > 0 ? pages : new List<string> { string.Empty });
    }

    private static void DrawPdfRow(
        StringBuilder sb,
        GridExportTable table,
        GridExportRow? row,
        int startColumn,
        int columnCount,
        IReadOnlyList<double> columnWidths,
        double left,
        double top,
        double rowHeight,
        bool isHeader,
        GridPdfColumnLayout columnLayout,
        double fontSize,
        double lineHeight,
        double paddingX,
        double paddingY,
        int maxWrappedLines,
        bool showGridLines)
    {
        var x = left;
        for (var offset = 0; offset < columnCount; offset++)
        {
            var columnIndex = startColumn + offset;
            var width = columnWidths[columnIndex];
            var text = isHeader
                ? table.Columns[columnIndex].Header
                : columnIndex < (row?.Values.Count ?? 0) ? ToExportString(row!.Values[columnIndex]) : string.Empty;
            var align = isHeader ? TextAlign.Left : table.Columns[columnIndex].TextAlign;
            var highlighted = !isHeader && table.HighlightColumnIndexes.Contains(columnIndex);
            var printCellIcon = isHeader
                ? GridPrintCellIcon.None
                : table.Columns[columnIndex].PrintCellIcon;

            DrawPdfCell(
                sb,
                text,
                x,
                top,
                width,
                rowHeight,
                align,
                isHeader,
                row?.IsBold == true,
                highlighted,
                printCellIcon,
                columnLayout,
                fontSize,
                lineHeight,
                paddingX,
                paddingY,
                maxWrappedLines,
                showGridLines);

            x += width;
        }
    }

    private static void DrawPdfCell(
        StringBuilder sb,
        string text,
        double x,
        double top,
        double width,
        double height,
        TextAlign align,
        bool isHeader,
        bool isBold,
        bool isHighlighted,
        GridPrintCellIcon printCellIcon,
        GridPdfColumnLayout columnLayout,
        double fontSize,
        double lineHeight,
        double paddingX,
        double paddingY,
        int maxWrappedLines,
        bool showGridLines)
    {
        var bottom = top - height;
        if (isHeader)
            AppendPdfFilledRectangle(sb, x, bottom, width, height, "0.92 0.92 0.92");
        else if (isHighlighted)
            AppendPdfFilledRectangle(sb, x, bottom, width, height, "1 1 0.86");

        if (showGridLines)
            AppendPdfBorder(sb, x, bottom, width, height);

        var iconInset = 0d;
        if (printCellIcon != GridPrintCellIcon.None && !string.IsNullOrWhiteSpace(text))
        {
            var iconHeight = Math.Max(1.5, Math.Min(9, height - (paddingY * 2)));
            iconInset = DrawPdfCellIcon(
                sb,
                printCellIcon,
                x + paddingX,
                bottom + Math.Max(0, (height - iconHeight) / 2),
                iconHeight) + Math.Max(1, paddingX * 0.6);
        }

        var contentLeft = x + paddingX + iconInset;
        var availableWidth = Math.Max(1, width - (paddingX * 2) - iconInset);
        var availableLines = Math.Max(1, (int)Math.Floor(Math.Max(lineHeight, height - (paddingY * 2)) / lineHeight));
        var effectiveMaxLines = Math.Min(maxWrappedLines, availableLines);
        var lines = GetPdfCellLines(text, availableWidth, fontSize, columnLayout, effectiveMaxLines);
        var fontName = isHeader || isBold ? "F2" : "F1";
        var baseline = top - paddingY - fontSize;

        foreach (var line in lines.Take(availableLines))
        {
            var lineWidth = EstimatePdfTextWidth(line, fontSize);
            var textX = align switch
            {
                TextAlign.Center => contentLeft + Math.Max(0, (availableWidth - lineWidth) / 2),
                TextAlign.Right => x + width - paddingX - Math.Min(lineWidth, availableWidth),
                _ => contentLeft
            };

            AppendPdfText(sb, line, textX, baseline, fontName, fontSize);
            baseline -= lineHeight;
        }
    }

    private static double DrawPdfCellIcon(
        StringBuilder sb,
        GridPrintCellIcon icon,
        double x,
        double y,
        double height)
    {
        if (icon != GridPrintCellIcon.IdentityCard)
            return 0;

        var width = height * 1.35;
        AppendPdfFilledRectangle(sb, x, y, width, height, "0.95 0.95 0.95");
        sb.AppendLine("0.45 0.45 0.45 RG");
        sb.AppendLine("0.35 w");
        AppendPdfRectangle(sb, x, y, width, height, "S");

        var portraitX = x + (height * 0.12);
        var portraitY = y + (height * 0.14);
        var portraitWidth = height * 0.34;
        var portraitHeight = height * 0.72;
        AppendPdfFilledRectangle(sb, portraitX, portraitY, portraitWidth, portraitHeight, "0.38 0.65 0.82");

        var headSize = height * 0.16;
        AppendPdfFilledRectangle(
            sb,
            portraitX + ((portraitWidth - headSize) / 2),
            portraitY + (portraitHeight * 0.56),
            headSize,
            headSize,
            "0.95 0.58 0.24");

        var lineX = portraitX + portraitWidth + (height * 0.12);
        var lineWidth = Math.Max(0.5, width - (lineX - x) - (height * 0.1));
        var lineHeight = Math.Max(0.35, height * 0.07);
        AppendPdfFilledRectangle(sb, lineX, y + (height * 0.58), lineWidth, lineHeight, "0.48 0.48 0.48");
        AppendPdfFilledRectangle(sb, lineX, y + (height * 0.34), lineWidth * 0.78, lineHeight, "0.60 0.60 0.60");
        return width;
    }

    private static double ComputePdfRowHeight(
        GridExportTable table,
        GridExportRow row,
        int startColumn,
        int columnCount,
        IReadOnlyList<double> columnWidths,
        GridPdfColumnLayout columnLayout,
        double fontSize,
        double lineHeight,
        double paddingX,
        double paddingY,
        int maxWrappedLines)
    {
        if (columnLayout != GridPdfColumnLayout.WrapText)
            return lineHeight + (paddingY * 2);

        var lineCount = 1;
        for (var offset = 0; offset < columnCount; offset++)
        {
            var columnIndex = startColumn + offset;
            var text = columnIndex < row.Values.Count ? ToExportString(row.Values[columnIndex]) : string.Empty;
            var availableWidth = Math.Max(1, columnWidths[columnIndex] - (paddingX * 2));
            lineCount = Math.Max(lineCount, GetPdfCellLines(text, availableWidth, fontSize, columnLayout, maxWrappedLines).Count);
        }

        return (lineCount * lineHeight) + (paddingY * 2);
    }

    private static List<double> ResolvePdfColumnWidths(
        GridExportTable table,
        double contentWidth,
        double fontSize,
        GridPdfColumnLayout columnLayout,
        double zoomScale,
        bool applyFitToPage = true)
    {
        var widths = new List<double>(table.Columns.Count);
        var scaledMinimumWidth = columnLayout == GridPdfColumnLayout.FitColumnsToPage
            ? Math.Max(4, 30 * Math.Clamp(zoomScale, 0.12, 2))
            : 30;
        var maximumWidth = Math.Max(scaledMinimumWidth, contentWidth);

        for (var index = 0; index < table.Columns.Count; index++)
        {
            var column = table.Columns[index];
            var width = column.Width is > 0
                ? column.Width.Value * 0.75 * zoomScale
                : EstimatePdfColumnWidth(table, index, fontSize, zoomScale);
            widths.Add(Math.Clamp(width, scaledMinimumWidth, maximumWidth));
        }

        if (applyFitToPage && columnLayout == GridPdfColumnLayout.FitColumnsToPage && widths.Count > 0)
        {
            var totalWidth = widths.Sum();
            if (totalWidth > 0)
            {
                var scale = contentWidth / totalWidth;
                var fitMinimumWidth = Math.Max(3, 10 * Math.Clamp(zoomScale, 0.12, 2));
                for (var i = 0; i < widths.Count; i++)
                    widths[i] = Math.Max(fitMinimumWidth, widths[i] * scale);

                var adjustedTotal = widths.Sum();
                if (adjustedTotal > contentWidth && adjustedTotal > 0)
                {
                    var adjustment = contentWidth / adjustedTotal;
                    for (var i = 0; i < widths.Count; i++)
                        widths[i] *= adjustment;
                }
            }
        }

        return widths;
    }

    private static double EstimatePdfColumnWidth(GridExportTable table, int columnIndex, double fontSize, double zoomScale)
    {
        var maxWidth = EstimatePdfTextWidth(table.Columns[columnIndex].Header, fontSize);
        foreach (var row in table.Rows.Take(100))
        {
            var value = columnIndex < row.Values.Count ? ToExportString(row.Values[columnIndex]) : string.Empty;
            maxWidth = Math.Max(maxWidth, Math.Min(EstimatePdfTextWidth(value, fontSize), 220));
        }

        return Math.Clamp(maxWidth + (14 * zoomScale), 36 * zoomScale, 240 * zoomScale);
    }

    private static List<(int Start, int Count)> BuildPdfColumnSegments(
        IReadOnlyList<double> widths,
        double contentWidth,
        GridPdfColumnLayout columnLayout)
    {
        if (widths.Count == 0)
            return [];

        if (columnLayout == GridPdfColumnLayout.FitColumnsToPage)
            return [(0, widths.Count)];

        var segments = new List<(int Start, int Count)>();
        var start = 0;
        while (start < widths.Count)
        {
            var used = 0d;
            var end = start;
            while (end < widths.Count)
            {
                var width = Math.Min(widths[end], contentWidth);
                if (end > start && used + width > contentWidth)
                    break;
                used += width;
                end++;
                if (width >= contentWidth)
                    break;
            }

            segments.Add((start, Math.Max(1, end - start)));
            start = Math.Max(start + 1, end);
        }

        return segments;
    }

    private static IReadOnlyList<string> GetPdfCellLines(
        string text,
        double maxWidth,
        double fontSize,
        GridPdfColumnLayout columnLayout,
        int maxLines)
    {
        var normalized = NormalizePdfText(text);
        if (columnLayout != GridPdfColumnLayout.WrapText)
            return [TrimPdfTextToWidth(normalized, maxWidth, fontSize)];

        return WrapPdfText(normalized, maxWidth, fontSize, maxLines);
    }

    private static IReadOnlyList<string> WrapPdfText(string text, double maxWidth, double fontSize, int maxLines)
    {
        if (string.IsNullOrEmpty(text))
            return [string.Empty];

        var lines = new List<string>();
        var current = "";
        var truncated = false;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = string.IsNullOrEmpty(current) ? word : current + " " + word;
            if (EstimatePdfTextWidth(candidate, fontSize) <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (!string.IsNullOrEmpty(current))
            {
                lines.Add(current);
                current = "";
                if (lines.Count >= maxLines)
                {
                    truncated = true;
                    break;
                }
            }

            if (EstimatePdfTextWidth(word, fontSize) <= maxWidth)
            {
                current = word;
                continue;
            }

            var chunk = "";
            foreach (var ch in word)
            {
                var charCandidate = chunk + ch;
                if (EstimatePdfTextWidth(charCandidate, fontSize) <= maxWidth || string.IsNullOrEmpty(chunk))
                {
                    chunk = charCandidate;
                    continue;
                }

                lines.Add(chunk);
                chunk = ch.ToString();
                if (lines.Count >= maxLines)
                {
                    truncated = true;
                    break;
                }
            }

            if (truncated)
                break;
            current = chunk;
        }

        if (!truncated && !string.IsNullOrEmpty(current))
            lines.Add(current);

        if (lines.Count == 0)
            lines.Add(string.Empty);

        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            truncated = true;
        }

        if (truncated && lines.Count > 0)
            lines[^1] = TrimPdfTextToWidth(lines[^1] + "...", maxWidth, fontSize);

        return lines;
    }

    private static string NormalizePdfText(string text) =>
        string.Join(" ", (text ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\t", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string TrimPdfTextToWidth(string text, double maxWidth, double fontSize)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
            return string.Empty;

        if (EstimatePdfTextWidth(text, fontSize) <= maxWidth)
            return text;

        const string ellipsis = "...";
        var ellipsisWidth = EstimatePdfTextWidth(ellipsis, fontSize);
        if (ellipsisWidth >= maxWidth)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            var candidate = sb.ToString() + ch;
            if (EstimatePdfTextWidth(candidate, fontSize) + ellipsisWidth > maxWidth)
                break;
            sb.Append(ch);
        }

        return sb.Length == 0 ? string.Empty : sb.ToString() + ellipsis;
    }

    private static double EstimatePdfTextWidth(string text, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var units = 0d;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
                units += 0.28;
            else if ("ilI.,:;|'!".IndexOf(ch) >= 0)
                units += 0.24;
            else if ("mwMW@#%&".IndexOf(ch) >= 0)
                units += 0.82;
            else if (char.IsUpper(ch))
                units += 0.62;
            else if (char.IsDigit(ch))
                units += 0.56;
            else
                units += 0.5;
        }

        return units * fontSize;
    }

    private static double ResolvePdfZoomScale(GridPdfPrintOptions options, GridExportTable table, double contentWidth)
    {
        if (options.ZoomMode != GridPdfZoomMode.FitToPage)
            return Math.Clamp(options.ZoomPercent, 25, 200) / 100d;

        var naturalWidths = ResolvePdfColumnWidths(
            table,
            contentWidth,
            fontSize: 8.5,
            columnLayout: GridPdfColumnLayout.ClipText,
            zoomScale: 1d,
            applyFitToPage: false);
        var naturalWidth = naturalWidths.Sum();
        if (naturalWidth <= 0 || naturalWidth <= contentWidth)
            return 1d;

        return Math.Clamp(contentWidth / naturalWidth, 0.12, 1d);
    }

    private static GridPdfColumnLayout ResolvePdfColumnLayout(GridPdfPrintOptions options) =>
        options.ZoomMode == GridPdfZoomMode.FitToPage
            ? GridPdfColumnLayout.FitColumnsToPage
            : options.ColumnLayout;

    private static void AppendPdfFilledRectangle(StringBuilder sb, double x, double y, double width, double height, string fillColor)
    {
        sb.Append(fillColor).AppendLine(" rg");
        AppendPdfRectangle(sb, x, y, width, height, "f");
    }

    private static void AppendPdfBorder(StringBuilder sb, double x, double y, double width, double height)
    {
        sb.AppendLine("0.82 0.82 0.82 RG");
        sb.AppendLine("0.35 w");
        AppendPdfRectangle(sb, x, y, width, height, "S");
    }

    private static void AppendPdfRectangle(StringBuilder sb, double x, double y, double width, double height, string operation)
    {
        sb.Append(PdfNumber(x)).Append(' ')
          .Append(PdfNumber(y)).Append(' ')
          .Append(PdfNumber(width)).Append(' ')
          .Append(PdfNumber(height)).Append(" re ")
          .AppendLine(operation);
    }

    private static void AppendPdfText(
        StringBuilder sb,
        string text,
        double x,
        double y,
        string fontName,
        double fontSize)
    {
        if (string.IsNullOrEmpty(text))
            return;

        sb.AppendLine("0 0 0 rg");
        sb.Append("BT /").Append(fontName).Append(' ')
          .Append(PdfNumber(fontSize)).Append(" Tf ")
          .Append(PdfNumber(x)).Append(' ')
          .Append(PdfNumber(y)).Append(" Td (")
          .Append(EscapePdfText(text))
          .AppendLine(") Tj ET");
    }

    private static (double Width, double Height) ResolvePdfPageSize(
        GridPdfPageSize pageSize,
        GridPdfOrientation orientation)
    {
        var size = pageSize switch
        {
            GridPdfPageSize.Legal => (Width: 612d, Height: 1008d),
            GridPdfPageSize.A4 => (Width: 595d, Height: 842d),
            _ => (Width: 612d, Height: 792d)
        };

        return orientation == GridPdfOrientation.Landscape
            ? (Math.Max(size.Width, size.Height), Math.Min(size.Width, size.Height))
            : (Math.Min(size.Width, size.Height), Math.Max(size.Width, size.Height));
    }

    private static void SetCellValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
            case DBNull _:
                return;
            case string s:
                cell.SetValue(s);
                break;
            case byte number:
                cell.SetValue((int)number);
                break;
            case sbyte number:
                cell.SetValue((int)number);
                break;
            case short number:
                cell.SetValue((int)number);
                break;
            case ushort number:
                cell.SetValue((int)number);
                break;
            case int i:
                cell.SetValue(i);
                break;
            case uint number:
                cell.SetValue((long)number);
                break;
            case long l:
                cell.SetValue(l);
                break;
            case ulong number when number <= long.MaxValue:
                cell.SetValue((long)number);
                break;
            case ulong number:
                cell.SetValue((double)number);
                break;
            case decimal d:
                cell.SetValue(d);
                break;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                cell.SetValue(d);
                break;
            case double d:
                cell.SetValue(d.ToString(CultureInfo.InvariantCulture));
                break;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                cell.SetValue(f);
                break;
            case float f:
                cell.SetValue(f.ToString(CultureInfo.InvariantCulture));
                break;
            case DateTime dt:
                cell.SetValue(dt);
                break;
            case DateOnly date:
                cell.SetValue(date.ToDateTime(TimeOnly.MinValue));
                break;
            case DateTimeOffset dateTimeOffset:
                cell.SetValue(dateTimeOffset.DateTime);
                break;
            case TimeOnly time:
                cell.SetValue(time.ToTimeSpan());
                break;
            case TimeSpan timeSpan:
                cell.SetValue(timeSpan);
                break;
            case bool b:
                cell.SetValue(b);
                break;
            default:
                cell.SetValue(Convert.ToString(value, CultureInfo.CurrentCulture) ?? "");
                break;
        }
    }

    private static string EscapeDelimited(string value, string delimiter)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        if (value.Contains(delimiter, StringComparison.Ordinal) || value.Contains('"') || value.Contains('\r') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

        return value;
    }

    private static string ToExportString(object? value) =>
        value switch
        {
            null or DBNull => "",
            IFormattable formattable => formattable.ToString(null, CultureInfo.CurrentCulture) ?? "",
            _ => value.ToString() ?? ""
        };

    private static string Html(string value) => System.Net.WebUtility.HtmlEncode(value);

    private static string PdfNumber(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string EnsureExtension(string fileName, string extension)
    {
        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName)))
            return fileName + extension;
        return fileName;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(fileName.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "export" : cleaned;
    }

    private static string SanitizeSheetName(string sheetName)
    {
        var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
        var cleaned = new string((string.IsNullOrWhiteSpace(sheetName) ? "Export" : sheetName)
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray());
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static string EscapePdfText(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static class PdfDocumentWriter
    {
        public static byte[] Write(double pageWidth, double pageHeight, IReadOnlyList<string> pageContents)
        {
            var objects = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>"
            };

            var pageObjectNumbers = new List<int>();
            foreach (var content in pageContents)
            {
                var contentBytes = Encoding.ASCII.GetBytes(content);
                var pageObjectNumber = objects.Count + 1;
                var contentObjectNumber = objects.Count + 2;
                pageObjectNumbers.Add(pageObjectNumber);

                objects.Add(
                    $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PdfNumber(pageWidth)} {PdfNumber(pageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentObjectNumber} 0 R >>");
                objects.Add($"<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream");
            }

            objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", pageObjectNumbers.Select(n => $"{n} 0 R"))}] /Count {pageObjectNumbers.Count} >>";

            using var stream = new MemoryStream();
            void WriteAscii(string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
            }

            WriteAscii("%PDF-1.4\n");
            var offsets = new List<long> { 0 };
            for (var i = 0; i < objects.Count; i++)
            {
                offsets.Add(stream.Position);
                WriteAscii($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }

            var xrefPosition = stream.Position;
            WriteAscii($"xref\n0 {objects.Count + 1}\n");
            WriteAscii("0000000000 65535 f \n");
            foreach (var offset in offsets.Skip(1))
                WriteAscii($"{offset:0000000000} 00000 n \n");
            WriteAscii($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF");

            return stream.ToArray();
        }
    }
}
