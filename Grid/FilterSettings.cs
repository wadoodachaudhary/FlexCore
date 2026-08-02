namespace Fx.ControlKit.Grid;

/// <summary>
/// Configures filtering behavior for GridControl. Equivalent to SyncFusion's GridFilterSettings.
/// Plain data object passed to GridControl via its <c>FilterSettingsRef</c> parameter — it is
/// never rendered as a component, so its properties carry no <c>[Parameter]</c> attribute
/// (which would otherwise trip BL0005 on every host that sets them in C#).
/// </summary>
public class FilterSettings
{
    public FilterType Type { get; set; } = FilterType.FilterBar;
    public bool EnableCaseSensitivity { get; set; }
    public int ImmediateModeDelay { get; set; } = 300;
}
