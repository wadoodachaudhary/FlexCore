namespace Fx.ControlKit.Grid;

/// <summary>
/// Supplies one flat, ordered range of rows to <see cref="GridControl{TValue}"/>.
/// The grid never assumes how the rows are stored; a provider can use SQL,
/// another service, or an in-memory source.
/// </summary>
public delegate ValueTask<GridItemsResult<TValue>> GridItemsProvider<TValue>(
    GridItemsRequest request);

/// <summary>
/// Immutable request for one provider-backed grid range.
/// </summary>
/// <param name="StartIndex">Zero-based index in the complete filtered and sorted result.</param>
/// <param name="Count">Maximum number of rows requested.</param>
/// <param name="QueryVersion">
/// Monotonically increasing version. Results from an older version must not be
/// applied after the grid's query or context changes.
/// </param>
/// <param name="IncludeTotalCount">
/// True when the grid needs an authoritative total for this query generation.
/// Providers may use this hint to avoid a repeated count query.
/// </param>
/// <param name="Query">Current sort, filter, and search description.</param>
/// <param name="ContextKey">
/// Opaque host value identifying the surrounding dataset (for example, a
/// selected community). FlexCore passes it through without interpreting it.
/// </param>
/// <param name="CancellationToken">Canceled when a newer range or query supersedes this request.</param>
public sealed record GridItemsRequest(
    int StartIndex,
    int Count,
    long QueryVersion,
    bool IncludeTotalCount,
    GridQueryDescriptor Query,
    object? ContextKey,
    CancellationToken CancellationToken)
{
    /// <summary>
    /// Explicitly named alias retained alongside the concise <see cref="Query"/>
    /// property for hosts that mirror the public contract name verbatim.
    /// </summary>
    public GridQueryDescriptor GridQueryDescriptor => Query;

    /// <summary>
    /// Identifies why the grid is requesting a range. The default remains
    /// <see cref="GridItemsRequestPurpose.Rows"/>, so existing providers can
    /// ignore this property and retain their previous behavior.
    /// </summary>
    public GridItemsRequestPurpose Purpose { get; init; } = GridItemsRequestPurpose.Rows;

    /// <summary>
    /// Group ancestry for lazy provider grouping requests. Null for ordinary
    /// row windows, root-group requests, and exports.
    /// </summary>
    public GridProviderGroupRequest? GroupRequest { get; init; }

    /// <summary>
    /// Field whose distinct values are requested when <see cref="Purpose"/> is
    /// <see cref="GridItemsRequestPurpose.FilterValues"/>. Null for every other
    /// request purpose.
    /// </summary>
    public string? FilterField { get; init; }
}

/// <summary>
/// Rows returned for a <see cref="GridItemsRequest"/> and the authoritative
/// number of rows in the complete filtered result.
/// </summary>
public sealed record GridItemsResult<TValue>(
    IReadOnlyList<TValue> Items,
    int TotalCount)
{
    /// <summary>
    /// Groups returned for <see cref="GridItemsRequestPurpose.Groups"/> or
    /// <see cref="GridItemsRequestPurpose.GroupChildren"/>. Existing row
    /// providers can leave this empty.
    /// </summary>
    public IReadOnlyList<GridProviderGroup<TValue>> Groups { get; init; } =
        Array.Empty<GridProviderGroup<TValue>>();

    /// <summary>
    /// Aggregate values for the complete current query. A provider can return
    /// these with the first row/group page so the grid never computes totals
    /// from only the currently loaded window.
    /// </summary>
    public IReadOnlyList<GridAggregateResult> Aggregates { get; init; } =
        Array.Empty<GridAggregateResult>();

    /// <summary>
    /// Number of entries in this particular result set (groups or leaf rows).
    /// When omitted the grid uses <see cref="TotalCount"/> for root rows and
    /// group metadata for child ranges.
    /// </summary>
    public int? ResultSetCount { get; init; }

    /// <summary>
    /// Explicit continuation hint for providers whose result-set size is not
    /// inexpensive to count. The grid also infers continuation from known
    /// counts when this is false.
    /// </summary>
    public bool HasMore { get; init; }

    /// <summary>
    /// Distinct raw/display values returned for a
    /// <see cref="GridItemsRequestPurpose.FilterValues"/> request. Existing row
    /// providers can leave this empty; the request type is opt-in at GridControl.
    /// </summary>
    public IReadOnlyList<GridProviderFilterValue> FilterValues { get; init; } =
        Array.Empty<GridProviderFilterValue>();
}

/// <summary>Describes the intent of an <see cref="GridItemsRequest"/>.</summary>
public enum GridItemsRequestPurpose
{
    /// <summary>A normal visible flat-row range.</summary>
    Rows,

    /// <summary>A page of root groups for the current query.</summary>
    Groups,

    /// <summary>A page of child groups beneath <see cref="GridItemsRequest.GroupRequest"/>.</summary>
    GroupChildren,

    /// <summary>A page of leaf rows beneath <see cref="GridItemsRequest.GroupRequest"/>.</summary>
    GroupItems,

    /// <summary>A flat row page used to export the complete current query.</summary>
    Export,

    /// <summary>
    /// A page of distinct values for <see cref="GridItemsRequest.FilterField"/>
    /// used by the Excel-style checklist filter.
    /// </summary>
    FilterValues
}

/// <summary>One provider-supplied value for an Excel-style checklist filter.</summary>
public sealed record GridProviderFilterValue(string Value, string? DisplayText = null, int? Count = null);

/// <summary>One typed key in a provider group ancestry chain.</summary>
public sealed record GridProviderGroupKey(string Field, object? Value);

/// <summary>
/// Identifies the parent group whose children or rows are being loaded.
/// <paramref name="Level"/> is the parent's zero-based grouping level.
/// </summary>
public sealed record GridProviderGroupRequest(
    int Level,
    IReadOnlyList<GridProviderGroupKey> ParentKeys,
    string ParentPath);

/// <summary>
/// Provider-neutral group metadata. Child groups and leaf items may be
/// supplied eagerly; otherwise <see cref="HasChildren"/> tells GridControl to
/// request them the first time the user expands the group.
/// </summary>
public sealed record GridProviderGroup<TValue>(
    string Field,
    object? Key,
    string? DisplayText,
    int Count)
{
    /// <summary>
    /// Optional stable path. When empty, GridControl derives an escaped path
    /// from the parent path, field, and key.
    /// </summary>
    public string? GroupPath { get; init; }

    /// <summary>True when expansion can reveal sub-groups or leaf rows.</summary>
    public bool HasChildren { get; init; } = true;

    /// <summary>Optional eager child-group payload.</summary>
    public IReadOnlyList<GridProviderGroup<TValue>> Groups { get; init; } =
        Array.Empty<GridProviderGroup<TValue>>();

    /// <summary>Optional eager leaf-row payload.</summary>
    public IReadOnlyList<TValue> Items { get; init; } = Array.Empty<TValue>();

    /// <summary>Aggregate values computed across all rows in this group.</summary>
    public IReadOnlyList<GridAggregateResult> Aggregates { get; init; } =
        Array.Empty<GridAggregateResult>();

    /// <summary>
    /// Optional number of direct children at the next level. This is distinct
    /// from <see cref="Count"/>, which is the recursive row count.
    /// </summary>
    public int? ChildCount { get; init; }
}

/// <summary>One provider-computed aggregate value.</summary>
public sealed record GridAggregateResult(string Field, AggregateType Type, object? Value);

/// <summary>Progress snapshot raised while GridControl pages a provider export.</summary>
public sealed record GridProviderExportProgress(
    int ExportedRows,
    int? TotalRows,
    bool IsCompleted,
    bool IsCanceled = false);

/// <summary>Provider-neutral description of the grid's current query.</summary>
public sealed record GridQueryDescriptor(
    IReadOnlyList<GridSortDescriptor> Sorts,
    IReadOnlyList<GridFilterDescriptor> Filters,
    string? SearchText,
    IReadOnlyList<string> SearchFields)
{
    /// <summary>
    /// Optional textual expression entered in the grid's detailed-search box.
    /// Providers that do not understand the expression can ignore it; callers can
    /// inspect this value to avoid silently returning unfiltered remote data.
    /// </summary>
    public string? ExpressionFilterText { get; init; }

    /// <summary>Active grouping terms, ordered from outermost to innermost.</summary>
    public IReadOnlyList<GridGroupQueryDescriptor> Groups { get; init; } =
        Array.Empty<GridGroupQueryDescriptor>();

    /// <summary>Aggregate computations requested by the configured aggregate rows.</summary>
    public IReadOnlyList<GridAggregateQueryDescriptor> Aggregates { get; init; } =
        Array.Empty<GridAggregateQueryDescriptor>();

    /// <summary>True when provider-side textual comparisons must be case-sensitive.</summary>
    public bool CaseSensitive { get; init; }

    public static GridQueryDescriptor Empty { get; } = new(
        Array.Empty<GridSortDescriptor>(),
        Array.Empty<GridFilterDescriptor>(),
        null,
        Array.Empty<string>());
}

/// <summary>One ordered sort term. Earlier entries have higher priority.</summary>
public sealed record GridSortDescriptor(string Field, SortDirection Direction);

/// <summary>
/// One active column filter. Only properties applicable to <see cref="Kind"/>
/// are populated.
/// </summary>
public sealed record GridFilterDescriptor(
    string Field,
    GridProviderFilterKind Kind,
    TextFilterOperator Operator,
    string? Value,
    IReadOnlyList<string> Values,
    decimal? Minimum,
    decimal? Maximum)
{
    /// <summary>Where the filter originated in GridControl's filtering UI.</summary>
    public GridProviderFilterSource Source { get; init; } = GridProviderFilterSource.ColumnMenu;

    /// <summary>Optional second condition for an advanced column filter.</summary>
    public TextFilterOperator? SecondOperator { get; init; }
    public string? SecondValue { get; init; }
    public LogicalFilterOperator LogicalOperator { get; init; } = LogicalFilterOperator.And;

    /// <summary>
    /// Exact operator values for the advanced-filter surface. These preserve operators
    /// (such as In/NotIn and IsNull) that do not have a one-to-one TextFilterOperator.
    /// </summary>
    public GridFilterOperator? AdvancedOperator { get; init; }
    public GridFilterOperator? SecondAdvancedOperator { get; init; }

    /// <summary>Independent blank/non-blank restriction applied to the field.</summary>
    public BlankRowFilterMode BlankRowFilter { get; init; } = BlankRowFilterMode.All;

    /// <summary>
    /// Selected generated numeric buckets. Bounds are included so a remote provider
    /// does not need access to GridControl's private bucket-generation implementation.
    /// </summary>
    public IReadOnlyList<GridNumericRangeDescriptor> NumericRanges { get; init; } =
        Array.Empty<GridNumericRangeDescriptor>();
}

/// <summary>One provider-side grouping term.</summary>
public sealed record GridGroupQueryDescriptor(string Field)
{
    public SortDirection Direction { get; init; } = SortDirection.Ascending;
}

/// <summary>One provider-side aggregate computation.</summary>
public sealed record GridAggregateQueryDescriptor(string Field, AggregateType Type);

/// <summary>A selected numeric checklist bucket.</summary>
public sealed record GridNumericRangeDescriptor(
    string Key,
    decimal? Minimum,
    decimal? Maximum,
    bool IncludeMaximum,
    bool IsBlank);

/// <summary>Provider-side filter forms currently emitted by GridControl.</summary>
public enum GridProviderFilterKind
{
    Text,
    CheckedValues,
    NumericBounds,
    NumericRanges
}

/// <summary>
/// Identifies parallel FlexCore filter surfaces without changing the original
/// provider descriptor constructor or forcing existing providers to handle them.
/// </summary>
public enum GridProviderFilterSource
{
    ColumnMenu,
    FilterRow,
    AdvancedColumn,
    ColumnCheckBox
}
