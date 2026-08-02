namespace Fx.ControlKit;

/// <summary>
/// Which side of its container a <see cref="SideNavigationControl"/> docks to. The accent
/// border and inner padding flip so the panel always faces the content area.
/// </summary>
public enum SideNavPosition
{
    Left,
    Right
}

/// <summary>
/// One selectable entry in a <see cref="SideNavigationControl"/>.
/// </summary>
public class SideNavigationItem
{
    /// <summary>Value identifying this item; compared against the control's SelectedKey.</summary>
    public object? Key { get; set; }

    /// <summary>Display caption.</summary>
    public string Text { get; set; } = "";

    /// <summary>Optional icon URL rendered to the left of the caption.</summary>
    public string? IconUrl { get; set; }

    /// <summary>Optional tooltip text.</summary>
    public string? Title { get; set; }
}
