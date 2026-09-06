using Microsoft.AspNetCore.Components;
namespace Fx.ControlKit.Grid;
public partial class TreeGridControl<TValue>
{
    [Parameter] public bool ShowCheckboxes { get; set; }
    [Parameter] public bool AutoCheckHierarchy { get; set; } = true;
    [Parameter] public Func<TValue, bool>? CanCheckRow { get; set; }
    [Parameter] public IReadOnlyList<TValue>? CheckedItems { get; set; }
    [Parameter] public EventCallback<IReadOnlyList<TValue>> CheckedItemsChanged { get; set; }
    private readonly HashSet<object> _checkedKeys = [], _mixedKeys = [];
    private object[] _lastCheckedParameter = [];
    private bool CanCheck(TreeNode<TValue> node)
    {
        if (CanCheckRow is null) return true;
        for (TreeNode<TValue>? current = node; current is not null; current = current.ParentId is null ? null : _flatNodes.FirstOrDefault(n => Equals(n.Id, current.ParentId)))
            if (!CanCheckRow(current.Data)) return false;
        return true;
    }
    private bool IsChecked(TreeNode<TValue> node) => node.Id is {} id && _checkedKeys.Contains(id);
    private bool IsMixed(TreeNode<TValue> node) => node.Id is {} id && _mixedKeys.Contains(id);
    private bool AllRowsChecked => _flatNodes.Any(CanCheck) && _flatNodes.Where(CanCheck).All(IsChecked);
    private bool SomeRowsChecked => !AllRowsChecked && _flatNodes.Where(CanCheck).Any(n => IsChecked(n) || IsMixed(n));
    public IReadOnlyList<TValue> GetCheckedRecords() => _flatNodes.Where(IsChecked).Select(n => n.Data).ToArray();
    private void ReceiveCheckedItems()
    {
        var ids = CheckedItems?.Select(RecordId).ToArray() ?? [];
        if (!_lastCheckedParameter.SequenceEqual(ids))
        {
            _lastCheckedParameter = ids; _checkedKeys.Clear(); _checkedKeys.UnionWith(ids);
            if (AutoCheckHierarchy)
                foreach (var node in _flatNodes.Where(IsChecked).ToArray()) CascadeCheck(node, true);
        }
        RefreshHierarchyChecks();
    }
    private void CascadeCheck(TreeNode<TValue> node, bool value)
    {
        if (!CanCheck(node) || node.Id is not {} id) return;
        if (value) _checkedKeys.Add(id); else _checkedKeys.Remove(id);
        if (AutoCheckHierarchy)
            foreach (var child in _flatNodes.Where(n => Equals(n.ParentId, id)).ToArray()) CascadeCheck(child, value);
    }
    private void RefreshHierarchyChecks()
    {
        _checkedKeys.IntersectWith(_flatNodes.Where(CanCheck).Select(n => n.Id!)); _mixedKeys.Clear();
        if (!AutoCheckHierarchy) return;
        var children = _flatNodes.Where(n => n.ParentId is not null).ToLookup(n => n.ParentId!);
        foreach (var node in _flatNodes.AsEnumerable().Reverse())
        {
            if (!CanCheck(node) || node.Id is not {} id) continue;
            var eligible = children[id].Where(CanCheck).ToArray();
            if (eligible.Length == 0) continue;
            if (eligible.All(IsChecked)) _checkedKeys.Add(id);
            else
            {
                _checkedKeys.Remove(id);
                if (eligible.Any(n => IsChecked(n) || IsMixed(n))) _mixedKeys.Add(id);
            }
        }
    }
    private Task PublishChecksAsync() => CheckedItemsChanged.InvokeAsync(GetCheckedRecords());
    public async Task SetRowCheckedAsync(object id, bool value)
    {
        var node = _flatNodes.FirstOrDefault(n => Equals(n.Id, id));
        if (node is null) return;
        CascadeCheck(node, value); RefreshHierarchyChecks(); await PublishChecksAsync(); await InvokeAsync(StateHasChanged);
    }
    /// <summary>Checks all loaded rows, including collapsed, filtered and paged rows. Disabled branches are skipped.</summary>
    public async Task CheckAllAsync(bool value = true)
    {
        var roots = _flatNodes.Where(n => n.Level == 0).ToArray();
        if (AutoCheckHierarchy) foreach (var node in roots) CascadeCheck(node, value);
        else foreach (var node in _flatNodes.Where(CanCheck)) CascadeCheck(node, value);
        RefreshHierarchyChecks(); await PublishChecksAsync(); await InvokeAsync(StateHasChanged);
    }
}
