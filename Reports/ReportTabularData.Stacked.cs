using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

public sealed partial record ReportTabularData
{
    private static ReportTabularData CreateStackedCrossTab(ReportAnalysisDefinition definition, ReportNativeCrossTabLayout native,
        PivotControl<Dictionary<string, object>> pivot, GridExportTable table)
    {
        var count = definition.Measures.Count;
        var columns = new List<ReportTabularColumn> { new("", definition.RowHeaderWidthTwips, true) };
        var parents = new List<string> { "" };
        var starts = new List<int>();
        var hasColumnGroups = definition.ColumnFields.Count > 0;
        for (var i = 1; i < table.Columns.Count; i += count)
        {
            // The generic pivot has both a value block and its identical total block.
            // A row-only native grid draws those measures once, in the total column.
            if (!hasColumnGroups && i > 1) break;
            var total = !hasColumnGroups || definition.ShowTotals && i + count == table.Columns.Count;
            if (hasColumnGroups && total && native.SuppressRowGrandTotals) continue;
            var path = pivot.ExportColumnPath(i - 1);
            columns.Add(new(total ? (native.ParentHeaderHeight > 0 ? "" : native.Appearance!.ColumnTotalCaption) : path.Last(), total ? native.TotalWidth : definition.ValueColumnWidthTwips, false, total));
            parents.Add((total ? native.Appearance!.ColumnTotalCaption : path.First()).ReplaceLineEndings("\n"));
            starts.Add(i);
        }
        var formats = native.StackFormats.Split(';').Select(group => group.Split(',')).ToArray();
        var rows = new List<ReportTabularRow>();
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            if (row.IsBold && native.SuppressColumnGrandTotals) continue;
            var cells = new List<string> { (row.IsBold ? native.Appearance!.RowTotalCaption : Format(row.Values[0], "")).ReplaceLineEndings("\n") };
            for (var c = 0; c < starts.Count; c++)
            {
                var mask = formats[(row.IsBold ? 2 : 0) + (columns[c + 1].IsGrandTotal ? 1 : 0)];
                cells.Add(string.Join("\n", Enumerable.Range(0, count).Select(m =>
                    FormatSummary(pivot.ExportCellValue(r, starts[c] + m), m < mask.Length ? mask[m] : definition.Measures[m].Format))));
            }
            rows.Add(new(cells, row.IsBold));
        }
        return new(columns, rows, 1) { NativeCrossTab = native, ParentHeadings = native.ParentHeaderHeight > 0 ? parents : null };
    }

    private static string FormatSummary(object? value, string format)
    {
        if (value is decimal or double or float && format.Length > 1 && format[0] is 'N' or 'F'
            && int.TryParse(format.AsSpan(1), out var places) && places is >= 0 and <= 28)
            value = decimal.Round(Convert.ToDecimal(value), places, MidpointRounding.AwayFromZero);
        return Format(value, format);
    }

    private ReportTableFragment ParentHeader(List<int> indexes, ReportTabularColumn[] columns, int pane, ReportNativeCrossTabLayout native)
    {
        var cells = new List<ReportTableCellFragment>();
        for (var i = 0; i < columns.Length;)
        {
            var value = ParentHeadings![indexes[i]];
            var end = i + 1;
            while (!columns[i].IsRowHeader && end < columns.Length && !columns[end].IsGrandTotal && ParentHeadings[indexes[end]] == value) end++;
            cells.Add(new(i, end - i, value, null, true, true, -2, indexes[i]));
            i = end;
        }
        return new(columns, cells.Select(c => c.Text).ToArray(), pane, -2, 0, true, false, native.ParentHeaderHeight)
        { LayoutCells = cells, NativeCrossTab = native with { ColumnStyle = native.ParentHeaderStyle } };
    }
}
