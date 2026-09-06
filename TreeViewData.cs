namespace Fx.ControlKit;

/// <summary>Hierarchy operations shared by TreeView's UI and programmatic APIs.</summary>
public static class TreeViewData
{
    public static List<TreeNode> FromFlat<T>(IEnumerable<T> items, Func<T, string> id, Func<T, string?> parentId, Func<T, string> text, Func<T, bool>? hasChildren = null)
    {
        var source = items.ToList();
        var nodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            var key = id(item);
            if (string.IsNullOrWhiteSpace(key) || !nodes.TryAdd(key, new() { Id = key, Text = text(item), Tag = item, HasUnloadedChildren = hasChildren?.Invoke(item) == true }))
                throw new ArgumentException("Tree node IDs must be nonempty and unique.");
        }
        var roots = new List<TreeNode>();
        foreach (var item in source)
        {
            var node = nodes[id(item)]; var parent = parentId(item);
            if (parent is not null && nodes.TryGetValue(parent, out var owner)) owner.Children.Add(node); else roots.Add(node);
        }
        if (Flatten(roots).Count != nodes.Count) throw new ArgumentException("Tree parent references contain a cycle.");
        return roots;
    }

    public static List<TreeNodeInfo> Flatten(IEnumerable<TreeNode> roots, IComparer<TreeNode>? comparer = null)
    {
        var result = new List<TreeNodeInfo>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        void Add(IEnumerable<TreeNode> source, TreeNode? parent, int level)
        {
            if (level > 512) throw new ArgumentException("Tree depth exceeds 512 levels.");
            var siblings = (comparer is null ? source : source.OrderBy(n => n, comparer)).ToArray();
            for (var i = 0; i < siblings.Length; i++)
            {
                var node = siblings[i];
                if (node is null || string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id)) throw new ArgumentException("Tree node IDs must be nonempty and unique; cycles are not allowed.");
                result.Add(new(node, parent, level, i + 1, siblings.Length)); Add(node.Children, node, level + 1);
            }
        }
        Add(roots, null, 0); return result;
    }

    public static void SetChecked(List<TreeNode> roots, TreeNode node, bool value, bool cascade)
    {
        void Set(TreeNode current)
        {
            if (current.IsDisabled) return;
            current.IsChecked = value; current.IsIndeterminate = false;
            if (cascade) foreach (var child in current.Children) Set(child);
        }
        Set(node); RefreshChecks(roots, cascade);
    }
    public static void RefreshChecks(List<TreeNode> roots, bool cascade)
    {
        foreach (var info in Flatten(roots).AsEnumerable().Reverse())
        {
            var node = info.Node; node.IsIndeterminate = false;
            if (!cascade || node.IsDisabled) continue;
            var children = node.Children.Where(c => !c.IsDisabled).ToArray();
            if (children.Length == 0) continue;
            node.IsChecked = children.All(c => c.IsChecked && !c.IsIndeterminate);
            node.IsIndeterminate = !node.IsChecked && children.Any(c => c.IsChecked || c.IsIndeterminate);
        }
    }
    public static void Move(List<TreeNode> roots, string nodeId, string targetId, TreeDropPosition position)
    {
        var index = Flatten(roots).ToDictionary(n => n.Node.Id, StringComparer.Ordinal);
        if (!index.TryGetValue(nodeId, out var source) || !index.TryGetValue(targetId, out var target)) throw new ArgumentException("Unknown tree node.");
        if (source.Node.IsDisabled || target.Node.IsDisabled) throw new InvalidOperationException("Disabled nodes cannot be moved or accept a drop.");
        for (var ancestor = target; ancestor is not null; ancestor = ancestor.Parent is null ? null : index[ancestor.Parent.Id])
            if (ancestor.Node == source.Node) throw new InvalidOperationException("A node cannot be moved into itself or its descendants.");
        if (!Enum.IsDefined(position)) throw new ArgumentException("Unknown drop position.");
        if (position == TreeDropPosition.Inside && target.Node.HasUnloadedChildren) throw new InvalidOperationException("Load the destination's children before moving a node into it.");
        var oldList = source.Parent?.Children ?? roots;
        var newList = position == TreeDropPosition.Inside ? target.Node.Children : target.Parent?.Children ?? roots;
        oldList.Remove(source.Node);
        var at = position == TreeDropPosition.Inside ? newList.Count : newList.IndexOf(target.Node) + (position == TreeDropPosition.After ? 1 : 0);
        newList.Insert(at, source.Node);
        if (position == TreeDropPosition.Inside) target.Node.IsExpanded = true;
    }
}
