using ClosedXML.Excel;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit.Spreadsheet;

public partial class SpreadsheetControl
{
    [Parameter] public bool AllowFiltering { get; set; } = true;
    public int FrozenRows => Math.Min(RowCount, _document.FrozenRows);
    public int FrozenColumns => Math.Min(ColumnCount, _document.FrozenColumns);
    private bool _filterOpen;
    private string _filterRange = "", _filterColumn = "A", _filterText = "";
    private SpreadsheetTextFilter _filterCondition = SpreadsheetTextFilter.Contains;

    private List<int> Axis(int start, int limit, int visible, int frozen, Func<int, bool> hidden)
    {
        var result = new List<int>();
        for (var i = 1; i <= frozen && result.Count < visible - 1; i++)
            if (!hidden(i)) result.Add(i);
        for (var i = Math.Max(frozen + 1, start); i <= limit && result.Count < visible; i++)
            if (!hidden(i)) result.Add(i);
        return result;
    }
    private List<int> ViewRows() => Axis(_rowStart, RowCount, VisibleRows, FrozenRows, r => _document.Sheet.Row(r).IsHidden);
    private List<int> ViewColumns() => Axis(_columnStart, ColumnCount, VisibleColumns, FrozenColumns, c => _document.Sheet.Column(c).IsHidden);
    private int NextVisibleRow(int row, int direction) => NextVisible(row, direction, RowCount, r => _document.Sheet.Row(r).IsHidden);
    private int NextVisibleColumn(int column, int direction) => NextVisible(column, direction, ColumnCount, c => _document.Sheet.Column(c).IsHidden);
    private static int NextVisible(int current, int direction, int limit, Func<int, bool> hidden)
    {
        for (var next = current + direction; next >= 1 && next <= limit; next += direction)
            if (!hidden(next)) return next;
        return current;
    }
    private bool HasNextRows => ViewRows().LastOrDefault() is var last && last > 0 && NextVisibleRow(last, 1) > last;
    private bool HasNextColumns => ViewColumns().LastOrDefault() is var last && last > 0 && NextVisibleColumn(last, 1) > last;

    private void MoveWindow(int rows, int columns)
    {
        if (rows > 0) _rowStart = NextVisibleRow(ViewRows().LastOrDefault(FrozenRows + 1), 1);
        if (columns > 0) _columnStart = NextVisibleColumn(ViewColumns().LastOrDefault(FrozenColumns + 1), 1);
        if (rows < 0)
            for (var i = 0; i < VisibleRows - Math.Min(FrozenRows, VisibleRows - 1); i++)
                _rowStart = Math.Max(FrozenRows + 1, NextVisibleRow(_rowStart, -1));
        if (columns < 0)
            for (var i = 0; i < VisibleColumns - Math.Min(FrozenColumns, VisibleColumns - 1); i++)
                _columnStart = Math.Max(FrozenColumns + 1, NextVisibleColumn(_columnStart, -1));
    }
    private void EnsureInView(int row, int column)
    {
        if (row > FrozenRows && !ViewRows().Contains(row)) _rowStart = row;
        if (column > FrozenColumns && !ViewColumns().Contains(column)) _columnStart = column;
    }
    private void NormalizeView()
    {
        _rowStart = Math.Clamp(_rowStart, 1, RowCount);
        _columnStart = Math.Clamp(_columnStart, 1, ColumnCount);
        var row = Math.Clamp(Selection.Row, 1, RowCount);
        var column = Math.Clamp(Selection.Column, 1, ColumnCount);
        if (_document.Sheet.Row(row).IsHidden) row = NextVisibleRow(row, 1);
        if (_document.Sheet.Row(row).IsHidden) row = NextVisibleRow(row, -1);
        if (_document.Sheet.Column(column).IsHidden) column = NextVisibleColumn(column, 1);
        if (_document.Sheet.Column(column).IsHidden) column = NextVisibleColumn(column, -1);
        if (row != Selection.Row || column != Selection.Column) Selection = new(row, column, row, column);
    }
    private string StickyStyle(int row, int column, List<int> rows, List<int> columns)
    {
        var top = row == 0 || row <= FrozenRows;
        var left = column == 0 || column <= FrozenColumns;
        if (!top && !left) return "";
        var style = "position:sticky;";
        if (top) style += $"top:{(row == 0 ? 0 : (rows.IndexOf(row) + 1) * 30)}px;";
        if (left) style += $"left:{(column == 0 ? 0 : 48 + columns.IndexOf(column) * 120)}px;";
        style += $"z-index:{(row == 0 || column == 0 ? (top && left ? 6 : 5) : top && left ? 4 : 2)};";
        return style;
    }

    public Task FreezeAsync(int rows, int columns) => Command(() => _document.Freeze(rows, columns), "Freeze");
    public Task ApplyTextFilterAsync(SpreadsheetSelection range, int column, string text, SpreadsheetTextFilter condition = SpreadsheetTextFilter.Contains)
        => !AllowFiltering ? Task.CompletedTask : Command(() => { _document.ApplyTextFilter(range, column, text, condition); _rowStart = 1; }, "Filter");
    public Task ClearFilterAsync(int column) => !AllowFiltering ? Task.CompletedTask : Command(() => _document.ClearFilter(column), "Clear column filter");
    public Task ClearFiltersAsync() => !AllowFiltering ? Task.CompletedTask : Command(_document.ClearFilters, "Clear filters");
    public Task ReapplyFiltersAsync() => !AllowFiltering ? Task.CompletedTask : Command(_document.ReapplyFilters, "Reapply filters");

    private void OpenFilter()
    {
        if (!CanEdit || !AllowFiltering) return;
        _filterRange = _document.Sheet.AutoFilter.IsEnabled ? _document.Sheet.AutoFilter.Range.RangeAddress.ToStringRelative()
            : Selection.Top != Selection.Bottom ? Selection.RangeAddress
            : _document.Sheet.RangeUsed()?.RangeAddress.ToStringRelative() ?? "A1:A2";
        _filterColumn = XLHelper.GetColumnLetterFromNumber(Selection.Column);
        _filterText = ""; _filterCondition = SpreadsheetTextFilter.Contains; _error = null; _filterOpen = true;
    }
    private async Task ApplyDialogFilterAsync()
    {
        try
        {
            var range = _document.Sheet.Range(_filterRange).RangeAddress;
            await ApplyTextFilterAsync(new(range.FirstAddress.RowNumber, range.FirstAddress.ColumnNumber, range.LastAddress.RowNumber, range.LastAddress.ColumnNumber),
                XLHelper.GetColumnNumberFromLetter(_filterColumn.Trim()), _filterText, _filterCondition);
            if (_error is null) _filterOpen = false;
        }
        catch (Exception ex) { _error = ex.Message; }
    }
    private async Task ClearDialogFilterAsync()
    {
        try { await ClearFilterAsync(XLHelper.GetColumnNumberFromLetter(_filterColumn.Trim())); if (_error is null) _filterOpen = false; }
        catch (Exception ex) { _error = ex.Message; }
    }
    private Task FilterKeyDown(KeyboardEventArgs e) => e.Key == "Enter" ? ApplyDialogFilterAsync() : Task.CompletedTask;
}
