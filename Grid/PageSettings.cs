using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Configures pagination for GridControl. Equivalent to SyncFusion's GridPageSettings.
/// </summary>
public class PageSettings : ComponentBase
{
    [Parameter] public int PageSize { get; set; } = 10;
    [Parameter] public int[] PageSizes { get; set; } = [5, 10, 20, 50];
    [Parameter] public int PageCount { get; set; } = 5;
}
