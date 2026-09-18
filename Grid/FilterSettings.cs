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
    /// Debounce, in milliseconds, for search as you type (<see cref="SearchAsYouType"/>):
    /// the search box, the filter-row boxes, the filter menu's boxes and the
    /// side-panel searches apply this long after the last key (Enter, Tab or
    /// leaving the box apply at once). It also delays filter-row values set through
    /// <c>OnColumnFilterInput</c> and a filter-menu template's checklist search. With
    /// <see cref="SearchAsYouType"/> off the grid's own boxes apply when the text is
    /// committed, not after a delay. Set to zero for immediate application.
    /// </summary>
    public int ImmediateModeDelay { get; set; } = 300;

    /// <summary>
    /// When true (the default) the grid's filter and search boxes search as you
    /// type: the text applies once typing pauses for <see cref="ImmediateModeDelay"/>,
    /// or at once on Enter, Tab or leaving the box. When false they apply only when
    /// the text is committed: Enter, Tab, leaving the box or, in the filter menu, the
    /// box's search button. Either way the browser owns the typed text, so a slow
    /// connection never loses or reorders characters.
    /// </summary>
    public bool SearchAsYouType { get; set; } = true;
}
