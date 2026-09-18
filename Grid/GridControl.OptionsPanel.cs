using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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

    private readonly record struct FilterValueCandidate(
        string Value,
        string DisplayText,
        int? Count = null);

    private string _columnPanelSearch = "";
    private string _pivotFieldSearch = "";

    private readonly Dictionary<string, string> _numericFilterMinText = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _numericFilterMaxText = new(StringComparer.Ordinal);
    private static readonly GridThemeGalleryItem[] GridThemeGallery =
    [
        new(GridTheme.Default, "Default", "Light", "#e9e9e9", "#ffffff", "#ffffff", "#9a9a9a"),
        new(GridTheme.Vb6Windows, "VB6 Windows", "Light", "#eeeeee", "#ffffff", "#ffffff", "#808080"),
        new(GridTheme.ExcelLightBlue, "Light Blue", "Light", "#d9eaf7", "#ffffff", "#edf7fd", "#5aa7d8"),
        new(GridTheme.ExcelLightGreen, "Light Green", "Light", "#e2f0d9", "#ffffff", "#f1f8eb", "#70ad47"),
        new(GridTheme.ExcelLightOrange, "Light Orange", "Light", "#fce4d6", "#ffffff", "#fff3ec", "#ed7d31"),
        new(GridTheme.ExcelMediumBlue, "Medium Blue", "Medium", "#1f4e79", "#ffffff", "#ddebf7", "#5b9bd5"),
        new(GridTheme.ExcelMediumGreen, "Medium Green", "Medium", "#548235", "#ffffff", "#e2f0d9", "#70ad47"),
        new(GridTheme.ExcelDarkSlate, "Dark Slate", "Dark", "#2f3542", "#111827", "#1f2937", "#64748b", true)
    ];
    private static readonly AggregateType[] PivotAggregateTypes =
    [
        AggregateType.Sum,
        AggregateType.Count,
        AggregateType.Average,
        AggregateType.Min,
        AggregateType.Max
    ];

    private IEnumerable<TextFilterOperatorChoice> GetFilterMenuOperatorChoices(GridColumn column) =>
        GetFilterRowOperators(column)
            .Concat([_filterOperatorDraft, _secondFilterOperatorDraft])
            .Distinct()
            .Select(filterOperator => new TextFilterOperatorChoice(
                filterOperator,
                GetTextFilterOperatorLabel(filterOperator)));

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

            if (Density == GridDensity.Compact)
                classes.Add("fx-grid-density-compact");

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
            if (!ScrollTrack && (UseRowWindowing || UseGroupedRowWindowing))
                classes.Add("fx-grid-scrolltrack-deferred");
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

    // The side-panel searches keep their text in the browser while typing and
    // narrow their list once per commit (Enter, Tab or leaving the box).
    private EventCallback<string?>? _columnPanelSearchCommitted;
    private EventCallback<string?> ColumnPanelSearchCommitted => SearchAsYouType ? default
        : _columnPanelSearchCommitted ??= NonRenderingEventHandler.Create<string?>(value =>
        {
            value ??= string.Empty;
            if (string.Equals(value, _columnPanelSearch, StringComparison.Ordinal))
                return;
            _columnPanelSearch = value;
            StateHasChanged();
        });

    private EventCallback<string?>? _pivotFieldSearchCommitted;
    private EventCallback<string?> PivotFieldSearchCommitted => SearchAsYouType ? default
        : _pivotFieldSearchCommitted ??= NonRenderingEventHandler.Create<string?>(value =>
        {
            value ??= string.Empty;
            if (string.Equals(value, _pivotFieldSearch, StringComparison.Ordinal))
                return;
            _pivotFieldSearch = value;
            StateHasChanged();
        });

    // Searching as you type the lists narrow once typing pauses (ImmediateModeDelay),
    // or at once on Enter, Tab or leaving the box; nothing renders per key.
    private CancellationTokenSource? _sidePanelSearchCts;
    private Action<string?>? _columnPanelSearchTyped;
    private Action<string?>? ColumnPanelSearchTyped => !SearchAsYouType ? null
        : _columnPanelSearchTyped ??= text =>
        {
            _columnPanelSearch = text ?? string.Empty;
            _ = RenderSidePanelSearchAfterDelayAsync();
        };
    private Action<string?>? _pivotFieldSearchTyped;
    private Action<string?>? PivotFieldSearchTyped => !SearchAsYouType ? null
        : _pivotFieldSearchTyped ??= text =>
        {
            _pivotFieldSearch = text ?? string.Empty;
            _ = RenderSidePanelSearchAfterDelayAsync();
        };
    private EventCallback<KeyboardEventArgs>? _sidePanelSearchKeyDown;
    private EventCallback<KeyboardEventArgs> SidePanelSearchKeyDown => !SearchAsYouType ? CommitKeyDown
        : _sidePanelSearchKeyDown ??= NonRenderingEventHandler.Create<KeyboardEventArgs>(e =>
        {
            if (IsApplyNowKey(e))
                RenderPendingSidePanelSearch();
        });
    private EventCallback? _sidePanelSearchLeft;
    private EventCallback SidePanelSearchLeft => !SearchAsYouType ? default
        : _sidePanelSearchLeft ??= new EventCallback(null, (Action)RenderPendingSidePanelSearch);

    private async Task RenderSidePanelSearchAfterDelayAsync()
    {
        _sidePanelSearchCts?.Cancel();
        var cts = _sidePanelSearchCts = new CancellationTokenSource();
        try
        {
            if (EffectiveFilterDelay > 0)
                await Task.Delay(EffectiveFilterDelay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ReferenceEquals(cts, _sidePanelSearchCts))
            await InvokeAsync(RenderPendingSidePanelSearch);
    }

    private void RenderPendingSidePanelSearch()
    {
        if (_sidePanelSearchCts is not { } cts)
            return;
        _sidePanelSearchCts = null;
        cts.Cancel();
        StateHasChanged();
    }

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
        // Every open starts from the applied bounds: a Min / Max typed in an
        // earlier open and never applied is discarded with it.
        _numericFilterMinText.Remove(field);
        _numericFilterMaxText.Remove(field);
        var state = GetColumnState(field);
        var column = FindColumnByField(field);
        var defaultOperator = column == null
            ? TextFilterOperator.Contains
            : GetDefaultFilterOperator(column);
        _filterTextDraft = state.FilterValue ?? "";
        _secondFilterTextDraft = state.SecondFilterValue ?? "";
        _filterOperatorDraft = _filterOperatorDraftsByField.TryGetValue(field, out var cachedOperator)
            ? cachedOperator
            : string.IsNullOrWhiteSpace(state.FilterValue)
                && state.FilterOperator == TextFilterOperator.Contains
                    ? defaultOperator
                    : state.FilterOperator;
        _secondFilterOperatorDraft = string.IsNullOrWhiteSpace(state.SecondFilterValue)
            && state.SecondFilterOperator == TextFilterOperator.Contains
                ? defaultOperator
                : state.SecondFilterOperator;
        _filterLogicalOperatorDraft = state.LogicalFilterOperator;
        _blankRowFilterDraft = state.BlankRowFilter;
        IEnumerable<string> checkedValues = state.UseCheckedFilter
            ? state.CheckedFilterValues
            : GetDistinctValues(field);
        _filterCheckedDraft = new HashSet<string>(checkedValues, FilterTextComparer);
        _filterChecklistSearchDraft = string.Empty;
        _filterChecklistDraftTouched = false;
        _filterChecklistCommitError = null;
        _filterPopupDraftGeneration++;
        _filterPopupAutoFocusTarget = null;
        _filterPopupApplyRejected = false;
        _filterPopupCommitApplyRejected = false;
        _filterPopupDragRegistered = false;
        CancelFilterPopupTyping(discard: true);
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

    private async Task OnSecondTextFilterOperatorValueChanged(TextFilterOperator filterOperator)
    {
        _secondFilterOperatorDraft = filterOperator;
        if (_filterPopupAutoApply && _filterPopupField != null)
            await ApplyFilterPopupAsync(close: false);
    }

    private async Task OnFilterLogicalOperatorChanged(LogicalFilterOperator op)
    {
        _filterLogicalOperatorDraft = op;
        if (_filterPopupAutoApply && _filterPopupField != null)
            await ApplyFilterPopupAsync(close: false);
    }

    private async Task SetBlankRowFilterDraft(BlankRowFilterMode mode)
    {
        _blankRowFilterDraft = mode;
        if (_filterPopupAutoApply && _filterPopupField != null)
            await ApplyFilterPopupAsync(close: false);
        else
            StateHasChanged();
    }

    private async Task OnFilterPopupAutoApplyChanged(ChangeEventArgs e)
    {
        _filterPopupAutoApply = e.Value is bool value && value;
        if (_filterPopupAutoApply && _filterPopupField != null)
            await ApplyFilterPopupAsync(close: false);
    }

    // The callbacks are rebuilt for every draft generation and carry it, so a box
    // instance that has already been replaced (re-seed, column switch, Clear
    // Filter) can never publish into the drafts of its successor.
    private void EnsureFilterPopupCallbackGeneration()
    {
        if (_filterPopupCallbackGeneration == _filterPopupDraftGeneration)
            return;
        _filterPopupCommitCallbacks.Clear();
        _filterPopupKeyDownCallbacks.Clear();
        _filterPopupSearchCallbacks.Clear();
        _filterPopupTypedCallbacks.Clear();
        _filterPopupLeftCallbacks.Clear();
        _filterPopupCallbackGeneration = _filterPopupDraftGeneration;
    }

    // ── Search as you type (FilterSettings.SearchAsYouType, the default) ─────
    // The boxes are Uncontrolled: the browser owns the text and every input reports
    // it (@bind keeps the server's copy in step, so no render writes an older text
    // back). Nothing renders per key: the checklist narrows and the filter applies
    // once typing pauses (ImmediateModeDelay), or at once on Enter, Tab or leaving
    // the box. Min / Max keep applying on Enter / Apply Range in both modes.
    private Action<string?>? FilterPopupBoxTyped(FilterPopupFocusTarget box)
    {
        if (!SearchAsYouType)
            return null;
        EnsureFilterPopupCallbackGeneration();
        if (_filterPopupTypedCallbacks.TryGetValue(box, out var typed))
            return typed;
        var generation = _filterPopupDraftGeneration;
        return _filterPopupTypedCallbacks[box] = text => OnFilterPopupBoxTyped(box, generation, text);
    }

    private EventCallback FilterPopupBoxLeft(FilterPopupFocusTarget box)
    {
        if (!SearchAsYouType)
            return default;
        EnsureFilterPopupCallbackGeneration();
        if (_filterPopupLeftCallbacks.TryGetValue(box, out var left))
            return left;
        var generation = _filterPopupDraftGeneration;
        return _filterPopupLeftCallbacks[box] = new EventCallback(null, (Func<Task>)(() => OnFilterPopupBoxLeftAsync(generation)));
    }

    private void OnFilterPopupBoxTyped(FilterPopupFocusTarget box, int generation, string? value)
    {
        var field = _filterPopupField;
        if (field == null || generation != _filterPopupDraftGeneration)
            return;

        var text = value ?? string.Empty;
        switch (box)
        {
            case FilterPopupFocusTarget.ConditionInput:
                _filterTextDraft = text;
                _filterOperatorDraftsByField[field] = _filterOperatorDraft;
                break;
            case FilterPopupFocusTarget.SecondConditionInput:
                _secondFilterTextDraft = text;
                break;
            case FilterPopupFocusTarget.ChecklistSearchInput:
                _filterChecklistSearchDraft = text;
                _filterPopupTypedSearchPending = true;
                break;
            default:
                return;
        }

        // A typed search narrows the checklist in manual mode too.
        if (_filterPopupAutoApply || box == FilterPopupFocusTarget.ChecklistSearchInput)
            QueueFilterPopupTypingApply(field, generation);
    }

    private void QueueFilterPopupTypingApply(string field, int generation)
    {
        CancelFilterPopupTyping(discard: false);
        var cts = _filterPopupTypingCts = new CancellationTokenSource();
        _ = ApplyFilterPopupTypingAfterDelayAsync(field, generation, cts);
    }

    private async Task ApplyFilterPopupTypingAfterDelayAsync(string field, int generation, CancellationTokenSource cts)
    {
        try
        {
            if (EffectiveFilterDelay > 0)
                await Task.Delay(EffectiveFilterDelay, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;     // a newer key, or Enter / Tab / leaving the box, owns the apply
        }

        await InvokeAsync(async () =>
        {
            // Superseded, or the menu closed, switched column or re-seeded meanwhile.
            if (!ReferenceEquals(cts, _filterPopupTypingCts) || cts.IsCancellationRequested
                || !IsCurrentFilterPopupField(field) || generation != _filterPopupDraftGeneration)
                return;
            CancelFilterPopupTyping(discard: false);
            await ApplyFilterPopupTypingAsync(field);
            StateHasChanged();
        });
    }

    private async Task OnFilterPopupBoxLeftAsync(int generation)
    {
        if (_filterPopupField == null || generation != _filterPopupDraftGeneration)
            return;
        if ((await ApplyPendingFilterPopupTypingAsync()).Applied)
            StateHasChanged();
    }

    // Typing not applied yet applies now (Enter, Tab, leaving the box).
    private async Task<(bool Applied, bool Refused)> ApplyPendingFilterPopupTypingAsync()
    {
        if (_filterPopupTypingCts == null || _filterPopupField is not { } field)
            return (false, false);
        CancelFilterPopupTyping(discard: false);
        return (true, await ApplyFilterPopupTypingAsync(field));
    }

    // Returns true when the apply was refused (provider checklist incomplete,
    // Filtering cancelled).
    private async Task<bool> ApplyFilterPopupTypingAsync(string field)
    {
        var apply = _filterPopupAutoApply;
        if (_filterPopupTypedSearchPending)
        {
            _filterPopupTypedSearchPending = false;
            apply &= SelectFilterChecklistSearchMatches(field);
        }
        if (!apply)
            return false;
        await ApplyFilterPopupAsync(close: false, render: false);
        return _filterPopupApplyRejected;
    }

    private void CancelFilterPopupTyping(bool discard)
    {
        if (discard)
            _filterPopupTypedSearchPending = false;
        var cts = _filterPopupTypingCts;
        _filterPopupTypingCts = null;
        if (cts == null)
            return;
        cts.Cancel();
        cts.Dispose();
    }

    // The search button beside a box does what Tab does in it: the box's current
    // browser text is read back (FlushClientBufferedValueAsync) and published
    // through the box's own ValueChanged, i.e. the same commit a Tab sends, so it
    // applies under Auto Apply and only stages without it. Focus stays in the box.
    private EventCallback<MouseEventArgs> FilterPopupSearchClicked(FilterPopupFocusTarget box)
    {
        EnsureFilterPopupCallbackGeneration();
        if (_filterPopupSearchCallbacks.TryGetValue(box, out var callback))
            return callback;
        var generation = _filterPopupDraftGeneration;
        return _filterPopupSearchCallbacks[box] = NonRenderingEventHandler.Create<MouseEventArgs>(
            _ => SearchFromFilterPopupBoxAsync(box, generation));
    }

    private Task SearchFromFilterPopupBoxAsync(FilterPopupFocusTarget box, int generation)
    {
        if (_filterPopupField == null || generation != _filterPopupDraftGeneration)
            return Task.CompletedTask;
        var textBox = box switch
        {
            FilterPopupFocusTarget.ConditionInput => _filterConditionBox,
            FilterPopupFocusTarget.SecondConditionInput => _secondFilterConditionBox,
            FilterPopupFocusTarget.ChecklistSearchInput => _filterChecklistSearchBox,
            _ => null
        };
        return textBox?.FlushClientBufferedValueAsync() ?? Task.CompletedTask;
    }

    private EventCallback<string?> FilterPopupBoxCommitted(FilterPopupFocusTarget box)
    {
        // Searching as you type, the text boxes report every input instead (Min /
        // Max keep committing in both modes).
        if (SearchAsYouType && box is not (FilterPopupFocusTarget.NumericMinInput or FilterPopupFocusTarget.NumericMaxInput))
            return default;
        EnsureFilterPopupCallbackGeneration();
        if (_filterPopupCommitCallbacks.TryGetValue(box, out var callback))
            return callback;
        var generation = _filterPopupDraftGeneration;
        return _filterPopupCommitCallbacks[box] = NonRenderingEventHandler.Create<string?>(
            value => OnFilterPopupBoxCommittedAsync(box, generation, value));
    }

    private EventCallback<KeyboardEventArgs> FilterPopupBoxKeyDown(FilterPopupFocusTarget box)
    {
        EnsureFilterPopupCallbackGeneration();
        if (_filterPopupKeyDownCallbacks.TryGetValue(box, out var callback))
            return callback;
        var generation = _filterPopupDraftGeneration;
        return _filterPopupKeyDownCallbacks[box] = NonRenderingEventHandler.Create<KeyboardEventArgs>(
            e => OnFilterPopupBoxKeyDownAsync(box, generation, e));
    }

    private async Task OnFilterPopupBoxCommittedAsync(FilterPopupFocusTarget box, int generation, string? value)
    {
        // Every commit key but Escape arrives here first, so a refusal left by an
        // earlier commit never reaches this commit's key.
        _filterPopupCommitApplyRejected = false;
        var field = _filterPopupField;
        if (field == null || generation != _filterPopupDraftGeneration)
            return;

        var text = value ?? string.Empty;
        var apply = _filterPopupAutoApply;
        switch (box)
        {
            case FilterPopupFocusTarget.ConditionInput:
                if (string.Equals(text, _filterTextDraft, StringComparison.Ordinal)) return;
                _filterTextDraft = text;
                _filterOperatorDraftsByField[field] = _filterOperatorDraft;
                break;
            case FilterPopupFocusTarget.SecondConditionInput:
                if (string.Equals(text, _secondFilterTextDraft, StringComparison.Ordinal)) return;
                _secondFilterTextDraft = text;
                break;
            case FilterPopupFocusTarget.ChecklistSearchInput:
                if (string.Equals(text, _filterChecklistSearchDraft, StringComparison.Ordinal)) return;
                _filterChecklistSearchDraft = text;
                // `&` (not `&&`): the matches are selected in manual mode too.
                // While provider values load, the provider path re-applies the search.
                apply &= SelectFilterChecklistSearchMatches(field);
                break;
            case FilterPopupFocusTarget.NumericMinInput:
                if (string.Equals(text, GetNumericFilterMinText(field), StringComparison.Ordinal)) return;
                // Stored as typed, "" included: a blank must clear the applied
                // bound, not fall back to it.
                _numericFilterMinText[field] = text;
                apply = false;          // bounds apply on Apply Range / Enter
                break;
            case FilterPopupFocusTarget.NumericMaxInput:
                if (string.Equals(text, GetNumericFilterMaxText(field), StringComparison.Ordinal)) return;
                _numericFilterMaxText[field] = text;
                apply = false;
                break;
        }

        if (apply)
        {
            await ApplyFilterPopupAsync(close: false, render: false);
            _filterPopupCommitApplyRejected = _filterPopupApplyRejected;
        }

        // The browser listener owns the box's text, so a committed box keeps it
        // by itself; one render shows the applied filter.
        StateHasChanged();
    }

    private async Task OnFilterPopupBoxKeyDownAsync(FilterPopupFocusTarget box, int generation, KeyboardEventArgs e)
    {
        var commitRefused = _filterPopupCommitApplyRejected;
        _filterPopupCommitApplyRejected = false;
        var field = _filterPopupField;
        // A key that confirms an IME composition is the composition's, not the menu's.
        if (field == null || generation != _filterPopupDraftGeneration
            || e.AltKey || e.CtrlKey || e.MetaKey || e.IsComposing)
            return;

        if (e.Key is "Enter" or "NumpadEnter")
        {
            if (box is FilterPopupFocusTarget.NumericMinInput or FilterPopupFocusTarget.NumericMaxInput)
            {
                await ApplyNumericBoundsFromMenuAsync(field, box);
                StateHasChanged();
                return;
            }

            // A search committed while provider values are still loading has not
            // selected anything yet; closing now would lose it (the provider path
            // re-applies it only while the menu is open).
            if (box == FilterPopupFocusTarget.ChecklistSearchInput && IsProviderFilterValuesLoading)
            {
                StateHasChanged();
                return;
            }

            // Searching as you type, typing not applied yet applies now.
            var typingApplied = false;
            if (SearchAsYouType)
                (typingApplied, commitRefused) = await ApplyPendingFilterPopupTypingAsync();

            // This Enter's own commit was just refused: the same drafts would only
            // be refused again (and the host's Filtering would run twice). Stay open.
            if (_filterPopupAutoApply && commitRefused)
            {
                if (typingApplied)
                    StateHasChanged();
                return;
            }

            // Enter is the menu's OK. Under Auto Apply the committed text is
            // already applied, unless an earlier apply was refused (provider
            // checklist incomplete, Filtering cancelled): then Enter retries it the
            // way the Apply button does and stays open if it is refused again.
            if (_filterPopupAutoApply && !_filterPopupApplyRejected)
            {
                CloseFilterPopup();
                StateHasChanged();
            }
            else
            {
                await ApplyFilterPopupAsync(close: true);
            }
            if (_filterPopupField == null)
                await FocusGridHostAsync();
            return;
        }

        // Searching as you type, Tab applies typing not applied yet at once.
        if (e.Key == "Tab" && SearchAsYouType)
        {
            if ((await ApplyPendingFilterPopupTypingAsync()).Applied)
                StateHasChanged();
            return;
        }

        // Escape never publishes the box's text (nor typing not applied yet): the
        // menu closes without applying anything and the keyboard returns to the grid.
        if (e.Key == "Escape")
            await CloseFilterPopupAndFocusGridAsync();
    }

    // Escape anywhere else in the menu (a checkbox, a button, Auto Apply) closes it
    // too. A text box's own Escape arrives through its OnKeyDown; one that also
    // bubbles here finds the menu already closed.
    private EventCallback<KeyboardEventArgs>? _filterPopupRootKeyDown;
    private EventCallback<KeyboardEventArgs> FilterPopupRootKeyDown =>
        _filterPopupRootKeyDown ??= NonRenderingEventHandler.Create<KeyboardEventArgs>(async e =>
        {
            if (e.Key == "Escape" && !e.AltKey && !e.CtrlKey && !e.MetaKey && !e.ShiftKey
                && _filterPopupField != null)
                await CloseFilterPopupAndFocusGridAsync();
        });

    private async Task CloseFilterPopupAndFocusGridAsync()
    {
        CloseFilterPopup();
        StateHasChanged();
        await FocusGridHostAsync();
    }

    private async Task ApplyNumericBoundsFromPopupAsync()
    {
        if (_filterPopupField is { } field)
            await ApplyNumericBoundsFromMenuAsync(field, refocus: null);   // focus stays on the button
    }

    private async Task ApplyNumericBoundsFromMenuAsync(string field, FilterPopupFocusTarget? refocus)
    {
        var generation = _filterPopupDraftGeneration;
        var minText = GetNumericFilterMinText(field);
        var maxText = GetNumericFilterMaxText(field);
        await ApplyNumericBoundsFilter(field);
        // The menu moved on while the apply awaited (another column, a re-seed).
        if (!IsCurrentFilterPopupField(field) || generation != _filterPopupDraftGeneration)
            return;
        // Bounds that come back normalised or swapped re-seed the boxes to show
        // them; text that is already normal keeps its box, and focus with it.
        if (!string.Equals(minText, GetNumericFilterMinText(field), StringComparison.Ordinal)
            || !string.Equals(maxText, GetNumericFilterMaxText(field), StringComparison.Ordinal))
        {
            _filterPopupAutoFocusTarget = refocus;
            _filterPopupDraftGeneration++;
        }
    }

    // (Select All) decides at the press what its click does. The press commits a
    // typed search first (blur), which narrows the list and selects its matches
    // before the click arrives; a click that read the list then would invert what
    // the user saw and clear every match.
    private EventCallback<MouseEventArgs>? _filterSelectAllPressed;
    private EventCallback<MouseEventArgs> FilterSelectAllPressed =>
        _filterSelectAllPressed ??= NonRenderingEventHandler.Create<MouseEventArgs>(_ =>
        {
            if (_filterPopupField is { } popupField)
                _filterSelectAllPressIntent = GetFilterFieldSelectionState(
                    popupField, VisibleFilterChecklistValues(popupField)) != GridFilterSelectionState.All;
        });

    private async Task ToggleFilterSelectAllAsync(MouseEventArgs e)
    {
        var pressIntent = _filterSelectAllPressIntent;
        _filterSelectAllPressIntent = null;
        if (_filterPopupField is not { } field)
            return;
        var values = VisibleFilterChecklistValues(field);
        // A pointer click does what its press saw; a key press (Detail 0) what is shown.
        var select = e.Detail != 0 && pressIntent is { } intent
            ? intent
            : GetFilterFieldSelectionState(field, values) != GridFilterSelectionState.All;
        await SetFilterFieldSelected(field, select, values);
    }

    private List<string> VisibleFilterChecklistValues(string field) =>
        GetColumnFilterValueCandidates(field).Select(candidate => candidate.Value).ToList();

    /// <summary>A search selects its matching values, as in Excel. Returns false
    /// while provider values are still loading.</summary>
    private bool SelectFilterChecklistSearchMatches(string field)
    {
        if (IsProviderFilterValuesLoading)
            return false;
        _filterCheckedDraft = new HashSet<string>(
            GetColumnFilterValueCandidates(field).Select(candidate => candidate.Value),
            FilterTextComparer);
        _filterChecklistDraftTouched = true;
        _filterChecklistCommitError = null;
        return true;
    }

    private async Task ApplyFilterPopupAsync(bool close, bool render = true)
    {
        var field = _filterPopupField;
        if (field == null)
            return;

        if (!CanCommitCheckedFilterDraft(field))
        {
            _filterPopupApplyRejected = true;
            if (render)
                await InvokeAsync(StateHasChanged);
            return;
        }

        // The drafts are read before the first await: a Filtering handler that
        // yields must not let a column switch hand this column the drafts of
        // the next one.
        var text = _filterTextDraft;
        var secondText = _secondFilterTextDraft;
        var secondOperator = _secondFilterOperatorDraft;
        var logicalOperator = _filterLogicalOperatorDraft;
        var blankRows = _blankRowFilterDraft;
        var checkedDraft = _filterChecklistDraftTouched
            ? new HashSet<string>(_filterCheckedDraft, FilterTextComparer)
            : null;

        // Commit the menu as one transaction. ApplyFilter normally reloads a
        // provider immediately, but here the second/checklist/blank criteria
        // must be copied first so one user action issues one complete request.
        var clearEpoch = FilterClearEpoch(field);
        if (!await ApplyFilter(field, text, _filterOperatorDraft, finalizeUpdate: false))
        {
            // Refused by the host, unless a clear superseded the apply.
            if (IsCurrentFilterPopupField(field) && clearEpoch == FilterClearEpoch(field))
                _filterPopupApplyRejected = true;
            return;
        }
        var state = GetColumnState(field);
        state.SecondFilterValue = string.IsNullOrWhiteSpace(secondText) ? null : secondText;
        state.SecondFilterOperator = secondOperator;
        state.LogicalFilterOperator = logicalOperator;
        state.BlankRowFilter = blankRows;
        // Applying a text condition must not accidentally turn an unavailable
        // provider checklist into an active "select nothing" predicate.
        if (checkedDraft != null)
            CommitCheckedFilterDraft(field, checkedDraft);
        if (IsCurrentFilterPopupField(field))
            _filterPopupApplyRejected = false;

        if (EventsRef?.Filtered.HasDelegate == true)
        {
            await EventsRef.Filtered.InvokeAsync(new FilterEventArgs
            {
                Field = field,
                Value = text
            });
        }

        if (UsesItemsProvider)
            await ReloadItemsAsync();
        await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);

        if (close && IsCurrentFilterPopupField(field))
            CloseFilterPopup();

        if (render)
            await InvokeAsync(StateHasChanged);
    }

    private void CommitCheckedFilterDraft(string field)
    {
        // Applying a text condition must not accidentally turn an unavailable
        // provider checklist into an active "select nothing" predicate.
        if (_filterChecklistDraftTouched)
            CommitCheckedFilterDraft(field, _filterCheckedDraft);
    }

    private void CommitCheckedFilterDraft(string field, HashSet<string> checkedDraft)
    {
        var state = GetColumnState(field);
        var all = GetDistinctValues(field);
        state.CheckedFilterValues = new HashSet<string>(checkedDraft, FilterTextComparer);

        state.UseCheckedFilter = all.Count > 0 && checkedDraft.Count < all.Count;
        if (!state.UseCheckedFilter)
            state.CheckedFilterValues.Clear();

        state.CheckedNumericRangeKeys.Clear();
        state.UseNumericRangeFilter = false;
        state.NumericFilterMin = null;
        state.NumericFilterMax = null;
        state.UseNumericBoundsFilter = false;
        _pageState.CurrentPage = 1;
    }

    /// <summary>
    /// A provider checklist is an inclusion list. Applying a partial page of
    /// distinct values as though it were the whole universe would silently
    /// exclude every value the provider did not return. Until the provider
    /// confirms the distinct set is complete, only the no-op "all currently
    /// shown values selected" state is safe to commit.
    /// </summary>
    private bool CanCommitCheckedFilterDraft(string field)
    {
        _filterChecklistCommitError = null;
        if (!_filterChecklistDraftTouched || !UsesItemsProvider)
            return true;

        var knownValues = GetDistinctValues(field);
        if (knownValues.Count > 0 && knownValues.All(_filterCheckedDraft.Contains))
            return true;

        var hasMore = true;
        var completenessKnown = EnableProviderFilterValueRequests
            && _providerFilterValuesHaveMore.TryGetValue(field, out hasMore);
        if (completenessKnown && !hasMore)
            return true;

        _filterChecklistCommitError = !EnableProviderFilterValueRequests
            ? "Partial checklist filtering needs provider distinct-value requests. Enable them or use the typed conditions above."
            : IsProviderFilterValuesLoading
                ? "Wait for all requested distinct values before applying a partial checklist selection."
                : ProviderFilterValuesLastError != null
                    ? "Distinct values could not be loaded, so a partial checklist selection cannot be applied safely."
                    : "The provider returned only part of the distinct-value set. Narrow the query or increase ProviderFilterValueRequestSize before applying a partial selection.";
        return false;
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
        TextFilterOperator.GreaterThan => "Greater Than",
        TextFilterOperator.GreaterThanOrEqual => "Greater Than or Equal",
        TextFilterOperator.LessThan => "Less Than",
        TextFilterOperator.LessThanOrEqual => "Less Than or Equal",
        TextFilterOperator.IsEmpty => "Is Blank / Empty",
        TextFilterOperator.IsNotEmpty => "Is Not Blank / Empty",
        _ => "Choose One"
    };

    private bool PassesTextFilter(string actual, string expected, TextFilterOperator filterOperator)
    {
        var comparison = FilterTextComparison;
        return filterOperator switch
        {
            TextFilterOperator.Equals => string.Equals(actual, expected, comparison),
            TextFilterOperator.DoesNotEqual => !string.Equals(actual, expected, comparison),
            TextFilterOperator.BeginsWith => actual.StartsWith(expected, comparison),
            TextFilterOperator.DoesNotBeginWith => !actual.StartsWith(expected, comparison),
            TextFilterOperator.EndsWith => actual.EndsWith(expected, comparison),
            TextFilterOperator.DoesNotEndWith => !actual.EndsWith(expected, comparison),
            TextFilterOperator.DoesNotContain => !actual.Contains(expected, comparison),
            TextFilterOperator.IsEmpty => string.IsNullOrWhiteSpace(actual),
            TextFilterOperator.IsNotEmpty => !string.IsNullOrWhiteSpace(actual),
            TextFilterOperator.GreaterThan => CompareFilterText(actual, expected) > 0,
            TextFilterOperator.GreaterThanOrEqual => CompareFilterText(actual, expected) >= 0,
            TextFilterOperator.LessThan => CompareFilterText(actual, expected) < 0,
            TextFilterOperator.LessThanOrEqual => CompareFilterText(actual, expected) <= 0,
            TextFilterOperator.ChooseOne or TextFilterOperator.Contains or _ => actual.Contains(expected, comparison)
        };
    }

    private int CompareFilterText(string actual, string expected)
    {
        if ((decimal.TryParse(actual, NumberStyles.Any, CultureInfo.CurrentCulture, out var actualNumber)
                || decimal.TryParse(actual, NumberStyles.Any, CultureInfo.InvariantCulture, out actualNumber))
            && (decimal.TryParse(expected, NumberStyles.Any, CultureInfo.CurrentCulture, out var expectedNumber)
                || decimal.TryParse(expected, NumberStyles.Any, CultureInfo.InvariantCulture, out expectedNumber)))
        {
            return actualNumber.CompareTo(expectedNumber);
        }

        if (TryParseFilterDate(actual, out var actualDate)
            && TryParseFilterDate(expected, out var expectedDate))
        {
            return actualDate.CompareTo(expectedDate);
        }

        return string.Compare(actual, expected, FilterTextComparison);
    }

    private bool IsCurrentFilterPopupField(string field) =>
        string.Equals(_filterPopupField, field, StringComparison.Ordinal);

    private bool IsNumericFilterColumn(GridColumn? col) =>
        col?.Type == ColumnType.Number;

    private IReadOnlyList<FilterValueCandidate> GetColumnFilterValueCandidates(string field) =>
        FilterChecklistCandidatesBySearch(GetDistinctFilterValueCandidates(field));

    private IReadOnlyList<FilterValueCandidate> FilterChecklistCandidatesBySearch(IReadOnlyList<FilterValueCandidate> all) =>
        string.IsNullOrWhiteSpace(_filterChecklistSearchDraft)
            ? all
            : all.Where(MatchesChecklistSearch).ToList();

    private bool MatchesChecklistSearch(FilterValueCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(_filterChecklistSearchDraft))
            return true;

        return CombineFilterSearchText(candidate.Value, candidate.DisplayText)
            .Contains(_filterChecklistSearchDraft, FilterTextComparison);
    }

    private IReadOnlyList<FilterValueCandidate> GetDistinctFilterValueCandidates(string field)
    {
        if (UsesItemsProvider)
        {
            return _providerFilterValueCandidates.TryGetValue(field, out var providerCandidates)
                ? providerCandidates
                : Array.Empty<FilterValueCandidate>();
        }

        var col = FindColumnByField(field);
        var candidates = new Dictionary<string, FilterValueCandidate>(FilterTextComparer);

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

    private bool PassesDisplayAwareTextFilter(string rawValue, string displayText, string expected, TextFilterOperator filterOperator)
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

    private GridFilterSelectionState GetFilterFieldSelectionState(
        string field,
        IReadOnlyList<string>? values = null)
    {
        var candidates = values ?? GetDistinctValues(field);
        if (candidates.Count == 0)
            return GridFilterSelectionState.None;

        var selectedCount = IsCurrentFilterPopupField(field)
            ? candidates.Count(_filterCheckedDraft.Contains)
            : !GetColumnState(field).UseCheckedFilter
                ? candidates.Count
                : candidates.Count(GetColumnState(field).CheckedFilterValues.Contains);

        return selectedCount switch
        {
            0 => GridFilterSelectionState.None,
            var count when count == candidates.Count => GridFilterSelectionState.All,
            _ => GridFilterSelectionState.Some
        };
    }

    private static string GetFilterSelectionAriaValue(GridFilterSelectionState state) => state switch
    {
        GridFilterSelectionState.All => "true",
        GridFilterSelectionState.Some => "mixed",
        _ => "false"
    };

    private async Task SetFilterChecklistSearchAsync(string? searchText)
    {
        _filterChecklistSearchDraft = searchText ?? string.Empty;
        var field = _filterPopupField;
        if (field == null)
            return;

        // A search selects its matching values, as in Excel. Keeping hidden
        // values selected makes both Auto Apply and Apply admit every row.
        // Clearing the search selects the complete checklist again.
        if (SelectFilterChecklistSearchMatches(field) && _filterPopupAutoApply)
            await QueueFilterPopupAutoApplyAsync();

        await InvokeAsync(StateHasChanged);
    }

    private async Task SetFilterFieldSelected(string field, bool selected, IReadOnlyList<string>? values = null)
    {
        if (IsCurrentFilterPopupField(field))
        {
            _filterChecklistDraftTouched = true;
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
            {
                if (!CanCommitCheckedFilterDraft(field))
                {
                    await InvokeAsync(StateHasChanged);
                    return;
                }
                CommitCheckedFilterDraft(field);
                if (UsesItemsProvider)
                    await ReloadItemsAsync();
                await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
            }
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
        if (UsesItemsProvider)
            await ReloadItemsAsync();
        await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
    }

    private async Task SetFilterValueChecked(string field, string value, bool selected)
    {
        if (IsCurrentFilterPopupField(field))
        {
            _filterChecklistDraftTouched = true;
            if (selected)
                _filterCheckedDraft.Add(value);
            else
                _filterCheckedDraft.Remove(value);

            if (_filterPopupAutoApply)
            {
                if (!CanCommitCheckedFilterDraft(field))
                {
                    await InvokeAsync(StateHasChanged);
                    return;
                }
                CommitCheckedFilterDraft(field);
                if (UsesItemsProvider)
                    await ReloadItemsAsync();
                await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
            }
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
        if (UsesItemsProvider)
            await ReloadItemsAsync();
        await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
    }

    private int GetSelectedFilterValueCount(string field) =>
        CountSelectedFilterValues(field, GetDistinctFilterValueCandidates(field));

    private int CountSelectedFilterValues(string field, IReadOnlyList<FilterValueCandidate> all)
    {
        if (IsCurrentFilterPopupField(field))
            return all.Count(candidate => _filterCheckedDraft.Contains(candidate.Value));

        var state = GetColumnState(field);
        if (!state.UseCheckedFilter)
            return all.Count;

        return all.Count(candidate => state.CheckedFilterValues.Contains(candidate.Value));
    }

    private bool ProviderFilterValuesHaveMore(string field) =>
        _providerFilterValuesHaveMore.TryGetValue(field, out var hasMore) && hasMore;

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

    private async Task ApplyNumericBoundsFilter(string field)
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
        if (UsesItemsProvider)
            await ReloadItemsAsync();
        await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
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

        foreach (var sample in EnumerateNumericFilterSamples(field))
        {
            if (!TryConvertToDecimal(sample.Value, out var number))
            {
                stats.BlankCount += sample.Count;
                continue;
            }

            stats.NumericCount += sample.Count;
            stats.Min = !stats.Min.HasValue || number < stats.Min.Value ? number : stats.Min;
            stats.Max = !stats.Max.HasValue || number > stats.Max.Value ? number : stats.Max;

            if (exactCounts == null)
                continue;

            exactCounts[number] = exactCounts.TryGetValue(number, out var count)
                ? count + sample.Count
                : sample.Count;
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
        foreach (var sample in EnumerateNumericFilterSamples(field))
        {
            if (!TryConvertToDecimal(sample.Value, out var number))
                continue;

            var index = GetNumericBucketIndex(number, min, width, bucketCount);
            counts[index] += sample.Count;
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

    private IEnumerable<(object? Value, int Count)> EnumerateNumericFilterSamples(string field)
    {
        if (UsesItemsProvider)
        {
            if (!_providerFilterValueCandidates.TryGetValue(field, out var candidates))
                yield break;

            foreach (var candidate in candidates)
            {
                // FilterValues are distinct values. Count carries the provider's
                // row frequency when available; one keeps older providers that
                // only return Value/DisplayText useful for range generation.
                var count = Math.Max(0, candidate.Count ?? 1);
                if (count > 0)
                    yield return (candidate.Value, count);
            }
            yield break;
        }

        foreach (var item in DataSource ?? Enumerable.Empty<TValue>())
            yield return (GetFilterRawValue(item, field), 1);
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

    private async Task SetNumericRangeChecked(string field, NumericFilterRange range, bool selected)
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
        if (UsesItemsProvider)
            await ReloadItemsAsync();
        await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
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
