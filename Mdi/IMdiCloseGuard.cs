namespace Fx.ControlKit.Mdi;

public interface IMdiCloseGuard
{
    Task<bool> CanCloseMdiWindowAsync(MdiCloseContext context);
}

public sealed class MdiCloseContext
{
    public string WindowId { get; init; } = "";
    public string Route { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsApplicationClosing { get; init; }
}
