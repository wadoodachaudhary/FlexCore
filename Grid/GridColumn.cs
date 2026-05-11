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
    [Parameter] public string? Format { get; set; }
    [Parameter] public bool Visible { get; set; } = true;
    [Parameter] public bool IsPrimaryKey { get; set; }
    [Parameter] public bool AllowSorting { get; set; } = true;
    [Parameter] public bool AllowFiltering { get; set; } = true;
    [Parameter] public bool AllowEditing { get; set; } = true;
    [Parameter] public bool AllowGrouping { get; set; } = true;
    [Parameter] public bool AllowResizing { get; set; } = true;
    [Parameter] public ClipMode ClipMode { get; set; } = ClipMode.Clip;
    [Parameter] public string? ValidationRules { get; set; }
    [Parameter] public string? DefaultValue { get; set; }

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
        parts.Add("padding:4px 4px");
        if (ClipMode == ClipMode.Ellipsis || ClipMode == ClipMode.EllipsisWithTooltip)
        {
            parts.Add("overflow:hidden");
            parts.Add("text-overflow:ellipsis");
            parts.Add("white-space:nowrap");
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
