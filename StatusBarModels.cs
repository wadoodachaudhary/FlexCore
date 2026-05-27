namespace Fx.ControlKit;

public class StatusBarPanel
{
    public string? Text { get; set; }
    public string? IconCss { get; set; }
    public string? Tooltip { get; set; }
    public string? Width { get; set; }
    public bool Spring { get; set; }

    public StatusBarPanel() { }
    public StatusBarPanel(string text, string? iconCss = null, string? width = null, bool spring = false)
    {
        Text = text;
        IconCss = iconCss;
        Width = width;
        Spring = spring;
    }
}
