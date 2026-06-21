namespace Fx.ControlKit.Grid;

public class FilterSettings
{
    public FilterType Type { get; set; } = FilterType.FilterBar;
    public bool EnableCaseSensitivity { get; set; }
    public int ImmediateModeDelay { get; set; } = 300;
}
