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
    /// <summary>
    /// Ctrl/Shift+click and Shift+Arrow add rows to a <see cref="SelectionType.Single"/>
    /// row selection (the VSFlexGrid flexSelectionListBox model). False keeps exactly one
    /// row: a modifier click selects the clicked row alone and Shift+Arrow does not extend,
    /// as in a VSFlexGrid with AllowSelection=False. Ignored for
    /// <see cref="SelectionType.Multiple"/> and for <see cref="SelectionMode.Cell"/>.
    /// </summary>
    public bool ModifierKeysExtendSelection { get; set; } = true;
    public GridMultiSelectBehavior MultiSelectBehavior { get; set; } = GridMultiSelectBehavior.FullMultiSelect;
}
