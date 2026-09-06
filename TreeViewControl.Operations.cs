using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Fx.ControlKit;

public partial class TreeViewControl
{
    [Parameter] public string AriaLabel { get; set; } = "Tree";
    [Parameter] public IReadOnlyList<string>? SelectedNodeIds { get; set; }
    [Parameter] public EventCallback<IReadOnlyList<string>> SelectedNodeIdsChanged { get; set; }
    [Parameter] public IReadOnlyList<string>? CheckedNodeIds { get; set; }
    [Parameter] public EventCallback<IReadOnlyList<string>> CheckedNodeIdsChanged { get; set; }
    [Parameter] public IReadOnlyList<string>? ExpandedNodeIds { get; set; }
    [Parameter] public EventCallback<IReadOnlyList<string>> ExpandedNodeIdsChanged { get; set; }
    [Parameter] public EventCallback<List<TreeNode>> NodesChanged { get; set; }
    [Parameter] public bool AutoCheck { get; set; }
    [Parameter] public bool AllowEditing { get; set; }
    [Parameter] public bool AllowDragAndDrop { get; set; }
    [Parameter] public bool EnableVirtualization { get; set; }
    [Parameter] public float ItemSize { get; set; } = 32;
    [Parameter] public string? FilterText { get; set; }
    [Parameter] public IComparer<TreeNode>? NodeComparer { get; set; }
    [Parameter] public RenderFragment<TreeNode>? NodeTemplate { get; set; }
    [Parameter] public Func<TreeNode, CancellationToken, Task<IReadOnlyList<TreeNode>>>? LoadChildren { get; set; }
    [Parameter] public EventCallback<TreeNodeEditEventArgs> NodeEditing { get; set; }
    [Parameter] public EventCallback<TreeNodeEditEventArgs> NodeEditCommitting { get; set; }
    [Parameter] public EventCallback<TreeNodeEditEventArgs> NodeEdited { get; set; }
    [Parameter] public EventCallback<TreeNodeMoveEventArgs> NodeMoving { get; set; }
    [Parameter] public EventCallback<TreeNodeMoveEventArgs> NodeMoved { get; set; }
    [Parameter] public Func<TreeNodeMoveEventArgs, bool>? CanDrop { get; set; }
    [Parameter] public EventCallback<string> Error { get; set; }

    private List<TreeNode> Roots => Nodes ??= [];
    private List<TreeNodeInfo> _all = [], _visible = [];
    private HashSet<string> _filteredParentIds = new(StringComparer.Ordinal);
    private bool DisplayExpanded(TreeNode node) => string.IsNullOrWhiteSpace(FilterText) ? node.IsExpanded : _filteredParentIds.Contains(node.Id);
    private readonly Dictionary<string, bool> _expansionIntent = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loading = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private string[]? _lastSelected, _lastChecked, _lastExpanded;
    private TreeNode? _lastPrimary;
    private List<TreeNode>? _lastRoots;
    private string? _focusId, _anchorId, _editingId, _dragId, _error, _revealId;
    private string _editText = "";
    private readonly string _instanceId = "tree-" + Guid.NewGuid().ToString("N");
    private ElementReference _host;
    private BrowserInterop? _browser;
    private bool _disposed, _editingBusy;
    private string NodeElementId(string id) => _instanceId + "-" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(id));

    protected override void OnParametersSet()
    {
        if (!float.IsFinite(ItemSize) || ItemSize < 20) throw new ArgumentException("Tree ItemSize must be at least 20 pixels.");
        if (!ReferenceEquals(_lastRoots, Roots))
        {
            _lastRoots = Roots;
            _lastSelected = _lastChecked = _lastExpanded = null;
            _lastPrimary = null;
        }
        _all = TreeViewData.Flatten(Roots, NodeComparer);
        ApplyIds(SelectedNodeIds, ref _lastSelected, (n, v) => n.IsSelected = v);
        ApplyIds(CheckedNodeIds, ref _lastChecked, (n, v) => { n.IsChecked = v; n.IsIndeterminate = false; });
        ApplyIds(ExpandedNodeIds, ref _lastExpanded, (n, v) => n.IsExpanded = v);
        if (SelectedNodeIds is null && !ReferenceEquals(SelectedNode, _lastPrimary))
        {
            if (!AllowMultiSelect || SelectedNode is null)
                foreach (var info in _all) info.Node.IsSelected = false;
            if (SelectedNode is not null && Find(SelectedNode.Id) is { } current)
            {
                current.IsSelected = true;
                _focusId = current.Id;
            }
        }
        if (SelectedNodeIds is not null) SelectedNode = _all.FirstOrDefault(n => n.Node.IsSelected)?.Node;
        _lastPrimary = SelectedNode;
        Reindex();
    }
    private void ApplyIds(IReadOnlyList<string>? values, ref string[]? previous, Action<TreeNode, bool> apply)
    {
        if (values is null) { previous = null; return; }
        if (previous is not null && values.SequenceEqual(previous)) return;
        previous = values.ToArray(); var set = values.ToHashSet(StringComparer.Ordinal);
        foreach (var info in _all) apply(info.Node, set.Contains(info.Node.Id));
        if (ReferenceEquals(values, CheckedNodeIds) && AutoCheck)
            foreach (var info in _all)
                if (!info.Node.IsDisabled && info.Parent is { IsChecked: true, IsDisabled: false }) info.Node.IsChecked = true;
    }
    private TreeNode? Find(string id) => _all.FirstOrDefault(n => n.Node.Id == id)?.Node;
    private void Reindex()
    {
        _all = TreeViewData.Flatten(Roots, NodeComparer);
        TreeViewData.RefreshChecks(Roots, AutoCheck);
        var included = new HashSet<string>(StringComparer.Ordinal);
        var index = _all.ToDictionary(n => n.Node.Id, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(FilterText))
            foreach (var info in _all.Where(n => n.Node.Text.Contains(FilterText, StringComparison.CurrentCultureIgnoreCase)))
                for (var ancestor = info; ancestor is not null; ancestor = ancestor.Parent is null ? null : index[ancestor.Parent.Id]) included.Add(ancestor.Node.Id);
        _visible = []; var hiddenDepth = int.MaxValue;
        foreach (var info in _all)
        {
            if (!string.IsNullOrWhiteSpace(FilterText)) { if (included.Contains(info.Node.Id)) _visible.Add(info); continue; }
            if (info.Level > hiddenDepth) continue;
            hiddenDepth = info.Node.IsExpanded ? int.MaxValue : info.Level; _visible.Add(info);
        }
        _filteredParentIds = _visible.Where(n => n.Parent is not null).Select(n => n.Parent!.Id).ToHashSet(StringComparer.Ordinal);
        if (!_visible.Any(n => n.Node.Id == _focusId && !n.Node.IsDisabled)) _focusId = _visible.FirstOrDefault(n => n.Node.IsSelected && !n.Node.IsDisabled)?.Node.Id ?? _visible.FirstOrDefault(n => !n.Node.IsDisabled)?.Node.Id;
        if (_editingId is not null && !_visible.Any(n => n.Node.Id == _editingId)) _editingId = null;
    }
    private async Task PublishSelectionAsync()
    {
        await SelectedNodeIdsChanged.InvokeAsync(_all.Where(n => n.Node.IsSelected).Select(n => n.Node.Id).ToArray());
    }
    private Task PublishChecksAsync() => CheckedNodeIdsChanged.InvokeAsync(_all.Where(n => n.Node.IsChecked).Select(n => n.Node.Id).ToArray());
    private Task PublishExpansionAsync() => ExpandedNodeIdsChanged.InvokeAsync(_all.Where(n => n.Node.IsExpanded).Select(n => n.Node.Id).ToArray());
    private async Task SelectAsync(TreeNode node, bool control = false, bool shift = false)
    {
        if (node.IsDisabled) return;
        var visible = _visible.Where(n => !n.Node.IsDisabled).Select(n => n.Node).ToList();
        if (!AllowMultiSelect || (!control && !shift)) foreach (var info in _all) info.Node.IsSelected = false;
        if (AllowMultiSelect && shift)
        {
            if (!control) foreach (var info in _all) info.Node.IsSelected = false;
            var from = visible.FindIndex(n => n.Id == (_anchorId ?? _focusId)); var to = visible.IndexOf(node);
            if (from < 0) from = to;
            for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++) if (i >= 0) visible[i].IsSelected = true;
        }
        else { node.IsSelected = !(AllowMultiSelect && control && node.IsSelected); _anchorId = node.Id; }
        _focusId = node.Id; SelectedNode = node; _lastPrimary = node;
        await SelectedNodeChanged.InvokeAsync(node); await PublishSelectionAsync();
        _revealId = node.Id; await InvokeAsync(StateHasChanged);
    }
    private async Task HandleNodeClick(TreeNode node, MouseEventArgs e)
    {
        if (node.IsDisabled) return;
        if (ToggleOnNodeClick && node.HasChildren && !e.CtrlKey && !e.MetaKey && !e.ShiftKey) await SetExpandedAsync(node.Id, !node.IsExpanded);
        await SelectAsync(node, e.CtrlKey || e.MetaKey, e.ShiftKey); await OnNodeClick.InvokeAsync(node);
    }
    private async Task HandleNodeDoubleClick(TreeNode node)
    {
        if (node.IsDisabled) return;
        await OnNodeDoubleClick.InvokeAsync(node);
        if (AllowEditing) await BeginEditAsync(node.Id);
    }
    public async Task SelectNodesAsync(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal); if (!AllowMultiSelect) set = set.Take(1).ToHashSet();
        foreach (var info in _all) info.Node.IsSelected = !info.Node.IsDisabled && set.Contains(info.Node.Id);
        var primary = _all.FirstOrDefault(n => n.Node.IsSelected)?.Node; SelectedNode = primary; _lastPrimary = primary;
        if (primary is not null) { _focusId = primary.Id; await SelectedNodeChanged.InvokeAsync(primary); }
        await PublishSelectionAsync(); await InvokeAsync(StateHasChanged);
    }
    public async Task SetCheckedAsync(string id, bool value)
    {
        var node = Find(id); if (node is null || node.IsDisabled) return;
        TreeViewData.SetChecked(Roots, node, value, AutoCheck); _focusId = id; _revealId = id; await PublishChecksAsync(); await InvokeAsync(StateHasChanged);
    }
    public async Task CheckAllAsync(bool value = true)
    {
        foreach (var info in _all.Where(n => !n.Node.IsDisabled)) { info.Node.IsChecked = value; info.Node.IsIndeterminate = false; }
        TreeViewData.RefreshChecks(Roots, AutoCheck); await PublishChecksAsync(); await InvokeAsync(StateHasChanged);
    }
    public async Task SetExpandedAsync(string id, bool expanded)
    {
        var node = Find(id); if (node is null || node.IsDisabled || !node.HasChildren) return;
        _expansionIntent[id] = expanded;
        if (_loading.Contains(id)) return;
        _error = null;
        if (expanded && node.HasUnloadedChildren && LoadChildren is not null)
        {
            _loading.Add(id); await InvokeAsync(StateHasChanged);
            try
            {
                var children = await LoadChildren(node, _lifetime.Token);
                if (_disposed || !TreeViewData.Flatten(Roots).Any(n => ReferenceEquals(n.Node, node))) return;
                var old = node.Children; node.Children = children.ToList();
                try { TreeViewData.Flatten(Roots); } catch { node.Children = old; throw; }
                node.HasUnloadedChildren = false;
                foreach (var child in TreeViewData.Flatten(node.Children))
                {
                    if (SelectedNodeIds?.Contains(child.Node.Id) == true) child.Node.IsSelected = true;
                    if (CheckedNodeIds?.Contains(child.Node.Id) == true) child.Node.IsChecked = true;
                    if (ExpandedNodeIds?.Contains(child.Node.Id) == true) child.Node.IsExpanded = true;
                }
                if (AutoCheck && node.IsChecked) foreach (var child in node.Children) TreeViewData.SetChecked(Roots, child, true, true);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception ex) { node.IsExpanded = false; await FailAsync(ex.Message); return; }
            finally { _loading.Remove(id); if (!_disposed) await InvokeAsync(StateHasChanged); }
        }
        expanded = _expansionIntent.GetValueOrDefault(id, expanded);
        node.IsExpanded = expanded;
        if (expanded) await OnNodeExpand.InvokeAsync(node);
        Reindex(); await PublishExpansionAsync(); await PublishChecksAsync(); await NodesChanged.InvokeAsync(Roots); await InvokeAsync(StateHasChanged);
    }
    /// <summary>Expand known branches. Lazy children are fetched only when loadChildren is explicitly true.</summary>
    public async Task ExpandAllAsync(bool loadChildren = false)
    {
        foreach (var id in _all.Where(n => n.Node.HasChildren && !n.Node.IsDisabled).Select(n => n.Node.Id).ToArray())
        {
            var node = Find(id)!; if (loadChildren) await SetExpandedAsync(id, true); else if (!node.HasUnloadedChildren) node.IsExpanded = true;
        }
        Reindex(); await PublishExpansionAsync(); await InvokeAsync(StateHasChanged);
    }
    public async Task CollapseAllAsync()
    {
        foreach (var id in _loading) _expansionIntent[id] = false;
        foreach (var info in _all) info.Node.IsExpanded = false;
        Reindex(); await PublishExpansionAsync(); await InvokeAsync(StateHasChanged);
    }
    public async Task EnsureVisibleAsync(string id)
    {
        var info = _all.FirstOrDefault(n => n.Node.Id == id) ?? throw new ArgumentException("Unknown tree node.");
        if (!string.IsNullOrWhiteSpace(FilterText) && !_visible.Any(n => n.Node.Id == id))
            throw new InvalidOperationException("Clear the tree filter before revealing this node.");
        for (var parent = info.Parent; parent is not null; parent = _all.First(n => n.Node == parent).Parent) parent.IsExpanded = true;
        Reindex(); _focusId = id; _revealId = id; await PublishExpansionAsync(); await InvokeAsync(StateHasChanged);
    }
    public async Task BeginEditAsync(string id)
    {
        if (!AllowEditing || Find(id) is not { } node || node.IsDisabled) return;
        if (!string.IsNullOrWhiteSpace(FilterText) && !_visible.Any(n => n.Node.Id == id))
        {
            await FailAsync("Clear the tree filter before editing this node.");
            return;
        }
        var args = new TreeNodeEditEventArgs(node, node.Text); await NodeEditing.InvokeAsync(args); if (args.Cancel) return;
        await EnsureVisibleAsync(id); _editingId = id; _editText = args.Text; _error = null; await InvokeAsync(StateHasChanged);
    }
    public async Task CommitEditAsync()
    {
        if (_editingBusy || _editingId is null || Find(_editingId) is not { } node || !AllowEditing) return;
        _editingBusy = true;
        try
        {
            var args = new TreeNodeEditEventArgs(node, _editText.Trim());
            if (args.Text.Length == 0) { await FailAsync("A node name is required."); return; }
            await NodeEditCommitting.InvokeAsync(args);
            if (args.Cancel || args.Error is not null) { if (args.Error is not null) await FailAsync(args.Error); return; }
            node.Text = args.Text; _editingId = null; _error = null; Reindex(); _revealId = node.Id;
            await NodeEdited.InvokeAsync(args); await NodesChanged.InvokeAsync(Roots); await InvokeAsync(StateHasChanged);
        }
        finally { _editingBusy = false; }
    }
    public Task CancelEditAsync() { _editingId = null; _error = null; _revealId = _focusId; return InvokeAsync(StateHasChanged); }
    private Task EditKeyDown(KeyboardEventArgs e) => e.Key == "Enter" ? CommitEditAsync() : e.Key == "Escape" ? CancelEditAsync() : Task.CompletedTask;
    public async Task AddNodeAsync(TreeNode node, string? parentId = null)
    {
        if (parentId is not null && Find(parentId)?.HasUnloadedChildren == true)
            throw new InvalidOperationException("Load the parent before adding children.");
        var target = parentId is null ? Roots : Find(parentId)?.Children ?? throw new ArgumentException("Unknown parent.");
        target.Add(node); try { Reindex(); } catch { target.Remove(node); Reindex(); throw; }
        await NodesChanged.InvokeAsync(Roots); await InvokeAsync(StateHasChanged);
    }
    public async Task RemoveNodeAsync(string id)
    {
        var info = _all.FirstOrDefault(n => n.Node.Id == id); if (info is null || info.Node.IsDisabled) return;
        (info.Parent?.Children ?? Roots).Remove(info.Node); Reindex(); SelectedNode = _all.FirstOrDefault(n => n.Node.IsSelected)?.Node; _lastPrimary = SelectedNode;
        await PublishSelectionAsync(); await PublishChecksAsync(); await PublishExpansionAsync(); await NodesChanged.InvokeAsync(Roots); await InvokeAsync(StateHasChanged);
    }
    public async Task MoveNodeAsync(string id, string targetId, TreeDropPosition position = TreeDropPosition.Inside)
    {
        var node = Find(id) ?? throw new ArgumentException("Unknown source node."); var target = Find(targetId) ?? throw new ArgumentException("Unknown target node.");
        var args = new TreeNodeMoveEventArgs(node, target, position);
        if (CanDrop?.Invoke(args) == false) return; await NodeMoving.InvokeAsync(args); if (args.Cancel) return;
        TreeViewData.Move(Roots, id, targetId, position); Reindex(); _revealId = id;
        await NodeMoved.InvokeAsync(args); await PublishChecksAsync(); await PublishExpansionAsync(); await NodesChanged.InvokeAsync(Roots); await InvokeAsync(StateHasChanged);
    }
    private async Task DropAsync(TreeNode target)
    {
        var id = _dragId; _dragId = null; if (!AllowDragAndDrop || id is null) return;
        try { await MoveNodeAsync(id, target.Id); } catch (Exception ex) { await FailAsync(ex.Message); }
    }
    private async Task FailAsync(string error) { _error = error; await Error.InvokeAsync(error); await InvokeAsync(StateHasChanged); }
    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (_editingId is not null) return;
        var visible = _visible.Where(n => !n.Node.IsDisabled).ToList(); if (visible.Count == 0) return;
        var at = visible.FindIndex(n => n.Node.Id == _focusId); if (at < 0) at = 0;
        var info = visible[at]; var control = e.CtrlKey || e.MetaKey; var next = at;
        switch (e.Key)
        {
            case "ArrowDown": next = Math.Min(at + 1, visible.Count - 1); break;
            case "ArrowUp": next = Math.Max(at - 1, 0); break;
            case "Home": next = 0; break;
            case "End": next = visible.Count - 1; break;
            case "ArrowRight": if (info.Node.HasChildren && !info.Node.IsExpanded) { await SetExpandedAsync(info.Node.Id, true); return; } if (at + 1 < visible.Count && visible[at + 1].Parent == info.Node) next = at + 1; break;
            case "ArrowLeft": if (info.Node.IsExpanded) { await SetExpandedAsync(info.Node.Id, false); return; } if (info.Parent is not null) next = Math.Max(0, visible.FindIndex(n => n.Node == info.Parent)); break;
            case "F2": await BeginEditAsync(info.Node.Id); return;
            case " ": if (ShowCheckboxes) await SetCheckedAsync(info.Node.Id, !info.Node.IsChecked || info.Node.IsIndeterminate); else await SelectAsync(info.Node, control, e.ShiftKey); return;
            case "Enter": if (OnNodeActivated.HasDelegate) await OnNodeActivated.InvokeAsync(info.Node); else await OnNodeDoubleClick.InvokeAsync(info.Node); return;
            case "a" when control && AllowMultiSelect: await SelectNodesAsync(visible.Select(n => n.Node.Id)); return;
            default:
                if (control || e.AltKey || e.Key.Length != 1) return;
                var found = Enumerable.Range(1, visible.Count).Select(i => (at + i) % visible.Count).FirstOrDefault(i => visible[i].Node.Text.StartsWith(e.Key, StringComparison.CurrentCultureIgnoreCase), -1);
                if (found < 0) return; next = found; break;
        }
        if (control && !e.ShiftKey) { _focusId = visible[next].Node.Id; _revealId = _focusId; await InvokeAsync(StateHasChanged); }
        else await SelectAsync(visible[next].Node, control, e.ShiftKey);
    }
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) { _browser = new(JS); await _browser.InvokeAsync<object?>("treeKeyboard", _host); }
        if (LoadChildren is not null)
            foreach (var lazyId in _all.Where(n => n.Node.IsExpanded && n.Node.HasUnloadedChildren && !n.Node.IsDisabled && !_loading.Contains(n.Node.Id)).Select(n => n.Node.Id).ToArray())
                await SetExpandedAsync(lazyId, true);
        if (_revealId is { } id && _browser is not null)
        {
            _revealId = null;
            await _browser.InvokeAsync<object?>("treeReveal", _host, new { id, index = _visible.FindIndex(n => n.Node.Id == id), itemSize = ItemSize, virtualized = EnableVirtualization });
            if (_editingId is null) await _host.FocusAsync(preventScroll: true);
        }
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; _lifetime.Cancel(); _lifetime.Dispose();
        if (_browser is not null) { try { await _browser.InvokeAsync<object?>("dispose", _host); await _browser.DisposeAsync(); } catch (JSDisconnectedException) { } }
    }
}
