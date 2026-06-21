using System;
using System.Collections.Generic;

namespace Fx.ControlKit;

public enum SidebarType
{
    Accordion,
    Tree
}

public class SidebarSection
{
    public string Title { get; set; } = "";
    public bool IsCollapsed { get; set; } = false;
    public List<SidebarItem> Items { get; set; } = new();
    public bool Disabled { get; set; } = false;
}

public class SidebarItem
{
    public string Text { get; set; } = "";
    public string Icon { get; set; } = "📄";
    public string TaskName { get; set; } = "";
    public bool IsBold { get; set; } = false;
    public bool IsRed { get; set; } = false;
    public bool Disabled { get; set; } = false;
}
