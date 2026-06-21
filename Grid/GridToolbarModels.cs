namespace Fx.ControlKit.Grid;

public sealed class GridToolbarItem
{
    public string Key { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Title { get; set; }
    public string? IconSrc { get; set; }
    public string? IconAlt { get; set; }
    public string? Glyph { get; set; }
    public GridToolbarAction Action { get; set; } = GridToolbarAction.Custom;
    public bool Disabled { get; set; }
    public bool Visible { get; set; } = true;
    public bool SeparatorBefore { get; set; }
    public bool SeparatorAfter { get; set; }
    public List<GridToolbarItem>? Items { get; set; }
}

public enum GridToolbarAction
{
    Custom,
    ExpandAll,
    CollapseAll,
    ToggleExpandCollapse,
    Refresh,
    Columns,
    ClearFilters
}

public sealed class GridToolbarClickEventArgs
{
    public string Key { get; set; } = "";
    public GridToolbarAction Action { get; set; } = GridToolbarAction.Custom;
    public GridToolbarItem? Item { get; set; }
    public bool IsHeaderToolbar { get; set; }
    public string? ColumnField { get; set; }
    public string? ColumnHeader { get; set; }
    public bool Cancel { get; set; }
}
