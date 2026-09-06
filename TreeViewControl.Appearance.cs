using Microsoft.AspNetCore.Components;
namespace Fx.ControlKit;

public partial class TreeViewControl
{
    [Parameter] public List<TreeNode>? Nodes { get; set; }
    [Parameter] public TreeNode? SelectedNode { get; set; }
    [Parameter] public EventCallback<TreeNode> SelectedNodeChanged { get; set; }
    [Parameter] public EventCallback<TreeNode> OnNodeClick { get; set; }
    [Parameter] public EventCallback<TreeNode> OnNodeDoubleClick { get; set; }
    [Parameter] public EventCallback<TreeNode> OnNodeActivated { get; set; }
    [Parameter] public EventCallback<TreeNode> OnNodeExpand { get; set; }
    [Parameter] public bool ShowCheckboxes { get; set; }
    [Parameter] public bool ShowIcons { get; set; } = true;
    [Parameter] public bool AllowMultiSelect { get; set; }
    [Parameter] public bool ToggleOnNodeClick { get; set; } = true;
    [Parameter] public string? CssClass { get; set; }
    [Parameter] public string? Height { get; set; }
    [Parameter] public bool UseVb6TreeStyle { get; set; }
    [Parameter] public TreeFolderStyle FolderStyle { get; set; } = TreeFolderStyle.Yellow;
    [Parameter] public int TreeIndentPixels { get; set; } = 16;
    [Parameter] public int TabIndex { get; set; } = 0;

    private bool IsVb6TreeStyle => UseVb6TreeStyle || HasCssClass("fx-treeview-vb6");
    private string? RootStyle => Height != null ? $"height:{Height};overflow:auto;" : null;

    private string RootCssClass
    {
        get
        {
            var classes = new List<string> { "fx-treeview" };

            if (IsVb6TreeStyle)
            {
                if (!HasCssClass("fx-treeview-vb6"))
                    classes.Add("fx-treeview-vb6");

                if (!HasFolderModifierClass())
                    classes.Add(GetFolderStyleCss());
            }

            if (!string.IsNullOrWhiteSpace(CssClass))
                classes.Add(CssClass.Trim());

            return string.Join(" ", classes);
        }
    }

    private string GetNodeCss(TreeNode node, int level)
    {
        var css = "fx-treeview-node";
        if (IsVb6TreeStyle)
        {
            css += " fx-treeview-vb6-node";
            css += level == 0 ? " fx-treeview-root" : "";
            css += node.HasChildren ? " fx-treeview-branch" : " fx-treeview-leaf";
            css += DisplayExpanded(node) ? " fx-treeview-expanded" : " fx-treeview-collapsed";
        }
        if (node.IsSelected)
            css += " fx-treeview-node-selected";
        if (node.IsDisabled)
            css += " fx-treeview-node-disabled";
        if (node.IsBold)
            css += " fx-treeview-node-bold";
        return css;
    }

    private string GetNodeStyle(int level)
    {
        if (!IsVb6TreeStyle)
            return $"padding-left:{level * 20}px;";

        var indent = Math.Max(0, TreeIndentPixels);
        var nodeLeft = level * indent;
        return $"padding-left:{nodeLeft}px;--tree-depth:{level};--tree-indent:{indent}px;--tree-node-left:{nodeLeft}px;";
    }

    private string GetArrowCss(TreeNode node)
    {
        var css = "fx-treeview-arrow";
        if (!node.HasChildren)
            return css + " fx-treeview-arrow-hidden";

        return DisplayExpanded(node)
            ? css + " fx-treeview-arrow-expanded"
            : css + " fx-treeview-arrow-collapsed";
    }

    private string GetRenderedIconCss(TreeNode node)
    {
        var iconCss = GetIconCss(node);
        if (!string.IsNullOrWhiteSpace(iconCss))
            return "fx-treeview-icon " + iconCss;

        if (!IsVb6TreeStyle)
            return "";

        return node.HasChildren
            ? $"fx-treeview-icon fx-treeview-folder-icon {(DisplayExpanded(node) ? "open" : "closed")}"
            : "fx-treeview-icon fx-treeview-folder-spacer";
    }

    private string GetIconCss(TreeNode node)
    {
        if (DisplayExpanded(node) && !string.IsNullOrEmpty(node.ExpandedIconCss))
            return node.ExpandedIconCss;
        return node.IconCss ?? "";
    }

    private string GetFolderStyleCss() => FolderStyle switch
    {
        TreeFolderStyle.None => "fx-treeview-no-folder",
        TreeFolderStyle.Grey => "fx-treeview-grey-folder",
        _ => "fx-treeview-yellow-folder"
    };

    private bool HasFolderModifierClass()
    {
        return HasCssClass("fx-treeview-yellow-folder")
            || HasCssClass("fx-treeview-no-folder")
            || HasCssClass("fx-treeview-grey-folder");
    }

    private bool HasCssClass(string cssClass)
    {
        if (string.IsNullOrWhiteSpace(CssClass))
            return false;

        return CssClass.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(value => string.Equals(value, cssClass, StringComparison.Ordinal));
    }

}
