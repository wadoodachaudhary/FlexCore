using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    /// <summary>
    /// Encoding used by CSV and TSV exports created through this grid. The
    /// default remains UTF-8 without a BOM. XLSX, XLS, HTML, JSON, and PDF
    /// exports ignore this parameter.
    /// </summary>
    [Parameter]
    public GridDelimitedTextEncoding DelimitedExportEncoding { get; set; }
        = GridDelimitedTextEncoding.Utf8NoBom;

    /// <summary>
    /// Exports using a one-off delimited-text encoding without changing
    /// <see cref="DelimitedExportEncoding"/>. Non-delimited formats ignore the
    /// supplied encoding.
    /// </summary>
    public async Task ExportWithEncodingAsync(
        GridExportFormat format,
        GridDelimitedTextEncoding delimitedEncoding,
        string? fileName = null,
        string title = "Export",
        GridPdfPrintOptions? pdfOptions = null,
        bool showCompletionStatus = true)
    {
        var table = await BuildExportTableAsync(ResolveExportTitle(title));
        var result = GridExporter.ExportWithEncoding(table, format, delimitedEncoding, fileName, pdfOptions);
        var saveResult = await GridExporter.SaveAsync(JsRuntime, result);
        if (showCompletionStatus)
            await ShowExportResultAsync(table.Rows.Count, saveResult);
    }

    /// <summary>
    /// Creates export bytes using a one-off delimited-text encoding without
    /// opening the browser save dialog.
    /// </summary>
    public GridExportResult CreateExportWithEncoding(
        GridExportFormat format,
        GridDelimitedTextEncoding delimitedEncoding,
        string? fileName = null,
        string title = "Export",
        GridPdfPrintOptions? pdfOptions = null)
    {
        var table = BuildExportTable(ResolveExportTitle(title));
        return GridExporter.ExportWithEncoding(table, format, delimitedEncoding, fileName, pdfOptions);
    }

    /// <summary>Exports CSV with an explicit encoding.</summary>
    public Task ExportToCsvWithEncodingAsync(
        GridDelimitedTextEncoding delimitedEncoding,
        string? fileName = null) =>
        ExportWithEncodingAsync(GridExportFormat.Csv, delimitedEncoding, fileName);

    /// <summary>Exports tab-delimited text with an explicit encoding.</summary>
    public Task ExportToTsvWithEncodingAsync(
        GridDelimitedTextEncoding delimitedEncoding,
        string? fileName = null) =>
        ExportWithEncodingAsync(GridExportFormat.Tsv, delimitedEncoding, fileName);

    /// <summary>
    /// Creates export bytes from the complete current provider query using an
    /// explicit CSV/TSV encoding. Provider retrieval semantics are unchanged.
    /// </summary>
    public async Task<GridExportResult> CreateProviderExportWithEncodingAsync(
        GridExportFormat format,
        GridDelimitedTextEncoding delimitedEncoding,
        string? fileName = null,
        string title = "Export",
        GridPdfPrintOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!UsesItemsProvider)
            return CreateExportWithEncoding(format, delimitedEncoding, fileName, title, pdfOptions);

        var table = await BuildCompleteProviderExportTableAsync(
            ResolveExportTitle(title),
            cancellationToken);
        return GridExporter.ExportWithEncoding(table, format, delimitedEncoding, fileName, pdfOptions);
    }

    /// <summary>
    /// Exports the complete current provider query using an explicit CSV/TSV
    /// encoding. Provider retrieval semantics are unchanged.
    /// </summary>
    public async Task ExportProviderQueryWithEncodingAsync(
        GridExportFormat format,
        GridDelimitedTextEncoding delimitedEncoding,
        string? fileName = null,
        string title = "Export",
        GridPdfPrintOptions? pdfOptions = null,
        bool showCompletionStatus = true,
        CancellationToken cancellationToken = default)
    {
        if (!UsesItemsProvider)
        {
            await ExportWithEncodingAsync(
                format,
                delimitedEncoding,
                fileName,
                title,
                pdfOptions,
                showCompletionStatus);
            return;
        }

        var table = await BuildCompleteProviderExportTableAsync(
            ResolveExportTitle(title),
            cancellationToken);
        var result = GridExporter.ExportWithEncoding(table, format, delimitedEncoding, fileName, pdfOptions);
        var saveResult = await GridExporter.SaveAsync(JsRuntime, result);
        if (showCompletionStatus)
            await ShowExportResultAsync(table.Rows.Count, saveResult);
    }
}
