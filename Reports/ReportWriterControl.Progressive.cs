using System.Data;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Reports;

public partial class ReportWriterControl
{
    private async Task<DataTable> ExecuteReportDataAsync(ReportDefinition definition, IDictionary<string, object>? parameters,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Structured fixture providers retain precedence over raw-SQL adapters.
        if (db is not IAsyncReportDataExecutor asynchronous || db is IReportDefinitionDataExecutor)
            return ExecuteReportData(definition, parameters);
        if (parameters is not null) parameters = ReportRangeParameters.BindSql(new Dictionary<string, object>(parameters, StringComparer.OrdinalIgnoreCase));
        if (definition.PositionedSqlSelection is not null) parameters ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var execution = definition.PrepareSqlExecution(parameters);
        return await asynchronous.ExecuteAsync(execution.Sql, parameters, cancellationToken);
    }

    /// <summary>Paginates the rest of the active report (exports need every page); false when
    /// it was cancelled, failed or the active report changed meanwhile.</summary>
    public Task<bool> CompletePaginationAsync() => EnsureNativePagesAsync(0);

    /// <summary>Loads through a requested page, or the complete report when page is zero.</summary>
    private async Task<bool> EnsureNativePagesAsync(int page)
    {
        var tab = ActiveTab;
        if (tab is null) return !IsLoading;
        if (tab.IsPaginationComplete || page > 0 && page <= tab.Pages.Count) return true;
        if (IsLoading || tab.NativeSession is not { } session) return false;
        var version = _nativeNavigationVersion;
        var token = _nativeLoad?.Token ?? CancellationToken.None;
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = page == 0 ? "Completing report..." : $"Loading page {page}...";
        await InvokeAsync(StateHasChanged);
        try
        {
            var result = await PaginateNativeSession(session, page == 0 ? 0 : Math.Min(10000, Math.Max(page, tab.Pages.Count + 5)), token);
            token.ThrowIfCancellationRequested();
            if (version != _nativeNavigationVersion || !Tabs.Contains(tab)) return false;
            var expanded = FlattenGroups(tab.Tree).Where(n => n.Expanded).Select(n => n.AnchorId).ToHashSet(StringComparer.Ordinal);
            foreach (var group in FlattenGroups(result.Tree)) group.Expanded = expanded.Contains(group.AnchorId);
            tab.Pages = result.Pages;
            tab.PageSnapshots = result.Snapshots;
            tab.Tree = result.Tree;
            tab.IsPaginationComplete = result.IsComplete;
            tab.RuntimeDiagnostics = tab.RuntimeDiagnostics.Concat(result.Diagnostics).Distinct().ToList();
            StatusMessage = result.IsComplete ? $"Report complete: {result.Pages.Count} pages." : $"Ready: {result.Pages.Count} pages available.";
            OnRenderComplete?.Invoke();
            return ReferenceEquals(tab, ActiveTab);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception error)
        {
            if (version == _nativeNavigationVersion)
            {
                Logger.LogWarning(error, "Report page retrieval failed");
                ErrorMessage = "Report page error: " + error.Message;
                StatusMessage = ErrorMessage;
            }
            return false;
        }
        finally
        {
            if (version == _nativeNavigationVersion)
            {
                IsLoading = false;
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private static IEnumerable<FxGroupNode> FlattenGroups(IEnumerable<FxGroupNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in FlattenGroups(node.Children)) yield return child;
        }
    }
}
