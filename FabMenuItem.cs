namespace Fx.ControlKit;

using Microsoft.AspNetCore.Components;

public sealed class FabMenuItem
{
    public string Title { get; set; } = "";
    public string? IconCss { get; set; }
    public BadgeVariant Color { get; set; } = BadgeVariant.Primary;
    public EventCallback OnClick { get; set; }

    public FabMenuItem() { }

    public FabMenuItem(string title, string? iconCss = null, BadgeVariant color = BadgeVariant.Primary)
    {
        Title = title;
        IconCss = iconCss;
        Color = color;
    }
}
