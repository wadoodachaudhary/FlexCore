namespace Fx.ControlKit;

public class KanbanColumn
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? AccentColor { get; set; }
}

public class KanbanMoveArgs<TItem>
{
    public TItem Item { get; set; } = default!;
    public string FromColumn { get; set; } = string.Empty;
    public string ToColumn { get; set; } = string.Empty;
}
