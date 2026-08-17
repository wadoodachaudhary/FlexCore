namespace Fx.ControlKit;

/// <summary>Controls how a navigation node resolves its focus targets.</summary>
public enum PageNavigationNodeMode
{
    /// <summary>Use the matched element when focusable; otherwise use its focusable descendants.</summary>
    Auto,

    /// <summary>Use only the matched element.</summary>
    Element,

    /// <summary>Use the focusable descendants of the matched element.</summary>
    Descendants
}

/// <summary>Action performed when a page shortcut is pressed.</summary>
public enum PageShortcutAction
{
    Focus,
    Activate
}

/// <summary>Modifier keys used by a page shortcut.</summary>
[Flags]
public enum PageShortcutModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Meta = 4,
    Shift = 8
}

/// <summary>A keyboard shortcut attached to a page navigation node.</summary>
public sealed record PageNavigationShortcut(
    string Key,
    PageShortcutModifiers Modifiers = PageShortcutModifiers.None,
    PageShortcutAction Action = PageShortcutAction.Focus,
    bool AllowInEditor = false);

/// <summary>
/// A named focus node in a <see cref="PageNavigationGraph"/>. Nodes use their
/// declared order by default; <see cref="Next"/> and <see cref="Previous"/>
/// are directed overrides for pages whose navigation is not linear.
/// </summary>
public sealed record PageNavigationNode(
    string Id,
    string Selector,
    int? Order = null,
    string? Next = null,
    string? Previous = null,
    PageNavigationNodeMode Mode = PageNavigationNodeMode.Auto,
    IReadOnlyList<PageNavigationShortcut>? Shortcuts = null);

/// <summary>
/// Defines page-level keyboard navigation. A node may represent one focusable
/// control, such as a grid, or a region whose controls are visited in DOM order.
/// </summary>
public sealed class PageNavigationGraph
{
    public PageNavigationGraph(IEnumerable<PageNavigationNode> nodes, bool wrap = true)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var declaredNodes = nodes
            .Select((node, declarationIndex) => (Node: node, DeclarationIndex: declarationIndex))
            .ToArray();

        foreach (var (node, _) in declaredNodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id))
                throw new ArgumentException("A page navigation node must have an id.", nameof(nodes));
            if (string.IsNullOrWhiteSpace(node.Selector))
                throw new ArgumentException($"Page navigation node '{node.Id}' must have a selector.", nameof(nodes));
            if (node.Shortcuts?.Any(shortcut => string.IsNullOrWhiteSpace(shortcut.Key)) == true)
                throw new ArgumentException($"Page navigation node '{node.Id}' has an empty shortcut key.", nameof(nodes));
        }

        var duplicateId = declaredNodes
            .GroupBy(item => item.Node.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateId is not null)
            throw new ArgumentException($"Page navigation node id '{duplicateId}' is duplicated.", nameof(nodes));

        var nodeIds = declaredNodes
            .Select(item => item.Node.Id)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (node, _) in declaredNodes)
        {
            ValidateEdge(node.Id, nameof(node.Next), node.Next, nodeIds, nodes);
            ValidateEdge(node.Id, nameof(node.Previous), node.Previous, nodeIds, nodes);
        }

        Nodes = declaredNodes
            .OrderBy(item => item.Node.Order ?? item.DeclarationIndex)
            .ThenBy(item => item.DeclarationIndex)
            .Select(item => item.Node)
            .ToArray();
        Wrap = wrap;
    }

    public IReadOnlyList<PageNavigationNode> Nodes { get; }
    public bool Wrap { get; }

    private static void ValidateEdge(
        string sourceId,
        string edgeName,
        string? targetId,
        IReadOnlySet<string> nodeIds,
        IEnumerable<PageNavigationNode> nodes)
    {
        if (!string.IsNullOrWhiteSpace(targetId) && !nodeIds.Contains(targetId))
        {
            throw new ArgumentException(
                $"Page navigation node '{sourceId}' has {edgeName} target '{targetId}', but that node does not exist.",
                nameof(nodes));
        }
    }
}

/// <summary>A named, ordered focus region within a <see cref="PageControl"/>.</summary>
public sealed record PageNavigationRegion(string Name, string Selector);

/// <summary>
/// Backward-compatible linear page map. New pages that need explicit edges or
/// shortcuts should use <see cref="PageNavigationGraph"/>.
/// </summary>
public sealed class PageNavigationMap
{
    public PageNavigationMap(IEnumerable<PageNavigationRegion> regions, bool wrap = true)
    {
        ArgumentNullException.ThrowIfNull(regions);
        Regions = regions
            .Where(region => !string.IsNullOrWhiteSpace(region.Selector))
            .ToArray();
        Wrap = wrap;
        Graph = new PageNavigationGraph(
            Regions.Select((region, index) => new PageNavigationNode(region.Name, region.Selector, index)),
            wrap);
    }

    public IReadOnlyList<PageNavigationRegion> Regions { get; }
    public bool Wrap { get; }
    internal PageNavigationGraph Graph { get; }
}

/// <summary>Lets child FlexKit controls know that their page owns Tab navigation.</summary>
public sealed class PageNavigationContext
{
    internal PageNavigationContext(bool handlesTabNavigation)
    {
        HandlesTabNavigation = handlesTabNavigation;
    }

    public bool HandlesTabNavigation { get; }
}
