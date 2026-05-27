namespace Fx.ControlKit;

public enum ListViewMode
{
    LargeIcon,
    SmallIcon,
    List,
    Details,
    Tile
}

public class ListViewItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = "";
    public string? SubText { get; set; }
    public string? IconCss { get; set; }
    public string? ImageUrl { get; set; }
    public bool IsSelected { get; set; }
    public Dictionary<string, string> SubItems { get; set; } = new();
    public object? Tag { get; set; }
}

public class ListViewColumn
{
    public string Field { get; set; } = "";
    public string Header { get; set; } = "";
    public string? Width { get; set; }
}
