namespace Fx.ControlKit.Grid;

public sealed class GridSettings
{
    public List<string>? ColumnOrder { get; set; }

    public Dictionary<string, bool>? Visibility { get; set; }

    public Dictionary<string, double>? Widths { get; set; }

    public Dictionary<string, string>? HeaderOverrides { get; set; }

    public List<string>? GroupColumns { get; set; }
}

public interface IGridSettingsStore
{
    Task<GridSettings?> LoadAsync(string key);

    Task SaveAsync(string key, GridSettings settings);
}
