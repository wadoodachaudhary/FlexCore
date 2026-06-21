namespace Fx.ControlKit.Grid;

public class SelectionSettings
{
    public SelectionType Type { get; set; } = SelectionType.Single;
    public SelectionMode Mode { get; set; } = SelectionMode.Row;
    public bool CheckboxOnly { get; set; }
    public bool PersistSelection { get; set; }
    public bool EnableToggle { get; set; } = true;
    public GridMultiSelectBehavior MultiSelectBehavior { get; set; } = GridMultiSelectBehavior.FullMultiSelect;
}
