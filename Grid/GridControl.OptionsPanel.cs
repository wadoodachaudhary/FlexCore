using Microsoft.AspNetCore.Components;
using System.Globalization;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    private enum GridOptionsPanel
    {
        None,
        Columns,
        Pivot,
        Theme
    }

    private enum PivotFieldArea
    {
        Row,
        Column,
        Value
    }

    private GridOptionsPanel _activeOptionsPanel = GridOptionsPanel.None;
    private bool _themeInitialized;
    private GridTheme _activeGridTheme = GridTheme.Default;
    private bool _advancedViewInitialized;
    private bool _advancedViewEnabled;

    private readonly record struct FilterValueCandidate(string Value, string DisplayText);

    private string _columnPanelSearch = "";
    private string _pivotFieldSearch = "";

    private readonly Dictionary<string, string> _numericFilterMinText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _numericFilterMaxText = new(StringComparer.Ordinal);
    private static readonly GridThemeGalleryItem[] GridThemeGallery =
    [
        new(GridTheme.Default, "Default", "Light", "#e9e9e9", "#ffffff", "#ffffff", "#9a9a9a"),
        new(GridTheme.Vb6Windows, "VB6 Windows", "Light", "#d4d0c8", "#ffffff", "#ffffff", "#808080"),
        new(GridTheme.ExcelLightBlue, "Light Blue", "Light", "#d9eaf7", "#ffffff", "#edf7fd", "#5aa7d8"),
        new(GridTheme.ExcelLightGreen, "Light Green", "Light", "#e2f0d9", "#ffffff", "#f1f8eb", "#70ad47"),
        new(GridTheme.ExcelLightOrange, "Light Orange", "Light", "#fce4d6", "#ffffff", "#fff3ec", "#ed7d31"),
        new(GridTheme.ExcelMediumBlue, "Medium Blue", "Medium", "#1f4e79", "#ffffff", "#ddebf7", "#5b9bd5"),
        new(GridTheme.ExcelMediumGreen, "Medium Green", "Medium", "#548235", "#ffffff", "#e2f0d9", "#70ad47"),
        new(GridTheme.ExcelDarkSlate, "Dark Slate", "Dark", "#2f3542", "#111827", "#1f2937", "#64748b", true)
    ];
    private static readonly TextFilterOperator[] TextFilterOperators =
    [
        TextFilterOperator.Contains,
        TextFilterOperator.Equals,
        TextFilterOperator.DoesNotEqual,
        TextFilterOperator.BeginsWith,
        TextFilterOperator.DoesNotBeginWith,
        TextFilterOperator.EndsWith,
        TextFilterOperator.DoesNotEndWith,
        TextFilterOperator.DoesNotContain,
        TextFilterOperator.ChooseOne
    ];
    private static readonly AggregateType[] PivotAggregateTypes =
    [
        AggregateType.Sum,
        AggregateType.Count,
        AggregateType.Average,
        AggregateType.Min,
        AggregateType.Max
    ];

    private IEnumerable<TextFilterOperatorChoice> TextFilterOperatorChoices =>
        TextFilterOperators.Select(op => new TextFilterOperatorChoice(op, GetTextFilterOperatorLabel(op)));

    private bool _pivotMode;
    private readonly List<string> _pivotRowFields = new();
    private readonly List<string> _pivotColumnFields = new();
    private readonly List<PivotValueConfig> _pivotValueFields = new();

    private bool AdvancedViewEnabled =>
        _advancedViewInitialized ? _advancedViewEnabled : DefaultAdvancedView;

    private bool ShouldShowAdvancedTools =>
        !ShowAdvancedViewToggleButton || AdvancedViewEnabled;

    private bool HasGridOptionsRailItems =>
        Columns.Count > 0
        && (ShowColumnOptionsButton || (AllowPivoting && ShowPivotPanelButton) || ShowGridThemeToggle || ShowGridBackButton);

    private bool HasAdvancedGridOptions =>
        (ShowGridOptionsRail && HasGridOptionsRailItems)
        || (AllowFiltering && ShowExpressionFilterButton);

    private bool ShouldRenderAdvancedViewToggle =>
        ShowAdvancedViewToggleButton && HasAdvancedGridOptions;

    private bool ShouldRenderExpressionFilterButton =>
        AllowFiltering && ShowExpressionFilterButton && ShouldShowAdvancedTools;

    private bool ShouldRenderGridOptionsRail =>
        ShowGridOptionsRail
        && ShouldShowAdvancedTools
        && HasGridOptionsRailItems;

    private string AdvancedViewToggleTitle =>
        AdvancedViewEnabled ? "Switch to normal grid view" : "Switch to advanced grid view";

    private string AdvancedViewTogglePressed =>
        AdvancedViewEnabled ? "true" : "false";

    private string GridHostCssClass
    {
        get
        {
            var classes = new List<string> { "fx-grid" };
            classes.Add(GridLines switch
            {
                GridLines.Default => "fx-grid-lines-both",
                GridLines.Both => "fx-grid-lines-both",
                GridLines.None => "fx-grid-lines-none",
                GridLines.Horizontal => "fx-grid-lines-horizontal",
                GridLines.Vertical => "fx-grid-lines-vertical",
                _ => ""
            });

            if (ShouldRenderGridOptionsRail)
                classes.Add("fx-grid-options-rail-on");
            if (ShouldRenderGridOptionsRail && _activeOptionsPanel != GridOptionsPanel.None)
                classes.Add("fx-grid-options-panel-open");
            if (ActiveGridThemeIsDark)
                classes.Add("fx-grid-dark");
            classes.Add(ActiveGridThemeCssClass);
            if (BatchEditBehavior == GridBatchEditBehavior.SingleCell)
                classes.Add("fx-grid-single-cell-batch");
            if (_isDragSelecting || _isCellDragSelecting)
                classes.Add("fx-grid-drag-selecting");
            if (IsPagingActive)
                classes.Add("fx-grid-paged");
            if (WidthMode == GridWidthMode.FitColumns && string.IsNullOrWhiteSpace(Width))
                classes.Add("fx-grid-width-fit-columns");
            if (!string.IsNullOrWhiteSpace(Height))
                classes.Add("fx-grid-has-height");
            if (ExtendVerticalScrollbarIntoHeader)
                classes.Add("fx-grid-vscroll-header-gutter");
            if (ShouldHideGridContentForNoVisibleColumns)
                classes.Add("fx-grid-no-visible-columns");
            if (_pivotMode)
                classes.Add("fx-grid-pivot-mode");
            if (!string.IsNullOrWhiteSpace(CssClass))
                classes.Add(CssClass.Trim());

            return string.Join(" ", classes.Where(c => !string.IsNullOrWhiteSpace(c)));
        }
    }

    private string ActiveOptionsPanelTitle => _activeOptionsPanel switch
    {
        GridOptionsPanel.Columns => "Column Options",
        GridOptionsPanel.Pivot => "Pivot Mode",
        GridOptionsPanel.Theme => "Theme",
        _ => ""
    };

    private bool ActiveGridThemeIsDark =>
        GetThemeGalleryItem(_activeGridTheme)?.Dark ?? false;

    private string ActiveGridThemeCssClass => _activeGridTheme switch
    {
        GridTheme.Vb6Windows => "fx-grid-theme-vb6-windows",
        GridTheme.ExcelLightBlue => "fx-grid-theme-excel-light-blue",
        GridTheme.ExcelLightGreen => "fx-grid-theme-excel-light-green",
        GridTheme.ExcelLightOrange => "fx-grid-theme-excel-light-orange",
        GridTheme.ExcelMediumBlue => "fx-grid-theme-excel-medium-blue",
        GridTheme.ExcelMediumGreen => "fx-grid-theme-excel-medium-green",
        GridTheme.ExcelDarkSlate => "fx-grid-theme-excel-dark-slate",
        _ => "fx-grid-theme-default"
    };

    private static IEnumerable<IGrouping<string, GridThemeGalleryItem>> ThemeGalleryGroups =>
        GridThemeGallery.GroupBy(t => t.Category);

    private static GridThemeGalleryItem? GetThemeGalleryItem(GridTheme theme) =>
        GridThemeGallery.FirstOrDefault(t => t.Theme == theme);

    private static string ThemePreviewStyle(GridThemeGalleryItem item) =>
        string.Create(CultureInfo.InvariantCulture,
            $"--fx-theme-header:{item.Header};--fx-theme-body:{item.Body};--fx-theme-alt:{item.Alt};--fx-theme-accent:{item.Accent};");

    private IEnumerable<GridColumn> ColumnPanelColumns =>
        Columns
            .Where(c => !string.IsNullOrWhiteSpace(c.Field))
            .Where(c => string.IsNullOrWhiteSpace(_columnPanelSearch)
                || HeaderColumnDisplay(c).Contains(_columnPanelSearch, StringComparison.OrdinalIgnoreCase)
                || c.Field.Contains(_columnPanelSearch, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<GridColumn> PivotPanelColumns =>
        Columns
            .Where(c => !string.IsNullOrWhiteSpace(c.Field))
            .Where(c => string.IsNullOrWhiteSpace(_pivotFieldSearch)
                || HeaderColumnDisplay(c).Contains(_pivotFieldSearch, StringComparison.OrdinalIgnoreCase)
                || c.Field.Contains(_pivotFieldSearch, StringComparison.OrdinalIgnoreCase));

    private Dictionary<string, string> PivotFieldLabels =>
        Columns
            .Where(c => !string.IsNullOrWhiteSpace(c.Field))
            .GroupBy(c => c.Field, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => HeaderColumnDisplay(g.First()), StringComparer.OrdinalIgnoreCase);

    private string GetPivotFieldLabel(string field)
    {
        var col = Columns.FirstOrDefault(c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
        return col == null ? field : HeaderColumnDisplay(col);
    }

    private string ResolvedPivotHeight =>
        string.IsNullOrWhiteSpace(Height) ? "420px" : Height;

    private void ToggleOptionsPanel(GridOptionsPanel panel)
    {
        _activeOptionsPanel = _activeOptionsPanel == panel ? GridOptionsPanel.None : panel;
        if (panel == GridOptionsPanel.Pivot && _activeOptionsPanel == GridOptionsPanel.Pivot)
            EnsurePivotSeeded();
    }

    private void EnsureAdvancedViewInitialized()
    {
        if (_advancedViewInitialized)
            return;

        _advancedViewEnabled = DefaultAdvancedView;
        _advancedViewInitialized = true;
    }

    private void ToggleAdvancedView()
    {
        SetAdvancedView(!AdvancedViewEnabled);
    }

    private void SetAdvancedView(bool enabled)
    {
        EnsureAdvancedViewInitialized();
        _advancedViewEnabled = enabled;

        if (enabled)
            return;

        _activeOptionsPanel = GridOptionsPanel.None;
        _expressionFilterOpen = false;
        _pivotMode = false;
    }

    private void CloseOptionsPanel()
    {
        _activeOptionsPanel = GridOptionsPanel.None;
    }

    private void ReturnToGridView()
    {
        _pivotMode = false;
        _activeOptionsPanel = GridOptionsPanel.None;
    }

    private void ToggleGridDarkMode()
    {
        ToggleOptionsPanel(GridOptionsPanel.Theme);
    }

    private void EnsureThemeInitialized()
    {
        if (_themeInitialized)
            return;

        _activeGridTheme = Theme;
        _themeInitialized = true;
    }

    private void SelectGridTheme(GridTheme theme)
    {
        _activeGridTheme = theme;
        _themeInitialized = true;
        _activeOptionsPanel = GridOptionsPanel.None;
    }

    private sealed record GridThemeGalleryItem(
        GridTheme Theme,
        string Name,
        string Category,
        string Header,
        string Body,
        string Alt,
        string Accent,
        bool Dark = false);

    private async Task SetColumnPanelVisibleAsync(GridColumn col, bool visible)
    {
        if (string.IsNullOrWhiteSpace(col.Field))
            return;

        if (!visible && IsColumnVisible(col) && !CanHideColumn(col))
            return;

        _visibilityOverrides[col.Field] = visible;

        if (OnColumnsChosen.HasDelegate)
        {
            var renderedColumnsByField = Columns
                .Where(c => !string.IsNullOrWhiteSpace(c.Field))
                .GroupBy(c => c.Field, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var snapshot = (AvailableColumns != null
                    ? AvailableColumns
                        .Where(c => !string.IsNullOrWhiteSpace(c.Field))
                        .Select(c =>
                        {
                            var field = c.Field;
                            var rendered = renderedColumnsByField.GetValueOrDefault(field);
                            return new ChooseColumnDescriptor
                            {
                                Field = field,
                                Header = !string.IsNullOrWhiteSpace(c.Header)
                                    ? c.Header
                                    : (rendered != null ? HeaderColumnDisplay(rendered) : field),
                                Visible = string.Equals(field, col.Field, StringComparison.Ordinal)
                                    ? visible
                                    : (rendered != null ? IsColumnVisible(rendered) : c.Visible)
                            };
                        })
                    : Columns
                        .Where(c => !string.IsNullOrWhiteSpace(c.Field))
                        .Select(c => new ChooseColumnDescriptor
                        {
                            Field = c.Field,
                            Header = HeaderColumnDisplay(c),
                            Visible = string.Equals(c.Field, col.Field, StringComparison.Ordinal)
                                ? visible
                                : IsColumnVisible(c)
                        }))
                .ToList();

            await OnColumnsChosen.InvokeAsync(new ChooseColumnsResult { Columns = snapshot });
            await SaveSnapshotSettingsAsync(snapshot);
        }
        else
        {
            await SaveGridSettingsAsync();
        }

        await FireLayoutChangedAsync();
        await InvokeAsync(StateHasChanged);
    }

    private void SeedFilterPopupDraft(string field)
    {
        var state = GetColumnState(field);
        _filterTextDraft = state.FilterValue ?? "";
        _filterOperatorDraft = _filterOperatorDraftsByField.TryGetValue(field, out var cachedOperator)
            ? cachedOperator
            : state.FilterOperator;
        IEnumerable<string> checkedValues = state.UseCheckedFilter
            ? state.CheckedFilterValues
            : GetDistinctValues(field);
        _filterCheckedDraft = new HashSet<string>(checkedValues, StringComparer.Ordinal);
    }

    private async Task OnTextFilterOperatorChanged(ChangeEventArgs e)
    {
        if (!Enum.TryParse<TextFilterOperator>(e.Value?.ToString(), out var parsed))
            parsed = TextFilterOperator.Contains;

        _filterOperatorDraft = parsed;
        if (_filterPopupField != null)
            _filterOperatorDraftsByField[_filterPopupField] = parsed;
        QueueFilterPopupFocus(FilterPopupFocusTarget.ConditionInput);
        if (_filterPopupAutoApply && _filterPopupField != null)
            await ApplyFilterPopupAsync(close: false);
    }

    private async Task OnTextFilterOperatorValueChanged(TextFilterOperator filterOperator)
    {
        _filterOperatorDraft = filterOperator;
        if (_filterPopupField != null)
            _filterOperatorDraftsByField[_filterPopupField] = filterOperator;
        QueueFilterPopupFocus(FilterPopupFocusTarget.ConditionInput);
        if (_filterPopupAutoApply && _filterPopupField != null)
            await ApplyFilterPopupAsync(close: false);
    }

    private async Task OnTextFilterInput(ChangeEventArgs e)
    {
        _filterTextDraft = e.Value?.ToString() ?? "";
        if (_filterPopupField != null)
            _filterOperatorDraftsByField[_filterPopupField] = _filterOperatorDraft;
        QueueFilterPopupFocus(FilterPopupFocusTarget.ConditionInput);
        if (_filterPopupAutoApply && _filterPopupField != null)
            await ApplyFilterPopupAsync(close: false);
    }

    private async Task OnFilterPopupAutoApplyChanged(ChangeEventArgs e)
    {
        _filterPopupAutoApply = e.Value is bool value && value;
        if (_filterPopupAutoApply && _filterPopupField != null)
            await ApplyFilterPopupAsync(close: false);
    }

    private async Task ApplyFilterPopupAsync(bool close)
    {
        var field = _filterPopupField;
        if (field == null)
            return;

        await ApplyFilter(field, _filterTextDraft, _filterOperatorDraft);
        CommitCheckedFilterDraft(field);

        if (close)
            CloseFilterPopup();
    }

    private void CommitCheckedFilterDraft(string field)
    {
        var state = GetColumnState(field);
        var all = GetDistinctValues(field);
        state.CheckedFilterValues.Clear();
        foreach (var value in _filterCheckedDraft)
            state.CheckedFilterValues.Add(value);

        state.UseCheckedFilter = _filterCheckedDraft.Count < all.Count || all.Count == 0;
        if (!state.UseCheckedFilter)
            state.CheckedFilterValues.Clear();

        state.CheckedNumericRangeKeys.Clear();
        state.UseNumericRangeFilter = false;
        state.NumericFilterMin = null;
        state.NumericFilterMax = null;
        state.UseNumericBoundsFilter = false;
        _pageState.CurrentPage = 1;
    }

    private static string GetTextFilterOperatorLabel(TextFilterOperator filterOperator) => filterOperator switch
    {
        TextFilterOperator.Equals => "Equals",
        TextFilterOperator.DoesNotEqual => "Does Not Equal",
        TextFilterOperator.BeginsWith => "Begins With",
        TextFilterOperator.DoesNotBeginWith => "Does Not Begin With",
        TextFilterOperator.EndsWith => "Ends With",
        TextFilterOperator.DoesNotEndWith => "Does Not End With",
        TextFilterOperator.Contains => "Contains",
        TextFilterOperator.DoesNotContain => "Does Not Contain",
        _ => "Choose One"
    };

    private static bool PassesTextFilter(string actual, string expected, TextFilterOperator filterOperator)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        return filterOperator switch
        {
            TextFilterOperator.Equals => string.Equals(actual, expected, comparison),
            TextFilterOperator.DoesNotEqual => !string.Equals(actual, expected, comparison),
            TextFilterOperator.BeginsWith => actual.StartsWith(expected, comparison),
            TextFilterOperator.DoesNotBeginWith => !actual.StartsWith(expected, comparison),
            TextFilterOperator.EndsWith => actual.EndsWith(expected, comparison),
            TextFilterOperator.DoesNotEndWith => !actual.EndsWith(expected, comparison),
            TextFilterOperator.DoesNotContain => !actual.Contains(expected, comparison),
            TextFilterOperator.ChooseOne or TextFilterOperator.Contains or _ => actual.Contains(expected, comparison)
        };
    }

    private bool IsCurrentFilterPopupField(string field) =>
        string.Equals(_filterPopupField, field, StringComparison.Ordinal);

    private bool IsNumericFilterColumn(GridColumn? col) =>
        col?.Type == ColumnType.Number;

    private IReadOnlyList<FilterValueCandidate> GetColumnFilterValueCandidates(string field)
    {
        return GetDistinctFilterValueCandidates(field)
            .Where(v => MatchesPopupTextFilter(field, v))
            .ToList();
    }

    private bool MatchesPopupTextFilter(string field, FilterValueCandidate candidate)
    {
        if (!IsCurrentFilterPopupField(field) || string.IsNullOrWhiteSpace(_filterTextDraft))
            return true;

        return PassesDisplayAwareTextFilter(candidate.Value, candidate.DisplayText, _filterTextDraft, _filterOperatorDraft);
    }

    private IReadOnlyList<FilterValueCandidate> GetDistinctFilterValueCandidates(string field)
    {
        var col = FindColumnByField(field);
        var candidates = new Dictionary<string, FilterValueCandidate>(StringComparer.Ordinal);

        foreach (var item in DataSource ?? Enumerable.Empty<TValue>())
        {
            var rawValue = GetFilterRawValue(item, field)?.ToString() ?? "";
            var displayText = GetFilterDisplayText(item, col, rawValue);
            var candidate = new FilterValueCandidate(
                rawValue,
                string.IsNullOrEmpty(displayText) ? "(blank)" : displayText);

            if (!candidates.TryGetValue(rawValue, out var existing)
                || IsBetterFilterCandidate(candidate, existing))
            {
                candidates[rawValue] = candidate;
            }
        }

        return candidates.Values
            .OrderBy(v => v.DisplayText, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(v => v.Value, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private string GetFilterDisplayText(TValue item, GridColumn? col, string rawValue)
    {
        if (col == null)
            return string.IsNullOrEmpty(rawValue) ? "" : rawValue;

        var displayText = GetCellDisplayValue(item, col);
        return string.IsNullOrWhiteSpace(displayText) ? rawValue : displayText;
    }

    private static string CombineFilterSearchText(string rawValue, string displayText)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return displayText;
        if (string.IsNullOrWhiteSpace(displayText)
            || string.Equals(rawValue, displayText, StringComparison.OrdinalIgnoreCase))
            return rawValue;

        return $"{displayText} {rawValue}";
    }

    private static bool PassesDisplayAwareTextFilter(string rawValue, string displayText, string expected, TextFilterOperator filterOperator)
    {
        displayText = string.IsNullOrWhiteSpace(displayText) ? rawValue : displayText;
        if (string.Equals(rawValue, displayText, StringComparison.Ordinal))
            return PassesTextFilter(displayText, expected, filterOperator);

        return filterOperator switch
        {
            TextFilterOperator.DoesNotEqual =>
                PassesTextFilter(rawValue, expected, filterOperator)
                && PassesTextFilter(displayText, expected, filterOperator),
            TextFilterOperator.DoesNotBeginWith =>
                PassesTextFilter(rawValue, expected, filterOperator)
                && PassesTextFilter(displayText, expected, filterOperator),
            TextFilterOperator.DoesNotEndWith =>
                PassesTextFilter(rawValue, expected, filterOperator)
                && PassesTextFilter(displayText, expected, filterOperator),
            TextFilterOperator.DoesNotContain =>
                PassesTextFilter(rawValue, expected, filterOperator)
                && PassesTextFilter(displayText, expected, filterOperator),
            _ =>
                PassesTextFilter(rawValue, expected, filterOperator)
                || PassesTextFilter(displayText, expected, filterOperator)
        };
    }

    private static bool IsBetterFilterCandidate(FilterValueCandidate candidate, FilterValueCandidate existing)
    {
        if (string.IsNullOrWhiteSpace(existing.DisplayText) || existing.DisplayText == "(blank)")
            return true;
        if (string.Equals(existing.DisplayText, existing.Value, StringComparison.Ordinal)
            && !string.Equals(candidate.DisplayText, candidate.Value, StringComparison.Ordinal))
            return true;

        return false;
    }

    private bool IsFilterValueChecked(string field, string value)
    {
        if (IsCurrentFilterPopupField(field))
            return _filterCheckedDraft.Contains(value);

        var state = GetColumnState(field);
        return !state.UseCheckedFilter || state.CheckedFilterValues.Contains(value);
    }

    private bool IsFilterFieldFullySelected(string field, IReadOnlyList<string>? values = null)
    {
        if (IsCurrentFilterPopupField(field))
        {
            var draftCandidates = values ?? GetDistinctValues(field);
            return draftCandidates.Count > 0 && draftCandidates.All(_filterCheckedDraft.Contains);
        }

        var state = GetColumnState(field);
        if (!state.UseCheckedFilter)
            return true;

        var candidates = values ?? GetDistinctValues(field);
        return candidates.Count > 0 && candidates.All(state.CheckedFilterValues.Contains);
    }

    private void SetFilterFieldSelected(string field, bool selected, IReadOnlyList<string>? values = null)
    {
        if (IsCurrentFilterPopupField(field))
        {
            var draftCandidates = values ?? GetDistinctValues(field);
            if (selected)
            {
                foreach (var candidate in draftCandidates)
                    _filterCheckedDraft.Add(candidate);
            }
            else
            {
                foreach (var candidate in draftCandidates)
                    _filterCheckedDraft.Remove(candidate);
            }

            if (_filterPopupAutoApply)
                CommitCheckedFilterDraft(field);
            return;
        }

        var state = GetColumnState(field);
        var candidates = values ?? GetDistinctValues(field);

        if (!state.UseCheckedFilter)
        {
            state.CheckedFilterValues.Clear();
            foreach (var candidate in GetDistinctValues(field))
                state.CheckedFilterValues.Add(candidate);
            state.UseCheckedFilter = true;
        }

        if (selected)
        {
            foreach (var candidate in candidates)
                state.CheckedFilterValues.Add(candidate);

            var all = GetDistinctValues(field);
            if (all.Count > 0 && all.All(state.CheckedFilterValues.Contains))
            {
                state.CheckedFilterValues.Clear();
                state.UseCheckedFilter = false;
            }
        }
        else
        {
            foreach (var candidate in candidates)
                state.CheckedFilterValues.Remove(candidate);
            state.UseCheckedFilter = true;
        }

        _pageState.CurrentPage = 1;
    }

    private void SetFilterValueChecked(string field, string value, bool selected)
    {
        if (IsCurrentFilterPopupField(field))
        {
            if (selected)
                _filterCheckedDraft.Add(value);
            else
                _filterCheckedDraft.Remove(value);

            if (_filterPopupAutoApply)
                CommitCheckedFilterDraft(field);
            return;
        }

        var state = GetColumnState(field);
        if (!state.UseCheckedFilter)
        {
            state.CheckedFilterValues.Clear();
            foreach (var candidate in GetDistinctValues(field))
                state.CheckedFilterValues.Add(candidate);
            state.UseCheckedFilter = true;
        }

        if (selected)
            state.CheckedFilterValues.Add(value);
        else
            state.CheckedFilterValues.Remove(value);

        var all = GetDistinctValues(field);
        if (all.Count > 0 && all.All(state.CheckedFilterValues.Contains))
        {
            state.CheckedFilterValues.Clear();
            state.UseCheckedFilter = false;
        }

        _pageState.CurrentPage = 1;
    }

    private int GetSelectedFilterValueCount(string field)
    {
        var all = GetDistinctValues(field);
        if (IsCurrentFilterPopupField(field))
            return all.Count(_filterCheckedDraft.Contains);

        var state = GetColumnState(field);
        if (!state.UseCheckedFilter)
            return all.Count;

        return all.Count(state.CheckedFilterValues.Contains);
    }

    private string GetNumericFilterMinText(string field)
    {
        if (_numericFilterMinText.TryGetValue(field, out var value))
            return value;

        var min = GetColumnState(field).NumericFilterMin;
        return min.HasValue ? FormatNumericFilterInputValue(min.Value) : "";
    }

    private string GetNumericFilterMaxText(string field)
    {
        if (_numericFilterMaxText.TryGetValue(field, out var value))
            return value;

        var max = GetColumnState(field).NumericFilterMax;
        return max.HasValue ? FormatNumericFilterInputValue(max.Value) : "";
    }

    private void SetNumericFilterMinText(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            _numericFilterMinText.Remove(field);
        else
            _numericFilterMinText[field] = value;
    }

    private void SetNumericFilterMaxText(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            _numericFilterMaxText.Remove(field);
        else
            _numericFilterMaxText[field] = value;
    }

    private void ApplyNumericBoundsFilter(string field)
    {
        var minText = GetNumericFilterMinText(field);
        var maxText = GetNumericFilterMaxText(field);
        if (!TryParseOptionalDecimal(minText, out var min) || !TryParseOptionalDecimal(maxText, out var max))
            return;

        if (min.HasValue && max.HasValue && min.Value > max.Value)
            (min, max) = (max, min);

        var state = GetColumnState(field);
        state.NumericFilterMin = min;
        state.NumericFilterMax = max;
        state.UseNumericBoundsFilter = min.HasValue || max.HasValue;
        state.UseNumericRangeFilter = false;
        state.CheckedNumericRangeKeys.Clear();

        if (min.HasValue)
            _numericFilterMinText[field] = FormatNumericFilterInputValue(min.Value);
        else
            _numericFilterMinText.Remove(field);

        if (max.HasValue)
            _numericFilterMaxText[field] = FormatNumericFilterInputValue(max.Value);
        else
            _numericFilterMaxText.Remove(field);

        _pageState.CurrentPage = 1;
    }

    private IReadOnlyList<NumericFilterRange> GetNumericFilterRanges(string field)
    {
        var stats = GetNumericFilterStats(field);
        var ranges = new List<NumericFilterRange>();

        if (stats.NumericCount > 0)
        {
            if (stats.ExactCounts != null)
            {
                ranges.AddRange(stats.ExactCounts
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => NumericFilterRange.Exact(kvp.Key, kvp.Value, FormatNumericFilterDisplayValue(kvp.Key))));
            }
            else if (stats.Min.HasValue && stats.Max.HasValue)
            {
                ranges.AddRange(BuildNumericBucketRanges(field, stats.Min.Value, stats.Max.Value, stats.NumericCount));
            }
        }

        if (stats.BlankCount > 0)
            ranges.Add(NumericFilterRange.Blank(stats.BlankCount));

        return ranges;
    }

    private NumericFilterStats GetNumericFilterStats(string field)
    {
        const int maxExactNumericFilterValues = 10;
        var stats = new NumericFilterStats();
        Dictionary<decimal, int>? exactCounts = new();

        foreach (var item in DataSource ?? Enumerable.Empty<TValue>())
        {
            var raw = GetFilterRawValue(item, field);
            if (!TryConvertToDecimal(raw, out var number))
            {
                stats.BlankCount++;
                continue;
            }

            stats.NumericCount++;
            stats.Min = !stats.Min.HasValue || number < stats.Min.Value ? number : stats.Min;
            stats.Max = !stats.Max.HasValue || number > stats.Max.Value ? number : stats.Max;

            if (exactCounts == null)
                continue;

            exactCounts[number] = exactCounts.TryGetValue(number, out var count) ? count + 1 : 1;
            if (exactCounts.Count > maxExactNumericFilterValues)
                exactCounts = null;
        }

        stats.ExactCounts = exactCounts;
        return stats;
    }

    private IReadOnlyList<NumericFilterRange> BuildNumericBucketRanges(string field, decimal min, decimal max, int numericCount)
    {
        const int maxNumericFilterBuckets = 10;
        if (numericCount <= 0)
            return Array.Empty<NumericFilterRange>();

        if (min == max)
            return new[] { NumericFilterRange.Exact(min, numericCount, FormatNumericFilterDisplayValue(min)) };

        var bucketCount = Math.Min(maxNumericFilterBuckets, Math.Max(2, (int)Math.Ceiling(Math.Sqrt(numericCount))));
        var width = (max - min) / bucketCount;
        if (width <= 0)
            return new[] { NumericFilterRange.Exact(min, numericCount, FormatNumericFilterDisplayValue(min)) };

        var counts = new int[bucketCount];
        foreach (var item in DataSource ?? Enumerable.Empty<TValue>())
        {
            if (!TryConvertToDecimal(GetFilterRawValue(item, field), out var number))
                continue;

            var index = GetNumericBucketIndex(number, min, width, bucketCount);
            counts[index]++;
        }

        var ranges = new List<NumericFilterRange>(bucketCount);
        for (var i = 0; i < bucketCount; i++)
        {
            var lower = min + (width * i);
            var upper = i == bucketCount - 1 ? max : min + (width * (i + 1));
            if (counts[i] == 0)
                continue;

            ranges.Add(NumericFilterRange.Range(
                key: CreateNumericRangeKey(i, lower, upper),
                min: lower,
                max: upper,
                includeMax: i == bucketCount - 1,
                count: counts[i],
                label: $"{FormatNumericFilterDisplayValue(lower)} - {FormatNumericFilterDisplayValue(upper)}"));
        }

        return ranges;
    }

    private static int GetNumericBucketIndex(decimal number, decimal min, decimal width, int bucketCount)
    {
        var index = (int)Math.Floor((double)((number - min) / width));
        if (index < 0)
            return 0;
        if (index >= bucketCount)
            return bucketCount - 1;
        return index;
    }

    private static string CreateNumericRangeKey(int index, decimal min, decimal max) =>
        string.Create(CultureInfo.InvariantCulture, $"range:{index}:{min:G29}:{max:G29}");

    private string GetNumericFilterSummary(string field, IReadOnlyList<NumericFilterRange> ranges, int selectedRangeCount)
    {
        var state = GetColumnState(field);
        if (state.UseNumericBoundsFilter)
        {
            var minText = state.NumericFilterMin.HasValue ? FormatNumericFilterDisplayValue(state.NumericFilterMin.Value) : "any";
            var maxText = state.NumericFilterMax.HasValue ? FormatNumericFilterDisplayValue(state.NumericFilterMax.Value) : "any";
            return $"Custom range: {minText} to {maxText}";
        }

        if (state.UseNumericRangeFilter)
            return $"{selectedRangeCount} of {ranges.Count} ranges selected";

        return ranges.Count == 1 ? "1 range available" : $"{ranges.Count} ranges available";
    }

    private int GetSelectedNumericRangeCount(string field, IReadOnlyList<NumericFilterRange> ranges)
    {
        var state = GetColumnState(field);
        if (!state.UseNumericRangeFilter)
            return ranges.Count;

        return ranges.Count(r => state.CheckedNumericRangeKeys.Contains(r.Key));
    }

    private bool IsNumericRangeChecked(string field, NumericFilterRange range)
    {
        var state = GetColumnState(field);
        return !state.UseNumericRangeFilter || state.CheckedNumericRangeKeys.Contains(range.Key);
    }

    private void SetNumericRangeChecked(string field, NumericFilterRange range, bool selected)
    {
        var state = GetColumnState(field);
        var ranges = GetNumericFilterRanges(field);
        if (ranges.Count == 0)
            return;

        if (!state.UseNumericRangeFilter)
        {
            state.CheckedNumericRangeKeys.Clear();
            foreach (var candidate in ranges)
                state.CheckedNumericRangeKeys.Add(candidate.Key);
            state.UseNumericRangeFilter = true;
        }

        if (selected)
            state.CheckedNumericRangeKeys.Add(range.Key);
        else
            state.CheckedNumericRangeKeys.Remove(range.Key);

        if (ranges.All(candidate => state.CheckedNumericRangeKeys.Contains(candidate.Key)))
        {
            state.CheckedNumericRangeKeys.Clear();
            state.UseNumericRangeFilter = false;
        }

        state.NumericFilterMin = null;
        state.NumericFilterMax = null;
        state.UseNumericBoundsFilter = false;
        _numericFilterMinText.Remove(field);
        _numericFilterMaxText.Remove(field);
        _pageState.CurrentPage = 1;
    }

    private bool PassesNumericFilter(string field, object? raw, ColumnState state, IReadOnlyList<NumericFilterRange> ranges)
    {
        if (!TryConvertToDecimal(raw, out var number))
        {
            return state.UseNumericRangeFilter
                && !state.UseNumericBoundsFilter
                && state.CheckedNumericRangeKeys.Contains(NumericFilterRange.BlankKey);
        }

        if (state.UseNumericBoundsFilter)
        {
            if (state.NumericFilterMin.HasValue && number < state.NumericFilterMin.Value)
                return false;
            if (state.NumericFilterMax.HasValue && number > state.NumericFilterMax.Value)
                return false;
        }

        if (state.UseNumericRangeFilter)
        {
            var matchedRange = ranges.FirstOrDefault(r => !r.IsBlank && r.Contains(number));
            return matchedRange != null && state.CheckedNumericRangeKeys.Contains(matchedRange.Key);
        }

        return true;
    }

    private object? GetFilterRawValue(TValue item, string field)
    {
        var col = FindColumnByField(field);
        return col == null ? GetPropertyValue(item, field) : ResolveCellValue(item, col);
    }

    private static bool TryParseOptionalDecimal(string? text, out decimal? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return true;

        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out var current)
            || decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out current))
        {
            value = current;
            return true;
        }

        return false;
    }

    private static bool TryConvertToDecimal(object? value, out decimal number)
    {
        number = 0;
        if (value == null || value == DBNull.Value)
            return false;

        switch (value)
        {
            case decimal decimalValue:
                number = decimalValue;
                return true;
            case double doubleValue when !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue):
                number = (decimal)doubleValue;
                return true;
            case float floatValue when !float.IsNaN(floatValue) && !float.IsInfinity(floatValue):
                number = (decimal)floatValue;
                return true;
            case string text:
                text = text.Trim();
                return text.Length > 0
                    && (decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out number)
                        || decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out number));
        }

        if (value is IConvertible convertible)
        {
            try
            {
                number = convertible.ToDecimal(CultureInfo.InvariantCulture);
                return true;
            }
            catch (FormatException) { }
            catch (InvalidCastException) { }
            catch (OverflowException) { }
        }

        return false;
    }

    private static string FormatNumericFilterInputValue(decimal value) =>
        value.ToString("G29", CultureInfo.InvariantCulture);

    private static string FormatNumericFilterDisplayValue(decimal value)
    {
        var rounded = Math.Round(value, 2);
        return rounded == decimal.Truncate(rounded)
            ? rounded.ToString("N0", CultureInfo.CurrentCulture)
            : rounded.ToString("N2", CultureInfo.CurrentCulture);
    }

    private sealed class NumericFilterStats
    {
        public int NumericCount { get; set; }
        public int BlankCount { get; set; }
        public decimal? Min { get; set; }
        public decimal? Max { get; set; }
        public Dictionary<decimal, int>? ExactCounts { get; set; }
    }

    private sealed class NumericFilterRange
    {
        public const string BlankKey = "__blank__";

        public string Key { get; init; } = "";
        public decimal Min { get; init; }
        public decimal Max { get; init; }
        public bool IncludeMax { get; init; }
        public int Count { get; init; }
        public string Label { get; init; } = "";
        public bool IsBlank { get; init; }
        public string CountLabel => Count == 1 ? "1 item" : $"{Count:N0} items";

        public bool Contains(decimal value) =>
            value >= Min && (IncludeMax ? value <= Max : value < Max);

        public static NumericFilterRange Exact(decimal value, int count, string label) =>
            new()
            {
                Key = string.Create(CultureInfo.InvariantCulture, $"value:{value:G29}"),
                Min = value,
                Max = value,
                IncludeMax = true,
                Count = count,
                Label = label
            };

        public static NumericFilterRange Range(string key, decimal min, decimal max, bool includeMax, int count, string label) =>
            new()
            {
                Key = key,
                Min = min,
                Max = max,
                IncludeMax = includeMax,
                Count = count,
                Label = label
            };

        public static NumericFilterRange Blank(int count) =>
            new()
            {
                Key = BlankKey,
                Count = count,
                Label = "(blank)",
                IsBlank = true
            };
    }

    private void SetPivotMode(bool enabled)
    {
        if (enabled)
        {
            EnsurePivotSeeded();
            _activeOptionsPanel = GridOptionsPanel.Pivot;
        }

        _pivotMode = enabled;
        _pageState.CurrentPage = 1;
    }

    private void EnsurePivotSeeded()
    {
        if (_pivotRowFields.Count == 0)
        {
            foreach (var group in _groupDescriptors)
            {
                if (Columns.Any(c => string.Equals(c.Field, group.Field, StringComparison.OrdinalIgnoreCase)))
                    _pivotRowFields.Add(group.Field);
            }
        }

        if (_pivotRowFields.Count == 0)
        {
            var firstText = VisibleColumns.FirstOrDefault(c => !IsPivotNumericField(c));
            if (firstText != null)
                _pivotRowFields.Add(firstText.Field);
        }

        if (_pivotValueFields.Count == 0)
        {
            var numeric = VisibleColumns.FirstOrDefault(IsPivotNumericField);
            var valueCol = numeric ?? VisibleColumns.FirstOrDefault();
            if (valueCol != null)
            {
                var aggregate = numeric == null ? AggregateType.Count : AggregateType.Sum;
                _pivotValueFields.Add(new PivotValueConfig
                {
                    Field = valueCol.Field,
                    Label = $"{aggregate} of {HeaderColumnDisplay(valueCol)}",
                    Aggregation = aggregate,
                    Format = valueCol.Format ?? string.Empty
                });
            }
        }
    }

    private bool IsPivotNumericField(GridColumn col)
    {
        if (col.Type == ColumnType.Number)
            return true;

        var sample = (DataSource ?? Enumerable.Empty<TValue>())
            .Take(30)
            .Select(item => GetPropertyValue(item, col.Field))
            .Where(v => v != null && v != DBNull.Value)
            .Take(10)
            .ToList();

        return sample.Count > 0 && sample.All(v => double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out _));
    }

    private bool IsPivotFieldInArea(string field, PivotFieldArea area)
    {
        return area switch
        {
            PivotFieldArea.Row => _pivotRowFields.Contains(field),
            PivotFieldArea.Column => _pivotColumnFields.Contains(field),
            PivotFieldArea.Value => _pivotValueFields.Any(v => string.Equals(v.Field, field, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private void AddPivotField(string field, PivotFieldArea area)
    {
        if (string.IsNullOrWhiteSpace(field))
            return;

        RemovePivotFieldEverywhere(field);

        switch (area)
        {
            case PivotFieldArea.Row:
                _pivotRowFields.Add(field);
                break;
            case PivotFieldArea.Column:
                _pivotColumnFields.Add(field);
                break;
            case PivotFieldArea.Value:
                var col = Columns.FirstOrDefault(c => string.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase));
                var aggregate = col != null && IsPivotNumericField(col) ? AggregateType.Sum : AggregateType.Count;
                _pivotValueFields.Add(new PivotValueConfig
                {
                    Field = field,
                    Label = col == null ? null : $"{aggregate} of {HeaderColumnDisplay(col)}",
                    Aggregation = aggregate,
                    Format = col?.Format ?? string.Empty
                });
                break;
        }

        _pivotMode = true;
        _activeOptionsPanel = GridOptionsPanel.Pivot;
        _pageState.CurrentPage = 1;
    }

    private void RemovePivotField(string field, PivotFieldArea area)
    {
        switch (area)
        {
            case PivotFieldArea.Row:
                _pivotRowFields.Remove(field);
                break;
            case PivotFieldArea.Column:
                _pivotColumnFields.Remove(field);
                break;
            case PivotFieldArea.Value:
                _pivotValueFields.RemoveAll(v => string.Equals(v.Field, field, StringComparison.OrdinalIgnoreCase));
                break;
        }
    }

    private void RemovePivotFieldEverywhere(string field)
    {
        _pivotRowFields.Remove(field);
        _pivotColumnFields.Remove(field);
        _pivotValueFields.RemoveAll(v => string.Equals(v.Field, field, StringComparison.OrdinalIgnoreCase));
    }

    private void ChangePivotAggregation(PivotValueConfig config, ChangeEventArgs e)
    {
        if (Enum.TryParse<AggregateType>(e.Value?.ToString(), out var aggregate))
            ChangePivotAggregation(config, aggregate);
    }

    private void ChangePivotAggregation(PivotValueConfig config, AggregateType? aggregate)
    {
        if (aggregate.HasValue)
        {
            config.Aggregation = aggregate.Value;
            var col = Columns.FirstOrDefault(c => string.Equals(c.Field, config.Field, StringComparison.OrdinalIgnoreCase));
            config.Label = col == null ? null : $"{aggregate.Value} of {HeaderColumnDisplay(col)}";
        }
    }

    private sealed record TextFilterOperatorChoice(TextFilterOperator Value, string Text);

    private static void SetPivotValueFormat(PivotValueConfig config, ChangeEventArgs e)
    {
        config.Format = e.Value?.ToString() ?? string.Empty;
    }
}
