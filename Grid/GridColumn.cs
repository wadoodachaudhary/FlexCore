using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Defines a column in the GridControl. Equivalent to SyncFusion's GridColumn.
/// Configured as a child component of GridColumns.
/// </summary>
public class GridColumn : ComponentBase, IDisposable
{
    [CascadingParameter] internal GridColumnsBase? Parent { get; set; }

    [Parameter] public string Field { get; set; } = "";
    [Parameter] public string? HeaderText { get; set; }
    /// <summary>
    /// Optional one-level parent header displayed above this column. Adjacent
    /// visible columns with the same value share one spanning header cell.
    /// Use <see cref="GridColumnHeaderBand"/> descriptors on the owning grid
    /// when the band needs a template, CSS class, or an explicit field list.
    /// </summary>
    [Parameter] public string? HeaderBand { get; set; }
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
    /// Optional data field used as the display/search/filter text when the
    /// stored <see cref="Field"/> value differs from the visible cell text.
    /// The raw <see cref="Field"/> value remains the checked-filter key.
    /// </summary>
    [Parameter] public string? DisplayField { get; set; }
    /// <summary>
    /// Optional per-row formula for computed cells. Supports arithmetic with
    /// column references, for example <c>[Qty] * [UnitPrice]</c> or
    /// <c>=Qty * UnitPrice</c>.
    /// </summary>
    [Parameter] public string? Formula { get; set; }
    [Parameter] public bool Visible { get; set; } = true;
    [Parameter] public bool IsPrimaryKey { get; set; }
    [Parameter] public bool IsFrozen { get; set; }
    [Parameter] public FrozenColumnPosition FrozenPosition { get; set; } = FrozenColumnPosition.Left;

    // A menu-applied lock must survive parent re-renders that re-apply the
    // declarative parameters above. Null means explicitly unlocked; when no
    // runtime override exists the declared IsFrozen/FrozenPosition win.
    internal bool HasRuntimeFrozenOverride { get; private set; }
    internal FrozenColumnPosition? RuntimeFrozenPosition { get; private set; }
    internal bool EffectiveIsFrozen => HasRuntimeFrozenOverride
        ? RuntimeFrozenPosition.HasValue
        : IsFrozen;
    internal FrozenColumnPosition EffectiveFrozenPosition =>
        RuntimeFrozenPosition ?? FrozenPosition;

    internal void SetRuntimeFrozenPosition(FrozenColumnPosition? position)
    {
        HasRuntimeFrozenOverride = true;
        RuntimeFrozenPosition = position;
    }

    internal void ClearRuntimeFrozenOverride()
    {
        HasRuntimeFrozenOverride = false;
        RuntimeFrozenPosition = null;
    }
    [Parameter] public bool AllowSorting { get; set; } = true;
    [Parameter] public bool AllowFiltering { get; set; } = true;
    /// <summary>Excludes this column from best-fit sizing (grip double-click, Best Fit
    /// menu, AutoFitColumnsAsync). For a column whose width is deliberately pinned —
    /// e.g. VB6 `ColWidth(0) = 240` followed by `AutoSize(1, .Cols - 1)`.</summary>
    [Parameter] public bool AllowAutoFit { get; set; } = true;

    /// <summary>
    /// Requires a typed edit value to match one of the column's allowed values
    /// before the cell can commit. Matching is case-insensitive and accepts
    /// either side of a mapped option such as <c>#3;To closest</c>; the stored
    /// value is normalized to the mapped value. When <see cref="EditValueList"/>
    /// and <see cref="EditValueListProvider"/> are both omitted, the grid uses
    /// <see cref="EditOptions"/> or <see cref="EditOptionsProvider"/>.
    /// </summary>
    [Parameter] public bool RequireEditValueInList { get; set; }

    /// <summary>Allowed values for <see cref="RequireEditValueInList"/> when the
    /// cell uses a popup button rather than an in-cell dropdown.</summary>
    [Parameter] public IEnumerable<string>? EditValueList { get; set; }

    /// <summary>Row-aware allowed values for <see cref="RequireEditValueInList"/>.</summary>
    [Parameter] public Func<object, IEnumerable<string>>? EditValueListProvider { get; set; }

    /// <summary>Message shown in the validation pill when a required list match is
    /// not found. Defaults to "{column} '{value}' not found."; a {0} placeholder
    /// is substituted with the refused entry.</summary>
    [Parameter] public string? EditValueNotFoundMessage { get; set; }

    [Parameter] public bool AllowEditing { get; set; } = true;
    [Parameter] public bool AllowHiding { get; set; } = true;
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
    [Parameter] public int? MaxLength { get; set; }

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
    /// Text rendered inside the edit button. Defaults to the legacy ellipsis
    /// picker marker; hosts can provide command labels such as "Edit Note".
    /// </summary>
    [Parameter] public string EditButtonText { get; set; } = "...";

    /// <summary>
    /// Paints the edit button in the display cell instead of only while the
    /// cell is in edit mode. Useful for VB6-style button cells that launch a
    /// picklist rather than accepting typed text.
    /// </summary>
    [Parameter] public bool AlwaysShowEditButton { get; set; }

    /// <summary>
    /// When true, double-clicking a <see cref="ShowEditButton"/> cell raises
    /// <see cref="GridControlEvents{T}.OnEditButtonClick"/>. Set false for
    /// picker-only cells where users must press the visible ellipsis button
    /// and the display cell itself should only select/focus.
    /// </summary>
    [Parameter] public bool OpenEditButtonOnDoubleClick { get; set; } = true;

    /// <summary>
    /// When true, a double-click on this editable cell opens the in-cell editor
    /// before any row-level double-click handler runs.
    /// </summary>
    [Parameter] public bool PreferCellEditOnDoubleClick { get; set; }

    /// <summary>
    /// Optional predicate for <see cref="AlwaysShowEditButton"/>. Return false
    /// for synthetic/header rows where a picker button should not be shown.
    /// </summary>
    [Parameter] public Func<object, bool>? ShowEditButtonPredicate { get; set; }

    /// <summary>
    /// Optional string choices for batch-mode in-cell dropdown editing. Plain
    /// entries use the same text for display and storage; VB6-style mapped
    /// entries such as <c>#3;To closest</c> display the text after the semicolon
    /// while committing the value after <c>#</c>.
    /// </summary>
    [Parameter] public IEnumerable<string>? EditOptions { get; set; }

    /// <summary>
    /// Optional row-aware choices for batch-mode in-cell dropdown editing.
    /// Use when the available options depend on the row data. Returned entries
    /// use the same format as <see cref="EditOptions"/>.
    /// </summary>
    [Parameter] public Func<object, IEnumerable<string>>? EditOptionsProvider { get; set; }

    /// <summary>
    /// Allows an <see cref="EditOptions"/> editor to accept a value that is not
    /// already present in its option list. FlexKit renders the column as an
    /// editable combo box while retaining the standard grid dropdown sizing,
    /// positioning, keyboard handling, and commit behavior.
    /// </summary>
    [Parameter] public bool AllowCustomEditOptionValue { get; set; }

    /// <summary>
    /// For <see cref="EditOptions"/> columns, automatically open the option
    /// list when editing starts from a click/double-click. The arrow button can
    /// still open the list when this is false.
    /// </summary>
    [Parameter] public bool OpenEditOptionsOnEdit { get; set; } = true;

    /// <summary>
    /// For <see cref="ColumnType.Date"/> columns, double-clicking the active date
    /// text editor opens the FlexKit calendar picker. Default false so date
    /// columns remain plain text unless the host opts in.
    /// </summary>
    [Parameter] public bool OpenDateCalendarOnDoubleClick { get; set; }

    /// <summary>Custom cell template. Context is the row data item (TValue).</summary>
    [Parameter] public RenderFragment<object>? Template { get; set; }

    /// <summary>Custom header template.</summary>
    [Parameter] public RenderFragment? HeaderTemplate { get; set; }

    /// <summary>Custom edit template. Context is the row data item.</summary>
    [Parameter] public RenderFragment<object>? EditTemplate { get; set; }

    /// <summary>
    /// Optional leading icon for this column's data cells in PDF output. Use
    /// this when the on-screen cell template contains a visual cue that should
    /// remain visible in the printed grid.
    /// </summary>
    [Parameter] public GridPrintCellIcon PrintCellIcon { get; set; }

    /// <summary>Child content (for Template shorthand).</summary>
    [Parameter] public RenderFragment<object>? ChildContent { get; set; }


    /// <summary>
    /// Legacy filter-row template. Prefer <see cref="FilterCellTemplate"/>,
    /// whose context exposes typed operators and apply/clear callbacks.
    /// </summary>
    [Parameter] public RenderFragment<GridColumn>? FilterTemplate { get; set; }

    /// <summary>Custom template for this column's filter-row cell.</summary>
    [Parameter] public RenderFragment<GridFilterCellTemplateContext>? FilterCellTemplate { get; set; }

    /// <summary>Custom content for this column's header filter menu.</summary>
    [Parameter] public RenderFragment<GridFilterMenuTemplateContext>? FilterMenuTemplate { get; set; }

    /// <summary>Custom action area for this column's header filter menu.</summary>
    [Parameter] public RenderFragment<GridFilterMenuButtonsTemplateContext>? FilterMenuButtonsTemplate { get; set; }








    /// <summary>Per-column filter settings override.</summary>
    [Parameter] public FilterSettings? FilterSettings { get; set; }

    /// <summary>Command buttons for the column (Edit, Delete, Save, Cancel).</summary>
    [Parameter] public List<GridCommandModel>? Commands { get; set; }

    /// <summary>Resolved display header. Null means "use Field"; an empty string is an intentional blank header.</summary>
    public string DisplayHeader => HeaderText ?? Field;

    /// <summary>The effective template (Template takes priority over ChildContent).</summary>
    public RenderFragment<object>? EffectiveTemplate => Template ?? ChildContent;

    /// <summary>Runtime column width (pixels) for resize — overrides Width when set.</summary>
    internal double? RuntimeWidth { get; set; }

    /// <summary>
    /// Calculated by the owning grid for a frozen column. Keeping the value on
    /// the column lets every rendering path (normal, grouped, windowed and
    /// server-optimized) use the same cumulative sticky offset.
    /// </summary>
    internal string? FrozenOffsetCss { get; set; }

    protected override void OnInitialized()
    {
        Parent?.AddColumn(this);
    }

    // Unregisters from the container, so a host foreach can REMOVE columns in
    // place — until now the only way to shrink or swap the column set was a
    // @key teardown of the whole GridColumnsBase (VSFlexGrid parity gap: VB6
    // mutated ColHidden/ColWidth on a live grid). Removal is by INSTANCE
    // reference inside RemoveColumn, so the dedup-replace case (same Field,
    // new instance — the old instance disposes later) stays a no-op.
    public void Dispose()
    {
        Parent?.RemoveColumn(this);
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
        var effectiveTextAlign = UsesMappedEditOptionDisplay() ? TextAlign.Left : TextAlign;
        parts.Add($"text-align:{effectiveTextAlign.ToString().ToLower()}");
        parts.Add("padding:0 4px");
        parts.Add("overflow:hidden");
        parts.Add("white-space:nowrap");
        if (EffectiveIsFrozen)
        {
            parts.Add("position:sticky");
            var offset = string.IsNullOrWhiteSpace(FrozenOffsetCss) ? "0px" : FrozenOffsetCss;
            parts.Add(EffectiveFrozenPosition == FrozenColumnPosition.Right
                ? $"right:{offset};z-index:2"
                : $"left:{offset};z-index:2");
            parts.Add("background-color:inherit");
        }
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

    private bool UsesMappedEditOptionDisplay()
    {
        return EditOptions?.Any(option =>
            !string.IsNullOrEmpty(option)
            && option[0] == '#'
            && option.IndexOf(';') > 1) == true;
    }

    public string GetHeaderStyle()
    {
        var parts = new List<string>();
        if (RuntimeWidth.HasValue)
            parts.Add($"width:{RuntimeWidth.Value}px");
        else if (!string.IsNullOrEmpty(Width))
            parts.Add($"width:{Width}");
        parts.Add($"text-align:{HeaderTextAlign.ToString().ToLower()}");
        if (EffectiveIsFrozen)
        {
            parts.Add("position:sticky");
            var offset = string.IsNullOrWhiteSpace(FrozenOffsetCss) ? "0px" : FrozenOffsetCss;
            parts.Add(EffectiveFrozenPosition == FrozenColumnPosition.Right
                ? $"right:{offset};z-index:3"
                : $"left:{offset};z-index:3");
        }
        return string.Join(";", parts);
    }

    /// <summary>Returns a CSS length suitable for a cumulative sticky offset.</summary>
    internal string GetEffectiveWidthCss(double fallbackWidthPx = 120)
    {
        string width;
        if (RuntimeWidth.HasValue)
        {
            width = $"{RuntimeWidth.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}px";
        }
        else
        {
            width = NormalizeCssLength(Width)
                ?? $"{fallbackWidthPx.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}px";
        }

        // Sticky offsets must use the width the browser ultimately lays out,
        // not merely Width before min/max constraints are applied.
        var minWidth = NormalizeCssLength(MinWidth);
        var maxWidth = NormalizeCssLength(MaxWidth);
        if (minWidth != null && maxWidth != null)
            return $"clamp({minWidth},{width},{maxWidth})";
        if (minWidth != null)
            return $"max({width},{minWidth})";
        if (maxWidth != null)
            return $"min({width},{maxWidth})";
        return width;
    }

    private static string? NormalizeCssLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var length = value.Trim();
        if (double.TryParse(length, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var unitless))
        {
            return $"{unitless.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}px";
        }

        // CSS-wide/content keywords cannot participate in an offset calc().
        return length.Equals("auto", StringComparison.OrdinalIgnoreCase)
            || length.Equals("min-content", StringComparison.OrdinalIgnoreCase)
            || length.Equals("max-content", StringComparison.OrdinalIgnoreCase)
            || length.StartsWith("fit-content(", StringComparison.OrdinalIgnoreCase)
            || length.Equals("fit-content", StringComparison.OrdinalIgnoreCase)
            || length.Equals("inherit", StringComparison.OrdinalIgnoreCase)
            || length.Equals("initial", StringComparison.OrdinalIgnoreCase)
            || length.Equals("unset", StringComparison.OrdinalIgnoreCase)
                ? null
                : length;
    }
}
