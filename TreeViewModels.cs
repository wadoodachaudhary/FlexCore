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
    public bool IsDisabled { get; set; }
    public bool IsBold { get; set; }
    public List<TreeNode> Children { get; set; } = new();
    public bool HasChildren => Children.Count > 0;
    public object? Tag { get; set; }
}
