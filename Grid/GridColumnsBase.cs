using Microsoft.AspNetCore.Components;
using System;

namespace Fx.ControlKit.Grid;

public class GridColumnsBase : ComponentBase
{
    private readonly List<GridColumn> _columns = new();

    [CascadingParameter] internal IGridOwner? ParentGrid { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public string? DebugTag { get; set; }

    public IReadOnlyList<GridColumn> Columns => _columns;

    internal void AddColumn(GridColumn column)
    {
        if (_columns.Contains(column)) return;

        if (!string.IsNullOrEmpty(column.Field))
        {
            var existing = _columns.FindIndex(c =>
                string.Equals(c.Field, column.Field, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                _columns[existing] = column;
                ParentGrid?.NotifyColumnsChanged();
                return;
            }
        }

        _columns.Add(column);
        ParentGrid?.NotifyColumnsChanged();
        if (false && !string.IsNullOrWhiteSpace(DebugTag))
            Console.WriteLine($"[GridColumnsBase:{DebugTag}] AddColumn field='{column.Field}' visible='{column.Visible}'");
    }

    internal bool ReorderColumn(string fromField, string toField, bool insertAfter = false)
    {
        if (string.IsNullOrEmpty(fromField) || string.IsNullOrEmpty(toField)
            || string.Equals(fromField, toField, StringComparison.Ordinal))
            return false;

        var fromIdx = _columns.FindIndex(c => string.Equals(c.Field, fromField, StringComparison.OrdinalIgnoreCase));
        var toIdx = _columns.FindIndex(c => string.Equals(c.Field, toField, StringComparison.OrdinalIgnoreCase));
        if (fromIdx < 0 || toIdx < 0) return false;

        var before = _columns.Select(c => c.Field).ToList();
        var moving = _columns[fromIdx];
        _columns.RemoveAt(fromIdx);

        if (fromIdx < toIdx) toIdx--;

        var insertIdx = insertAfter ? toIdx + 1 : toIdx;
        if (insertIdx < 0) insertIdx = 0;
        if (insertIdx > _columns.Count) insertIdx = _columns.Count;

        _columns.Insert(insertIdx, moving);
        var after = _columns.Select(c => c.Field).ToList();
        return !before.SequenceEqual(after);
    }

    internal bool ReorderColumns(IEnumerable<string> fieldsInOrder)
    {
        if (fieldsInOrder == null) return false;
        var orderList = fieldsInOrder.Where(f => !string.IsNullOrEmpty(f)).ToList();
        if (orderList.Count == 0) return false;

        var byField = _columns
            .Where(c => !string.IsNullOrEmpty(c.Field))
            .GroupBy(c => c.Field, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var newList = new List<GridColumn>(_columns.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in orderList)
        {
            if (seen.Add(f) && byField.TryGetValue(f, out var col))
                newList.Add(col);
        }
        foreach (var col in _columns)
        {
            if (!seen.Contains(col.Field))
                newList.Add(col);
        }

        if (newList.SequenceEqual(_columns)) return false;
        _columns.Clear();
        _columns.AddRange(newList);
        return true;
    }

    protected override void OnInitialized()
    {
        ParentGrid?.RegisterColumnsContainer(this);
        if (false && !string.IsNullOrWhiteSpace(DebugTag))
            Console.WriteLine($"[GridColumnsBase:{DebugTag}] Initialized");
    }

    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<GridColumnsBase>>(0);
        builder.AddComponentParameter(1, "Value", this);
        builder.AddComponentParameter(2, "ChildContent", ChildContent);
        builder.CloseComponent();
    }
}
