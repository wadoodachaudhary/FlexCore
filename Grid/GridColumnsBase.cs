using Microsoft.AspNetCore.Components;
using System;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Base class that collects GridColumn children.
/// Acts as a CascadingValue so columns can register themselves.
/// Also registers itself with the parent GridControl via cascading parameter.
/// </summary>
public class GridColumnsBase : ComponentBase, IDisposable
{
    private readonly List<GridColumn> _columns = new();

    [CascadingParameter] internal IGridOwner? ParentGrid { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter] public string? DebugTag { get; set; }

    public IReadOnlyList<GridColumn> Columns => _columns;

    private bool _columnsDirty;
    private bool _renderArmed;
    private bool _disposed;

    // Wave-scoped change flags, consumed by the grid's pre-render structure
    // sync (SyncPendingColumnStructure). "Shape" = a column was ADDED or
    // REMOVED (positional cell state must be cleared — CellIndex would silently
    // retarget); "instance" = a same-Field REPLACEMENT (only instance-keyed
    // caches need invalidating; position and selection are unaffected).
    private bool _shapeChanged;
    private bool _instanceReplaced;

    internal bool TakeShapeChanged()
    {
        var v = _shapeChanged; _shapeChanged = false; return v;
    }

    internal bool TakeInstanceReplaced()
    {
        var v = _instanceReplaced; _instanceReplaced = false; return v;
    }

    /// <summary>Bumped once per completed registration wave (see OnAfterRender).
    /// Identifies the column-set version handed to the grid.</summary>
    public int ColumnsGeneration { get; private set; }

    internal void AddColumn(GridColumn column)
    {
        if (_columns.Contains(column)) return;

        // Dedup by Field. Blazor disposes and recreates a GridColumn instance
        // when the host's foreach changes which @if/@else branch occupies a
        // given position (each branch compiles to a different sequence number,
        // so a branch switch is treated as a structural mismatch). Without
        // dedup, the disposed instance stays in _columns while the new one
        // gets appended, and the grid renders the column twice. Replacing at
        // the existing index keeps _columns in sync with the live render tree
        // and preserves the order set by drag-reorder.
        if (!string.IsNullOrEmpty(column.Field))
        {
            var existing = _columns.FindIndex(c =>
                string.Equals(c.Field, column.Field, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                // Carry the user's drag-resize forward — the replacing instance
                // starts with a null RuntimeWidth and silently reset the column.
                column.RuntimeWidth ??= _columns[existing].RuntimeWidth;
                _columns[existing] = column;
                _instanceReplaced = true;
                MarkColumnsDirty();
                return;
            }
        }

        _columns.Add(column);
        _shapeChanged = true;
        MarkColumnsDirty();
    }

    /// <summary>Reference-only removal, called from GridColumn.Dispose. A
    /// dedup-replaced instance (same Field, no longer in the list) no-ops here,
    /// whichever order Blazor runs the init and the dispose in. Silent while
    /// the container itself is being torn down (@key swap) — the dying list
    /// must not arm renders.</summary>
    internal void RemoveColumn(GridColumn column)
    {
        if (_disposed)
            return;

        var idx = _columns.IndexOf(column);   // reference equality — no overrides on GridColumn
        if (idx < 0)
            return;

        _columns.RemoveAt(idx);
        _shapeChanged = true;
        MarkColumnsDirty();
    }

    public void Dispose()
    {
        _disposed = true;
        ParentGrid?.UnregisterColumnsContainer(this);
    }

    // Registration no longer redraws the grid per column. The FIRST change of a
    // wave arms exactly one guarded redraw; because columns register while the
    // render pass is still in flight, that redraw joins the SAME wire batch and
    // the browser receives the complete column set in one paint. OnAfterRender
    // (post-batch) then stamps the completed generation.
    private void MarkColumnsDirty()
    {
        _columnsDirty = true;
        if (_renderArmed) return;
        _renderArmed = true;
        ParentGrid?.NotifyColumnsChanged();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (!_columnsDirty) return;
        _columnsDirty = false;
        _renderArmed = false;
        ColumnsGeneration++;
        ParentGrid?.NotifyColumnsCompleted(ColumnsGeneration);
    }

    /// <summary>Moves the column with field <paramref name="fromField"/> relative
    /// to the target <paramref name="toField"/>. When <paramref name="insertAfter"/>
    /// is false the column is inserted before target; when true it is inserted
    /// after target. Used by GridControl header drag-and-drop reordering.</summary>
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

        // After removing the dragged column, the target index shifts left by one
        // when the source was before the target.
        if (fromIdx < toIdx) toIdx--;

        var insertIdx = insertAfter ? toIdx + 1 : toIdx;
        if (insertIdx < 0) insertIdx = 0;
        if (insertIdx > _columns.Count) insertIdx = _columns.Count;

        _columns.Insert(insertIdx, moving);
        var after = _columns.Select(c => c.Field).ToList();
        var changed = !before.SequenceEqual(after);
        if (changed)
            _columnsDirty = true;   // generation stamp only — caller redraws
        return changed;
    }

    /// <summary>Rebuilds the column order in-place to match <paramref name="fieldsInOrder"/>.
    /// Columns whose Field appears in the list are placed in that order; any column
    /// not mentioned is appended at the end (preserving its relative order). Used by
    /// the Choose Columns dialog after the user reorders rows. Returns true if the
    /// order changed.</summary>
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
        // Append any columns the caller didn't mention so we never lose a column.
        foreach (var col in _columns)
        {
            if (!seen.Contains(col.Field))
                newList.Add(col);
        }

        if (newList.SequenceEqual(_columns)) return false;
        _columns.Clear();
        _columns.AddRange(newList);
        _columnsDirty = true;   // generation stamp only — caller redraws
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
