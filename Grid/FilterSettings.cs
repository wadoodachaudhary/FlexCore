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

    /// <summary>
    /// When true, text search and all built-in text filter surfaces use ordinal
    /// case-sensitive matching. The setting is also sent to ItemsProvider hosts.
    /// </summary>
    public bool EnableCaseSensitivity { get; set; }

    /// <summary>
    /// Shows type-specific operator dropdowns in the filter row by default.
    /// When false, every built-in filter cell is a textbox using Contains on
    /// the column's raw/display text, including numbers, dates, and Booleans.
    /// Header-menu conditions and custom filter templates remain available.
    /// </summary>
    public bool ShowFilterRowOperators { get; set; } = true;

    /// <summary>
    /// Debounce, in milliseconds, for the SearchBox, typed filter-row inputs,
    /// and an auto-applying filter menu. Set to zero for immediate application.
    /// </summary>
    public int ImmediateModeDelay { get; set; } = 300;
}
