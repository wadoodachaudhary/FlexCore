namespace Fx.ControlKit.Grid;

public enum ColumnType
{
    Text,
    Number,
    Date,
    Boolean,
    CheckBox
}

public enum TextAlign
{
    Left,
    Center,
    Right
}

public enum SortDirection
{
    Ascending,
    Descending
}

public enum FilterType
{
    FilterBar,
    Menu,
    CheckBox
}

public enum TextFilterOperator
{
    ChooseOne,
    Equals,
    DoesNotEqual,
    BeginsWith,
    DoesNotBeginWith,
    EndsWith,
    DoesNotEndWith,
    Contains,
    DoesNotContain
}

public enum EditMode
{
    Inline,
    Dialog,
    Batch
}

public enum GridBatchEditBehavior
{
    MultiRow,
    SingleCell
}

public enum SelectionType
{
    Single,
    Multiple
}

public enum GridMultiSelectBehavior
{
    FullMultiSelect,
    VBMultiSelect
}

public enum SelectionMode
{
    Row,
    Cell
}

public enum ClipMode
{
    Clip,
    Ellipsis,
    EllipsisWithTooltip
}

public enum GridLines
{
    Default,
    Both,
    Horizontal,
    Vertical,
    None
}

public enum GridWidthMode
{
    /// <summary>Table keeps at least the grid host width; this is the existing full-width behavior.</summary>
    FillAvailable,
    /// <summary>Grid host and table size to the sum of visible column widths, up to the parent width.</summary>
    FitColumns
}

public enum GridTheme
{
    HomeFront,
    Vb6Windows,
    ExcelLightBlue,
    ExcelLightGreen,
    ExcelLightOrange,
    ExcelMediumBlue,
    ExcelMediumGreen,
    ExcelDarkSlate
}

public enum NewRowPosition
{
    Top,
    Bottom
}

/// <summary>
/// Visual style of the expand/collapse icon shown on grouped-column header
/// rows. Project default is <see cref="PlusMinus"/> — a small bordered
/// "+" / "−" box like the VB6 tree (and Windows Explorer). Use
/// <see cref="Triangle"/> for the chevron-style ▶ / ▼ glyphs that older
/// Blazor grids favour.
/// </summary>
public enum GroupExpandIconStyle
{
    /// <summary>Bordered "+" (collapsed) / "−" (expanded) box. Project default.</summary>
    PlusMinus,
    /// <summary>Right-pointing "▶" (collapsed) / down-pointing "▼" (expanded) chevrons.</summary>
    Triangle
}

public enum AggregateType
{
    Sum,
    Average,
    Count,
    Min,
    Max
}
