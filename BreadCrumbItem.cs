namespace Fx.ControlKit;

public sealed class BreadCrumbItem
{
    public string Text { get; set; } = "";
    public string? Href { get; set; }
    public string? IconCss { get; set; }
    public bool Disabled { get; set; }

    public BreadCrumbItem() { }

    public BreadCrumbItem(string text, string? href = null, string? iconCss = null)
    {
        Text = text;
        Href = href;
        IconCss = iconCss;
    }
}
