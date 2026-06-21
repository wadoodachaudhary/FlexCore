namespace Fx.ControlKit;

public enum SideNavPosition
{
    Left,
    Right
}

public class SideNavigationItem
{
    public object? Key { get; set; }

    public string Text { get; set; } = "";

    public string? IconUrl { get; set; }

    public string? Title { get; set; }
}
