namespace Fx.ControlKit.Grid;

/// <summary>Validates and orders flat records without changing caller-owned objects.</summary>
public static class TreeGridHierarchy
{
    public static IReadOnlyList<T> Order<T>(IEnumerable<T> records, Func<T, object?> id, Func<T, object?> parent)
    {
        var items = records.ToArray();
        var ids = new HashSet<object>();
        foreach (var item in items)
            if (id(item) is not { } key || !ids.Add(key)) throw new ArgumentException("Tree IDs must be non-null and unique.");
        var children = items.Where(r => parent(r) is { } key && ids.Contains(key)).ToLookup(r => parent(r)!);
        var ordered = new List<T>();
        void Add(T item, int level)
        {
            if (level > 512) throw new ArgumentException("Tree depth cannot exceed 512 levels.");
            ordered.Add(item);
            foreach (var child in children[id(item)!]) Add(child, level + 1);
        }
        foreach (var root in items.Where(r => parent(r) is not { } key || !ids.Contains(key))) Add(root, 0);
        if (ordered.Count != items.Length) throw new ArgumentException("Tree parent mappings contain a cycle.");
        return ordered;
    }

    public static HashSet<object> DescendantIds<T>(IEnumerable<T> records, object rootId, Func<T, object?> id, Func<T, object?> parent)
    {
        var children = records.Where(r => parent(r) is not null).ToLookup(r => parent(r)!);
        var result = new HashSet<object>(); var pending = new Stack<object>(); pending.Push(rootId);
        while (pending.TryPop(out var key))
            if (result.Add(key)) foreach (var child in children[key]) pending.Push(id(child)!);
        return result;
    }

    public static IReadOnlyList<T> Move<T>(IEnumerable<T> records, object sourceId, object targetId, TreeDropPosition position,
        Func<T, object?> id, Func<T, object?> parent, Func<T, T> clone, Action<T, object?> setParent)
    {
        if (!Enum.IsDefined(position)) throw new ArgumentException("Unknown drop position.");
        var ordered = Order(records, id, parent).ToList();
        var index = ordered.ToDictionary(r => id(r)!);
        if (!index.TryGetValue(sourceId, out var source) || !index.TryGetValue(targetId, out var target))
            throw new ArgumentException("Unknown source or destination row.");
        var branch = DescendantIds(ordered, sourceId, id, parent);
        if (branch.Contains(targetId)) throw new InvalidOperationException("A row cannot be moved into itself or its descendants.");
        var moved = ordered.Where(r => branch.Contains(id(r)!)).ToList();
        var copy = clone(source);
        if (ReferenceEquals(copy, source)) throw new InvalidOperationException("CloneFactory must return an independent record.");
        setParent(copy, position == TreeDropPosition.Inside ? targetId : parent(target));
        moved[0] = copy;
        ordered.RemoveAll(r => branch.Contains(id(r)!));
        var at = ordered.FindIndex(r => Equals(id(r), targetId));
        if (position != TreeDropPosition.Before)
        {
            var targetBranch = DescendantIds(ordered, targetId, id, parent);
            while (at < ordered.Count && targetBranch.Contains(id(ordered[at])!)) at++;
        }
        ordered.InsertRange(at, moved);
        return Order(ordered, id, parent);
    }
}
