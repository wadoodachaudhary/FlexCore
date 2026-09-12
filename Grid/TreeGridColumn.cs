using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Non-generic interface for column registration from TreeGridColumn.
/// </summary>
public interface ITreeGridControlOwner
{
    void AddColumn(TreeGridColumn column);
    void RemoveColumn(TreeGridColumn column);
}

/// <summary>
/// The cell editor host handed to a <see cref="TreeGridColumn.CellEditTemplate"/>
/// (see <see cref="ICellEditorHost"/> for the two-phase contract). The tree creates
/// ONE context per phase of the current editable cell and keys the template on
/// <see cref="Generation"/>, so the editor is a fresh instance in each phase. FlexKit
/// editors receive it as a cascading <see cref="ICellEditorHost"/> and wire
/// themselves; the members below stay for templates that wire explicitly.
/// </summary>
public sealed class TreeGridCellEditContext : ICellEditorHost
{
    /// <summary>The row's data item.</summary>
    public required object Item { get; init; }

    /// <summary>The column field being edited.</summary>
    public required string Field { get; init; }

    /// <summary>False in the cursor phase (the cell is current; a popup editor shows
    /// closed and inert), true while the cell is being edited.</summary>
    public bool IsEditing { get; init; }

    /// <summary>Editing phase: the editor opens its list / calendar as soon as it
    /// mounts — Enter on the cell, or a second click on the current cell.</summary>
    public bool OpenOnRender { get; init; }

    /// <summary>The character(s) that started this edit when the user typed on the
    /// current cell (vsFlexGrid Editable=flexEDKbdMouse): a text editor starts its
    /// entry with them, replacing the value; a list highlights the first match.
    /// Null when Enter, F2 or a click started the edit.</summary>
    public string? InitialText { get; init; }

    /// <summary>Bumped on every phase change; the template is keyed on it.</summary>
    public int Generation { get; init; }

    internal object? NodeId { get; init; }

    /// <summary>Ends the edit (the value was committed, the entry cancelled or the
    /// popup closed): the cell returns to the cursor phase and the tree takes the
    /// keyboard back unless focus has moved elsewhere on the page. Ignored once
    /// this context is no longer the tree's current one.</summary>
    public required Action CloseEditor { get; init; }

    /// <summary>The tree's own editing keys — the keys an editor does not consume:
    /// Enter commits and stays on the row, Escape leaves the edit, Tab / Shift+Tab and
    /// Up / Down end the edit and move, Right / Left end it and act on the outline
    /// (see <see cref="ICellEditorHost.KeyDownAsync"/>).</summary>
    public required Func<Microsoft.AspNetCore.Components.Web.KeyboardEventArgs, Task> EditorKeyDown { get; init; }

    /// <summary>The tree's guarded focus step (see <see cref="ICellEditorHost.FocusAsync"/>).</summary>
    public required Func<Microsoft.AspNetCore.Components.ElementReference, bool, Task> FocusEditor { get; init; }

    /// <summary>Keys that reach the tree root while the editing editor is mounting or
    /// has not taken focus yet are handed here (see <see cref="AttachKeyRelay"/>).</summary>
    internal Func<Microsoft.AspNetCore.Components.Web.KeyboardEventArgs, Task>? Relay { get; private set; }

    public void EndEdit() => CloseEditor();

    public Task KeyDownAsync(Microsoft.AspNetCore.Components.Web.KeyboardEventArgs e) => EditorKeyDown(e);

    public Task FocusAsync(Microsoft.AspNetCore.Components.ElementReference element, bool selectText = false) => FocusEditor(element, selectText);

    public void AttachKeyRelay(Func<Microsoft.AspNetCore.Components.Web.KeyboardEventArgs, Task>? relay) => Relay = relay;
}

/// <summary>
/// Defines a column in the TreeGridControl. Equivalent to SyncFusion's TreeGridColumn.
/// </summary>
public class TreeGridColumn : ComponentBase, IDisposable
{
    [CascadingParameter] internal ITreeGridControlOwner? Owner { get; set; }

    [Parameter] public string Field { get; set; } = "";
    [Parameter] public string HeaderText { get; set; } = "";
    [Parameter] public string? Width { get; set; }
    [Parameter] public string? MinWidth { get; set; }
    [Parameter] public ColumnType Type { get; set; } = ColumnType.Text;
    /// <summary>Cell text alignment. Unset defaults by column type: Date
    /// columns right-align (vsFlexGrid flexDTDate parity, HHM-920); everything
    /// else left-aligns. Set explicitly to override.</summary>
    [Parameter] public TextAlign? TextAlign { get; set; }

    /// <summary>The alignment actually applied: the explicit
    /// <see cref="TextAlign"/> when set, else the per-type default.</summary>
    public TextAlign ResolvedTextAlign =>
        TextAlign ?? (Type == ColumnType.Date ? Grid.TextAlign.Right : Grid.TextAlign.Left);
    [Parameter] public string? Format { get; set; }
    [Parameter] public bool Visible { get; set; } = true;
    [Parameter] public bool IsFrozen { get; set; }
    [Parameter] public FrozenColumnPosition FrozenPosition { get; set; } = FrozenColumnPosition.Left;
    [Parameter] public bool IsPrimaryKey { get; set; }
    [Parameter] public bool AllowEditing { get; set; } = true;
    [Parameter] public bool AllowSorting { get; set; } = true;
    [Parameter] public bool AllowFiltering { get; set; } = true;
    [Parameter] public bool AllowResizing { get; set; } = true;
    [Parameter] public RenderFragment<object>? Template { get; set; }
    [Parameter] public RenderFragment? HeaderTemplate { get; set; }
    [Parameter] public TreeGridHeaderIconKind HeaderIconKind { get; set; } = TreeGridHeaderIconKind.None;
    [Parameter] public string? HeaderIconSrc { get; set; }
    [Parameter] public string? HeaderIconAlt { get; set; }
    [Parameter] public string? HeaderIconTitle { get; set; }
    [Parameter] public string? HeaderIconCssClass { get; set; }
    [Parameter] public bool HeaderIconVisible { get; set; } = true;
    [Parameter] public EventCallback HeaderIconClicked { get; set; }
    [Parameter] public IReadOnlyList<GridToolbarItem>? HeaderToolbarItems { get; set; }
    [Parameter] public EventCallback<GridToolbarClickEventArgs> HeaderToolbarItemClicked { get; set; }
    [Parameter] public RenderFragment<object>? EditTemplate { get; set; }

    /// <summary>In-cell editor for this column. When set, the control renders the
    /// cell's display content (the <see cref="Template"/>, or the formatted field
    /// value) inside a click target; clicking it activates the cell and swaps in
    /// this template. The control owns the two-click contract, the double-click
    /// race (<see cref="TreeGridCellEditContext.OpenOnRender"/>), clearing on
    /// row change, and focus return — pages declare the editor, nothing else.</summary>
    [Parameter] public RenderFragment<TreeGridCellEditContext>? CellEditTemplate { get; set; }

    /// <summary>Optional per-row gate for <see cref="CellEditTemplate"/> — return
    /// false to render the row's cell as plain (non-clickable) content, e.g. only
    /// detail-level tree rows are editable. Null = every row offers the editor.</summary>
    [Parameter] public Func<object, bool>? CellEditPredicate { get; set; }

    /// <summary>Extra CSS class(es) for the display-mode click target rendered
    /// when <see cref="CellEditTemplate"/> is set (base class
    /// fx-treegrid-cell-edit-display).</summary>
    [Parameter] public string? CellEditDisplayCssClass { get; set; }

    public string DisplayHeader => string.IsNullOrEmpty(HeaderText) ? Field : HeaderText;

    protected override void OnInitialized()
    {
        Owner?.AddColumn(this);
    }

    public void Dispose()
    {
        Owner?.RemoveColumn(this);
    }

    public string GetCellStyle()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Width)) parts.Add($"width:{Width}");
        if (!string.IsNullOrEmpty(MinWidth)) parts.Add($"min-width:{MinWidth}");
        parts.Add($"text-align:{ResolvedTextAlign.ToString().ToLower()}");
        return string.Join(";", parts);
    }

    public string GetHeaderStyle()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Width)) parts.Add($"width:{Width}");
        parts.Add($"text-align:{ResolvedTextAlign.ToString().ToLower()}");
        return string.Join(";", parts);
    }
}
