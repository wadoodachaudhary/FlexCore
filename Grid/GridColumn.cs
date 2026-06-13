using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Defines a column in the GridControl. Equivalent to SyncFusion's GridColumn.
/// Configured as a child component of GridColumns.
/// </summary>
public class GridColumn : ComponentBase
{
    [CascadingParameter] internal GridColumnsBase? Parent { get; set; }

    [Parameter] public string Field { get; set; } = "";
    [Parameter] public string HeaderText { get; set; } = "";
    [Parameter] public string? Width { get; set; }
    [Parameter] public string? MinWidth { get; set; }
    [Parameter] public string? MaxWidth { get; set; }
    [Parameter] public ColumnType Type { get; set; } = ColumnType.Text;
    [Parameter] public TextAlign TextAlign { get; set; } = TextAlign.Left;
    [Parameter] public TextAlign HeaderTextAlign { get; set; } = TextAlign.Left;
    /// <summary>
    /// Optional .NET display format. Numeric columns default to plain ungrouped
    /// text when this is blank; set values such as <c>N0</c>, <c>N2</c>, or
    /// <c>C2</c> when comma grouping, decimal precision, or currency display is
    /// intentionally wanted.
    /// </summary>
    [Parameter] public string? Format { get; set; }
    /// <summary>
    /// Optional per-row formula for computed cells. Supports arithmetic with
    /// column references, for example <c>[Qty] * [UnitPrice]</c> or
    /// <c>=Qty * UnitPrice</c>.
    /// </summary>
    [Parameter] public string? Formula { get; set; }
    [Parameter] public bool Visible { get; set; } = true;
    [Parameter] public bool IsPrimaryKey { get; set; }
    [Parameter] public bool AllowSorting { get; set; } = true;
    [Parameter] public bool AllowFiltering { get; set; } = true;
    [Parameter] public bool AllowEditing { get; set; } = true;
    /// <summary>
    /// Allows this column to participate in single-cell column drag selection
    /// even when <see cref="AllowEditing"/> is false. Use for lookup/picker
    /// template cells that support bulk picklist updates but must not accept
    /// typed text edits.
    /// </summary>
    [Parameter] public bool AllowCellDragSelection { get; set; }
    [Parameter] public bool AllowGrouping { get; set; } = true;
    [Parameter] public bool AllowResizing { get; set; } = true;
    [Parameter] public ClipMode ClipMode { get; set; } = ClipMode.Clip;
    [Parameter] public string? ValidationRules { get; set; }
    [Parameter] public string? DefaultValue { get; set; }

    /// <summary>
    /// When true, entering this column's batch-edit input pre-selects the
    /// entire existing value so the user's first keystroke replaces it
    /// instead of appending. Use for numeric columns where the typical
    /// gesture is "click → type new value" (e.g. OrderQty) — there's no
    /// upside to a positional cursor in a column that never gets edited
    /// in place. Default false so existing columns keep their current
    /// caret-placement behaviour.
    /// </summary>
    [Parameter] public bool SelectAllOnEdit { get; set; }

    /// <summary>
    /// When true, numeric editors render as native browser number inputs.
    /// Defaults to false so numeric grid cells use a normal typable text editor
    /// without spinner arrows.
    /// </summary>
    [Parameter] public bool ShowNumericSpinner { get; set; }

    /// <summary>
    /// When true, this column's batch-edit cell renders a trailing "…" picker
    /// button beside the editor input (VB6 VSFlexGrid <c>ShowComboButton</c> +
    /// <c>.ComboList</c> set in <c>BeforeEdit</c>). Clicking it raises
    /// <see cref="GridControlEvents{T}.OnEditButtonClick"/> so the host can open
    /// a picklist and write the chosen value back. The button appears ONLY while
    /// the cell is in edit mode — matching VB6, which attaches the button to the
    /// active edit cell rather than painting one on every row. Default false.
    /// </summary>
    [Parameter] public bool ShowEditButton { get; set; }

    /// <summary>
    /// Paints the edit button in the display cell instead of only while the
    /// cell is in edit mode. Useful for VB6-style button cells that launch a
    /// picklist rather than accepting typed text.
    /// </summary>
    [Parameter] public bool AlwaysShowEditButton { get; set; }

    /// <summary>
    /// Optional predicate for <see cref="AlwaysShowEditButton"/>. Return false
    /// for synthetic/header rows where a picker button should not be shown.
    /// </summary>
    [Parameter] public Func<object, bool>? ShowEditButtonPredicate { get; set; }

    /// <summary>
    /// Optional string choices for batch-mode in-cell dropdown editing.
    /// </summary>
    [Parameter] public IEnumerable<string>? EditOptions { get; set; }

    /// <summary>Custom cell template. Context is the row data item (TValue).</summary>
    [Parameter] public RenderFragment<object>? Template { get; set; }

    /// <summary>Custom header template.</summary>
    [Parameter] public RenderFragment? HeaderTemplate { get; set; }

    /// <summary>Custom edit template. Context is the row data item.</summary>
    [Parameter] public RenderFragment<object>? EditTemplate { get; set; }

    /// <summary>Child content (for Template shorthand).</summary>
    [Parameter] public RenderFragment<object>? ChildContent { get; set; }

    /// <summary>Per-column filter settings override.</summary>
    [Parameter] public FilterSettings? FilterSettings { get; set; }

    /// <summary>Command buttons for the column (Edit, Delete, Save, Cancel).</summary>
    [Parameter] public List<GridCommandModel>? Commands { get; set; }

    /// <summary>Resolved display header — falls back to Field name.</summary>
    public string DisplayHeader => string.IsNullOrEmpty(HeaderText) ? Field : HeaderText;

    /// <summary>The effective template (Template takes priority over ChildContent).</summary>
    public RenderFragment<object>? EffectiveTemplate => Template ?? ChildContent;

    /// <summary>Runtime column width (pixels) for resize — overrides Width when set.</summary>
    internal double? RuntimeWidth { get; set; }

    protected override void OnInitialized()
    {
        Parent?.AddColumn(this);
    }

    public string GetCellStyle()
    {
        var parts = new List<string>();
        if (RuntimeWidth.HasValue)
            parts.Add($"width:{RuntimeWidth.Value}px");
        else if (!string.IsNullOrEmpty(Width))
            parts.Add($"width:{Width}");
        if (!string.IsNullOrEmpty(MinWidth)) parts.Add($"min-width:{MinWidth}");
        if (!string.IsNullOrEmpty(MaxWidth)) parts.Add($"max-width:{MaxWidth}");
        parts.Add($"text-align:{TextAlign.ToString().ToLower()}");
        parts.Add("padding:0 4px");
        parts.Add("overflow:hidden");
        parts.Add("white-space:nowrap");
        if (ClipMode == ClipMode.Ellipsis || ClipMode == ClipMode.EllipsisWithTooltip)
        {
            parts.Add("text-overflow:ellipsis");
        }
        else
        {
            parts.Add("text-overflow:clip");
        }
        return string.Join(";", parts);
    }

    public string GetHeaderStyle()
    {
        var parts = new List<string>();
        if (RuntimeWidth.HasValue)
            parts.Add($"width:{RuntimeWidth.Value}px");
        else if (!string.IsNullOrEmpty(Width))
            parts.Add($"width:{Width}");
        parts.Add($"text-align:{HeaderTextAlign.ToString().ToLower()}");
        return string.Join(";", parts);
    }
}
