namespace Fx.ControlKit;

public class TabStripItem
{
    public string Text { get; set; } = "";
    public string? IconCss { get; set; }
    public bool Closable { get; set; }
    public bool Disabled { get; set; }
    public object? Tag { get; set; }

    public TabStripItem() { }
    public TabStripItem(string text, string? iconCss = null, bool closable = false)
    {
        Text = text;
        IconCss = iconCss;
        Closable = closable;
    }
}
