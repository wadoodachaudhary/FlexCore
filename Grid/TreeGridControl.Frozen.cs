using Microsoft.AspNetCore.Components;
namespace Fx.ControlKit.Grid;
public partial class TreeGridControl<TValue>
{
    [Parameter] public int FrozenColumns { get; set; }
    private readonly Dictionary<string, FrozenColumnPosition?> _frozenOverrides = [];
    private TreeGridColumn? _menuColumn;
    private FrozenColumnPosition? FrozenPosition(TreeGridColumn column)
    {
        if (_frozenOverrides.TryGetValue(column.Field, out var value)) return value;
        if (column.IsFrozen) return column.FrozenPosition;
        return _columns.Where(IsColumnVisible).Take(Math.Max(0, FrozenColumns)).Contains(column) ? FrozenColumnPosition.Left : null;
    }
    private string? FrozenSide(TreeGridColumn column) => FrozenPosition(column)?.ToString().ToLowerInvariant();
    private string FrozenStyle(TreeGridColumn column)
    {
        var side = FrozenSide(column); if (side is null) return "";
        var columns = VisibleColumns;
        var offset = side == "left" ? (ShowCheckboxes ? 36d : 0d) : 0d;
        foreach (var other in side == "left" ? columns : columns.AsEnumerable().Reverse())
        {
            if (ReferenceEquals(other, column)) break;
            if (FrozenSide(other) == side) offset += GetEffectiveColumnWidth(other) is > 0 and var width ? width : 120;
        }
        return $";position:sticky;{side}:var(--fx-tree-col-{columns.IndexOf(column)},{offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)";
    }
    public async Task FreezeColumnAsync(string field, FrozenColumnPosition? position)
    {
        if (!_columns.Any(c => c.Field == field)) throw new ArgumentException("Unknown column: " + field);
        if (position.HasValue && !Enum.IsDefined(position.Value)) throw new ArgumentException("Unknown frozen position.");
        if (!await FinishActiveEditorAsync()) return;
        var cursor = VisibleColumns.ElementAtOrDefault(CurrentCellColumnIndex);
        _frozenOverrides[field] = position;
        if (cursor is not null) _activeCellColumnIndex = VisibleColumns.IndexOf(cursor);
        await InvokeAsync(StateHasChanged);
    }
}
