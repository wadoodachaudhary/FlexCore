namespace Fx.ControlKit;

/// <summary>One column (lane) of a <see cref="KanbanBoardControl{TItem}"/>. <see cref="Key"/> is the
/// value an item's column-selector must return to land in this column (e.g. a status name).</summary>
public class KanbanColumn
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>Optional accent colour for the column header strip (any CSS colour).</summary>
    public string? AccentColor { get; set; }
}

/// <summary>Fired by <see cref="KanbanBoardControl{TItem}"/> when a card is dropped into a different column.
/// The host applies the move (e.g. set the item's status to <see cref="ToColumn"/>) and persists it.</summary>
public class KanbanMoveArgs<TItem>
{
    public TItem Item { get; set; } = default!;
    public string FromColumn { get; set; } = string.Empty;
    public string ToColumn { get; set; } = string.Empty;
}
