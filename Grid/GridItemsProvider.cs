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
}

/// <summary>
/// Rows returned for a <see cref="GridItemsRequest"/> and the authoritative
/// number of rows in the complete filtered result.
/// </summary>
public sealed record GridItemsResult<TValue>(
    IReadOnlyList<TValue> Items,
    int TotalCount);

/// <summary>Provider-neutral description of the grid's current query.</summary>
public sealed record GridQueryDescriptor(
    IReadOnlyList<GridSortDescriptor> Sorts,
    IReadOnlyList<GridFilterDescriptor> Filters,
    string? SearchText,
    IReadOnlyList<string> SearchFields)
{
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
    decimal? Maximum);

/// <summary>Provider-side filter forms currently emitted by GridControl.</summary>
public enum GridProviderFilterKind
{
    Text,
    CheckedValues,
    NumericBounds
}
