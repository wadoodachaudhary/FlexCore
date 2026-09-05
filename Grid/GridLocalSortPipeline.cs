namespace Fx.ControlKit.Grid;

/// <summary>
/// Applies the grid's ordered sort descriptors to an in-memory data source.
/// Kept separate from the component so the local pipeline can be exercised
/// deterministically by focused tests without rendering a Blazor component.
/// </summary>
internal static class GridLocalSortPipeline
{
    internal static IEnumerable<TItem> Apply<TItem>(
        IEnumerable<TItem> source,
        IReadOnlyList<GridSortDescriptor> sorts,
        Func<TItem, string, object?> keySelector,
        IComparer<object?> comparer)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sorts);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(comparer);

        IOrderedEnumerable<TItem>? ordered = null;

        foreach (var sort in sorts)
        {
            if (string.IsNullOrWhiteSpace(sort.Field))
                continue;

            if (ordered == null)
            {
                ordered = sort.Direction == SortDirection.Descending
                    ? source.OrderByDescending(item => keySelector(item, sort.Field), comparer)
                    : source.OrderBy(item => keySelector(item, sort.Field), comparer);
            }
            else
            {
                ordered = sort.Direction == SortDirection.Descending
                    ? ordered.ThenByDescending(item => keySelector(item, sort.Field), comparer)
                    : ordered.ThenBy(item => keySelector(item, sort.Field), comparer);
            }
        }

        return ordered ?? source;
    }
}
