using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit.Grid;

public partial class TreeGridControl<TValue> : ComponentBase, ITreeGridControlOwner
{

    [Parameter] public IEnumerable<TValue>? DataSource { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    [Parameter] public string IdMapping { get; set; } = "";

    [Parameter] public string ParentIdMapping { get; set; } = "";

    [Parameter] public int TreeColumnIndex { get; set; } = 0;

    [Parameter] public bool EnableCollapseAll { get; set; }

    [Parameter] public bool AllowSelection { get; set; } = true;
    [Parameter] public string? Height { get; set; }
    [Parameter] public string? Width { get; set; }
    [Parameter] public bool EnableHover { get; set; } = true;
    [Parameter] public bool ToggleOnRowClick { get; set; } = true;
    [Parameter] public int TabIndex { get; set; } = 0;
    [Parameter] public bool AllowSorting { get; set; }
    [Parameter] public bool AllowFiltering { get; set; }
    [Parameter] public bool AllowResizing { get; set; }
    [Parameter] public bool ShowColumnOptionsButton { get; set; }
    [Parameter] public bool ShowGridOptionsRail { get; set; }
    [Parameter] public bool ShowColumnHeaders { get; set; } = true;
    [Parameter] public bool ShowHeaderFilterIcon { get; set; } = true;
    [Parameter] public List<string>? Toolbar { get; set; }
    [Parameter] public IReadOnlyList<GridToolbarItem>? ToolbarItems { get; set; }
    [Parameter] public bool? ShowGridToolbar { get; set; }

    [Parameter] public string? CssClass { get; set; }

    [Parameter] public int IndentPerLevel { get; set; } = 16;

    [Parameter] public GroupExpandIconStyle NodeExpandIconStyle { get; set; } = GroupExpandIconStyle.PlusMinus;

    [Parameter] public string? CollapsedGlyph { get; set; }

    [Parameter] public string? ExpandedGlyph { get; set; }

    [Parameter] public string? LeafGlyph { get; set; }

    [Parameter] public string? ExpandIconStyle { get; set; }

    [Parameter] public string? LeafIconStyle { get; set; }

    [Parameter] public RenderFragment<bool>? ExpandIconTemplate { get; set; }

    [Parameter] public RenderFragment? LeafIconTemplate { get; set; }

    [Parameter] public Func<TValue, bool, string?>? GetNodeIcon { get; set; }

    [Parameter] public bool ChangeNodeIconOnExpand { get; set; }

    [Parameter] public bool ShowLeafNodeIcons { get; set; } = true;

    internal string ResolveCollapsedGlyph() =>
        CollapsedGlyph ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus ? "+" : "▶");

    internal string ResolveExpandedGlyph() =>
        ExpandedGlyph ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus ? "\u2212" : "▼");

    internal string? ResolveExpandIconStyle() =>
        ExpandIconStyle ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus
            ? FxGridIconStyles.PlusMinus
            : null);

    internal string? ResolveLeafIconStyle() =>
        LeafIconStyle ?? (NodeExpandIconStyle == GroupExpandIconStyle.PlusMinus
            ? FxGridIconStyles.LeafSpacer
            : null);

    internal string GetHeaderIconButtonCss(TreeGridColumn column) =>
        string.IsNullOrWhiteSpace(column.HeaderIconCssClass)
            ? "fx-treegrid-header-icon-button"
            : $"fx-treegrid-header-icon-button {column.HeaderIconCssClass.Trim()}";

    internal string? ResolveHeaderIconSrc(TreeGridColumn column)
    {
        if (!string.IsNullOrWhiteSpace(column.HeaderIconSrc))
            return column.HeaderIconSrc;

        return column.HeaderIconKind switch
        {
            TreeGridHeaderIconKind.ExpandAll => $"{StaticAssetRoot}/images/16/expand_all.svg",
            TreeGridHeaderIconKind.CollapseAll => $"{StaticAssetRoot}/images/16/collapse_all.svg",
            _ => null
        };
    }

    internal async Task HandleHeaderIconClickAsync(TreeGridColumn column)
    {
        if (column.HeaderIconClicked.HasDelegate)
            await column.HeaderIconClicked.InvokeAsync();
    }

    private string StaticAssetRoot =>
        _staticAssetRoot ??= $"_content/{GetType().Assembly.GetName().Name}";

    internal string YellowFolderIconSrc => $"{StaticAssetRoot}/images/16/folder-open.ico";
    internal string TreeOpenFolderIconSrc => $"{StaticAssetRoot}/images/16/folder-open.ico";
    internal string TreeClosedFolderIconSrc => $"{StaticAssetRoot}/images/32/folder.ico";

    internal string? ResolveNodeIcon(TreeNode<TValue> node) =>
        GetNodeIcon?.Invoke(node.Data, ChangeNodeIconOnExpand && node.IsExpanded);

    internal string ResolveTreeFolderIconSrc(TreeNode<TValue> node) =>
        ChangeNodeIconOnExpand && node.IsExpanded ? TreeOpenFolderIconSrc : TreeClosedFolderIconSrc;

    internal string ResolveFolderIconState(TreeNode<TValue> node) =>
        ChangeNodeIconOnExpand && node.IsExpanded ? "open" : "closed";

    internal bool UseYellowFolderIcons =>
        CssClass?.Contains("fx-treegrid-yellow-folder", StringComparison.OrdinalIgnoreCase) == true;

    internal bool UseTreeFolderIcons =>
        UseYellowFolderIcons ||
        CssClass?.Contains("fx-treegrid-blue-leaf-folder", StringComparison.OrdinalIgnoreCase) == true;

    internal bool UseDottedTreeLines =>
        CssClass?.Contains("fx-treegrid-dotted-lines", StringComparison.OrdinalIgnoreCase) == true;

    internal bool IsCompact =>
        CssClass?.Contains("fx-treegrid-compact", StringComparison.OrdinalIgnoreCase) == true;

    [Parameter] public SelectionMode SelectionMode { get; set; } = SelectionMode.Row;

    [Parameter] public EventCallback<TreeRowSelectEventArgs<TValue>> RowSelected { get; set; }
    [Parameter] public EventCallback<TreeRowSelectEventArgs<TValue>> RowDeselected { get; set; }
    [Parameter] public EventCallback<TreeRowSelectEventArgs<TValue>> RowDoubleClicked { get; set; }
    [Parameter] public EventCallback<TreeRowSelectEventArgs<TValue>> RowActivated { get; set; }
    [Parameter] public EventCallback<TreeNodeEventArgs<TValue>> Expanded { get; set; }
    [Parameter] public EventCallback<TreeNodeEventArgs<TValue>> Collapsed { get; set; }
    [Parameter] public EventCallback<string> OnToolbarItemClick { get; set; }
    [Parameter] public EventCallback<GridToolbarClickEventArgs> ToolbarItemClicked { get; set; }

    private List<TreeNode<TValue>> _flatNodes = new();
    private TValue? _selectedItem;
    private int _selectedIndex = -1;
    internal List<TreeGridColumn> _columns = new();
    private IEnumerable<TValue>? _previousDataSource;
    private bool _treeBuilt;
    private int _lastRenderedColumnCount;
    private string? _staticAssetRoot;
    private readonly Dictionary<string, ColumnState> _columnStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _visibilityOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _columnWidthOverrides = new(StringComparer.OrdinalIgnoreCase);
    private string? _filterPopupField;
    private string _filterDraft = "";
    private bool _treeColumnPanelOpen;
    private string _columnPanelSearch = "";
    private bool _isColumnResizing;
    private TreeGridColumn? _resizingColumn;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private string? _openToolbarMenuKey;

    public void AddColumn(TreeGridColumn column)
    {
        if (!_columns.Contains(column))
            _columns.Add(column);
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (_columns.Count != _lastRenderedColumnCount)
        {
            _lastRenderedColumnCount = _columns.Count;
            StateHasChanged();
        }
    }

    internal List<TreeGridColumn> VisibleColumns => _columns.Where(IsColumnVisible).ToList();

    internal string TreeGridCssClass
    {
        get
        {
            var parts = new List<string> { "fx-treegrid" };
            if (!string.IsNullOrWhiteSpace(CssClass))
                parts.Add(CssClass.Trim());
            if (ShowGridOptionsRail && ShowColumnOptionsButton)
                parts.Add("fx-treegrid-options-on");
            if (ShouldRenderToolbar)
                parts.Add("fx-treegrid-toolbar-on");
            if (_isColumnResizing)
                parts.Add("fx-treegrid-resizing");
            return string.Join(" ", parts);
        }
    }

    internal bool ShouldRenderToolbar =>
        ShowGridToolbar ?? (ResolvedToolbarItems.Any());

    internal IEnumerable<GridToolbarItem> ResolvedToolbarItems =>
        ResolveToolbarItems(ToolbarItems, Toolbar);

    internal bool ShouldRenderColumnOptions =>
        ShowColumnOptionsButton && _columns.Any(c => !string.IsNullOrWhiteSpace(c.Field));

    internal IEnumerable<TreeGridColumn> ColumnPanelColumns
    {
        get
        {
            var query = _columns.Where(c => !string.IsNullOrWhiteSpace(c.Field));
            if (!string.IsNullOrWhiteSpace(_columnPanelSearch))
            {
                query = query.Where(c =>
                    c.DisplayHeader.Contains(_columnPanelSearch, StringComparison.OrdinalIgnoreCase) ||
                    c.Field.Contains(_columnPanelSearch, StringComparison.OrdinalIgnoreCase));
            }
            return query;
        }
    }

    private bool IsColumnVisible(TreeGridColumn column)
    {
        if (string.IsNullOrWhiteSpace(column.Field))
            return column.Visible;

        return _visibilityOverrides.TryGetValue(column.Field, out var visible)
            ? visible
            : column.Visible;
    }

    private bool CanHideColumn(TreeGridColumn column) =>
        !IsColumnVisible(column) || VisibleColumns.Count > 1;

    private string GetColumnKey(TreeGridColumn column) =>
        !string.IsNullOrWhiteSpace(column.Field)
            ? column.Field
            : column.DisplayHeader;

    private ColumnState GetColumnState(TreeGridColumn column) =>
        GetColumnState(GetColumnKey(column));

    private IEnumerable<GridToolbarItem> ResolveToolbarItems(
        IReadOnlyList<GridToolbarItem>? richItems,
        IReadOnlyList<string>? textItems)
    {
        if (richItems != null)
        {
            foreach (var item in richItems.Where(i => i.Visible))
                yield return item;
        }

        if (textItems != null)
        {
            foreach (var item in textItems.Where(i => !string.IsNullOrWhiteSpace(i)))
                yield return new GridToolbarItem { Key = item, Text = item };
        }
    }

    internal GridToolbarItem ResolveToolbarItem(GridToolbarItem item)
    {
        var resolved = new GridToolbarItem
        {
            Key = item.Key,
            Text = item.Text,
            Title = item.Title,
            IconSrc = item.IconSrc,
            IconAlt = item.IconAlt,
            Glyph = item.Glyph,
            Action = item.Action,
            Disabled = item.Disabled,
            Visible = item.Visible,
            SeparatorBefore = item.SeparatorBefore,
            SeparatorAfter = item.SeparatorAfter,
            Items = item.Items
        };

        if (string.IsNullOrWhiteSpace(resolved.Key))
            resolved.Key = !string.IsNullOrWhiteSpace(resolved.Text) ? resolved.Text : resolved.Action.ToString();

        if (resolved.Action == GridToolbarAction.Custom)
            resolved.Action = ResolveToolbarAction(resolved.Key);

        ApplyToolbarDefaults(resolved);
        return resolved;
    }

    private GridToolbarAction ResolveToolbarAction(string key)
    {
        var normalized = NormalizeToolbarKey(key);
        return normalized switch
        {
            "expandall" or "expand" => GridToolbarAction.ExpandAll,
            "collapseall" or "collapse" => GridToolbarAction.CollapseAll,
            "toggleexpandcollapse" or "expandcollapse" or "toggleall" => GridToolbarAction.ToggleExpandCollapse,
            "refresh" or "reload" => GridToolbarAction.Refresh,
            "columns" or "columnchooser" or "columnoptions" => GridToolbarAction.Columns,
            "clearfilters" or "clearfilter" => GridToolbarAction.ClearFilters,
            _ => GridToolbarAction.Custom
        };
    }

    private void ApplyToolbarDefaults(GridToolbarItem item)
    {
        switch (item.Action)
        {
            case GridToolbarAction.ExpandAll:
                item.Title ??= "Expand All";
                item.IconSrc ??= $"{StaticAssetRoot}/images/16/expand_all.svg";
                item.IconAlt ??= "";
                break;
            case GridToolbarAction.CollapseAll:
                item.Title ??= "Collapse All";
                item.IconSrc ??= $"{StaticAssetRoot}/images/16/collapse_all.svg";
                item.IconAlt ??= "";
                break;
            case GridToolbarAction.ToggleExpandCollapse:
                item.Title ??= AreAllExpandableNodesExpanded ? "Collapse All" : "Expand All";
                item.IconSrc ??= $"{StaticAssetRoot}/images/16/{(AreAllExpandableNodesExpanded ? "collapse_all" : "expand_all")}.svg";
                item.IconAlt ??= "";
                break;
            case GridToolbarAction.Refresh:
                item.Title ??= "Refresh";
                item.Glyph ??= "↻";
                break;
            case GridToolbarAction.Columns:
                item.Title ??= "Columns";
                item.Glyph ??= "▦";
                break;
            case GridToolbarAction.ClearFilters:
                item.Title ??= "Clear Filters";
                item.Glyph ??= "⌧";
                break;
        }
    }

    internal bool HasHeaderToolbar(TreeGridColumn column) =>
        column.HeaderToolbarItems?.Any(i => i.Visible) == true;

    internal IEnumerable<GridToolbarItem> GetHeaderToolbarItems(TreeGridColumn column) =>
        ResolveToolbarItems(column.HeaderToolbarItems, null);

    internal bool HasToolbarMenu(GridToolbarItem item) =>
        item.Items?.Any(i => i.Visible) == true;

    internal IEnumerable<GridToolbarItem> GetVisibleToolbarChildren(GridToolbarItem item) =>
        item.Items?.Where(i => i.Visible) ?? Enumerable.Empty<GridToolbarItem>();

    internal bool IsToolbarMenuOpen(GridToolbarItem item, bool isHeaderToolbar, TreeGridColumn? column) =>
        string.Equals(_openToolbarMenuKey, GetToolbarMenuKey(item, isHeaderToolbar, column), StringComparison.Ordinal);

    internal string GetToolbarButtonCss(GridToolbarItem item) =>
        !string.IsNullOrWhiteSpace(item.Text) ? "fx-treegrid-toolbar-button has-text" : "fx-treegrid-toolbar-button";

    internal string GetHeaderToolbarButtonCss(GridToolbarItem item) =>
        !string.IsNullOrWhiteSpace(item.Text) ? "fx-treegrid-header-toolbar-button has-text" : "fx-treegrid-header-toolbar-button";

    internal string ResolveToolbarTitle(GridToolbarItem item) =>
        item.Title ?? item.Text ?? item.Key;

    internal string ResolveToolbarAriaLabel(GridToolbarItem item) =>
        ResolveToolbarTitle(item);

    internal string? ResolveToolbarIconSrc(GridToolbarItem item) =>
        item.IconSrc;

    internal string ResolveToolbarIconAlt(GridToolbarItem item) =>
        item.IconAlt ?? "";

    internal string? ResolveToolbarGlyph(GridToolbarItem item) =>
        item.Glyph;

    internal string ResolveToolbarMenuText(GridToolbarItem item) =>
        !string.IsNullOrWhiteSpace(item.Text) ? item.Text : ResolveToolbarTitle(item);

    internal async Task HandleToolbarMenuItemClickAsync(GridToolbarItem item, bool isHeaderToolbar, TreeGridColumn? column)
    {
        _openToolbarMenuKey = null;
        await HandleToolbarItemClickAsync(item, isHeaderToolbar, column, menuChild: true);
    }

    internal async Task HandleToolbarItemClickAsync(
        GridToolbarItem item,
        bool isHeaderToolbar,
        TreeGridColumn? column,
        bool menuChild = false)
    {
        if (item.Disabled)
            return;

        if (!menuChild && HasToolbarMenu(item))
        {
            var key = GetToolbarMenuKey(item, isHeaderToolbar, column);
            _openToolbarMenuKey = string.Equals(_openToolbarMenuKey, key, StringComparison.Ordinal) ? null : key;
            await InvokeAsync(StateHasChanged);
            return;
        }

        var args = new GridToolbarClickEventArgs
        {
            Key = item.Key,
            Action = item.Action,
            Item = item,
            IsHeaderToolbar = isHeaderToolbar,
            ColumnField = column?.Field,
            ColumnHeader = column?.DisplayHeader
        };

        if (isHeaderToolbar && column?.HeaderToolbarItemClicked.HasDelegate == true)
            await column.HeaderToolbarItemClicked.InvokeAsync(args);
        else if (!isHeaderToolbar && ToolbarItemClicked.HasDelegate)
            await ToolbarItemClicked.InvokeAsync(args);

        if (args.Cancel)
            return;

        await RunToolbarActionAsync(item.Action);

        if (!isHeaderToolbar && OnToolbarItemClick.HasDelegate)
            await OnToolbarItemClick.InvokeAsync(item.Key);
    }

    private string GetToolbarMenuKey(GridToolbarItem item, bool isHeaderToolbar, TreeGridColumn? column) =>
        $"{(isHeaderToolbar ? "h" : "t")}:{column?.Field ?? column?.DisplayHeader ?? ""}:{NormalizeToolbarKey(item.Key)}";

    private static string NormalizeToolbarKey(string? key) =>
        new((key ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private async Task RunToolbarActionAsync(GridToolbarAction action)
    {
        switch (action)
        {
            case GridToolbarAction.ExpandAll:
                await ExpandAllAsync();
                break;
            case GridToolbarAction.CollapseAll:
                await CollapseAllAsync();
                break;
            case GridToolbarAction.ToggleExpandCollapse:
                if (AreAllExpandableNodesExpanded)
                    await CollapseAllAsync();
                else
                    await ExpandAllAsync();
                break;
            case GridToolbarAction.Columns:
                if (ShouldRenderColumnOptions)
                {
                    _treeColumnPanelOpen = !_treeColumnPanelOpen;
                    await InvokeAsync(StateHasChanged);
                }
                break;
            case GridToolbarAction.ClearFilters:
                await ClearFiltersAsync();
                break;
        }
    }

    private ColumnState GetColumnState(string field)
    {
        if (string.IsNullOrWhiteSpace(field))
            field = "__treegrid_column__";

        if (!_columnStates.TryGetValue(field, out var state))
        {
            state = new ColumnState { Field = field };
            _columnStates[field] = state;
        }

        return state;
    }

    internal int ResolvedTreeColumnIndex
    {
        get
        {
            if (TreeColumnIndex < _columns.Count)
            {
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
            return TreeColumnIndex;
        }
    }

    protected override void OnParametersSet()
    {
        if (!_treeBuilt || !ReferenceEquals(DataSource, _previousDataSource))
        {
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

    private void BuildTree()
    {
        _flatNodes.Clear();
        if (DataSource == null || string.IsNullOrEmpty(IdMapping) || string.IsNullOrEmpty(ParentIdMapping))
            return;

        var items = DataSource.ToList();
        var idProp = typeof(TValue).GetProperty(IdMapping);
        var parentIdProp = typeof(TValue).GetProperty(ParentIdMapping);
        if (idProp == null || parentIdProp == null) return;

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

        var allIds = new HashSet<object>(allItems.Where(a => a.Id != null).Select(a => a.Id!));
        var roots = allItems.Where(a => a.ParentId == null || !allIds.Contains(a.ParentId)).ToList();
        SortRows(roots, row => row.Item);

        void AddNodes(object? parentKey, int level, IReadOnlyList<bool> ancestorLineContinuations)
        {
            var key = parentKey ?? "__root__";
            if (!childrenMap.TryGetValue(key, out var children)) return;
            SortRows(children, row => row.Item);

            for (var index = 0; index < children.Count; index++)
            {
                var (item, id) = children[index];
                var hasChildren = childrenMap.ContainsKey(id) && childrenMap[id].Count > 0;
                var isLastSibling = index == children.Count - 1;
                _flatNodes.Add(new TreeNode<TValue>
                {
                    Data = item,
                    Level = level,
                    HasChildren = hasChildren,
                    IsExpanded = !EnableCollapseAll,
                    Id = id,
                    ParentId = parentKey,
                    IsLastSibling = isLastSibling,
                    AncestorLineContinuations = ancestorLineContinuations.ToArray()
                });

                if (hasChildren)
                {
                    var childAncestorLines = ancestorLineContinuations.Concat(new[] { !isLastSibling }).ToArray();
                    AddNodes(id, level + 1, childAncestorLines);
                }
            }
        }

        for (var index = 0; index < roots.Count; index++)
        {
            var root = roots[index];
            var hasChildren = root.Id != null && childrenMap.ContainsKey(root.Id) && childrenMap[root.Id].Count > 0;
            var isLastSibling = index == roots.Count - 1;
            _flatNodes.Add(new TreeNode<TValue>
            {
                Data = root.Item,
                Level = 0,
                HasChildren = hasChildren,
                IsExpanded = !EnableCollapseAll,
                Id = root.Id,
                ParentId = root.ParentId,
                IsLastSibling = isLastSibling,
                AncestorLineContinuations = Array.Empty<bool>()
            });

            if (hasChildren && root.Id != null)
                AddNodes(root.Id, 1, new[] { !isLastSibling });
        }
    }

    private IEnumerable<TreeNode<TValue>> VisibleNodes
    {
        get
        {
            var result = new List<TreeNode<TValue>>();
            var collapsedLevels = new Stack<int>();

            foreach (var node in _flatNodes)
            {
                while (collapsedLevels.Count > 0 && collapsedLevels.Peek() >= node.Level)
                    collapsedLevels.Pop();

                if (collapsedLevels.Count > 0)
                    continue;

                result.Add(node);

                if (node.HasChildren && !node.IsExpanded)
                    collapsedLevels.Push(node.Level);
            }

            if (!HasActiveFilters)
                return result;

            var included = BuildFilterInclusionSet();
            return result.Where(included.Contains).ToList();
        }
    }

    private async Task ToggleNode(TreeNode<TValue> node)
    {
        await SetNodeExpandedAsync(node, !node.IsExpanded);
    }

    private async Task SetNodeExpandedAsync(TreeNode<TValue> node, bool expanded)
    {
        if (!node.HasChildren || node.IsExpanded == expanded)
            return;

        node.IsExpanded = expanded;

        if (node.IsExpanded && Expanded.HasDelegate)
            await Expanded.InvokeAsync(new TreeNodeEventArgs<TValue> { Data = node.Data, Level = node.Level });
        else if (!node.IsExpanded && Collapsed.HasDelegate)
            await Collapsed.InvokeAsync(new TreeNodeEventArgs<TValue> { Data = node.Data, Level = node.Level });

        StateHasChanged();
    }

    private async Task HandleRowClick(TreeNode<TValue> node, int visibleIndex)
    {
        if (ToggleOnRowClick && node.HasChildren)
            await ToggleNode(node);

        await SelectNodeAsync(node, visibleIndex);
    }

    private async Task HandleRowDoubleClick(TreeNode<TValue> node, int visibleIndex)
    {
        await SelectNodeAsync(node, visibleIndex);

        if (RowDoubleClicked.HasDelegate)
            await RowDoubleClicked.InvokeAsync(CreateRowEventArgs(node, visibleIndex));
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        switch (e.Key)
        {
            case "ArrowDown":
            case "Down":
                await MoveSelectionAsync(1);
                break;
            case "ArrowUp":
            case "Up":
                await MoveSelectionAsync(-1);
                break;
            case "Home":
                await SelectVisibleNodeAsync(0);
                break;
            case "End":
                await SelectVisibleNodeAsync(VisibleNodes.ToList().Count - 1);
                break;
            case "Enter":
                await ActivateSelectedNodeAsync();
                break;
            case "ArrowRight":
            case "Right":
                await ExpandOrMoveToChildAsync();
                break;
            case "ArrowLeft":
            case "Left":
                await CollapseOrMoveToParentAsync();
                break;
        }
    }

    private async Task MoveSelectionAsync(int delta)
    {
        var visible = VisibleNodes.ToList();
        if (visible.Count == 0)
            return;

        var selectedIndex = GetSelectedVisibleIndex(visible);
        var targetIndex = selectedIndex < 0
            ? (delta >= 0 ? 0 : visible.Count - 1)
            : Math.Clamp(selectedIndex + delta, 0, visible.Count - 1);

        await SelectVisibleNodeAsync(targetIndex, visible);
    }

    private async Task SelectVisibleNodeAsync(int index, List<TreeNode<TValue>>? visible = null)
    {
        visible ??= VisibleNodes.ToList();
        if (visible.Count == 0 || index < 0 || index >= visible.Count)
            return;

        await SelectNodeAsync(visible[index], index);
    }

    private async Task ActivateSelectedNodeAsync()
    {
        var visible = VisibleNodes.ToList();
        if (visible.Count == 0)
            return;

        var selectedIndex = GetSelectedVisibleIndex(visible);
        if (selectedIndex < 0)
        {
            await SelectVisibleNodeAsync(0, visible);
            return;
        }

        var node = visible[selectedIndex];
        if (RowActivated.HasDelegate)
            await RowActivated.InvokeAsync(CreateRowEventArgs(node, selectedIndex));
    }

    private async Task ExpandOrMoveToChildAsync()
    {
        var visible = VisibleNodes.ToList();
        var selectedIndex = GetSelectedVisibleIndex(visible);
        if (selectedIndex < 0)
        {
            await SelectVisibleNodeAsync(0, visible);
            return;
        }

        var node = visible[selectedIndex];
        if (!node.HasChildren)
            return;

        if (!node.IsExpanded)
        {
            await SetNodeExpandedAsync(node, true);
            return;
        }

        var nextIndex = selectedIndex + 1;
        if (nextIndex < visible.Count && visible[nextIndex].Level == node.Level + 1)
            await SelectVisibleNodeAsync(nextIndex, visible);
    }

    private async Task CollapseOrMoveToParentAsync()
    {
        var visible = VisibleNodes.ToList();
        var selectedIndex = GetSelectedVisibleIndex(visible);
        if (selectedIndex < 0)
        {
            await SelectVisibleNodeAsync(0, visible);
            return;
        }

        var node = visible[selectedIndex];
        if (node.HasChildren && node.IsExpanded)
        {
            await SetNodeExpandedAsync(node, false);
            return;
        }

        for (var i = selectedIndex - 1; i >= 0; i--)
        {
            if (visible[i].Level < node.Level)
            {
                await SelectVisibleNodeAsync(i, visible);
                return;
            }
        }
    }

    private int GetSelectedVisibleIndex(List<TreeNode<TValue>> visible)
    {
        if (_selectedItem == null)
            return -1;

        for (var i = 0; i < visible.Count; i++)
        {
            if (EqualityComparer<TValue>.Default.Equals(visible[i].Data, _selectedItem))
                return i;
        }

        return -1;
    }

    private async Task SelectNodeAsync(TreeNode<TValue> node, int visibleIndex)
    {
        if (!AllowSelection) return;

        var prevSelected = _selectedItem;
        _selectedItem = node.Data;
        _selectedIndex = visibleIndex;

        if (prevSelected != null && RowDeselected.HasDelegate)
            await RowDeselected.InvokeAsync(new TreeRowSelectEventArgs<TValue> { Data = prevSelected });

        if (RowSelected.HasDelegate)
            await RowSelected.InvokeAsync(CreateRowEventArgs(node, visibleIndex));
    }

    private static TreeRowSelectEventArgs<TValue> CreateRowEventArgs(TreeNode<TValue> node, int visibleIndex) =>
        new()
        {
            Data = node.Data,
            RowIndex = visibleIndex,
            Level = node.Level,
            HasChildren = node.HasChildren,
            IsExpanded = node.IsExpanded
        };

    public async Task SetExpandedAsync(TValue? item, bool expanded)
    {
        if (item == null)
            return;

        var node = _flatNodes.FirstOrDefault(n => EqualityComparer<TValue>.Default.Equals(n.Data, item));
        if (node != null)
            await SetNodeExpandedAsync(node, expanded);
    }

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

    public bool HasExpandableNodes => _flatNodes.Any(node => node.HasChildren);

    public bool AreAllExpandableNodesExpanded =>
        HasExpandableNodes && _flatNodes.Where(node => node.HasChildren).All(node => node.IsExpanded);

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

    internal async Task HandleHeaderClickAsync(TreeGridColumn column)
    {
        if (!AllowSorting || !column.AllowSorting || string.IsNullOrWhiteSpace(column.Field))
            return;

        var state = GetColumnState(column);
        var nextDirection = state.SortDirection switch
        {
            null => SortDirection.Ascending,
            SortDirection.Ascending => SortDirection.Descending,
            _ => (SortDirection?)null
        };

        foreach (var columnState in _columnStates.Values)
            columnState.SortDirection = null;

        state.SortDirection = nextDirection;
        RebuildPreservingExpansion();
        await InvokeAsync(StateHasChanged);
    }

    internal string GetHeaderCellCss(TreeGridColumn column)
    {
        var state = GetColumnState(column);
        var parts = new List<string> { "fx-treegrid-header-cell" };
        if (AllowSorting && column.AllowSorting && !string.IsNullOrWhiteSpace(column.Field))
            parts.Add("fx-treegrid-header-sortable");
        if (state.SortDirection.HasValue)
            parts.Add("fx-treegrid-sorted");
        if (state.FilterActive)
            parts.Add("fx-treegrid-filtered");
        if (AllowResizing && column.AllowResizing)
            parts.Add("fx-treegrid-resizable");
        return string.Join(" ", parts);
    }

    internal string GetSortGlyph(TreeGridColumn column) =>
        GetColumnState(column).SortDirection switch
        {
            SortDirection.Ascending => "▲",
            SortDirection.Descending => "▼",
            _ => ""
        };

    internal bool IsColumnFilterPopupOpen(TreeGridColumn column) =>
        !string.IsNullOrWhiteSpace(column.Field) &&
        string.Equals(_filterPopupField, column.Field, StringComparison.OrdinalIgnoreCase);

    internal void ToggleFilterPopup(TreeGridColumn column)
    {
        if (!AllowFiltering || !column.AllowFiltering || string.IsNullOrWhiteSpace(column.Field))
            return;

        if (IsColumnFilterPopupOpen(column))
        {
            _filterPopupField = null;
            return;
        }

        _filterPopupField = column.Field;
        _filterDraft = GetColumnState(column).FilterValue ?? "";
    }

    internal async Task ApplyFilterAsync(TreeGridColumn column)
    {
        var state = GetColumnState(column);
        state.FilterValue = string.IsNullOrWhiteSpace(_filterDraft) ? null : _filterDraft.Trim();
        state.FilterOperator = TextFilterOperator.Contains;
        _filterPopupField = null;
        await InvokeAsync(StateHasChanged);
    }

    internal async Task ClearFilterAsync(TreeGridColumn column)
    {
        var state = GetColumnState(column);
        state.FilterValue = null;
        state.CheckedFilterValues.Clear();
        state.UseCheckedFilter = false;
        state.UseNumericBoundsFilter = false;
        state.UseNumericRangeFilter = false;
        _filterDraft = "";
        _filterPopupField = null;
        await InvokeAsync(StateHasChanged);
    }

    public async Task ClearFiltersAsync()
    {
        foreach (var state in _columnStates.Values)
        {
            state.FilterValue = null;
            state.CheckedFilterValues.Clear();
            state.UseCheckedFilter = false;
            state.UseNumericBoundsFilter = false;
            state.UseNumericRangeFilter = false;
        }

        _filterDraft = "";
        _filterPopupField = null;
        await InvokeAsync(StateHasChanged);
    }

    internal async Task SetColumnVisibleAsync(TreeGridColumn column, bool visible)
    {
        if (string.IsNullOrWhiteSpace(column.Field))
            return;

        if (!visible && !CanHideColumn(column))
            return;

        _visibilityOverrides[column.Field] = visible;
        await InvokeAsync(StateHasChanged);
    }

    internal void StartColumnResize(TreeGridColumn column, MouseEventArgs e)
    {
        if (!AllowResizing || !column.AllowResizing)
            return;

        _isColumnResizing = true;
        _resizingColumn = column;
        _resizeStartX = e.ClientX;
        _resizeStartWidth = GetEffectiveColumnWidth(column);
    }

    internal async Task HandleColumnResizeMove(MouseEventArgs e)
    {
        if (!_isColumnResizing || _resizingColumn == null)
            return;

        var delta = e.ClientX - _resizeStartX;
        var minWidth = ParseCssPixels(_resizingColumn.MinWidth, 32);
        var width = Math.Max(minWidth, _resizeStartWidth + delta);
        _columnWidthOverrides[GetColumnKey(_resizingColumn)] = width;
        await InvokeAsync(StateHasChanged);
    }

    internal async Task EndColumnResize(MouseEventArgs e)
    {
        if (!_isColumnResizing)
            return;

        await HandleColumnResizeMove(e);
        _isColumnResizing = false;
        _resizingColumn = null;
        await InvokeAsync(StateHasChanged);
    }

    internal string GetHeaderStyle(TreeGridColumn column)
    {
        var parts = new List<string>();
        var width = GetEffectiveColumnWidth(column);
        if (width > 0)
            parts.Add($"width:{width:0.##}px");
        else if (!string.IsNullOrWhiteSpace(column.Width))
            parts.Add($"width:{column.Width}");

        if (!string.IsNullOrWhiteSpace(column.MinWidth))
            parts.Add($"min-width:{column.MinWidth}");
        parts.Add($"text-align:{column.TextAlign.ToString().ToLowerInvariant()}");
        return string.Join(";", parts);
    }

    internal string GetCellStyle(TreeGridColumn column)
    {
        var parts = new List<string>();
        var width = GetEffectiveColumnWidth(column);
        if (width > 0)
            parts.Add($"width:{width:0.##}px");
        else if (!string.IsNullOrWhiteSpace(column.Width))
            parts.Add($"width:{column.Width}");

        if (!string.IsNullOrWhiteSpace(column.MinWidth))
            parts.Add($"min-width:{column.MinWidth}");
        parts.Add($"text-align:{column.TextAlign.ToString().ToLowerInvariant()}");
        return string.Join(";", parts);
    }

    private bool HasActiveFilters => _columnStates.Values.Any(s => s.FilterActive);

    private void RebuildPreservingExpansion()
    {
        var expandStates = _flatNodes
            .Where(node => node.Id != null)
            .ToDictionary(node => node.Id!, node => node.IsExpanded);

        BuildTree();

        foreach (var node in _flatNodes)
        {
            if (node.Id != null && expandStates.TryGetValue(node.Id, out var expanded))
                node.IsExpanded = expanded;
        }
    }

    private void SortRows<TRow>(List<TRow> rows, Func<TRow, TValue> itemSelector)
    {
        var sortState = _columnStates.Values.FirstOrDefault(s => s.SortDirection.HasValue);
        if (sortState?.SortDirection == null || string.IsNullOrWhiteSpace(sortState.Field))
            return;

        rows.Sort((left, right) =>
        {
            var comparison = CompareColumnValues(itemSelector(left), itemSelector(right), sortState.Field);
            return sortState.SortDirection == SortDirection.Descending ? -comparison : comparison;
        });
    }

    private int CompareColumnValues(TValue left, TValue right, string field)
    {
        var leftValue = GetPropertyValue(left, field);
        var rightValue = GetPropertyValue(right, field);
        if (leftValue == null && rightValue == null) return 0;
        if (leftValue == null) return -1;
        if (rightValue == null) return 1;

        if (leftValue is IComparable comparable && leftValue.GetType().IsInstanceOfType(rightValue))
            return comparable.CompareTo(rightValue);

        return string.Compare(leftValue.ToString(), rightValue.ToString(), StringComparison.CurrentCultureIgnoreCase);
    }

    private HashSet<TreeNode<TValue>> BuildFilterInclusionSet()
    {
        var included = new HashSet<TreeNode<TValue>>();
        foreach (var node in _flatNodes)
        {
            if (!NodeMatchesFilters(node))
                continue;

            IncludeNodeAndAncestors(node, included);
            IncludeDescendants(node, included);
        }

        return included;
    }

    private bool NodeMatchesFilters(TreeNode<TValue> node)
    {
        foreach (var state in _columnStates.Values.Where(s => s.FilterActive && !string.IsNullOrWhiteSpace(s.FilterValue)))
        {
            var value = GetPropertyValue(node.Data, state.Field)?.ToString() ?? "";
            if (!value.Contains(state.FilterValue!, StringComparison.CurrentCultureIgnoreCase))
                return false;
        }

        return true;
    }

    private void IncludeNodeAndAncestors(TreeNode<TValue> node, HashSet<TreeNode<TValue>> included)
    {
        included.Add(node);
        var parentId = node.ParentId;
        while (parentId != null)
        {
            var parent = _flatNodes.FirstOrDefault(n => Equals(n.Id, parentId));
            if (parent == null || !included.Add(parent))
                return;
            parentId = parent.ParentId;
        }
    }

    private void IncludeDescendants(TreeNode<TValue> node, HashSet<TreeNode<TValue>> included)
    {
        var children = _flatNodes.Where(n => Equals(n.ParentId, node.Id)).ToList();
        foreach (var child in children)
        {
            included.Add(child);
            IncludeDescendants(child, included);
        }
    }

    private double GetEffectiveColumnWidth(TreeGridColumn column)
    {
        var key = GetColumnKey(column);
        if (_columnWidthOverrides.TryGetValue(key, out var width))
            return width;

        return ParseCssPixels(column.Width, 0);
    }

    private static double ParseCssPixels(string? value, double fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var trimmed = value.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2];

        return double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pixels)
            ? pixels
            : fallback;
    }

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

public class TreeNode<TValue>
{
    public TValue Data { get; set; } = default!;
    public int Level { get; set; }
    public bool HasChildren { get; set; }
    public bool IsExpanded { get; set; } = true;
    public object? Id { get; set; }
    public object? ParentId { get; set; }
    public bool IsLastSibling { get; set; }
    public IReadOnlyList<bool> AncestorLineContinuations { get; set; } = Array.Empty<bool>();
}

public class TreeRowSelectEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int RowIndex { get; set; }
    public int Level { get; set; }
    public bool HasChildren { get; set; }
    public bool IsExpanded { get; set; }
}

public class TreeNodeEventArgs<TValue>
{
    public TValue? Data { get; set; }
    public int Level { get; set; }
}
