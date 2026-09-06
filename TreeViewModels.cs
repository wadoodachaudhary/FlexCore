namespace Fx.ControlKit;

public enum TreeFolderStyle
{
    Yellow,
    None,
    Grey
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
