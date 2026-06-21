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

public sealed class GridExportColumn
{
    public GridExportColumn(string header, string? format = null, TextAlign textAlign = TextAlign.Left, double? width = null)
    {
        Header = header;
        Format = format;
        TextAlign = textAlign;
        Width = width;
    }

    public string Header { get; set; }
    public string? Format { get; set; }
    public TextAlign TextAlign { get; set; }
    public double? Width { get; set; }
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
    public bool IsBold { get; set; }
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

    public static GridExportResult Export(GridExportTable table, GridExportFormat format, string? fileName = null)
    {
        var resolvedName = EnsureExtension(
            string.IsNullOrWhiteSpace(fileName) ? SanitizeFileName(table.Title) : fileName!,
            DefaultExtension(format));

        return format switch
        {
            GridExportFormat.Csv => new GridExportResult(BuildDelimited(table, ","), resolvedName, CsvMime),
            GridExportFormat.Tsv => new GridExportResult(BuildDelimited(table, "\t"), resolvedName, TsvMime),
            GridExportFormat.Html => new GridExportResult(BuildHtml(table, standalone: true), resolvedName, HtmlMime),
            GridExportFormat.Xls => new GridExportResult(BuildHtml(table, standalone: false), resolvedName, XlsMime),
            GridExportFormat.Xlsx => new GridExportResult(BuildXlsx(table), resolvedName, XlsxMime),
            GridExportFormat.Pdf => new GridExportResult(BuildPdf(table), resolvedName, PdfMime),
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
            var modulePath = $"./_content/{typeof(GridExporter).Assembly.GetName().Name}/grid-control.js";
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
        var rowIndex = 1;

        if (table.IncludeHeaderRow && table.Columns.Count > 0)
        {
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
                SetCellValue(cell, colIndex < row.Values.Count ? row.Values[colIndex] : null);

                if (row.IsBold)
                    cell.Style.Font.Bold = true;

                if (table.HighlightColumnIndexes.Contains(colIndex))
                    cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#ffffc1");

                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#e6e6e6");

                if (colIndex < table.Columns.Count)
                {
                    var column = table.Columns[colIndex];
                    if (!string.IsNullOrWhiteSpace(column.Format))
                        cell.Style.NumberFormat.Format = column.Format;

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
                worksheet.Column(colIndex + 1).Width = width;
            else
                worksheet.Column(colIndex + 1).AdjustToContents();
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static byte[] BuildDelimited(GridExportTable table, string delimiter)
    {
        var sb = new StringBuilder();
        if (table.IncludeHeaderRow && table.Columns.Count > 0)
            sb.AppendLine(string.Join(delimiter, table.Columns.Select(c => EscapeDelimited(c.Header, delimiter))));

        foreach (var row in table.Rows)
            sb.AppendLine(string.Join(delimiter, row.Values.Select(v => EscapeDelimited(ToExportString(v), delimiter))));

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

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

    private static byte[] BuildPdf(GridExportTable table)
    {
        const double pageWidth = 842;
        const double pageHeight = 595;
        const double margin = 24;
        const double fontSize = 8.5;
        const double lineHeight = 12;
        var maxRowsPerPage = Math.Max(1, (int)((pageHeight - (margin * 2) - 24) / lineHeight));

        var textRows = new List<(bool Bold, string Text)>();
        if (!string.IsNullOrWhiteSpace(table.Title))
            textRows.Add((true, table.Title));
        if (table.IncludeHeaderRow && table.Columns.Count > 0)
            textRows.Add((true, string.Join("  ", table.Columns.Select(c => c.Header))));
        textRows.AddRange(table.Rows.Select(row => (row.IsBold, string.Join("  ", row.Values.Select(ToExportString)))));

        var pages = textRows.Chunk(maxRowsPerPage).Select(chunk =>
        {
            var sb = new StringBuilder();
            var y = pageHeight - margin;
            foreach (var row in chunk)
            {
                var escaped = EscapePdfText(TrimForPdf(row.Text));
                sb.Append("BT /F1 ")
                  .Append(row.Bold ? fontSize + 0.5 : fontSize)
                  .Append(" Tf ")
                  .Append(margin.ToString(CultureInfo.InvariantCulture))
                  .Append(' ')
                  .Append(y.ToString(CultureInfo.InvariantCulture))
                  .Append(" Td (")
                  .Append(escaped)
                  .AppendLine(") Tj ET");
                y -= lineHeight;
            }
            return sb.ToString();
        }).ToList();

        return PdfDocumentWriter.Write(pageWidth, pageHeight, pages);
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
            case int i:
                cell.SetValue(i);
                break;
            case long l:
                cell.SetValue(l);
                break;
            case decimal d:
                cell.SetValue(d);
                break;
            case double d:
                cell.SetValue(d);
                break;
            case float f:
                cell.SetValue(f);
                break;
            case DateTime dt:
                cell.SetValue(dt);
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

    private static string TrimForPdf(string text) =>
        text.Length <= 180 ? text : text[..177] + "...";

    private static class PdfDocumentWriter
    {
        public static byte[] Write(double pageWidth, double pageHeight, IReadOnlyList<string> pageContents)
        {
            var objects = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
            };

            var pageObjectNumbers = new List<int>();
            foreach (var content in pageContents)
            {
                var contentBytes = Encoding.ASCII.GetBytes(content);
                var pageObjectNumber = objects.Count + 1;
                var contentObjectNumber = objects.Count + 2;
                pageObjectNumbers.Add(pageObjectNumber);

                objects.Add(
                    $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth.ToString(CultureInfo.InvariantCulture)} {pageHeight.ToString(CultureInfo.InvariantCulture)}] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentObjectNumber} 0 R >>");
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
