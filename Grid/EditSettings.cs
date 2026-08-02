namespace Fx.ControlKit.Grid;

/// <summary>
/// Configures editing for GridControl. Equivalent to SyncFusion's GridEditSettings.
/// Plain data object passed to GridControl via its <c>EditSettingsRef</c> parameter — it is
/// never rendered as a component, so its properties carry no <c>[Parameter]</c> attribute
/// (which would otherwise trip BL0005 on every host that sets them in C#).
/// </summary>
public class EditSettings
{
    public bool AllowEditing { get; set; }
    public bool AllowAdding { get; set; }
    public bool AllowDeleting { get; set; }
    public EditMode Mode { get; set; } = EditMode.Inline;
    public bool ShowConfirmDialog { get; set; } = true;
    public NewRowPosition NewRowPosition { get; set; } = NewRowPosition.Top;
    public bool AllowEditOnDblClick { get; set; } = true;
}
