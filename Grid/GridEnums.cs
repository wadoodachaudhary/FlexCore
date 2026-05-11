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

public enum EditMode
{
    Inline,
    Dialog,
    Batch
}

public enum SelectionType
{
    Single,
    Multiple
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
