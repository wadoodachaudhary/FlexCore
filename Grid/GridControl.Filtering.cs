using Microsoft.AspNetCore.Components;
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
                if (!cts.IsCancellationRequested && _filterRowDrafts.TryGetValue(field, out var value))
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

    private async Task CommitFilterRowAsync(string field, string? value)
    {
        var column = FindColumnByField(field);
        var filterOperator = column == null
            ? TextFilterOperator.Contains
            : GetFilterRowOperator(column);
        var normalized = value ?? string.Empty;

        if (EventsRef?.Filtering.HasDelegate == true)
        {
            var args = new FilterEventArgs { Field = field, Value = normalized };
            await EventsRef.Filtering.InvokeAsync(args);
            if (args.Cancel)
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
        if (_filterRowDebounce.Remove(field, out var pending))
        {
            pending.Cancel();
            pending.Dispose();
        }

        _filterRowDrafts.Remove(field);
        _filterRowOperators.Remove(field);
        _simpleColumnFilters.Remove(field);
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
        CancelProviderFilterValuesLoad();
    }
}
