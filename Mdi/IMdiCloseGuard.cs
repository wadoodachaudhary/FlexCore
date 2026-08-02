namespace Fx.ControlKit.Mdi;

/// <summary>
/// Implement on a page hosted by <see cref="MdiHost"/> when the page needs
/// to approve, save, discard, or cancel before its MDI tab is closed.
/// </summary>
public interface IMdiCloseGuard
{
    Task<bool> CanCloseMdiWindowAsync(MdiCloseContext context);
}

/// <summary>Context supplied to an MDI child during a close request.</summary>
public sealed class MdiCloseContext
{
    public string WindowId { get; init; } = "";
    public string Route { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsApplicationClosing { get; init; }
}
