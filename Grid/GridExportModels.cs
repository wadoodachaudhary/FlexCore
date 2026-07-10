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

    public static GridExportResult Export(
        GridExportTable table,
        GridExportFormat format,
        string? fileName = null,
        GridPdfPrintOptions? pdfOptions = null)
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

        var excelWidth = (width - 5) / 7d;
        return Math.Clamp(excelWidth, 4, 80);
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

        var availableWidth = Math.Max(1, width - (paddingX * 2));
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
                TextAlign.Center => x + paddingX + Math.Max(0, (availableWidth - lineWidth) / 2),
                TextAlign.Right => x + width - paddingX - Math.Min(lineWidth, availableWidth),
                _ => x + paddingX
            };

            AppendPdfText(sb, line, textX, baseline, fontName, fontSize);
            baseline -= lineHeight;
        }
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
