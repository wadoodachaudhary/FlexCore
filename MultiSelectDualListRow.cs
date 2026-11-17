namespace Fx.ControlKit;

/// <summary>
/// One row of a <c>MultiSelectDualListControl</c> pane. Each pane is a one-column grid over
/// these rows, so the panes get the grid's header search, sorting, type-to-jump and paging.
/// </summary>
public sealed class MultiSelectDualListRow
{
    /// <summary>The item's unique key (what the host receives back).</summary>
    public string Key { get; init; } = "";

    /// <summary>The visible text.</summary>
    public string Label { get; init; } = "";

    /// <summary>The host's item.</summary>
    public object? Item { get; init; }
}
