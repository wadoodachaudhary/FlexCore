using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    /// <summary>
    /// When true, async exports page the complete current provider query using
    /// <see cref="GridItemsRequestPurpose.Export"/>. The default is false and
    /// therefore preserves the original behavior of exporting only the loaded
    /// provider window. Local DataSource exports are unaffected.
    /// </summary>
    [Parameter] public bool ExportAllProviderItems { get; set; }

    /// <summary>Requested batch size for complete provider exports.</summary>
    [Parameter] public int ProviderExportPageSize { get; set; } = 1_000;

    /// <summary>Raised after every received export page and on completion/cancellation.</summary>
    [Parameter] public EventCallback<GridProviderExportProgress> ProviderExportProgressChanged { get; set; }

    /// <summary>True while a complete provider export is retrieving pages.</summary>
    public bool IsProviderExporting { get; private set; }

    /// <summary>Most recent provider-export progress snapshot.</summary>
    public GridProviderExportProgress? ProviderExportProgress { get; private set; }

    /// <summary>Most recent non-cancellation provider-export failure.</summary>
    public Exception? ProviderExportLastError { get; private set; }

    private readonly object _providerExportSync = new();
    private CancellationTokenSource? _providerExportCts;
    private long _providerExportSerial;

    /// <summary>Requests cancellation of the active provider export, if any.</summary>
    public void CancelProviderExport()
    {
        CancellationTokenSource? cts;
        lock (_providerExportSync)
            cts = _providerExportCts;

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Creates export bytes from the complete current provider query without
    /// opening the browser save dialog. Unlike synchronous
    /// <c>CreateExport</c>, this method always pages all provider rows.
    /// </summary>
    public async Task<GridExportResult> CreateProviderExportAsync(
        GridExportFormat format,
        string? fileName = null,
        string title = "Export",
        GridPdfPrintOptions? pdfOptions = null,
        CancellationToken cancellationToken = default)
    {
        if (!UsesItemsProvider)
            return CreateExport(format, fileName, title, pdfOptions);

        var table = await BuildCompleteProviderExportTableAsync(
            ResolveExportTitle(title),
            cancellationToken);
        return GridExporter.ExportWithEncoding(
            table,
            format,
            DelimitedExportEncoding,
            fileName,
            pdfOptions);
    }

    /// <summary>
    /// Exports the complete current provider query and opens the browser save
    /// dialog. This explicit API does not require
    /// <see cref="ExportAllProviderItems"/> to be enabled.
    /// </summary>
    public async Task ExportProviderQueryAsync(
        GridExportFormat format,
        string? fileName = null,
        string title = "Export",
        GridPdfPrintOptions? pdfOptions = null,
        bool showCompletionStatus = true,
        CancellationToken cancellationToken = default)
    {
        if (!UsesItemsProvider)
        {
            await ExportAsync(format, fileName, title, pdfOptions, showCompletionStatus);
            return;
        }

        var table = await BuildCompleteProviderExportTableAsync(
            ResolveExportTitle(title),
            cancellationToken);
        var result = GridExporter.ExportWithEncoding(
            table,
            format,
            DelimitedExportEncoding,
            fileName,
            pdfOptions);
        var saveResult = await GridExporter.SaveAsync(JsRuntime, result);
        if (showCompletionStatus)
            await ShowExportResultAsync(table.Rows.Count, saveResult);
    }

    private async Task<GridExportTable> BuildExportTableAsync(
        string title,
        CancellationToken cancellationToken = default)
    {
        if (UsesItemsProvider && ExportAllProviderItems)
            return await BuildCompleteProviderExportTableAsync(title, cancellationToken);

        return BuildExportTable(title);
    }

    private async Task<GridExportTable> BuildCompleteProviderExportTableAsync(
        string title,
        CancellationToken cancellationToken)
    {
        var provider = ItemsProvider
            ?? throw new InvalidOperationException(
                $"{nameof(CreateProviderExportAsync)} requires {nameof(ItemsProvider)}.");

        // Establish the same query generation the visible provider pipeline
        // uses. This also makes an export invoked through @ref before the
        // grid's first after-render load deterministic.
        CaptureCurrentProviderIdentity(forceNewGeneration: false);
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? supersededCts;
        long exportSerial;
        lock (_providerExportSync)
        {
            supersededCts = _providerExportCts;
            _providerExportCts = linkedCts;
            exportSerial = ++_providerExportSerial;
            IsProviderExporting = true;
            ProviderExportLastError = null;
        }

        // Preserve the established cancel-and-replace behavior. The superseded
        // operation owns and disposes its CTS in its own finally block; disposing
        // it here would race its provider/callback continuations.
        try { supersededCts?.Cancel(); }
        catch (ObjectDisposedException) { }

        var token = linkedCts.Token;
        var query = BuildProviderQueryDescriptor();
        var queryVersion = _providerQueryVersion;
        var contextKey = ItemsProviderContextKey;
        var pageSize = Math.Clamp(ProviderExportPageSize, 1, 100_000);
        var rows = new List<TValue>();
        var aggregates = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        int? totalRows = null;
        var providerSaysMore = true;

        try
        {
            // Keep the initial callback inside the protected lifetime. A host
            // callback is user code and can throw just like a later progress
            // callback; cleanup must still clear the exporting flag and CTS.
            await ReportProviderExportProgressAsync(
                new GridProviderExportProgress(0, null, false),
                exportSerial,
                linkedCts);

            while (providerSaysMore || !totalRows.HasValue || rows.Count < totalRows.Value)
            {
                token.ThrowIfCancellationRequested();
                var request = new GridItemsRequest(
                    rows.Count,
                    pageSize,
                    queryVersion,
                    IncludeTotalCount: !totalRows.HasValue,
                    query,
                    contextKey,
                    token)
                {
                    Purpose = GridItemsRequestPurpose.Export
                };
                var result = await provider(request);
                token.ThrowIfCancellationRequested();

                // Query/context changes invalidate the export snapshot just as
                // they invalidate a visible window. Mixing generations could
                // otherwise produce duplicate or missing rows.
                if (queryVersion != _providerQueryVersion
                    || !Equals(provider, ItemsProvider)
                    || !Equals(contextKey, ItemsProviderContextKey))
                {
                    throw new OperationCanceledException(
                        "The provider query changed while it was being exported.",
                        token);
                }

                totalRows ??= Math.Max(0, result.TotalCount);
                CaptureProviderAggregates(result.Aggregates, aggregates);
                var page = (result.Items ?? Array.Empty<TValue>())
                    .Take(pageSize)
                    .ToList();

                if (page.Count == 0)
                {
                    if (rows.Count < totalRows.Value || result.HasMore)
                    {
                        throw new InvalidOperationException(
                            "The ItemsProvider returned an empty export page before the complete " +
                            "result was reached. Export stopped to avoid an endless request loop " +
                            "or a silently truncated file.");
                    }
                    break;
                }

                rows.AddRange(page);
                // A provider can omit/approximate TotalCount and drive export
                // continuation explicitly through HasMore. Otherwise the
                // authoritative total governs the loop.
                providerSaysMore = result.HasMore;
                if (!providerSaysMore && rows.Count >= totalRows.Value)
                    providerSaysMore = false;

                await ReportProviderExportProgressAsync(
                    new GridProviderExportProgress(rows.Count, totalRows, false),
                    exportSerial,
                    linkedCts);
            }

            var table = BuildGridExportTable(
                title,
                rows,
                aggregates.Count > 0 ? aggregates : null);
            await ReportProviderExportProgressAsync(
                new GridProviderExportProgress(rows.Count, totalRows, true),
                exportSerial,
                linkedCts);
            return table;
        }
        catch (OperationCanceledException)
        {
            await ReportProviderExportProgressAsync(
                new GridProviderExportProgress(rows.Count, totalRows, false, IsCanceled: true),
                exportSerial,
                linkedCts);
            throw;
        }
        catch (Exception ex)
        {
            lock (_providerExportSync)
            {
                if (IsCurrentProviderExport(exportSerial, linkedCts))
                    ProviderExportLastError = ex;
            }
            throw;
        }
        finally
        {
            var completedCurrentExport = false;
            lock (_providerExportSync)
            {
                if (IsCurrentProviderExport(exportSerial, linkedCts))
                {
                    _providerExportCts = null;
                    IsProviderExporting = false;
                    completedCurrentExport = true;
                }
            }

            linkedCts.Dispose();
            if (completedCurrentExport)
                await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ReportProviderExportProgressAsync(
        GridProviderExportProgress progress,
        long exportSerial,
        CancellationTokenSource exportCts)
    {
        lock (_providerExportSync)
        {
            if (!IsCurrentProviderExport(exportSerial, exportCts))
                return;
            ProviderExportProgress = progress;
        }

        if (ProviderExportProgressChanged.HasDelegate)
            await ProviderExportProgressChanged.InvokeAsync(progress);

        // A progress callback can synchronously start another export. Do not
        // let the older continuation repaint or overwrite the replacement's
        // shared state after that reentrant call returns.
        lock (_providerExportSync)
        {
            if (!IsCurrentProviderExport(exportSerial, exportCts))
                return;
        }
        await InvokeAsync(StateHasChanged);
    }

    private bool IsCurrentProviderExport(long exportSerial, CancellationTokenSource exportCts) =>
        _providerExportSerial == exportSerial
        && ReferenceEquals(_providerExportCts, exportCts);

    private void DisposeProviderExportState()
    {
        CancellationTokenSource? cts;
        lock (_providerExportSync)
        {
            cts = _providerExportCts;
            _providerExportCts = null;
            _providerExportSerial++;
            IsProviderExporting = false;
        }

        // The operation owns disposal and will observe this cancellation in
        // its finally block. Invalidating the serial prevents any continuation
        // from touching component state during/after disposal.
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
