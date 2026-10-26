namespace Fx.ControlKit;

/// <summary>Horizontal scroll, wrapped horizontal rows, or a vertical rail beside the content.</summary>
public enum TabOrientation { HybridVertical, Horizontal, Vertical }

public enum TabTheme { WindowsClassic, FolderTabs, ColorfulCards, Minimal, Dark }

internal static class TabAppearance
{
    internal static string Style(TabTheme theme) => theme switch
    {
        TabTheme.FolderTabs => Variables("#edf2f3", "#c6dde5", "#243b46", "#285766", "#fff", "#829fa9", "7px", "inset 0 1px #ffffffaa", "#dcebf0"),
        TabTheme.ColorfulCards => Variables("#e6eaeb", "#fff", "#242424", "#333", "#fff", "#a5adaf", "7px", "0 -1px 3px #0002, inset 0 1px #ffffffaa", "#f4f4f4"),
        TabTheme.Minimal => Variables("#fff", "#fff", "#333", "#e3e3e3", "#111", "#ccc", "0", "none", "#f1f1f1"),
        TabTheme.Dark => Variables("#242424", "#353535", "#eee", "#111", "#fff", "#666", "3px", "none", "#454545"),
        _ => Variables("#e4e4e4", "#d4d4d4", "#222", "#f5f5f5", "#000", "#7a7a7a", "3px", "inset 1px 1px 0 #efefef, inset -1px 0 0 #a0a0a0", "#dfdfdf")
    };

    private static string Variables(string bar, string tab, string text, string active, string activeText, string border, string radius, string shadow, string hover)
        => $"--fx-tab-bg:{bar};--fx-tab-item-bg:{tab};--fx-tab-color:{text};--fx-tab-active-bg:{active};--fx-tab-active-color:{activeText};--fx-tab-active-border-color:{active};--fx-tab-border:{border};--fx-tab-separator-color:{border};--fx-tab-radius:{radius};--fx-tab-shadow:{shadow};--fx-tab-hover-bg:{hover};";
}

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
