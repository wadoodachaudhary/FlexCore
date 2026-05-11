using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Configures selection for GridControl. Equivalent to SyncFusion's GridSelectionSettings.
/// </summary>
public class SelectionSettings : ComponentBase
{
    [Parameter] public SelectionType Type { get; set; } = SelectionType.Single;
    [Parameter] public SelectionMode Mode { get; set; } = SelectionMode.Row;
    [Parameter] public bool CheckboxOnly { get; set; }
    [Parameter] public bool PersistSelection { get; set; }
    [Parameter] public bool EnableToggle { get; set; } = true;
}
