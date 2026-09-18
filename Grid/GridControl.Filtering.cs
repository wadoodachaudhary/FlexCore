using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using System.Globalization;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    // Filter-row state is deliberately separate from the header-menu state so
    // both surfaces can be composed and serialized as independent predicates.
    private readonly Dictionary<string, TextFilterOperator> _filterRowOperators =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _filterRowDrafts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _filterRowDebounce =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EventCallback<string?>> _filterRowBoxCommits =
        new(StringComparer.OrdinalIgnoreCase);
    // @key of a filter-row text box: its column, and generations bumped when code
    // sets or clears that column's value (per column) or every value (all). The
    // text is browser-owned, so a bump re-seeds only the boxes it concerns.
    private readonly Dictionary<string, int> _filterRowBoxGenerations =
        new(StringComparer.OrdinalIgnoreCase);
    private int _filterRowBoxGeneration;

    private (string Field, int All, int Own, bool AsYouType) FilterRowBoxKey(string field) =>
        (field, _filterRowBoxGeneration, _filterRowBoxGenerations.GetValueOrDefault(field), SearchAsYouType);

    private void ReseedFilterRowBox(string field) =>
        _filterRowBoxGenerations[field] = _filterRowBoxGenerations.GetValueOrDefault(field) + 1;
    private CancellationTokenSource? _filterPopupAutoApplyCts;
    private string _filterChecklistSearchDraft = string.Empty;
    private bool _filterChecklistDraftTouched;
    private string? _filterChecklistCommitError;

    private StringComparison FilterTextComparison =>
        FilterSettingsRef?.EnableCaseSensitivity == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    private StringComparer FilterTextComparer =>
        FilterSettingsRef?.EnableCaseSensitivity == true
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

    private int EffectiveFilterDelay => Math.Max(0, FilterSettingsRef?.ImmediateModeDelay ?? 300);

    // The grid's filter and search boxes search as you type unless the host turns
    // it off (then they apply on commit). Either way the browser owns the text.
    private bool SearchAsYouType => FilterSettingsRef?.SearchAsYouType != false;

    // As you type: Uncontrolled (@bind keeps the server's copy of the text in step,
    // so a render never writes an older text back) and typing keys never dispatched.
    // On commit: ClientBuffered (no traffic until the text is committed).
    private TextBoxTypingBehavior FilterBoxTypingBehavior =>
        SearchAsYouType ? TextBoxTypingBehavior.ServerBacked : TextBoxTypingBehavior.ClientBuffered;

    private bool ShowFilterRowOperators => FilterSettingsRef?.ShowFilterRowOperators != false;

    private TextFilterOperator GetFilterRowOperator(GridColumn column)
    {
        if (!ShowFilterRowOperators)
            return TextFilterOperator.Contains;

        if (_filterRowOperators.TryGetValue(column.Field, out var filterOperator))
            return filterOperator;

        return GetDefaultFilterOperator(column);
    }

    private static TextFilterOperator GetDefaultFilterOperator(GridColumn column) =>
        column.Type == ColumnType.Text || column.Type == ColumnType.Password
            ? TextFilterOperator.Contains
            : TextFilterOperator.Equals;

    private IReadOnlyList<TextFilterOperator> GetFilterRowOperators(GridColumn column) =>
        column.Type switch
        {
            ColumnType.Number or ColumnType.Date =>
            [
                TextFilterOperator.Equals,
                TextFilterOperator.DoesNotEqual,
                TextFilterOperator.GreaterThan,
                TextFilterOperator.GreaterThanOrEqual,
                TextFilterOperator.LessThan,
                TextFilterOperator.LessThanOrEqual,
                TextFilterOperator.IsEmpty,
                TextFilterOperator.IsNotEmpty
            ],
            ColumnType.Boolean or ColumnType.CheckBox =>
            [
                TextFilterOperator.Equals,
                TextFilterOperator.DoesNotEqual,
                TextFilterOperator.IsEmpty,
                TextFilterOperator.IsNotEmpty
            ],
            _ =>
            [
                TextFilterOperator.Contains,
                TextFilterOperator.DoesNotContain,
                TextFilterOperator.Equals,
                TextFilterOperator.DoesNotEqual,
                TextFilterOperator.BeginsWith,
                TextFilterOperator.DoesNotBeginWith,
                TextFilterOperator.EndsWith,
                TextFilterOperator.DoesNotEndWith,
                TextFilterOperator.GreaterThan,
                TextFilterOperator.GreaterThanOrEqual,
                TextFilterOperator.LessThan,
                TextFilterOperator.LessThanOrEqual,
                TextFilterOperator.IsEmpty,
                TextFilterOperator.IsNotEmpty
            ]
        };

    private static bool FilterOperatorNeedsNoValue(TextFilterOperator filterOperator) =>
        filterOperator is TextFilterOperator.IsEmpty or TextFilterOperator.IsNotEmpty;

    private string GetFilterRowInputType(GridColumn column) => !ShowFilterRowOperators
        ? "text"
        : column.Type switch
    {
        ColumnType.Number => "number",
        ColumnType.Date => "date",
        _ => "text"
    };

    private IEnumerable<TextFilterOperatorChoice> GetFilterRowOperatorChoices(GridColumn column) =>
        GetFilterRowOperators(column).Select(filterOperator => new TextFilterOperatorChoice(
            filterOperator, GetTextFilterOperatorLabel(filterOperator)));

    private sealed record BooleanFilterChoice(string Value, string Text);
    private static readonly BooleanFilterChoice[] BooleanFilterChoices =
        [new("", "All"), new("true", "True"), new("false", "False")];

    private async Task OnFilterRowOperatorChanged(GridColumn column, TextFilterOperator filterOperator)
    {
        _filterRowOperators[column.Field] = filterOperator;
        await CommitFilterRowAsync(column.Field, GetColumnFilterValue(column.Field));
    }

    private void QueueFilterRowValue(string field, string? value)
    {
        ReseedFilterRowBox(field);
        QueueFilterRowDraft(field, value);
    }

    // A filter-row value that applies after ImmediateModeDelay: set from code, or
    // typed while searching as you type (the box keeps its own text then).
    private void QueueFilterRowDraft(string field, string? value)
    {
        _filterRowDrafts[field] = value ?? string.Empty;

        if (_filterRowDebounce.Remove(field, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _filterRowDebounce[field] = cts;
        _ = ApplyFilterRowAfterDelayAsync(field, cts);
    }

    private async Task ApplyFilterRowAfterDelayAsync(string field, CancellationTokenSource cts)
    {
        try
        {
            if (EffectiveFilterDelay > 0)
                await Task.Delay(EffectiveFilterDelay, cts.Token);

            await InvokeAsync(async () =>
            {
                if (cts.IsCancellationRequested || !_filterRowDrafts.TryGetValue(field, out var value))
                    return;
                // In flight from here: an Enter / Tab / leave during the commit (a slow Filtering
                // handler or reload) finds nothing queued and does not apply the same text twice.
                if (_filterRowDebounce.TryGetValue(field, out var queued) && ReferenceEquals(queued, cts))
                    _filterRowDebounce.Remove(field);
                await CommitFilterRowAsync(field, value);
            });
        }
        catch (OperationCanceledException)
        {
            // A newer keystroke owns the commit.
        }
        finally
        {
            if (_filterRowDebounce.TryGetValue(field, out var current) && ReferenceEquals(current, cts))
                _filterRowDebounce.Remove(field);
            cts.Dispose();
        }
    }

    private readonly Dictionary<string, (object Key, Action<string?> Typed)> _filterRowBoxTypedCallbacks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EventCallback<KeyboardEventArgs>> _filterRowBoxKeyDowns =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EventCallback> _filterRowBoxLefts =
        new(StringComparer.OrdinalIgnoreCase);

    // Search as you type: each input queues the value (no render); Enter, Tab or
    // leaving the box apply a queued value at once.
    private Action<string?>? FilterRowBoxTyped(string field)
    {
        if (!SearchAsYouType)
            return null;
        object key = FilterRowBoxKey(field);
        if (_filterRowBoxTypedCallbacks.TryGetValue(field, out var cached) && Equals(cached.Key, key))
            return cached.Typed;
        // A box replaced by a re-seed can still report a late input; its key no
        // longer matches, so it is ignored.
        Action<string?> typed = text =>
        {
            if (Equals(key, FilterRowBoxKey(field)))
                QueueFilterRowDraft(field, text);
        };
        _filterRowBoxTypedCallbacks[field] = (key, typed);
        return typed;
    }

    private EventCallback<KeyboardEventArgs> FilterRowBoxKeyDown(string field)
    {
        if (!SearchAsYouType)
            return CommitKeyDown;
        if (!_filterRowBoxKeyDowns.TryGetValue(field, out var keyDown))
            _filterRowBoxKeyDowns[field] = keyDown = NonRenderingEventHandler.Create<KeyboardEventArgs>(
                e => IsApplyNowKey(e) ? ApplyQueuedFilterRowAsync(field) : Task.CompletedTask);
        return keyDown;
    }

    private EventCallback FilterRowBoxLeft(string field)
    {
        if (!SearchAsYouType)
            return default;
        if (!_filterRowBoxLefts.TryGetValue(field, out var left))
            _filterRowBoxLefts[field] = left = new EventCallback(null, (Func<Task>)(() => ApplyQueuedFilterRowAsync(field)));
        return left;
    }

    private Task ApplyQueuedFilterRowAsync(string field)
    {
        if (!_filterRowDebounce.Remove(field, out var pending))
            return Task.CompletedTask;
        pending.Cancel();
        pending.Dispose();
        return CommitFilterRowAsync(field, _filterRowDrafts.GetValueOrDefault(field));
    }

    private EventCallback<string?> FilterRowBoxCommitted(string field)
    {
        if (SearchAsYouType)
            return default;
        if (!_filterRowBoxCommits.TryGetValue(field, out var callback))
            _filterRowBoxCommits[field] = callback = NonRenderingEventHandler.Create<string?>(
                value => CommitFilterRowBoxAsync(field, value));
        return callback;
    }

    // A filter-row box commits on Enter, Tab or leaving it and applies at once.
    private Task CommitFilterRowBoxAsync(string field, string? value)
    {
        var text = value ?? string.Empty;
        if (string.Equals(text, GetColumnFilterValue(field), StringComparison.Ordinal))
            return Task.CompletedTask;
        // The typed value wins over one still queued through OnColumnFilterInput.
        if (_filterRowDebounce.Remove(field, out var pending))
        {
            pending.Cancel();
            pending.Dispose();
        }
        return CommitFilterRowAsync(field, text);
    }

    private async Task CommitFilterRowAsync(string field, string? value)
    {
        var column = FindColumnByField(field);
        var filterOperator = column == null
            ? TextFilterOperator.Contains
            : GetFilterRowOperator(column);
        var normalized = value ?? string.Empty;
        var draftBefore = _filterRowDrafts.GetValueOrDefault(field);

        if (EventsRef?.Filtering.HasDelegate == true)
        {
            var clearEpoch = FilterClearEpoch(field);
            var args = new FilterEventArgs { Field = field, Value = normalized };
            await EventsRef.Filtering.InvokeAsync(args);
            if (args.Cancel)
                return;
            // The row was cleared (or every filter reset / restored) while the host
            // decided: the clear wins. Text typed meanwhile is newer than this commit:
            // its own debounce (or Enter / Tab) applies it, so this one stands down.
            if (clearEpoch != FilterClearEpoch(field)
                || !string.Equals(_filterRowDrafts.GetValueOrDefault(field), draftBefore, StringComparison.Ordinal))
                return;
        }

        if (string.IsNullOrWhiteSpace(normalized) && !FilterOperatorNeedsNoValue(filterOperator))
            _simpleColumnFilters.Remove(field);
        else
            _simpleColumnFilters[field] = normalized;

        _filterRowDrafts[field] = normalized;
        _pageState.CurrentPage = 1;
        ClearPassViewMemos();
        InvalidateBlazorServerOptimizationCaches();

        if (UsesItemsProvider)
            await ReloadItemsAsync();

        if (EventsRef?.Filtered.HasDelegate == true)
            await EventsRef.Filtered.InvokeAsync(new FilterEventArgs { Field = field, Value = normalized });

        await InvokeAsync(StateHasChanged);
        await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
    }

    private async Task ClearFilterRowAsync(string field)
    {
        _filterClearEpochs[field] = _filterClearEpochs.GetValueOrDefault(field) + 1;
        if (_filterRowDebounce.Remove(field, out var pending))
        {
            pending.Cancel();
            pending.Dispose();
        }

        _filterRowDrafts.Remove(field);
        _filterRowOperators.Remove(field);
        _simpleColumnFilters.Remove(field);
        ReseedFilterRowBox(field);
        _pageState.CurrentPage = 1;
        ClearPassViewMemos();
        InvalidateBlazorServerOptimizationCaches();
        if (UsesItemsProvider)
            await ReloadItemsAsync();
        await InvokeAsync(StateHasChanged);
        await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
    }

    private bool PassesTypedFilterRow(TValue item, GridColumn column, string value)
    {
        var rawValue = GetFilterRawValue(item, column.Field);
        var displayValue = GetFilterDisplayText(item, column, rawValue?.ToString() ?? string.Empty);
        var filterOperator = GetFilterRowOperator(column);

        if (filterOperator == TextFilterOperator.IsEmpty)
            return rawValue == null || string.IsNullOrWhiteSpace(displayValue);
        if (filterOperator == TextFilterOperator.IsNotEmpty)
            return rawValue != null && !string.IsNullOrWhiteSpace(displayValue);

        // With the operator selector hidden, partial text is meaningful for
        // every type (for example, "12" matches both numeric 12 and 312).
        if (!ShowFilterRowOperators)
            return PassesDisplayAwareTextFilter(
                rawValue?.ToString() ?? string.Empty, displayValue, value, filterOperator);

        if (column.Type == ColumnType.Number)
        {
            if (TryConvertDecimal(rawValue, displayValue, out var number)
                && (decimal.TryParse(value, NumberStyles.Any, CultureInfo.CurrentCulture, out var expectedNumber)
                    || decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out expectedNumber)))
            {
                return CompareTyped(number.CompareTo(expectedNumber), filterOperator);
            }
            return false;
        }

        if (column.Type == ColumnType.Date)
        {
            return TryConvertDate(rawValue, displayValue, out var date)
                && TryParseFilterDate(value, out var expectedDate)
                && CompareTyped(date.CompareTo(expectedDate), filterOperator);
        }

        if (column.Type is ColumnType.Boolean or ColumnType.CheckBox)
        {
            return TryConvertBoolean(rawValue, displayValue, out var boolean)
                && bool.TryParse(value, out var expectedBoolean)
                && CompareTyped(boolean.CompareTo(expectedBoolean), filterOperator);
        }

        return PassesDisplayAwareTextFilter(
            rawValue?.ToString() ?? string.Empty,
            displayValue,
            value,
            filterOperator);
    }

    private static bool CompareTyped(int comparison, TextFilterOperator filterOperator) => filterOperator switch
    {
        TextFilterOperator.Equals => comparison == 0,
        TextFilterOperator.DoesNotEqual => comparison != 0,
        TextFilterOperator.GreaterThan => comparison > 0,
        TextFilterOperator.GreaterThanOrEqual => comparison >= 0,
        TextFilterOperator.LessThan => comparison < 0,
        TextFilterOperator.LessThanOrEqual => comparison <= 0,
        _ => false
    };

    private static bool TryConvertDecimal(object? raw, string display, out decimal result)
    {
        if (raw is IConvertible convertible && raw is not string && raw is not DateTime && raw is not bool)
        {
            try
            {
                result = convertible.ToDecimal(CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                // Fall through to the formatted text.
            }
        }

        return decimal.TryParse(display, NumberStyles.Any, CultureInfo.CurrentCulture, out result)
            || decimal.TryParse(display, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryConvertDate(object? raw, string display, out DateTime result)
    {
        if (raw is DateTime date)
        {
            result = date.Date;
            return true;
        }
        return TryParseFilterDate(display, out result);
    }

    private static bool TryParseFilterDate(string value, out DateTime result)
    {
        if (DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out result)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out result))
        {
            result = result.Date;
            return true;
        }
        return false;
    }

    private static bool TryConvertBoolean(object? raw, string display, out bool result)
    {
        if (raw is bool boolean)
        {
            result = boolean;
            return true;
        }
        if (bool.TryParse(display, out result))
            return true;
        if (string.Equals(display, "1", StringComparison.Ordinal))
        {
            result = true;
            return true;
        }
        if (string.Equals(display, "0", StringComparison.Ordinal))
        {
            result = false;
            return true;
        }
        return false;
    }

    private GridFilterCellTemplateContext CreateFilterCellTemplateContext(GridColumn column) => new()
    {
        Column = column,
        Value = GetColumnFilterValue(column.Field),
        Operator = GetFilterRowOperator(column),
        Operators = GetFilterRowOperators(column),
        ValueChanged = value =>
        {
            _filterRowDrafts[column.Field] = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
            return InvokeAsync(StateHasChanged);
        },
        OperatorChanged = filterOperator =>
        {
            _filterRowOperators[column.Field] = filterOperator;
            return InvokeAsync(StateHasChanged);
        },
        ApplyAsync = () => CommitFilterRowAsync(column.Field, GetColumnFilterValue(column.Field)),
        ClearAsync = () => ClearFilterRowAsync(column.Field)
    };

    private GridFilterMenuTemplateContext CreateFilterMenuTemplateContext(GridColumn column)
    {
        var values = GetColumnFilterValueCandidates(column.Field)
            .Select(candidate => new GridFilterValueOption(
                candidate.Value,
                candidate.DisplayText,
                _filterCheckedDraft.Contains(candidate.Value)))
            .ToArray();
        var allVisibleValues = values.Select(value => value.Value).ToArray();

        return new GridFilterMenuTemplateContext
        {
            Column = column,
            Value = _filterTextDraft,
            Operator = _filterOperatorDraft,
            SecondValue = _secondFilterTextDraft,
            SecondOperator = _secondFilterOperatorDraft,
            LogicalOperator = _filterLogicalOperatorDraft,
            Operators = GetFilterRowOperators(column),
            DistinctValues = values,
            ChecklistSearchText = _filterChecklistSearchDraft,
            SelectAllState = GetFilterFieldSelectionState(column.Field, allVisibleValues),
            ValueChanged = value =>
            {
                _filterTextDraft = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
                return InvokeAsync(StateHasChanged);
            },
            OperatorChanged = filterOperator =>
            {
                _filterOperatorDraft = filterOperator;
                return InvokeAsync(StateHasChanged);
            },
            SecondValueChanged = value =>
            {
                _secondFilterTextDraft = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
                return InvokeAsync(StateHasChanged);
            },
            SecondOperatorChanged = filterOperator =>
            {
                _secondFilterOperatorDraft = filterOperator;
                return InvokeAsync(StateHasChanged);
            },
            LogicalOperatorChanged = logicalOperator =>
            {
                _filterLogicalOperatorDraft = logicalOperator;
                return InvokeAsync(StateHasChanged);
            },
            ChecklistSearchTextChanged = SetFilterChecklistSearchAsync,
            SetValueSelectedAsync = (value, selected) =>
            {
                _filterChecklistDraftTouched = true;
                if (selected)
                    _filterCheckedDraft.Add(value);
                else
                    _filterCheckedDraft.Remove(value);
                return InvokeAsync(StateHasChanged);
            },
            SetAllVisibleValuesSelectedAsync = selected =>
                SetFilterFieldSelected(column.Field, selected, allVisibleValues),
            ApplyAsync = () => ApplyFilterPopupAsync(close: true),
            ClearAsync = () => ClearFilterMenuTemplateAsync(column.Field),
            CloseAsync = () =>
            {
                CloseFilterPopup();
                return InvokeAsync(StateHasChanged);
            }
        };
    }

    private GridFilterMenuButtonsTemplateContext CreateFilterMenuButtonsTemplateContext(GridColumn column) => new()
    {
        Column = column,
        AutoApply = _filterPopupAutoApply,
        ApplyAsync = () => ApplyFilterPopupAsync(close: true),
        ClearAsync = () => ClearFilterMenuTemplateAsync(column.Field),
        CloseAsync = () =>
        {
            CloseFilterPopup();
            return InvokeAsync(StateHasChanged);
        }
    };

    private async Task ClearFilterMenuTemplateAsync(string field)
    {
        ClearFilter(field);
        CloseFilterPopup();
        if (UsesItemsProvider)
            await ReloadItemsAsync();
        await InvokeAsync(StateHasChanged);
        await NotifyGridStateChangedAsync(GridStateChangeKind.Filtering);
    }

    private async Task QueueFilterPopupAutoApplyAsync()
    {
        _filterPopupAutoApplyCts?.Cancel();
        _filterPopupAutoApplyCts?.Dispose();
        var cts = _filterPopupAutoApplyCts = new CancellationTokenSource();
        try
        {
            if (EffectiveFilterDelay > 0)
                await Task.Delay(EffectiveFilterDelay, cts.Token);
            if (!cts.IsCancellationRequested && _filterPopupAutoApply && _filterPopupField != null)
                await InvokeAsync(() => ApplyFilterPopupAsync(close: false));
        }
        catch (OperationCanceledException)
        {
            // A newer popup change owns the apply.
        }
    }

    private void DisposeFilteringState()
    {
        foreach (var cts in _filterRowDebounce.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _filterRowDebounce.Clear();
        _filterPopupAutoApplyCts?.Cancel();
        _filterPopupAutoApplyCts?.Dispose();
        _filterPopupAutoApplyCts = null;
        CancelFilterPopupTyping(discard: true);
        _sidePanelSearchCts?.Cancel();
        _sidePanelSearchCts = null;
        CancelProviderFilterValuesLoad();
    }
}
