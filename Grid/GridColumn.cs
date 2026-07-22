using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

public class GridColumn : ComponentBase
{
    [CascadingParameter] internal GridColumnsBase? Parent { get; set; }

    [Parameter] public string Field { get; set; } = "";
    [Parameter] public string? HeaderText { get; set; }
    [Parameter] public string? Width { get; set; }
    [Parameter] public string? MinWidth { get; set; }
    [Parameter] public string? MaxWidth { get; set; }
    [Parameter] public ColumnType Type { get; set; } = ColumnType.Text;
    [Parameter] public TextAlign TextAlign { get; set; } = TextAlign.Left;
    [Parameter] public TextAlign HeaderTextAlign { get; set; } = TextAlign.Left;
    [Parameter] public string? Format { get; set; }
    [Parameter] public string? Formula { get; set; }
    [Parameter] public bool Visible { get; set; } = true;
    [Parameter] public bool IsPrimaryKey { get; set; }
    [Parameter] public bool AllowSorting { get; set; } = true;
    [Parameter] public bool AllowFiltering { get; set; } = true;
    [Parameter] public bool AllowEditing { get; set; } = true;
    [Parameter] public bool AllowHiding { get; set; } = true;
    [Parameter] public bool AllowCellDragSelection { get; set; }
    [Parameter] public bool AllowGrouping { get; set; } = true;
    [Parameter] public bool AllowResizing { get; set; } = true;
    [Parameter] public ClipMode ClipMode { get; set; } = ClipMode.Clip;
    [Parameter] public string? ValidationRules { get; set; }
    [Parameter] public string? DefaultValue { get; set; }
    [Parameter] public int? MaxLength { get; set; }

    [Parameter] public bool SelectAllOnEdit { get; set; }

    [Parameter] public bool ShowNumericSpinner { get; set; }

    [Parameter] public bool ShowEditButton { get; set; }
    [Parameter] public string EditButtonText { get; set; } = "...";

    [Parameter] public bool AlwaysShowEditButton { get; set; }

    [Parameter] public bool OpenEditButtonOnDoubleClick { get; set; } = true;

    [Parameter] public bool PreferCellEditOnDoubleClick { get; set; }

    [Parameter] public Func<object, bool>? ShowEditButtonPredicate { get; set; }

    [Parameter] public IEnumerable<string>? EditOptions { get; set; }

    [Parameter] public bool OpenEditOptionsOnEdit { get; set; } = true;

    [Parameter] public RenderFragment<object>? Template { get; set; }

    [Parameter] public RenderFragment? HeaderTemplate { get; set; }

    [Parameter] public RenderFragment<object>? EditTemplate { get; set; }

    [Parameter] public RenderFragment<object>? ChildContent { get; set; }

    [Parameter] public FilterSettings? FilterSettings { get; set; }

    [Parameter] public List<GridCommandModel>? Commands { get; set; }

    public string DisplayHeader => HeaderText ?? Field;

    public RenderFragment<object>? EffectiveTemplate => Template ?? ChildContent;

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
        var effectiveTextAlign = UsesMappedEditOptionDisplay() ? TextAlign.Left : TextAlign;
        parts.Add($"text-align:{effectiveTextAlign.ToString().ToLower()}");
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
        return string.Join(";", parts);
    }
}
