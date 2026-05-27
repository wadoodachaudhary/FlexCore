using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Configures filtering behavior for GridControl. Equivalent to SyncFusion's GridFilterSettings.
/// </summary>
public class FilterSettings : ComponentBase
{
    [Parameter] public FilterType Type { get; set; } = FilterType.FilterBar;
    [Parameter] public bool EnableCaseSensitivity { get; set; }
    [Parameter] public int ImmediateModeDelay { get; set; } = 300;
}
