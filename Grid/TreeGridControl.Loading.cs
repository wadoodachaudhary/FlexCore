using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Fx.ControlKit.Grid;

public partial class TreeGridControl<TValue> : IAsyncDisposable
{
    /// <summary>Returns direct children whose mapped parent ID equals the requested record's ID.</summary>
    [Parameter] public Func<TValue, CancellationToken, Task<IReadOnlyList<TValue>>>? LoadChildren { get; set; }
    [Parameter] public EventCallback<string> LoadFailed { get; set; }
    private readonly Dictionary<object, IReadOnlyList<TValue>> _loadedChildren = [];
    private readonly HashSet<object> _completedChildLoads = [];
    private readonly Dictionary<object, int> _loadingChildren = [];
    private readonly Dictionary<object, bool> _expansionIntent = [];
    private readonly CancellationTokenSource _loadLifetime = new();
    private int _dataGeneration;
    private bool _disposed;
    private string? _loadError;

    private bool MayHaveUnloadedChildren(TValue item, object id) =>
        TreatAsParent?.Invoke(item) == true && !_completedChildLoads.Contains(id);
    private bool NeedsChildLoad(TreeNode<TValue> node) => LoadChildren is not null && node.Id is not null &&
        MayHaveUnloadedChildren(node.Data, node.Id) && !_flatNodes.Any(n => Equals(n.ParentId, node.Id));
    private bool IsLoading(TreeNode<TValue> node) => node.Id is not null && _loadingChildren.ContainsKey(node.Id);
    private async Task<bool> LoadNodeChildrenAsync(TreeNode<TValue> node)
    {
        if (node.Id is not { } id || LoadChildren is null) return true;
        var generation = _dataGeneration;
        _loadingChildren[id] = generation; _loadError = null;
        await InvokeAsync(StateHasChanged);
        try
        {
            var children = await LoadChildren(node.Data, _loadLifetime.Token);
            if (_disposed || generation != _dataGeneration) return false;
            var parent = typeof(TValue).GetProperty(ParentIdMapping)!;
            if (children.Any(child => !Equals(parent.GetValue(child), id)))
                throw new ArgumentException("The provider must return direct children with the requested parent ID.");
            var inheritCheck = _checkedKeys.Contains(id);
            _loadedChildren[id] = children.ToArray(); _completedChildLoads.Add(id);
            try { RebuildPreservingExpansion(); }
            catch { _completedChildLoads.Remove(id); _loadedChildren.Remove(id); RebuildPreservingExpansion(); throw; }
            if (inheritCheck && AutoCheckHierarchy) CascadeCheck(_flatNodes.First(n => Equals(n.Id, id)), true);
            RefreshHierarchyChecks(); await PublishChecksAsync();
            return true;
        }
        catch (OperationCanceledException) when (_loadLifetime.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (_disposed || generation != _dataGeneration) return false;
            _loadError = ex.Message;
            var current = _flatNodes.FirstOrDefault(n => Equals(n.Id, id));
            if (current is not null) current.IsExpanded = false;
            await LoadFailed.InvokeAsync(ex.Message);
            return false;
        }
        finally
        {
            if (_loadingChildren.GetValueOrDefault(id, -1) == generation) _loadingChildren.Remove(id);
            if (!_disposed) await InvokeAsync(StateHasChanged);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ClearEditSessions();
        _disposed = true; _loadLifetime.Cancel(); _loadLifetime.Dispose();
        if (_legacyScrollModule is not null)
            try { await _legacyScrollModule.InvokeVoidAsync("disposeTreeGridLayout", _treeGridElement); await _legacyScrollModule.DisposeAsync(); } catch (JSDisconnectedException) { }
    }
}
