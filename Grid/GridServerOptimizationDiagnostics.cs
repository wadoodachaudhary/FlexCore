namespace Fx.ControlKit.Grid;

/// <summary>
/// Lightweight counters used by the FlexKitTester Blazor optimizer harness.
/// They are process-wide, like <see cref="GridRenderDiagnostics"/>, and do not
/// affect GridControl behavior.
/// </summary>
public static class GridServerOptimizationDiagnostics
{
    public static long RowIndexRequests;
    public static long DirectRowIndexHits;
    public static long PersistentRowIndexHits;
    public static long PersistentRowIndexBuilds;
    public static long BaselineRenderLookupBuilds;
    public static long BaselineRenderLookupRows;
    public static long BaselineIndexOfCalls;
    public static long NavigationRowRequests;
    public static long NavigationRowReuses;
    public static long NavigationRowBuilds;
    public static long BaselineNavigationRowBuilds;
    public static long BaselineNavigationRowsMaterialized;
    public static long SelectedRowSetBuilds;
    public static long BaselineDisplayValueBuilds;
    public static long DisplayValueCacheHits;
    public static long DisplayValueCacheMisses;
    // Gauge, not a counter: rows currently retained across the two display-value
    // generations (bounded ≈ 2 rendered windows). Stamped at end of render pass.
    public static long DisplayValueCacheRows;
    public static long BaselineColumnStyleBuilds;
    public static long ColumnStyleCacheHits;
    public static long ColumnStyleCacheMisses;
    public static long BaselineEditableCueBuilds;
    public static long EditableCueCacheHits;
    public static long EditableCueCacheMisses;
    public static long ClientNavigationSilentSyncs;
    public static long ClientNavigationFinalSyncs;
    public static long DragSelectionCommits;

    public static void Reset()
    {
        RowIndexRequests = 0;
        DirectRowIndexHits = 0;
        PersistentRowIndexHits = 0;
        PersistentRowIndexBuilds = 0;
        BaselineRenderLookupBuilds = 0;
        BaselineRenderLookupRows = 0;
        BaselineIndexOfCalls = 0;
        NavigationRowRequests = 0;
        NavigationRowReuses = 0;
        NavigationRowBuilds = 0;
        BaselineNavigationRowBuilds = 0;
        BaselineNavigationRowsMaterialized = 0;
        SelectedRowSetBuilds = 0;
        BaselineDisplayValueBuilds = 0;
        DisplayValueCacheHits = 0;
        DisplayValueCacheMisses = 0;
        DisplayValueCacheRows = 0;
        BaselineColumnStyleBuilds = 0;
        ColumnStyleCacheHits = 0;
        ColumnStyleCacheMisses = 0;
        BaselineEditableCueBuilds = 0;
        EditableCueCacheHits = 0;
        EditableCueCacheMisses = 0;
        ClientNavigationSilentSyncs = 0;
        ClientNavigationFinalSyncs = 0;
        DragSelectionCommits = 0;
    }

    public static GridServerOptimizationSnapshot Snapshot() => new(
        RowIndexRequests,
        DirectRowIndexHits,
        PersistentRowIndexHits,
        PersistentRowIndexBuilds,
        BaselineRenderLookupBuilds,
        BaselineRenderLookupRows,
        BaselineIndexOfCalls,
        NavigationRowRequests,
        NavigationRowReuses,
        NavigationRowBuilds,
        BaselineNavigationRowBuilds,
        BaselineNavigationRowsMaterialized,
        SelectedRowSetBuilds,
        BaselineDisplayValueBuilds,
        DisplayValueCacheHits,
        DisplayValueCacheMisses,
        BaselineColumnStyleBuilds,
        ColumnStyleCacheHits,
        ColumnStyleCacheMisses,
        BaselineEditableCueBuilds,
        EditableCueCacheHits,
        EditableCueCacheMisses,
        ClientNavigationSilentSyncs,
        ClientNavigationFinalSyncs,
        DragSelectionCommits);
}

public readonly record struct GridServerOptimizationSnapshot(
    long RowIndexRequests,
    long DirectRowIndexHits,
    long PersistentRowIndexHits,
    long PersistentRowIndexBuilds,
    long BaselineRenderLookupBuilds,
    long BaselineRenderLookupRows,
    long BaselineIndexOfCalls,
    long NavigationRowRequests,
    long NavigationRowReuses,
    long NavigationRowBuilds,
    long BaselineNavigationRowBuilds,
    long BaselineNavigationRowsMaterialized,
    long SelectedRowSetBuilds,
    long BaselineDisplayValueBuilds,
    long DisplayValueCacheHits,
    long DisplayValueCacheMisses,
    long BaselineColumnStyleBuilds,
    long ColumnStyleCacheHits,
    long ColumnStyleCacheMisses,
    long BaselineEditableCueBuilds,
    long EditableCueCacheHits,
    long EditableCueCacheMisses,
    long ClientNavigationSilentSyncs,
    long ClientNavigationFinalSyncs,
    long DragSelectionCommits);
