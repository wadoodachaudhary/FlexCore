namespace Fx.ControlKit.Grid;

/// <summary>
/// Configures selection for GridControl. Equivalent to SyncFusion's GridSelectionSettings.
/// Plain data object passed to GridControl via its <c>SelectionSettingsRef</c> parameter — it
/// is never rendered as a component, so its properties carry no <c>[Parameter]</c> attribute
/// (which would otherwise trip BL0005 on every host that sets them in C#).
/// </summary>
public class SelectionSettings
{
    public SelectionType Type { get; set; } = SelectionType.Single;
    public SelectionMode Mode { get; set; } = SelectionMode.Row;
    /// <summary>Show row selection checkboxes while allowing ordinary cell/row clicks to select.</summary>
    public bool ShowCheckboxes { get; set; }
    public bool CheckboxOnly { get; set; }
    public bool PersistSelection { get; set; }
    public bool EnableToggle { get; set; } = true;
    public GridMultiSelectBehavior MultiSelectBehavior { get; set; } = GridMultiSelectBehavior.FullMultiSelect;
}
