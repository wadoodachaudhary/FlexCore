namespace Fx.ControlKit.Grid;

public enum ColumnType
{
    Text,
    Password,
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
    FillAvailable,
    FitColumns
}

public enum GridRowSelectorHandleShape
{
    HalfButton,
    Button,
    CheckBox
}

public enum GridTheme
{
    Default = 0,
    [Obsolete("Use Default.")]
    HomeFront = Default,
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

public enum GroupExpandIconStyle
{
    PlusMinus,
    Triangle
}

public enum TreeGridHeaderIconKind
{
    None,
    ExpandAll,
    CollapseAll
}

public enum AggregateType
{
    Sum,
    Average,
    Count,
    Min,
    Max
}
