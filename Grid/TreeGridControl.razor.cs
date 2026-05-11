using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit.Grid;

/// <summary>
/// TreeGridControl — hierarchical data grid with expand/collapse, parent/child mapping,
/// and row selection. Equivalent to SyncFusion's SfTreeGrid.
/// </summary>
public partial class TreeGridControl<TValue> : ComponentBase, ITreeGridControlOwner
{
    // ── Parameters ───────────────────────────────────────────────────────

    [Parameter] public IEnumerable<TValue>? DataSource { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>Property name of the unique ID field (e.g. "NodeID").</summary>
    [Parameter] public string IdMapping { get; set; } = "";

    /// <summary>Property name of the parent ID field (e.g. "ParentID").</summary>
    [Parameter] public string ParentIdMapping { get; set; } = "";

    /// <summary>Index (0-based) of the column that renders the tree expand/collapse icons.</summary>
    [Parameter] public int TreeColumnIndex { get; set; } = 0;

    /// <summary>Start with all nodes collapsed.</summary>
    [Parameter] public bool EnableCollapseAll { get; set; }

    [Parameter] public bool AllowSelection { get; set; } = true;
    [Parameter] public string? Height { get; set; }
    [Parameter] public string? Width { get; set; }
    [Parameter] public bool EnableHover { get; set; } = true;

    /// <summary>
    /// Extra CSS class added to the root .fx-treegrid element. Use
    /// "fx-treegrid-compact" for the dense VB6-style row layout.
    /// </summary>
    [Parameter] public string? CssClass { get; set; }

    /// <summary>
    /// Pixels of horizontal indent added per tree level (before the expand
    /// icon). Default 16 — matches VB6's tight tree. Older callers using
    /// 40px-per-level can override to 40 to keep the previous wider look.
    /// </summary>
    [Parameter] public int IndentPerLevel { get; set; } = 16;

    /// <summary>
    /// Built-in preset for the per-node expand/collapse icon. Use the
    /// override parameters below for full customisation.
    /// </summary>
    [Parameter] public GroupExpandIconStyle NodeExpandIconStyle { get; set; } = GroupExpandIconStyle.PlusMinus;

    // ── Caller-supplied glyph/icon overrides ─────────────────────────────
    // Resolution order (highest to lowest):
    //   1. ExpandIconTemplate / LeafIconTemplate — full RenderFragment override
    //   2. CollapsedGlyph / ExpandedGlyph / LeafGlyph + *Style strings
    //   3. NodeExpandIconStyle preset (toolkit default)

    /// <summary>Glyph rendered for a collapsed parent node. Null → preset default.</summary>
    [Parameter] public string? CollapsedGlyph { get; set; }

    /// <summary>Glyph rendered for an expanded parent node. Null → preset default.</summary>
    [Parameter] public string? ExpandedGlyph { get; set; }

    /// <summary>Optional glyph rendered for leaf nodes. Null → empty placeholder of icon-width.</summary>
    [Parameter] public string? LeafGlyph { get; set; }

    /// <summary>Inline CSS style for the parent expand/collapse icon span.</summary>
    [Parameter] public string? ExpandIconStyle { get; set; }

    /// <summary>Inline CSS style for the leaf-node icon / placeholder span.</summary>
    [Parameter] public string? LeafIconStyle { get; set; }

    /// <summary>
    /// RenderFragment that fully overrides the parent expand/collapse icon.
    /// Receives a bool indicating whether the node is currently expanded.
    /// The fragment is responsible for click handling.
    /// </summary>
    [Parameter] public RenderFragment<bool>? ExpandIconTemplate { get; set; }

    /// <summary>RenderFragment that fully overrides the leaf-node icon.</summary>
    [Parameter] public RenderFragment? LeafIconTemplate { get; set; }

    // Resolved values used by TreeGridControl.razor markup.
    internal string ResolveCollapsedGlyph() =>
        CollapsedGlyph ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus ? "+" : "▶");

    internal string ResolveExpandedGlyph() =>
        ExpandedGlyph ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus ? "−" : "▼");

    internal string? ResolveExpandIconStyle() =>
        ExpandIconStyle ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus
            ? HfGridIconStyles.PlusMinus
            : null);

    internal string? ResolveLeafIconStyle() =>
        LeafIconStyle ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus
            ? HfGridIconStyles.LeafSpacer
            : null);


    // Selection settings
    [Parameter] public SelectionMode SelectionMode { get; set; } = SelectionMode.Row;

    // Events
    [Parameter] public EventCallback<TreeRowSelectEventArgs<TValue>> RowSelected { get; set; }
    [Parameter] public EventCallback<TreeRowSelectEventArgs<TValue>> RowDeselected { get; set; }
    [Parameter] public EventCallback<TreeNodeEventArgs<TValue>> Expanded { get; set; }
    [Parameter] public EventCallback<TreeNodeEventArgs<TValue>> Collapsed { get; set; }

    // ── Internal State ──────────────────────────────────────────────────

    private List<TreeNode<TValue>> _flatNodes = new();
    private TValue? _selectedItem;
    private int _selectedIndex = -1;
    internal List<TreeGridColumn> _columns = new();
    private IEnumerable<TValue>? _previousDataSource;
    private bool _treeBuilt;

    // ── Column Registration ─────────────────────────────────────────────

    public void AddColumn(TreeGridColumn column)
    {
        if (!_columns.Contains(column))
            _columns.Add(column);
    }

    internal List<TreeGridColumn> VisibleColumns => _columns.Where(c => c.Visible).ToList();

    /// <summary>
    /// Resolves TreeColumnIndex to the correct visible column index.
    /// If TreeColumnIndex refers to a hidden column, finds the matching visible index.
    /// </summary>
    internal int ResolvedTreeColumnIndex
    {
        get
        {
            if (TreeColumnIndex < _columns.Count)
            {
                // TreeColumnIndex refers to the overall column list — find its position in visible columns
                var targetCol = _columns[TreeColumnIndex];
                if (targetCol.Visible)
                {
                    var visibleCols = VisibleColumns;
                    for (int i = 0; i < visibleCols.Count; i++)
                    {
                        if (ReferenceEquals(visibleCols[i], targetCol))
                            return i;
                    }
                }
            }
            // Fallback: treat TreeColumnIndex as visible column index directly
            return TreeColumnIndex;
        }
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    protected override void OnParametersSet()
    {
        // Only rebuild the tree when DataSource actually changes, to preserve
        // expand/collapse state across re-renders triggered by StateHasChanged.
        if (!_treeBuilt || !ReferenceEquals(DataSource, _previousDataSource))
        {
            // Preserve existing expand/collapse states before rebuilding
            Dictionary<object, bool>? expandStates = null;
            if (_treeBuilt && _flatNodes.Count > 0)
            {
                expandStates = new Dictionary<object, bool>();
                foreach (var node in _flatNodes)
                {
                    if (node.Id != null)
                        expandStates[node.Id] = node.IsExpanded;
                }
            }

            BuildTree();

            // Restore expand/collapse states from before the rebuild
            if (expandStates != null)
            {
                foreach (var node in _flatNodes)
                {
                    if (node.Id != null && expandStates.TryGetValue(node.Id, out var wasExpanded))
                        node.IsExpanded = wasExpanded;
                }
            }

            _previousDataSource = DataSource;
            _treeBuilt = true;
        }
    }

    // ── Tree Building ────────────────────────────────────────────────────

    private void BuildTree()
    {
        _flatNodes.Clear();
        if (DataSource == null || string.IsNullOrEmpty(IdMapping) || string.IsNullOrEmpty(ParentIdMapping))
            return;

        var items = DataSource.ToList();
        var idProp = typeof(TValue).GetProperty(IdMapping);
        var parentIdProp = typeof(TValue).GetProperty(ParentIdMapping);
        if (idProp == null || parentIdProp == null) return;

        // Build lookup: parentId -> children
        var childrenMap = new Dictionary<object, List<(TValue Item, object Id)>>();
        var allItems = new List<(TValue Item, object? Id, object? ParentId)>();

        foreach (var item in items)
        {
            var id = idProp.GetValue(item);
            var parentId = parentIdProp.GetValue(item);
            allItems.Add((item, id, parentId));

            var parentKey = parentId ?? "__root__";
            if (!childrenMap.ContainsKey(parentKey))
                childrenMap[parentKey] = new();
            childrenMap[parentKey].Add((item, id!));
        }

        // Find root nodes (parentId is null or not found in any id)
        var allIds = new HashSet<object>(allItems.Where(a => a.Id != null).Select(a => a.Id!));
        var roots = allItems.Where(a => a.ParentId == null || !allIds.Contains(a.ParentId)).ToList();

        void AddNodes(object? parentKey, int level)
        {
            var key = parentKey ?? "__root__";
            if (!childrenMap.TryGetValue(key, out var children)) return;

            foreach (var (item, id) in children)
            {
                var hasChildren = childrenMap.ContainsKey(id) && childrenMap[id].Count > 0;
                _flatNodes.Add(new TreeNode<TValue>
                {
                    Data = item,
                    Level = level,
                    HasChildren = hasChildren,
                    IsExpanded = !EnableCollapseAll,
                    Id = id
                });

                if (hasChildren)
                    AddNodes(id, level + 1);
            }
        }

        // Process roots
        foreach (var root in roots)
        {
            var hasChildren = root.Id != null && childrenMap.ContainsKey(root.Id) && childrenMap[root.Id].Count > 0;
            _flatNodes.Add(new TreeNode<TValue>
            {
                Data = root.Item,
                Level = 0,
                HasChildren = hasChildren,
                IsExpanded = !EnableCollapseAll,
                Id = root.Id
            });

            if (hasChildren && root.Id != null)
                AddNodes(root.Id, 1);
        }
    }

    // ── Visible Nodes (respecting expand/collapse) ──────────────────────

    private IEnumerable<TreeNode<TValue>> VisibleNodes
    {
        get
        {
            var result = new List<TreeNode<TValue>>();
            var collapsedLevels = new Stack<int>();

            foreach (var node in _flatNodes)
            {
                // Check if this node is hidden by a collapsed ancestor
                while (collapsedLevels.Count > 0 && collapsedLevels.Peek() >= node.Level)
                    collapsedLevels.Pop();

                if (collapsedLevels.Count > 0)
                    continue;

                result.Add(node);

                if (node.HasChildren && !node.IsExpanded)
                    collapsedLevels.Push(node.Level);
            }

            return result;
        }
    }

    // ── Event Handlers ──────────────────────────────────────────────────

    private async Task ToggleNode(TreeNode<TValue> node)
    {
        node.IsExpanded = !node.IsExpanded;

        if (node.IsExpanded && Expanded.HasDelegate)
            await Expanded.InvokeAsync(new TreeNodeEventArgs<TValue> { Data = node.Data, Level = node.Level });
        else if (!node.IsExpanded && Collapsed.HasDelegate)
            await Collapsed.InvokeAsync(new TreeNodeEventArgs<TValue> { Data = node.Data, Level = node.Level });

        StateHasChanged();
    }

    private async Task HandleRowClick(TreeNode<TValue> node, int visibleIndex)
    {
        // Toggle expand/collapse when clicking anywhere on a parent node row
        if (node.HasChildren)
            await ToggleNode(node);

        if (!AllowSelection) return;

        var prevSelected = _selectedItem;
        _selectedItem = node.Data;
        _selectedIndex = visibleIndex;

        if (prevSelected != null && RowDeselected.HasDelegate)
            await RowDeselected.InvokeAsync(new TreeRowSelectEventArgs<TValue> { Data = prevSelected });

        if (RowSelected.HasDelegate)
            await RowSelected.InvokeAsync(new TreeRowSelectEventArgs<TValue> { Data = node.Data, RowIndex = visibleIndex });
    }

    // ── Public API ──────────────────────────────────────────────────────

    public async Task ExpandAllAsync()
    {
        foreach (var node in _flatNodes)
            if (node.HasChildren)
                node.IsExpanded = true;
        await InvokeAsync(StateHasChanged);
    }

    public async Task CollapseAllAsync()
    {
        foreach (var node in _flatNodes)
            if (node.HasChildren)
                node.IsExpanded = false;
        await InvokeAsync(StateHasChanged);
    }

    public async Task ExpandAtLevelAsync(int level)
    {
        foreach (var node in _flatNodes)
            if (node.HasChildren && node.Level <= level)
                node.IsExpanded = true;
        await InvokeAsync(StateHasChanged);
    }

    public async Task CollapseAtLevelAsync(int level)
    {
        foreach (var node in _flatNodes)
            if (node.HasChildren && node.Level >= level)
                node.IsExpanded = false;
        await InvokeAsync(StateHasChanged);
    }

    public TValue? GetSelectedRecord() => _selectedItem;

    // ── Helper ──────────────────────────────────────────────────────────

    private object? GetPropertyValue(TValue? item, string propertyName)
    {
        if (item == null || string.IsNullOrEmpty(propertyName)) return null;
        var prop = typeof(TValue).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        return prop?.GetValue(item);
    }

    private string GetCellDisplayValue(TValue item, TreeGridColumn col)
    {
        var val = GetPropertyValue(item, col.Field);
        if (val == null) return "";

        if (!string.IsNullOrEmpty(col.Format) && val is IFormattable formattable)
            return formattable.ToString(col.Format, System.Globalization.CultureInfo.CurrentCulture);

        return val.ToString() ?? "";
    }
}

// ── Supporting Types ────────────────────────────────────────────────────

public class TreeNode<TValue>
{
    public TValue Data { get; set; } = default!;
    public int Level { get; set; }
    public bool HasChildren { get; set; }
    public bool IsExpanded { get; set; } = true;
    public object? Id { get; set; }
}

public class TreeRowSelectEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
}

public class TreeNodeEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int Level { get; set; }
}
