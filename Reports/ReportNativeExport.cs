using System.Data;
using System.Globalization;
using System.Text;
using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

public enum ReportNativeExportFormat { Html, Csv, Xlsx }

/// <summary>Native report-page HTML and typed data exports. Never invokes a Crystal engine.</summary>
public static class ReportNativeExport
{
    public static GridExportResult Export(ReportTab tab, ReportNativeExportFormat format, string? fileName = null)
    {
        var name = Path.GetFileNameWithoutExtension(fileName ?? tab.Title);
        name = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')).Trim();
        if (name.Length == 0) name = "Report";
        if (format == ReportNativeExportFormat.Html)
        {
            if (tab.Pages.Count == 0) throw new InvalidOperationException("There are no rendered report pages to export.");
            var paper = tab.PositionedPage;
            var size = paper is null ? (tab.Orientation == ReportOrientation.Landscape ? "landscape" : "portrait")
                : ReportObjectRenderer.Number((paper.ContentWidthTwips + paper.MarginLeftTwips + paper.MarginRightTwips) / 1440d) + "in " +
                  ReportObjectRenderer.Number((paper.ContentHeightTwips + paper.MarginTopTwips + paper.MarginBottomTwips) / 1440d) + "in";
            var html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">" +
                "<title>" + ReportObjectRenderer.Encode(tab.Title) + "</title><style>body{margin:0;font-family:Arial;color:#000;background:#fff}table{border-collapse:collapse;width:100%}th,td{padding:3px 6px;text-align:left}.fx-export-page{break-after:page}.fx-export-page:last-child,.fx-report-positioned-page:last-child{break-after:auto!important}@page{size:" + size + ";margin:0}</style></head><body>" +
                string.Concat(tab.Pages.Select(page => "<div class=\"fx-export-page\">" + page + "</div>")) + "</body></html>";
            return new(Encoding.UTF8.GetBytes(html), name + ".html", "text/html;charset=utf-8");
        }
        if (format is not (ReportNativeExportFormat.Csv or ReportNativeExportFormat.Xlsx)) throw new NotSupportedException("Unknown native report export format.");
        var data = tab.CreateDataExport?.Invoke() ?? throw new InvalidOperationException("This report tab has no data export snapshot.");
        if (format == ReportNativeExportFormat.Xlsx) return GridExporter.Export(data, GridExportFormat.Xlsx, name + ".xlsx");
        // CSV has no typed cells. Prefix formula-like text without changing the native XLSX values.
        var csv = new GridExportTable { Title = data.Title, SheetName = data.SheetName, IncludeHeaderRow = data.IncludeHeaderRow };
        foreach (var column in data.Columns) csv.Columns.Add(new(SafeText(column.Header)));
        foreach (var row in data.Rows) csv.Rows.Add(new(row.Values.Select((value, index) => value is string text &&
            (index >= row.XlsxValues.Count || row.XlsxValues[index] is string) ? SafeText(text) : value)));
        return GridExporter.ExportWithEncoding(csv, GridExportFormat.Csv, GridDelimitedTextEncoding.Utf8WithBom, name + ".csv");
    }

    public static GridExportTable TabularSnapshot(string title, DataTable data, IReadOnlyList<ReportColumn> columns)
    {
        var table = new GridExportTable { Title = title, SheetName = title };
        // On-demand subreport columns are links with no data; the viewer draws them as links, not values.
        var visible = columns.Where(c => !c.Hidden && !c.IsSubreportObject).ToList();
        foreach (var column in visible) table.Columns.Add(new(column.HeaderText, column.Format, width: column.Width));
        foreach (DataRow row in data.Rows)
        {
            var export = new GridExportRow();
            foreach (var column in visible)
            {
                // A column the query did not return exports blank, as the viewer shows it.
                var value = !data.Columns.Contains(column.Field) || row[column.Field] is DBNull ? null : row[column.Field];
                export.XlsxValues.Add(value); export.XlsxFormats.Add(column.Format);
                export.Values.Add(value is IFormattable f ? f.ToString(column.Format, CultureInfo.CurrentCulture) : value);
            }
            table.Rows.Add(export);
        }
        return table;
    }

    public static Func<GridExportTable> CaptureData(string title, DataTable data, IReadOnlyList<ReportColumn> columns)
    {
        var snapshot = data.Copy();
        var fields = columns.Select(column => new ReportColumn { Field = column.Field, HeaderText = column.HeaderText,
            Format = column.Format, Hidden = column.Hidden, Width = column.Width, IsSubreportObject = column.IsSubreportObject }).ToList();
        return () => TabularSnapshot(title, snapshot, fields);
    }

    private static string SafeText(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        return trimmed.Length > 0 && (trimmed[0] is '=' or '+' or '-' or '@' || value[0] is '\t' or '\r' or '\n') ? "'" + value : value;
    }
}
