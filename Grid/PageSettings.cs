namespace Fx.ControlKit.Grid;

/// <summary>
/// Configures pagination for GridControl. Equivalent to SyncFusion's GridPageSettings.
/// Plain data object passed to GridControl via its <c>PageSettingsRef</c> parameter — it is
/// never rendered as a component, so its properties carry no <c>[Parameter]</c> attribute
/// (which would otherwise trip BL0005 on every host that sets them in C#).
/// </summary>
public class PageSettings
{
    public int PageSize { get; set; } = 10;
    public int[] PageSizes { get; set; } = [5, 10, 20, 50];
    public int PageCount { get; set; } = 5;
}
