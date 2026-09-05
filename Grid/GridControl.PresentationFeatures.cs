using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using System.Globalization;

namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    /// <summary>Pager placement. The default preserves the historical bottom pager.</summary>
    [Parameter] public GridPagerPosition PagerPosition { get; set; } = GridPagerPosition.Bottom;

    /// <summary>Optional complete replacement for the default pager contents.</summary>
    [Parameter] public RenderFragment<GridPagerContext>? PagerTemplate { get; set; }

    /// <summary>
    /// Optional template for all data cells in a flat row. The template must emit
    /// the appropriate number of td elements. As with other full-row templates,
    /// built-in cell editing and per-cell behavior inside that row are host-owned.
    /// </summary>
    [Parameter] public RenderFragment<GridRowTemplateContext<TValue>>? RowTemplate { get; set; }

    /// <summary>Content shown when the current data view has no records.</summary>
    [Parameter] public RenderFragment? NoDataTemplate { get; set; }

    /// <summary>Custom content appended to the built-in grid toolbar.</summary>
    [Parameter] public RenderFragment? GridToolBarTemplate { get; set; }

    /// <summary>Optional replacement for the built-in column chooser body.</summary>
    [Parameter] public RenderFragment<GridColumnChooserContext>? ColumnChooserTemplate { get; set; }

    [Parameter] public GridDataLayoutMode DataLayoutMode { get; set; } = GridDataLayoutMode.Columns;
    [Parameter] public GridAdaptiveMode AdaptiveMode { get; set; } = GridAdaptiveMode.None;
    [Parameter] public int StackedColumnsCount { get; set; } = 1;

    /// <summary>Accessible name announced for the data grid.</summary>
    [Parameter] public string AccessibleLabel { get; set; } = "Data grid";

    /// <summary>
    /// Optional host keyboard overrides. Keys use browser names with optional
    /// Ctrl, Alt, Shift, or Meta prefixes, for example Ctrl+Home or Alt+ArrowDown.
    /// </summary>
    [Parameter] public IReadOnlyDictionary<string, GridKeyboardCommand>? CustomKeyboardShortcuts { get; set; }

    private bool ShowPagerAtTop =>
        PagerPosition is GridPagerPosition.Top or GridPagerPosition.Both;

    private bool ShowPagerAtBottom =>
        PagerPosition is GridPagerPosition.Bottom or GridPagerPosition.Both;

    private GridPagerContext BuildPagerContext() => new(
        _pageState.CurrentPage,
        _pageState.TotalPages,
        _pageState.PageSize,
        _pageState.TotalRecords,
        ResolvedPageSizes,
        GetPageNumbers().ToList(),
        GridRowCountText,
        GoToPage,
        SetPageSizeFromTemplateAsync);

    private async Task SetPageSizeFromTemplateAsync(int size)
    {
        await HandlePageSizeValueChanged(size);
        await InvokeAsync(StateHasChanged);
    }

    private GridRowTemplateContext<TValue> BuildRowTemplateContext(
        TValue item,
        int rowIndex,
        bool selected) => new(item, rowIndex, selected, IsRowExpanded(item));

    private GridColumnChooserContext BuildColumnChooserTemplateContext() => new(
        _chooseColumnsRows.Select(row => new GridColumnChooserItem(
            row.Field,
            row.Header,
            row.Visible,
            row.CanHide,
            string.Equals(row.Field, _chooseColumnsSelectedField, StringComparison.OrdinalIgnoreCase))).ToList(),
        ChooseColumnsSelect,
        SetChooseColumnVisibility);

    private void SetChooseColumnVisibility(string field, bool visible)
    {
        var row = _chooseColumnsRows.FirstOrDefault(candidate =>
            string.Equals(candidate.Field, field, StringComparison.OrdinalIgnoreCase));
        if (row == null || (!visible && !row.CanHide))
            return;

        row.Visible = visible;
        ChooseColumnsSelect(row.Field);
    }

    private string PresentationCssClass => string.Join(' ', new[]
    {
        DataLayoutMode == GridDataLayoutMode.Stacked ? "fx-grid-layout-stacked" : null,
        AdaptiveMode == GridAdaptiveMode.Auto ? "fx-grid-adaptive-auto" : null
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private string StackedLayoutStyle =>
        $"--fx-grid-stacked-columns:{Math.Clamp(StackedColumnsCount, 1, 6)};";

    private int GridHeaderAriaRowCount
    {
        get
        {
            if (!ShowHeader)
                return 0;

            var count = 1;
            if (GetColumnHeaderBandRuns().Any(run => run.Band != null))
                count++;
            if (FilterMode is GridFilterMode.Simple or GridFilterMode.SimpleWithMenu or GridFilterMode.Excel)
                count++;
            return count;
        }
    }

    private bool HasIndeterminateAriaRowStructure =>
        (AllowGrouping && _groupDescriptors.Count > 0)
        || HasDetailTemplate
        || AggregateRows?.Any(row => row.ShowInFooter || row.ShowInGroupFooter) == true
        || (_isEditing && EditSettingsRef?.Mode == EditMode.Inline)
        || (UsesItemsProvider && _providerTotalCount < 0);

    private string GridAriaRowCount
    {
        get
        {
            // ARIA permits -1 when the total row count is not known. Group,
            // detail, inline-edit and aggregate rows make the accessible row
            // structure dynamic, so reporting only the record count would be
            // actively misleading.
            if (HasIndeterminateAriaRowStructure)
                return "-1";

            var records = Math.Max(0, UsesItemsProvider ? _providerTotalCount : GetPassSortedRows().Count);
            var emptyRow = records == 0 ? 1 : 0;
            return (GridHeaderAriaRowCount + records + emptyRow).ToString(CultureInfo.InvariantCulture);
        }
    }

    private int? GetGridDataAriaRowIndex(int zeroBasedRowIndex) =>
        HasIndeterminateAriaRowStructure
            ? null
            : GridHeaderAriaRowCount + Math.Max(0, zeroBasedRowIndex) + 1;

    private string GridAriaColumnCount => Math.Max(0, TotalColumnCount).ToString();

    private static string? GetColumnAriaSort(ColumnState state) => state.SortDirection switch
    {
        SortDirection.Ascending => "ascending",
        SortDirection.Descending => "descending",
        _ => null
    };

    private async Task<bool> TryHandleCustomKeyboardShortcutAsync(KeyboardEventArgs args)
    {
        if (CustomKeyboardShortcuts is not { Count: > 0 })
            return false;

        var gesture = NormalizeKeyboardGesture(args);
        if (!CustomKeyboardShortcuts.TryGetValue(gesture, out var command)
            && !TryGetCaseInsensitiveShortcut(gesture, out command))
            return false;

        switch (command)
        {
            case GridKeyboardCommand.MoveLeft:
                return await NavigateFromActiveCellAsync(backwards: true, allowRowWrap: false);
            case GridKeyboardCommand.MoveRight:
                return await NavigateFromActiveCellAsync(backwards: false, allowRowWrap: false);
            case GridKeyboardCommand.MoveUp:
                return await NavigateVerticallyFromActiveCellAsync(-1);
            case GridKeyboardCommand.MoveDown:
                return await NavigateVerticallyFromActiveCellAsync(1);
            case GridKeyboardCommand.FirstRow:
                return await NavigateByScrollKeyFromActiveCellAsync(GridScrollNavigationKey.Home);
            case GridKeyboardCommand.LastRow:
                return await NavigateByScrollKeyFromActiveCellAsync(GridScrollNavigationKey.End);
            case GridKeyboardCommand.PageUp:
                return await NavigateByScrollKeyFromActiveCellAsync(GridScrollNavigationKey.PageUp);
            case GridKeyboardCommand.PageDown:
                return await NavigateByScrollKeyFromActiveCellAsync(GridScrollNavigationKey.PageDown);
            case GridKeyboardCommand.BeginEdit:
                if (_activeCell.HasValue
                    && TryGetActiveKeyboardBatchEditCell(out var editItem, out var editRow, out var editColumn))
                {
                    return await TryStartBatchEdit(editItem, editRow, editColumn, selectAllOnStart: true);
                }
                return false;
            case GridKeyboardCommand.SaveEdit:
                if (_isEditing)
                {
                    await SaveEdit();
                    return true;
                }
                if (_batchEditItem != null)
                {
                    await CommitBatchEdit();
                    return true;
                }
                return false;
            case GridKeyboardCommand.CancelEdit:
                if (_isEditing)
                {
                    CancelEdit();
                    return true;
                }
                if (_batchEditItem != null)
                {
                    var activeBatchItem = _batchEditItem;
                    await HandleBatchEditKeyDown(
                        activeBatchItem,
                        _batchEditField,
                        new KeyboardEventArgs { Key = "Escape" });
                    return _batchEditItem == null;
                }
                return false;
            case GridKeyboardCommand.SelectAll:
                ToggleSelectAll();
                return true;
            case GridKeyboardCommand.ClearSelection:
                await ClearSelectionAsync();
                return true;
            case GridKeyboardCommand.ExpandDetail:
            case GridKeyboardCommand.CollapseDetail:
                var selected = _selectedItems.FirstOrDefault();
                if (selected == null || !HasDetailTemplate)
                    return false;
                if (command == GridKeyboardCommand.ExpandDetail)
                    await ExpandRow(selected);
                else
                    await CollapseRow(selected);
                return true;
            default:
                return false;
        }
    }

    private bool TryGetCaseInsensitiveShortcut(string gesture, out GridKeyboardCommand command)
    {
        if (CustomKeyboardShortcuts != null)
        {
            foreach (var shortcut in CustomKeyboardShortcuts)
            {
                if (string.Equals(shortcut.Key?.Trim(), gesture, StringComparison.OrdinalIgnoreCase))
                {
                    command = shortcut.Value;
                    return true;
                }
            }
        }

        command = default;
        return false;
    }

    private string? CustomKeyboardShortcutDomValue =>
        CustomKeyboardShortcuts is { Count: > 0 }
            ? string.Join('|', CustomKeyboardShortcuts.Keys
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim().ToLowerInvariant()))
            : null;

    private static string NormalizeKeyboardGesture(KeyboardEventArgs args)
    {
        var parts = new List<string>(5);
        if (args.CtrlKey) parts.Add("Ctrl");
        if (args.AltKey) parts.Add("Alt");
        if (args.ShiftKey) parts.Add("Shift");
        if (args.MetaKey) parts.Add("Meta");
        parts.Add(args.Key switch
        {
            " " => "Space",
            "Esc" => "Escape",
            _ => args.Key
        });
        return string.Join('+', parts);
    }
}
