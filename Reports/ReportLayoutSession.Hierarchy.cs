using System.Data;

namespace Fx.ControlKit.Reports;

// Crystal hierarchical grouping (GroupOptions "group hierarchically"): inside each run of the enclosing groups the group
// instances form a tree through ParentIDField -> InstanceIDField. Instances print depth first, children in group sort order,
// and a group's footer follows its descendants. Each instance keeps its own records; "across hierarchy" totals add the subtree.
public sealed partial class ReportLayoutSession
{
    private sealed record HierarchyNode(int Start, int End, int Parent, int Depth, int SubtreeEnd, int Children);
    private sealed record Hierarchy(HierarchyNode[] Nodes, int[] NodeOfRow);
    private readonly Dictionary<int, (DataRow[] Rows, Hierarchy Tree)> _hierarchies = new();

    private bool IsHierarchical(int level) => level >= 0 && level < _layout.Document.Groups.Count && _layout.Document.Groups[level].IsHierarchical;

    private void OrderHierarchies()
    {
        for (var level = 0; level < _layout.Document.Groups.Count; level++)
        {
            if (!IsHierarchical(level)) continue;
            var ordered = new List<DataRow>(_rows.Length);
            foreach (var (first, last) in Runs(0, _rows.Length, level - 1))
            {
                var instances = Runs(first, last, level);
                var parents = Parents(instances, level);
                var children = instances.Select(_ => new List<int>()).ToArray();
                var roots = new List<int>();
                for (var index = 0; index < instances.Count; index++)
                    (parents[index] < 0 ? roots : children[parents[index]]).Add(index);
                var visited = 0;
                var pending = new Stack<int>(Enumerable.Reverse(roots));
                while (pending.TryPop(out var node))
                {
                    visited++;
                    for (var row = instances[node].Start; row < instances[node].End; row++) ordered.Add(_rows[row]);
                    for (var child = children[node].Count - 1; child >= 0; child--) pending.Push(children[node][child]);
                }
                if (visited != instances.Count)
                    throw new InvalidDataException($"Hierarchical group {_layout.Document.Groups[level].Condition}: parent IDs form a cycle, so {instances.Count - visited} group(s) have no place in the hierarchy.");
            }
            _rows = ordered.ToArray(); ClearValues();
        }
    }

    private Hierarchy? HierarchyAt(int level)
    {
        if (!IsHierarchical(level)) return null;
        if (_hierarchies.TryGetValue(level, out var cached) && ReferenceEquals(cached.Rows, _rows)) return cached.Tree;
        var nodes = new List<HierarchyNode>();
        var nodeOfRow = new int[_rows.Length];
        foreach (var (first, last) in Runs(0, _rows.Length, level - 1))
        {
            var instances = Runs(first, last, level);
            var parents = Parents(instances, level);
            var offset = nodes.Count;
            var depth = new int[instances.Count];
            var subtreeEnd = new int[instances.Count];
            var childCount = new int[instances.Count];
            var open = new Stack<int>();
            for (var index = 0; index < instances.Count; index++)
            {
                if (parents[index] >= index)
                    throw new InvalidDataException($"Hierarchical group {_layout.Document.Groups[level].Condition}: a group prints before its parent; the rows are not in hierarchy order.");
                depth[index] = parents[index] < 0 ? 0 : depth[parents[index]] + 1;
                if (parents[index] >= 0) childCount[parents[index]]++;
                while (open.Count > 0 && depth[open.Peek()] >= depth[index]) subtreeEnd[open.Pop()] = instances[index].Start;
                open.Push(index);
            }
            while (open.Count > 0) subtreeEnd[open.Pop()] = last;
            for (var index = 0; index < instances.Count; index++)
            {
                nodes.Add(new(instances[index].Start, instances[index].End, parents[index] < 0 ? -1 : offset + parents[index], depth[index], subtreeEnd[index], childCount[index]));
                for (var row = instances[index].Start; row < instances[index].End; row++) nodeOfRow[row] = offset + index;
            }
        }
        var tree = new Hierarchy(nodes.ToArray(), nodeOfRow);
        _hierarchies[level] = (_rows, tree);
        return tree;
    }

    // Crystal CountHierarchicalChildren(level): child groups of the current hierarchical group; -1 when the level is not hierarchical.
    private int HierarchicalChildren(int level, int row) =>
        HierarchyAt(level - 1) is { } tree && row >= 0 && row < _rows.Length ? tree.Nodes[tree.NodeOfRow[row]].Children : -1;

    // Crystal's hierarchicalIndent: each hierarchical group up to the section's level adds depth x indent (DataProcessor2).
    private int HierarchicalIndent(string sectionId, int row)
    {
        if (row < 0 || row >= _rows.Length || !_layout.Document.Groups.Any(g => g.IsHierarchical && g.HierarchicalIndent > 0)) return 0;
        var section = _layout.Document.Sections.FirstOrDefault(s => s.Id == sectionId);
        var last = section?.Kind switch
        {
            "GroupHeader" or "GroupFooter" => ReportDesignerEditing.GetGroupNumber(_layout.Document, section) - 1,
            "Detail" => _layout.Document.Groups.Count - 1,
            _ => -1
        };
        var indent = 0;
        for (var level = 0; level <= last; level++)
            if (HierarchyAt(level) is { } tree) indent += tree.Nodes[tree.NodeOfRow[row]].Depth * _layout.Document.Groups[level].HierarchicalIndent;
        return indent;
    }

    private List<(int Start, int End)> Runs(int first, int last, int level)
    {
        var runs = new List<(int Start, int End)>();
        for (var start = first; start < last;)
        {
            var end = start + 1;
            while (end < last && (level < 0 || SameGroup(start, end, level))) end++;
            runs.Add((start, end));
            start = end;
        }
        return runs;
    }

    private int[] Parents(List<(int Start, int End)> instances, int level)
    {
        var group = _layout.Document.Groups[level];
        var ids = new Dictionary<object, int>(TotalValueComparer.Instance);
        for (var index = 0; index < instances.Count; index++)
            if (Value(group.InstanceIdField, instances[index].Start) is { } id and not DBNull) ids.TryAdd(id, index);
        return instances.Select((instance, index) => Value(group.ParentIdField, instance.Start) is { } parent and not DBNull
            && ids.TryGetValue(parent, out var owner) && owner != index ? owner : -1).ToArray();
    }
}
