using System.Data;
using System.Diagnostics;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Reports;

public partial class ReportWriterControl
{
    // The print copy (every page of the active report, shown only by @media print) is rendered only once Print prepares
    // it. Kept for every paginated page, it went out again with every page added: Last page on a 448-page report sent
    // one render batch of about 75 MB, more than a slow link delivers before the connection times out. Without the copy
    // the browser's own print prints the shown page.
    private ReportPrintCopy? _print;
    private bool _printPreparing;
    // Each Print of a positioned report takes the next number (ReportTab.PrintNumber holds its tab's latest): a print
    // that has stopped leaves the guard, and the note of a tab printed again, to the later one.
    private int _printNumber;
    // Moved on by every Print pressed and every StopPrint: a press that waits on the round trip that reads its window
    // again goes ahead only if neither happened meanwhile.
    private int _printPress;
    // Moved on by every StopPrint only: a PDF export that falls back to Print does so only if no refresh happened
    // meanwhile.
    private int _printStops;
    // An unpositioned report's print dialog, from its request until window.print returns (in Chromium, once the dialog
    // closes): Print pressed for its tab meanwhile, the second click of a double click, does nothing.
    private PrintDialogRequest? _printDialog;
    private sealed record PrintDialogRequest(ReportTab? Tab);
    private const string PrintStoppedMessage = "Printing stopped: the browser did not answer.";
    private const string PrintLostMessage = "Printing stopped: the view changed before the print dialog opened.";
    private const string PrintHeldMessage = "Printing held while you were in another window: press Print to print.";
    private const string PrintLoadingMessage = "The report is still loading: press Print once it is ready.";
    // The press whose Print completes the report before printing it; zero otherwise. A press meanwhile leaves the
    // print to it.
    private int _printCompleting;

    // Set by an MDI host: whether this viewer's window is the one the user is on.
    [CascadingParameter(Name = "IsActiveWindow")] private Func<bool>? IsActiveWindow { get; set; }

    // The copy is rendered while Print prepares it and, once prepared, while its page is shown (a browser whose print
    // returns at once lays the pages out afterwards); a page turn or another tab drops it.
    private bool PrintCopyShown => _print is { Lost: false } copy && ReferenceEquals(copy.Tab, ActiveTab)
        && (copy.Page is not { } page || page == ActiveTab!.CurrentPage);

    // Only a copy whose dialog was requested prints: the browser's own print while the pages are prepared prints the
    // shown page, not the part of the copy sent so far.
    private bool PrintCopyReady => PrintCopyShown && _print!.Requested;

    // Print's progress or why it stopped, shown in the status bar over the tab's status while that status is unchanged.
    // The note of a press made while the report loaded goes with the load.
    private string ShownStatus => ActiveTab is { PrintNote: { } note } tab && tab.PrintNoteOver == StatusMessage
        && (IsLoading || note != PrintLoadingMessage) ? note : StatusMessage;

    private void SetPrintNote(ReportTab tab, string? note)
    {
        tab.PrintNote = note;
        tab.PrintNoteOver = note is null ? null : StatusMessage;
    }

    // A refreshed or re-prompted report ends a print in progress and drops a kept copy.
    private void StopPrint()
    {
        _printPress++;
        _printStops++;
        _printCompleting = 0;
        _print?.Stop();
        _print = null;
    }

    // An unpositioned report's mark (a copy without parts) stays while its page is shown, as a browser that prints after
    // window.print returns still needs it, and goes once the page or tab changes. A positioned copy goes when the render
    // drops its parts (ReportPrintPart); nothing renders for the mark, so whatever changes the shown page or tab calls this.
    private void DropLeftPrintMark()
    {
        if (_print is { Parts.Count: 0, Page: { } page } mark && (!ReferenceEquals(mark.Tab, ActiveTab) || page != mark.Tab.CurrentPage))
            _print = null;
    }

    /// <summary>Opens the browser's print dialog for the active report: every page, paginated first if needed. A
    /// positioned report's pages are prepared first, a part at a time; a print that stops meanwhile, or whose window the
    /// user left, opens no dialog and says why in the status bar. A call while the viewer loads prints nothing and says so;
    /// one while the report's print dialog is requested or open does nothing.</summary>
    public Task Print() => PrintAsync();

    /// <summary>Prints the active report; false when the print dialog was not requested.</summary>
    private Task<bool> PrintAsync() => PrintPressedAsync(IsActiveWindow?.Invoke() ?? true);

    /// <summary>Prints the active report for a press that found its window active or not (<paramref name="active"/>):
    /// read when the user pressed, before the report is completed, so leaving the window while it completes holds the
    /// print. False when the print dialog was not requested.</summary>
    private async Task<bool> PrintPressedAsync(bool active)
    {
        var press = ++_printPress;
        // Print pressed while the shown copy is prepared or printed does nothing. A print whose copy was dropped (a
        // refresh, another tab) does not stop a new one, although its last round trip may still be out.
        if (_printPreparing && _print is { Lost: false, Stopped: false } preparing && ReferenceEquals(preparing.Tab, ActiveTab)) return false;
        if (_printDialog is { } open && ReferenceEquals(open.Tab, ActiveTab)) return false;
        if (ActiveTab is { } shown) SetPrintNote(shown, null);
        // A press in a split-view partner makes its window the active one, but the render that tells this viewer so can
        // be held back behind renders the browser has not acknowledged yet. The browser answers a round trip after
        // those acknowledgements, which let the held render run.
        if (!active)
        {
            // Print pressed again meanwhile goes ahead instead of this press, and so does a print of this tab that started
            // preparing meanwhile (one pressed earlier, while the report was completed). A refresh, a closed viewer (its
            // disposal moves the version on) or another tab shown when the round trip returns ends this press.
            var pressedTab = ActiveTab;
            var pressedVersion = _nativeNavigationVersion;
            var pressedPrint = pressedTab?.PrintNumber;
            bool Moved() => press != _printPress || pressedTab?.PrintNumber != pressedPrint || pressedVersion != _nativeNavigationVersion
                || !ReferenceEquals(pressedTab, ActiveTab);
            try { await JSRuntime.InvokeVoidAsync("performance.now"); }
            catch (Exception error) when (error is JSDisconnectedException or OperationCanceledException or JSException)
            {
                Logger.LogWarning(error, "Report print stopped");
                // No note once something ended this press, or while another Print is under way.
                if (!Moved() && !_printPreparing && pressedTab is { } stoppedTab)
                {
                    SetPrintNote(stoppedTab, PrintStoppedMessage);
                    await InvokeAsync(StateHasChanged);
                }
                return false;
            }
            if (Moved()) return false;
            active = IsActiveWindow?.Invoke() ?? true;
        }
        // A load under way (a page, Last page, Find, an export, a drill-down, a refresh, the first page) changes or
        // replaces what Print would print. Print is disabled meanwhile; a press that still arrives (sent before the
        // button was disabled, or a host's call) prints nothing and says so while the load runs. Pressed while an
        // earlier Print completes the report, it leaves the print to that press.
        if (IsLoading)
        {
            if (_printCompleting == 0 && ActiveTab is { } loading)
            {
                SetPrintNote(loading, PrintLoadingMessage);
                await InvokeAsync(StateHasChanged);
            }
            return false;
        }
        _printCompleting = press;
        try
        {
            if (!await EnsureNativePagesAsync(0)) return false;
        }
        finally
        {
            if (_printCompleting == press) _printCompleting = 0;
        }
        var tab = ActiveTab;
        if (tab?.PositionedPage is null)
        {
            // Checked again where it is set: a press that waited above may find another press's dialog requested.
            if (_printDialog is { } requested && ReferenceEquals(requested.Tab, tab)) return false;
            var request = _printDialog = new PrintDialogRequest(tab);
            // The shown page prints, marked for print (fx-print-only) while it stays shown; a page turn or another tab
            // drops the mark (DropLeftPrintMark). Like a positioned report's Print, it replaces a copy kept for another tab.
            ReportPrintCopy? marked = null;
            if (tab is not null) _print = marked = new ReportPrintCopy(tab, DropPrintCopy) { Page = tab.CurrentPage, Requested = true };
            // As the positioned report's Gone(): the viewer closed, or the dialog would not print the marked page (a
            // refresh stopped it, the page turned, the tab was left or closed). Only the marked tab, still shown, says
            // the view changed.
            bool MarkGone(ReportPrintCopy mark)
            {
                var shown = !_disposed && ReferenceEquals(mark.Tab, ActiveTab);
                if (shown && ReferenceEquals(mark, _print) && !mark.Stopped && mark.Page == mark.Tab.CurrentPage) return false;
                if (shown && !mark.Stopped) SetPrintNote(mark.Tab, PrintLostMessage);
                return true;
            }
            // As its Held(): the user left the window the press found active; the dialog would print that window.
            bool MarkHeld(ReportPrintCopy mark)
            {
                if (!active || IsActiveWindow?.Invoke() != false) return false;
                SetPrintNote(mark.Tab, PrintHeldMessage);
                return true;
            }
            var dialogSent = false;
            try
            {
                await InvokeAsync(StateHasChanged);
                // The render that marks the page reaches the browser before window.print (see the positioned report's).
                await JSRuntime.InvokeVoidAsync("performance.now");
                if (marked is not null && (MarkGone(marked) || MarkHeld(marked)))
                {
                    marked.Requested = false;
                    if (!_disposed) await InvokeAsync(StateHasChanged);
                    return false;
                }
                dialogSent = true;
                await JSRuntime.InvokeVoidAsync("window.print");
                return true;
            }
            catch (Exception error) when (error is JSDisconnectedException or OperationCanceledException or JSException)
            {
                // The circuit went away, the browser stopped answering, or the dialog outlived the call's timeout.
                Logger.LogWarning(error, "Report print stopped");
                if (dialogSent) return true;
                if (marked is not null)
                {
                    marked.Requested = false;
                    if (ReferenceEquals(marked, _print) && ReferenceEquals(marked.Tab, ActiveTab)) SetPrintNote(marked.Tab, PrintStoppedMessage);
                    await InvokeAsync(StateHasChanged);
                }
                return false;
            }
            finally
            {
                if (ReferenceEquals(_printDialog, request)) _printDialog = null;
            }
        }
        var version = _nativeNavigationVersion;
        // A copy kept from printing the shown page, or held while the user was in another window, is complete: it
        // prints as it is.
        var copy = PrintCopyShown && _print!.Prepared == tab.Pages.Count ? _print : new ReportPrintCopy(tab, DropPrintCopy);
        _print = copy;
        var number = tab.PrintNumber = ++_printNumber;
        bool Gone()
        {
            if (version == _nativeNavigationVersion && ReferenceEquals(copy, _print) && !copy.Lost && ReferenceEquals(tab, ActiveTab)
                && Tabs.Contains(tab)) return false;
            // A later Print of this tab owns its note.
            if (tab.PrintNumber != number) return true;
            // The copy left the view (its pane was re-created, its tab was left and shown again, or the page turned
            // once the pages were ready): what was sent is not sent again. Otherwise the report or tab changed and the
            // count of parts goes.
            SetPrintNote(tab, copy.Lost && !copy.Stopped && version == _nativeNavigationVersion && ReferenceEquals(tab, ActiveTab)
                && Tabs.Contains(tab) ? PrintLostMessage : null);
            return true;
        }
        // The user moved to another window meanwhile: the dialog would print that window. The pages stay for the next
        // Print.
        bool Held()
        {
            if (!active || IsActiveWindow?.Invoke() != false) return false;
            copy.Requested = false;
            SetPrintNote(tab, PrintHeldMessage);
            return true;
        }
        _printPreparing = true;
        var kept = false;
        var sent = false;
        try
        {
            // The copy goes out a part at a time, each after a browser round trip that follows the part before (a part
            // held back behind unacknowledged renders goes out at that round trip), so a slow link receives every part
            // well within the connection's timeout. A part is sized to take about two seconds at the last part's pace:
            // at most twice the last part and at most 32 pages.
            var total = tab.Pages.Count;
            for (int start = copy.Prepared, size = 4; start < total;)
            {
                var count = Math.Min(size, total - start);
                copy.Parts.Add((start, count));
                start += count;
                SetPrintNote(tab, $"Preparing pages for printing: {start} of {total}...");
                // Inside a click handler the render would go out only after the round trip below.
                await Task.Yield();
                if (Gone()) return false;
                var watch = Stopwatch.StartNew();
                await InvokeAsync(StateHasChanged);
                // A round trip to a built-in browser function. The browser answers after applying the renders it has
                // received, and its acknowledgements release a render held back behind them, so parts go out in turn.
                await JSRuntime.InvokeVoidAsync("Date.now");
                if (Gone()) return false;
                size = (int)Math.Clamp(count * 2000 / Math.Max(1, watch.ElapsedMilliseconds), 1, Math.Min(count * 2, 32));
            }
            copy.Page = tab.CurrentPage;
            kept = true;
            if (Held()) return false;
            SetPrintNote(tab, null);
            // Work the circuit has already queued (a window left, a page turned, another tab shown) runs first.
            await Task.Yield();
            if (Gone() || Held()) return false;
            copy.Requested = true;
            await InvokeAsync(StateHasChanged);
            // The browser answers after its acknowledgements of the renders sent before this call, and those release
            // this render if Blazor held it back behind them: it reaches the browser before window.print. A different
            // built-in from the parts' Date.now.
            await JSRuntime.InvokeVoidAsync("performance.now");
            if (Gone())
            {
                copy.Requested = false;
                return false;
            }
            if (Held()) return false;
            sent = true;
            await JSRuntime.InvokeVoidAsync("window.print");
            return true;
        }
        catch (Exception error) when (error is JSDisconnectedException or OperationCanceledException or JSException)
        {
            // The circuit went away, the browser stopped answering, or the dialog outlived the call's timeout (the copy
            // then stays, as after any print).
            Logger.LogWarning(error, "Report print stopped");
            if (sent) return true;
            copy.Requested = false;
            if (!Gone()) SetPrintNote(tab, PrintStoppedMessage);
            return false;
        }
        finally
        {
            if (number == _printNumber) _printPreparing = false;
            if (!kept)
            {
                copy.Stop();
                DropPrintCopy(copy);
            }
            if (version == _nativeNavigationVersion) await InvokeAsync(StateHasChanged);
        }
    }

    private void DropPrintCopy(ReportPrintCopy copy)
    {
        if (ReferenceEquals(_print, copy)) _print = null;
    }

    // One part of the print copy, rendered once: later renders of the viewer leave the pages already sent alone.
    private static RenderFragment PrintPart(ReportPrintCopy copy, int start, int count) => builder =>
    {
        builder.OpenComponent<ReportPrintPart>(0);
        builder.SetKey(start);
        builder.AddComponentParameter(1, nameof(ReportPrintPart.Copy), copy);
        builder.AddComponentParameter(2, nameof(ReportPrintPart.Start), start);
        builder.AddComponentParameter(3, nameof(ReportPrintPart.Count), count);
        builder.CloseComponent();
    };

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

    /// <summary>Loads through a requested page, or the complete report when page is zero; the status line shows
    /// <paramref name="status"/> meanwhile, or what is being loaded.</summary>
    private async Task<bool> EnsureNativePagesAsync(int page, string? status = null)
    {
        var tab = ActiveTab;
        if (tab is null) return !IsLoading;
        if (tab.IsPaginationComplete || page > 0 && page <= tab.Pages.Count) return true;
        if (IsLoading || tab.NativeSession is not { } session) return false;
        var version = _nativeNavigationVersion;
        // A refresh started meanwhile owns IsLoading from then on (see _loadGeneration).
        var load = _loadGeneration;
        var token = _nativeLoad?.Token ?? CancellationToken.None;
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = status ?? (page == 0 ? "Completing report..." : $"Loading page {page}...");
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
            // The pages stay with the report, but what waited on them (Print, an export) does not act on a report a
            // refresh is replacing.
            return load == _loadGeneration && ReferenceEquals(tab, ActiveTab);
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
                if (load == _loadGeneration) IsLoading = false;
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

/// <summary>One Print's copy of a report's pages, sent to the browser a part at a time. A part is rendered once: shown
/// again (a re-created pane) it would send every page prepared so far in one batch, so it renders nothing and the copy
/// is lost.</summary>
internal sealed class ReportPrintCopy(ReportTab tab, Action<ReportPrintCopy> dropped)
{
    private readonly HashSet<int> _mounted = [];
    public ReportTab Tab { get; } = tab;
    public List<(int Start, int Count)> Parts { get; } = [];
    public int Prepared => Parts.Sum(part => part.Count);
    /// <summary>The page shown when every part was prepared; null while they are prepared.</summary>
    public int? Page { get; set; }
    /// <summary>The print dialog was requested for <see cref="Page"/>.</summary>
    public bool Requested { get; set; }
    /// <summary>A part left the page or was shown again: the copy is no longer whole.</summary>
    public bool Lost { get; private set; }
    /// <summary>Print ended without the dialog, or the report was refreshed.</summary>
    public bool Stopped { get; private set; }

    public bool Mount(int start)
    {
        if (!Lost && _mounted.Add(start)) return true;
        Lose();
        return false;
    }

    public void Lose()
    {
        Lost = true;
        dropped(this);
    }

    public void Stop() => Stopped = true;
}

/// <summary>Pages <see cref="Start"/> to <see cref="Start"/> + <see cref="Count"/> - 1 of a report's print copy.</summary>
internal sealed class ReportPrintPart : ComponentBase, IDisposable
{
    private bool _mounted;
    [Parameter] public ReportPrintCopy Copy { get; set; } = default!;
    [Parameter] public int Start { get; set; }
    [Parameter] public int Count { get; set; }

    protected override void OnInitialized() => _mounted = Copy.Mount(Start);

    protected override bool ShouldRender() => false;

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (!_mounted) return;
        var tab = Copy.Tab;
        var snapshots = tab.PageSnapshots.Count == tab.Pages.Count;
        for (var index = Start; index < Start + Count && index < tab.Pages.Count; index++)
        {
            if (snapshots)
            {
                builder.OpenComponent<ReportPageControl>(0);
                builder.AddComponentParameter(1, nameof(ReportPageControl.Page), tab.PageSnapshots[index]);
                builder.AddComponentParameter(2, nameof(ReportPageControl.IncludeNavigationAnchors), false);
                builder.CloseComponent();
            }
            else builder.AddMarkupContent(3, tab.Pages[index]);
        }
    }

    public void Dispose()
    {
        if (_mounted) Copy.Lose();
    }
}
