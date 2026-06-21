namespace Fx.ControlKit.Grid;

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
