using System.Data;
using System.Globalization;
using System.Text;
using Fx.ControlKit.Grid;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Reports;

public enum ReportNativeExportFormat { Html, Csv, Xlsx, Pdf }

/// <summary>Native PDF font realization. Neither mode changes completed report pagination.</summary>
public enum ReportPdfFontMode
{
    LegacyVb = 0,
    Designer = 1
}

/// <summary>Native report-page PDF/HTML and typed data exports. Never invokes a browser printer or Crystal engine.</summary>
public static class ReportNativeExport
{
    private static readonly Lazy<string> AnalysisStyles = new(() => string.Concat(new[] { "Fx.Reports.ChartStyles", "Fx.Reports.PivotStyles" }.Select(name =>
    {
        using var stream = typeof(ReportNativeExport).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing report export stylesheet: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    })));

    public static Task<GridExportResult> ExportAsync(ReportTab tab, ReportNativeExportFormat format, string? fileName = null)
        => ExportAsync(tab, format, ReportPdfFontMode.LegacyVb, fileName);

    public static async Task<GridExportResult> ExportAsync(ReportTab tab, ReportNativeExportFormat format, ReportPdfFontMode pdfFontMode, string? fileName = null)
    {
        RequireComplete(tab);
        if (format != ReportNativeExportFormat.Html || tab.PageSnapshots.Count == 0) return Export(tab, format, pdfFontMode, fileName);
        var pages = await RenderPagesAsync(tab.PageSnapshots);
        return Export(new ReportTab { Title = tab.Title, Pages = pages, PositionedPage = tab.PositionedPage, Orientation = tab.Orientation }, format, fileName);
    }

    /// <summary>Renders pages one at a time through one renderer, for a caller that stops early: a search ends at its first match.</summary>
    public sealed class PageRenderer : IAsyncDisposable
    {
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly HtmlRenderer _renderer;

        public PageRenderer() => _renderer = new HtmlRenderer(_services, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        public Task<string> RenderAsync(ReportPageSnapshot page) => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await _renderer.RenderComponentAsync<ReportPageControl>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Page"] = page, [nameof(ReportPageControl.PrintNumericValues)] = true }));
            return root.ToHtmlString();
        });

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            _services.Dispose();
        }
    }

    public static async Task<List<string>> RenderPagesAsync(IReadOnlyList<ReportPageSnapshot> snapshots)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var pages = new List<string>();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            foreach (var page in snapshots)
            {
                var root = await renderer.RenderComponentAsync<ReportPageControl>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Page"] = page, [nameof(ReportPageControl.PrintNumericValues)] = true }));
                pages.Add(root.ToHtmlString());
            }
        });
        return pages;
    }

    public static GridExportResult Export(ReportTab tab, ReportNativeExportFormat format, string? fileName = null)
        => Export(tab, format, ReportPdfFontMode.LegacyVb, fileName);

    public static GridExportResult Export(ReportTab tab, ReportNativeExportFormat format, ReportPdfFontMode pdfFontMode, string? fileName = null)
    {
        RequireComplete(tab);
        if (format == ReportNativeExportFormat.Html && tab.PageSnapshots.Any(page => page.Bands.Any(band => band.Objects.Any(item => item.Analysis is not null || item.Table is not null))))
            throw new InvalidOperationException("Use ExportAsync for reports containing chart or cross-tab components.");
        var name = Path.GetFileNameWithoutExtension(fileName ?? tab.Title);
        name = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')).Trim();
        if (name.Length == 0) name = "Report";
        if (format == ReportNativeExportFormat.Pdf)
            return new(ReportPdfExport.Export(tab, pdfFontMode), name + ".pdf", "application/pdf");
        if (format == ReportNativeExportFormat.Html)
        {
            if (tab.Pages.Count == 0) throw new InvalidOperationException("There are no rendered report pages to export.");
            var paper = tab.PositionedPage;
            var size = paper is null ? (tab.Orientation == ReportOrientation.Landscape ? "landscape" : "portrait")
                : ReportObjectRenderer.Number((paper.ContentWidthTwips + paper.MarginLeftTwips + paper.MarginRightTwips) / 1440d) + "in " +
                  ReportObjectRenderer.Number((paper.ContentHeightTwips + paper.MarginTopTwips + paper.MarginBottomTwips) / 1440d) + "in";
            var html = "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data:; font-src data:; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">" +
                "<title>" + ReportObjectRenderer.Encode(tab.Title) + "</title><style>" + AnalysisStyles.Value + ReportFontCatalog.Css + "body{margin:0;font-family:Arial;color:#000;background:#fff}table{border-collapse:collapse;width:100%}th,td{padding:3px 6px;text-align:left}.fx-export-page{break-after:page}.fx-export-page:last-child,.fx-report-positioned-page:last-child{break-after:auto!important}@page{size:" + size + ";margin:0}</style></head><body>" +
                string.Concat(tab.Pages.Select(page => "<div class=\"fx-export-page\">" + page + "</div>")) + "</body></html>";
            return new(Encoding.UTF8.GetBytes(html), name + ".html", "text/html;charset=utf-8");
        }
        if (format is not (ReportNativeExportFormat.Csv or ReportNativeExportFormat.Xlsx)) throw new NotSupportedException("Unknown native report export format.");
        if (format == ReportNativeExportFormat.Xlsx && tab.CreateDataExport?.Target is TabularData tabular) return tabular.Xlsx(name + ".xlsx");
        var data = tab.CreateDataExport?.Invoke() ?? throw new InvalidOperationException("This report tab has no data export snapshot.");
        if (format == ReportNativeExportFormat.Xlsx) return GridExporter.Export(ReadableSpreadsheet(data), GridExportFormat.Xlsx, name + ".xlsx");
        // CSV has no typed cells. Prefix formula-like text without changing the native XLSX values.
        var csv = new GridExportTable { Title = data.Title, SheetName = data.SheetName, IncludeHeaderRow = data.IncludeHeaderRow };
        foreach (var column in data.Columns) csv.Columns.Add(new(SafeText(column.Header)));
        foreach (var row in data.Rows) csv.Rows.Add(new(row.Values.Select((value, index) => value is string text &&
            (index >= row.XlsxValues.Count || row.XlsxValues[index] is string) ? SafeText(text) : value)));
        return GridExporter.ExportWithEncoding(csv, GridExportFormat.Csv, GridDelimitedTextEncoding.Utf8WithBom, name + ".csv");
    }

    // Paper frames may deliberately clip a value; data columns must not inherit that clipping.
    // Size from display text, not an XLSX shared-string index or a date's numeric serial.
    private static GridExportTable ReadableSpreadsheet(GridExportTable source)
    {
        var result = new GridExportTable { Title = source.Title, SheetName = source.SheetName, IncludeHeaderRow = source.IncludeHeaderRow, PreserveOversizedText = true };
        for (var index = 0; index < source.Columns.Count; index++)
        {
            var column = source.Columns[index];
            var characters = source.IncludeHeaderRow ? LineLength(column.Header) : 0;
            foreach (var row in source.Rows)
            {
                var display = index < row.Values.Count ? row.Values[index] : index < row.XlsxValues.Count ? row.XlsxValues[index] : null;
                characters = Math.Max(characters, LineLength(Convert.ToString(display, CultureInfo.CurrentCulture)));
                if (characters >= 80) break;
            }
            result.Columns.Add(new(column.Header, column.Format, column.TextAlign, ReadablePixels(column.Width, characters), column.PrintCellIcon)
            { Field = column.Field, Formula = column.Formula });
        }
        result.Rows.AddRange(source.Rows);
        result.HighlightColumnIndexes.UnionWith(source.HighlightColumnIndexes);
        return result;
    }

    // Excel's standard character-width approximation, with padding and a bounded memo column.
    private static double ReadablePixels(double? width, int characters)
    {
        var authored = width is double value && double.IsFinite(value) && value > 0 ? value : 0;
        return Math.Clamp(Math.Max(authored, (characters + 2) * 7d + 5), 73, 565);
    }

    private static int LineLength(string? text)
    {
        var longest = 0;
        var current = 0;
        foreach (var character in text ?? "")
        {
            current = character is '\r' or '\n' ? 0 : current + (character == '\t' ? 4 : 1);
            longest = Math.Max(longest, current);
            if (longest >= 80) return 80;
        }
        return longest;
    }

    public static GridExportTable TabularSnapshot(string title, DataTable data, IReadOnlyList<ReportColumn> columns)
    {
        var table = new GridExportTable { Title = title, SheetName = title };
        var (visible, sources) = TabularColumns(data, columns);
        foreach (var column in visible) table.Columns.Add(new(column.HeaderText, column.Format, width: column.Width));
        // Lists sized up front: a large export holds one of these rows per record until it is written.
        table.Rows.EnsureCapacity(data.Rows.Count);
        foreach (DataRow row in data.Rows)
        {
            var export = new GridExportRow();
            export.XlsxValues.EnsureCapacity(visible.Count); export.XlsxFormats.EnsureCapacity(visible.Count); export.Values.EnsureCapacity(visible.Count);
            for (var index = 0; index < visible.Count; index++)
            {
                var value = TabularValue(row, sources[index]);
                export.XlsxValues.Add(value); export.XlsxFormats.Add(visible[index].Format);
                export.Values.Add(TabularDisplay(value, visible[index].Format));
            }
            table.Rows.Add(export);
        }
        return table;
    }

    // On-demand subreport columns are links with no data; the viewer draws them as links, not values.
    // A column the query did not return has no source and exports blank, as the viewer shows it.
    private static (List<ReportColumn> Visible, DataColumn?[] Sources) TabularColumns(DataTable data, IReadOnlyList<ReportColumn> columns)
    {
        var visible = columns.Where(c => !c.Hidden && !c.IsSubreportObject).ToList();
        return (visible, visible.Select(column => data.Columns.Contains(column.Field) ? data.Columns[column.Field] : null).ToArray());
    }

    private static object? TabularValue(DataRow row, DataColumn? source)
    {
        var value = source is null ? null : row[source];
        return value is DBNull ? null : value;
    }

    private static object? TabularDisplay(object? value, string format) => value is IFormattable f ? f.ToString(format, CultureInfo.CurrentCulture) : value;

    public static Func<GridExportTable> CaptureData(string title, DataTable data, IReadOnlyList<ReportColumn> columns)
    {
        var fields = columns.Select(column => new ReportColumn { Field = column.Field, HeaderText = column.HeaderText,
            Format = column.Format, Hidden = column.Hidden, Width = column.Width, IsSubreportObject = column.IsSubreportObject }).ToList();
        return new TabularData(title, data.Copy(), fields).Snapshot;
    }

    /// <summary>A tabular report's captured data. Its XLSX export reads the rows straight from the table, one at a time,
    /// giving the same workbook as <see cref="ReadableSpreadsheet"/> over <see cref="Snapshot"/> without building it.</summary>
    private sealed class TabularData(string title, DataTable data, List<ReportColumn> fields)
    {
        public GridExportTable Snapshot() => TabularSnapshot(title, data, fields);

        public GridExportResult Xlsx(string fileName)
        {
            var (visible, sources) = TabularColumns(data, fields);
            var rows = new DataRow[data.Rows.Count];
            data.Rows.CopyTo(rows, 0);
            var sheet = new GridExportTable { Title = title, SheetName = title, PreserveOversizedText = true };
            for (var index = 0; index < visible.Count; index++)
            {
                var column = visible[index];
                var characters = LineLength(column.HeaderText);
                foreach (var row in rows)
                {
                    var display = TabularDisplay(TabularValue(row, sources[index]), column.Format);
                    characters = Math.Max(characters, LineLength(Convert.ToString(display, CultureInfo.CurrentCulture)));
                    if (characters >= 80) break;
                }
                sheet.Columns.Add(new(column.HeaderText, column.Format, TextAlign.Left, ReadablePixels(column.Width, characters)));
            }

            // One reused row. XLSX reads the typed values; display text is only for sizing and CSV.
            var export = new GridExportRow();
            return GridExporter.ExportXlsx(sheet, rows.Length, index =>
            {
                export.XlsxValues.Clear(); export.XlsxFormats.Clear(); export.Values.Clear();
                for (var column = 0; column < visible.Count; column++)
                {
                    export.XlsxValues.Add(TabularValue(rows[index], sources[column]));
                    export.XlsxFormats.Add(visible[column].Format);
                    export.Values.Add(null);
                }
                return export;
            }, fileName);
        }
    }

    private static void RequireComplete(ReportTab tab)
    {
        if (!tab.IsPaginationComplete) throw new InvalidOperationException("Complete report pagination before exporting; the loaded pages are only a preview.");
    }

    private static string SafeText(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        return trimmed.Length > 0 && (trimmed[0] is '=' or '+' or '-' or '@' || value[0] is '\t' or '\r' or '\n') ? "'" + value : value;
    }
}
