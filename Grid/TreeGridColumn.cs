using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

public interface ITreeGridControlOwner
{
    void AddColumn(TreeGridColumn column);
}

public class TreeGridColumn : ComponentBase
{
    [CascadingParameter] internal ITreeGridControlOwner? Owner { get; set; }

    [Parameter] public string Field { get; set; } = "";
    [Parameter] public string HeaderText { get; set; } = "";
    [Parameter] public string? Width { get; set; }
    [Parameter] public string? MinWidth { get; set; }
    [Parameter] public ColumnType Type { get; set; } = ColumnType.Text;
    [Parameter] public TextAlign TextAlign { get; set; } = TextAlign.Left;
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

    public string DisplayHeader => string.IsNullOrEmpty(HeaderText) ? Field : HeaderText;

    protected override void OnInitialized()
    {
        Owner?.AddColumn(this);
    }

    public string GetCellStyle()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Width)) parts.Add($"width:{Width}");
        if (!string.IsNullOrEmpty(MinWidth)) parts.Add($"min-width:{MinWidth}");
        parts.Add($"text-align:{TextAlign.ToString().ToLower()}");
        return string.Join(";", parts);
    }

    public string GetHeaderStyle()
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(Width)) parts.Add($"width:{Width}");
        parts.Add($"text-align:{TextAlign.ToString().ToLower()}");
        return string.Join(";", parts);
    }
}
