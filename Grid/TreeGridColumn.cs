using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Non-generic interface for column registration from TreeGridColumn.
/// </summary>
public interface ITreeGridControlOwner
{
    void AddColumn(TreeGridColumn column);
}

/// <summary>
/// Defines a column in the TreeGridControl. Equivalent to SyncFusion's TreeGridColumn.
/// </summary>
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
    [Parameter] public RenderFragment<object>? Template { get; set; }
    [Parameter] public RenderFragment? HeaderTemplate { get; set; }
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
