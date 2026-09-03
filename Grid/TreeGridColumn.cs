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
/// Context handed to a <see cref="TreeGridColumn.CellEditTemplate"/> while its
/// cell is in edit mode. The CONTROL owns the two-click contract, activation
/// state, and focus return — the template only renders the editor and calls
/// <see cref="CloseEditor"/> when done.
/// </summary>
public sealed class TreeGridCellEditContext
{
    /// <summary>The row's data item.</summary>
    public required object Item { get; init; }

    /// <summary>The column field being edited.</summary>
    public required string Field { get; init; }

    /// <summary>True when the user clicked the cell AGAIN while the editor swap
    /// was still in flight (the Blazor Server double-click race): a popup editor
    /// should open immediately — pass this to e.g. DropDownListControl's
    /// OpenOnRender. False on a plain first activation (two-click contract:
    /// the editor mounts closed).</summary>
    public bool OpenOnRender { get; init; }

    /// <summary>Ends the edit: clears the active-cell state and returns keyboard
    /// focus to the tree host (no-trap rule). Call from the editor's commit,
    /// close, and Escape paths.</summary>
    public required Action CloseEditor { get; init; }
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
