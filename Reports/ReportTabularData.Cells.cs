namespace Fx.ControlKit.Reports;

public sealed partial record ReportTabularData
{
    private sealed record Cell(int Row, int Column, int RowSpan, int ColumnSpan, string Text, ReportTabularData? Region);
    private readonly List<Cell> _cells = [];
    private int MinimumLineHeight { get; set; }
    private bool RepeatHeaders { get; set; }

    private ReportTabularData WithCells(ReportDesignerElement element, ReportAnalysisSnapshot snapshot,
        Func<ReportMatrixCell, IReadOnlyList<Dictionary<string, object>>> scope)
    {
        MinimumLineHeight = snapshot.Definition.Tree().Max(node => node.RowHeightTwips);
        RepeatHeaders = snapshot.Definition.RepeatHeaders;
        foreach (var cell in snapshot.Definition.Cells)
        {
            if (Rows.Count == 0) continue;
            if (cell.Row >= Rows.Count || cell.Column >= Columns.Count || (long)cell.Row + cell.RowSpan > Rows.Count
                || (long)cell.Column + cell.ColumnSpan > Columns.Count)
                throw new InvalidDataException("A merged/nested cell extends outside the evaluated data region.");
            if (_cells.Any(other => cell.Row < other.Row + other.RowSpan && other.Row < cell.Row + cell.RowSpan
                && cell.Column < other.Column + other.ColumnSpan && other.Column < cell.Column + cell.ColumnSpan))
                throw new InvalidDataException("Merged/nested cell rectangles overlap.");
            var nested = cell.Region is null ? null : Create(new() { Kind = cell.RegionKind, FontSize = element.FontSize }, new(cell.Region, scope(cell)));
            _cells.Add(new(cell.Row, cell.Column, cell.RowSpan, cell.ColumnSpan,
                cell.Text ?? Rows[cell.Row].Cells[cell.Column], nested));
        }
        return this;
    }

    private ReportTableFragment[] LayoutCells(IReadOnlyList<int> indexes, ReportTabularColumn[] selected,
        int pane, decimal fontSize, int lineHeight)
    {
        var owner = new Dictionary<(int Row, int Column), Cell>();
        foreach (var cell in _cells)
            for (var row = cell.Row; row < cell.Row + cell.RowSpan; row++)
            foreach (var column in indexes.Where(column => column >= cell.Column && column < cell.Column + cell.ColumnSpan))
                owner[(row, column)] = cell;
        for (var row = 0; row < Rows.Count; row++)
        foreach (var column in indexes)
            owner.TryAdd((row, column), new(row, column, 1, 1, Rows[row].Cells[column], null));

        var contents = new Dictionary<Cell, (string Text, ReportTableFragment? Nested)[]>();
        var repeatHeaders = new Dictionary<ReportTableFragment, IReadOnlyList<ReportTableFragment>>(ReferenceEqualityComparer.Instance);
        var rowLines = Enumerable.Repeat(1, Rows.Count).ToArray();
        foreach (var cell in owner.Values.Distinct())
        {
            var width = indexes.Select((column, position) => column >= cell.Column && column < cell.Column + cell.ColumnSpan ? selected[position].WidthTwips : 0).Sum();
            // A merge crossing horizontal panes owns its content at its anchor. Continuation panes
            // retain the cell's rectangle without evaluating or printing the data a second time.
            var visible = indexes.Contains(cell.Column) && !(pane > 0 && cell.Column < RepeatedColumns && _cells.Contains(cell));
            var content = !visible ? [] : cell.Region is null
                ? Wrap(cell.Text, width, fontSize).Select(text => (text, (ReportTableFragment?)null)).ToArray()
                : NestedLines(cell.Region, Math.Max(MinimumPaneWidthTwips, width - 30)).Select(fragment => ("", (ReportTableFragment?)fragment)).ToArray();
            contents[cell] = content;
            if (cell.RowSpan == 1) rowLines[cell.Row] = Math.Max(rowLines[cell.Row], content.Length);
        }
        foreach (var cell in contents.Keys.Where(cell => cell.RowSpan > 1).OrderBy(cell => cell.RowSpan))
        {
            var available = rowLines.Skip(cell.Row).Take(cell.RowSpan).Sum();
            rowLines[cell.Row + cell.RowSpan - 1] += Math.Max(0, contents[cell].Length - available);
        }
        if (rowLines.Sum(line => (long)line) * selected.Length > 2000000)
            throw new InvalidDataException("Expanded nested cells exceed two million line cells.");
        var starts = new int[Rows.Count + 1];
        for (var row = 0; row < Rows.Count; row++) starts[row + 1] = checked(starts[row] + rowLines[row]);
        var fragments = new List<ReportTableFragment>();
        for (var row = 0; row < Rows.Count; row++)
        for (var line = 0; line < rowLines[row]; line++)
        {
            var cells = new List<ReportTableCellFragment>();
            var text = Enumerable.Repeat("", selected.Length).ToArray();
            for (var column = 0; column < indexes.Count;)
            {
                var cell = owner[(row, indexes[column])];
                var span = 1;
                while (column + span < indexes.Count && owner[(row, indexes[column + span])] == cell) span++;
                var offset = starts[row] + line - starts[cell.Row];
                var length = starts[cell.Row + cell.RowSpan] - starts[cell.Row];
                var content = offset < contents[cell].Length ? contents[cell][offset] : ("", (ReportTableFragment?)null);
                cells.Add(new(column, span, content.Item1, content.Item2, offset == 0, offset == length - 1, cell.Row, cell.Column));
                text[column] = content.Item1;
                column += span;
            }
            var fragment = new ReportTableFragment(selected, text, pane, row, line, false, Rows[row].IsTotal, lineHeight)
            { LayoutCells = cells, KeepWithNext = cells.Any(cell => cell.Nested is { IsHeader: true } or { KeepWithNext: true }) };
            var headers = cells.Select(cell => cell.Nested is { } nested && repeatHeaders.TryGetValue(nested, out var repeated) ? repeated : []).ToArray();
            fragment = fragment with { ContinuationHeaders = Enumerable.Range(0, headers.Select(h => h.Count).DefaultIfEmpty(0).Max()).Select(index => fragment with
            {
                Cells = Enumerable.Repeat("", selected.Length).ToArray(),
                LayoutCells = cells.Select((cell, position) => cell with { Text = "", Nested = index < headers[position].Count ? headers[position][index] : null }).ToArray()
            }).ToArray() };
            fragments.Add(fragment);
        }
        return fragments.ToArray();

        IEnumerable<ReportTableFragment> NestedLines(ReportTabularData region, int width)
        {
            foreach (var part in region.PaginateColumns(width, fontSize, lineHeight))
            {
                foreach (var header in part.Headers) yield return header;
                foreach (var body in part.Rows)
                {
                    repeatHeaders[body] = (region.RepeatHeaders ? part.Headers : []).Concat(body.ContinuationHeaders).ToArray();
                    yield return body;
                }
            }
        }
    }
}
