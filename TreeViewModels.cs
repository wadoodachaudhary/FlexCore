namespace Fx.ControlKit;

public enum TreeFolderStyle
{
    Yellow,
    None,
    Grey
}

/// <summary>Row layout. Standard is the classic tree; Outline is a course-outline / checklist layout
/// (status circles, connector line, secondary type line, count badge, chevron on the right).</summary>
public enum TreeViewDisplayMode
{
    Standard,
    Outline
}

/// <summary>Progress state drawn by <see cref="TreeViewDisplayMode.Outline"/>. None draws no status mark.</summary>
public enum TreeNodeStatus
{
    None,
    NotStarted,
    InProgress,
    Complete
}

public class TreeNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Text { get; set; } = "";
    public string? IconCss { get; set; }
    public string? ExpandedIconCss { get; set; }
    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set; }
    public bool IsChecked { get; set; }
    public bool IsIndeterminate { get; internal set; }
    public bool IsDisabled { get; set; }
    public bool IsBold { get; set; }
    public List<TreeNode> Children { get; set; } = new();
    /// <summary>Show an expander before children have been fetched.</summary>
    public bool HasUnloadedChildren { get; set; }
    public bool HasChildren => Children.Count > 0 || HasUnloadedChildren;
    public object? Tag { get; set; }
    /// <summary>Outline mode: progress mark (circle for groups, dot on the connector line for items).</summary>
    public TreeNodeStatus Status { get; set; }
    /// <summary>Outline mode: small caption above the text (for example "Session 1").</summary>
    public string? Caption { get; set; }
    /// <summary>Outline mode: second line under the text (for example "VIDEO · 5 MIN").</summary>
    public string? SecondaryText { get; set; }
    /// <summary>Outline mode: icon before <see cref="SecondaryText"/>; a FlexIcons name or raw SVG path data (24x24 view box).</summary>
    public string? SecondaryIcon { get; set; }
    /// <summary>Outline mode: right-aligned count or label (for example "2/5").</summary>
    public string? BadgeText { get; set; }
}

public enum TreeDropPosition { Before, Inside, After }
public sealed record TreeNodeInfo(TreeNode Node, TreeNode? Parent, int Level, int Position, int SetSize);
public sealed class TreeNodeEditEventArgs(TreeNode node, string text)
{
    public TreeNode Node { get; } = node;
    public string Text { get; set; } = text;
    public bool Cancel { get; set; }
    public string? Error { get; set; }
}
public sealed class TreeNodeMoveEventArgs(TreeNode node, TreeNode target, TreeDropPosition position)
{
    public TreeNode Node { get; } = node;
    public TreeNode Target { get; } = target;
    public TreeDropPosition Position { get; } = position;
    public bool Cancel { get; set; }
}
