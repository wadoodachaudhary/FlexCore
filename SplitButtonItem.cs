namespace Fx.ControlKit;

public sealed class SplitButtonItem
{
    public string Text { get; set; } = "";
    public string? IconCss { get; set; }
    public bool Disabled { get; set; }
    public object? Value { get; set; }

    public SplitButtonItem() { }

    public SplitButtonItem(string text, string? iconCss = null, object? value = null, bool disabled = false)
    {
        Text = text;
        IconCss = iconCss;
        Value = value;
        Disabled = disabled;
    }
}
