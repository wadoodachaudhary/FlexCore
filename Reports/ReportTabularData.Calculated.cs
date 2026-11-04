using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

public sealed partial record ReportTabularData
{
    private sealed record GridAxis(PivotExportAxis Source, ReportCalculatedMember? Member = null)
    {
        internal IReadOnlyList<object?> Path => Source.Path;
        internal int Level => Path.Count;
        internal string Label => Source.Labels.LastOrDefault() ?? "";
        internal bool Total => Source.IsTotal;
    }

    private static ReportTabularData CreateCalculatedCrossTab(ReportDesignerElement element, ReportAnalysisSnapshot snapshot,
        PivotControl<Dictionary<string, object>> pivot, GridExportTable table)
    {
        var definition = snapshot.Definition;
        if (definition.Validate("CrossTab") is { } error) throw new NotSupportedException(error);
        var native = definition.NativeCrossTab;
        var rows = pivot.ExportAxis(false, definition.ShowTotals).Select(axis => new GridAxis(axis)).ToList();
        var columns = pivot.ExportAxis(true, definition.ShowTotals).Select(axis => new GridAxis(axis)).ToList();
        if (native?.ColumnTotalsBefore == true) columns = columns.OrderBy(axis => axis.Level == 0 ? 0 : 1).ToList();
        if (native?.RowTotalsBefore == true) rows = rows.OrderBy(axis => axis.Level == 0 ? 0 : 1).ToList();
        if ((long)rows.Count * columns.Count * definition.Measures.Count > 2_000_000)
            throw new InvalidDataException("Cross-tab hierarchy exceeds its cell limit.");
        var cache = new Dictionary<(GridAxis Row, GridAxis Column, int Summary), object?>();
        var evaluating = new HashSet<(GridAxis Row, GridAxis Column, int Summary)>();
        var formulas = definition.CalculatedMembers.ToDictionary(member => member, member => member.Values.Select(value => value.Compile()).ToArray());
        var steps = 0;
        var aliases = definition.RowFields.Select((field, level) => (Name: field.Trim('{', '}'), Column: false, Level: level))
            .Concat(definition.ColumnFields.Select((field, level) => (Name: field.Trim('{', '}'), Column: true, Level: level))).ToList();
        foreach (var template in native?.Templates ?? [])
            if (template.Alias.Length > 0 && template.Axis is 207 or 211)
            {
                var level = template.Axis == 207 ? template.ColumnLevel : template.RowLevel;
                if (level > 0) aliases.Add((template.Alias.Trim('{', '}'), template.Axis == 207, level - 1));
            }
        (bool Column, int Level) Axis(string alias)
        {
            var found = aliases.FirstOrDefault(item => item.Name.Equals(alias.Trim('{', '}'), StringComparison.OrdinalIgnoreCase));
            return found.Name is not null ? (found.Column, found.Level) : throw new InvalidDataException($"Unknown cross-tab group alias '{alias}'.");
        }
        object? Part(GridAxis axis, int level) => axis.Path.ElementAtOrDefault(level) is PivotGroupValue group ? group.Label : axis.Path.ElementAtOrDefault(level);
        int Find(List<GridAxis> axis, IReadOnlyList<object?> path) => axis.FindIndex(item => item.Path.Count == path.Count
            && item.Path.Select((_, i) => Equal(Part(item, i), path[i])).All(value => value));
        object? Resolve(string reference, int row, int column)
        {
            var axis = aliases.FirstOrDefault(item => item.Name.Equals(reference.Trim('{', '}'), StringComparison.OrdinalIgnoreCase));
            if (axis.Name is not null) return Part(At(axis.Column ? columns : rows, axis.Column ? column : row), axis.Level);
            var source = reference.StartsWith("{?", StringComparison.Ordinal) ? snapshot.Rows
                : pivot.ExportAxisRows(At(rows, row).Source, At(columns, column).Source);
            var values = source.Select(record => record.GetValueOrDefault(reference)).Distinct().Take(2).ToArray();
            return values.Length <= 1 ? values.FirstOrDefault() : throw new InvalidDataException($"Cross-tab condition '{reference}' has no unique value in this grid scope.");
        }
        CrystalFormulaContext Context(int row, int column, int summary, object? current = null, object? attribute = null) => new()
        {
            Now = snapshot.PrintTime, CurrentFieldValue = () => current, DefaultAttribute = () => attribute,
            Resolve = reference => Resolve(reference, row, column),
            Grid = new()
            {
                RowIndex = row, ColumnIndex = column, SummaryIndex = summary,
                RowCount = rows.Count, ColumnCount = columns.Count, SummaryCount = definition.Measures.Count,
                RowGroupCount = definition.RowFields.Count, ColumnGroupCount = definition.ColumnFields.Count,
                ValueAt = Value, PathIndex = (isColumn, path) => Find(isColumn ? columns : rows, path),
                GroupValue = (alias, index) =>
                {
                    var a = Axis(alias);
                    return Part(At(a.Column ? columns : rows, index ?? (a.Column ? column : row)), a.Level);
                },
                LabelAt = (alias, index) =>
                {
                    var a = Axis(alias);
                    return At(a.Column ? columns : rows, index).Source.Labels.ElementAtOrDefault(a.Level) ?? "";
                },
                GroupIndex = (isColumn, index) => At(isColumn ? columns : rows, index).Level,
                IndexOf = (alias, value, start) =>
                {
                    var a = Axis(alias); var items = a.Column ? columns : rows;
                    if (start < 0 || start > items.Count) throw new InvalidDataException("Cross-tab search start is out of range.");
                    return items.FindIndex(start, item => item.Level > a.Level && Equal(Part(item, a.Level), value));
                },
                TotalValue = (alias, measure) =>
                {
                    var a = Axis(alias);
                    var items = a.Column ? columns : rows;
                    var currentAxis = At(items, a.Column ? column : row);
                    var parent = Find(items, Enumerable.Range(0, Math.Min(a.Level, currentAxis.Level)).Select(i => Part(currentAxis, i)).ToArray());
                    return a.Column ? Value(row, parent, measure) : Value(parent, column, measure);
                }
            }
        };
        object? Value(int row, int column, int summary)
        {
            if (++steps > 2_000_000) throw new InvalidDataException("Cross-tab calculation limit exceeded.");
            if (summary < 0 || summary >= definition.Measures.Count) throw new InvalidDataException("Cross-tab summary index is out of range.");
            var r = At(rows, row); var c = At(columns, column); var key = (r, c, summary);
            if (cache.TryGetValue(key, out var known)) return known;
            if (evaluating.Count >= 128 || !evaluating.Add(key)) throw new InvalidDataException("Circular cross-tab calculated-member reference.");
            try
            {
                // At a calculated row/column intersection Crystal uses the row's formula.
                var member = r.Member ?? c.Member;
                return cache[key] = member is null ? pivot.ExportAxisValue(r.Source, c.Source, summary)
                    : formulas[member][summary].Evaluate(Context(row, column, summary));
            }
            finally { evaluating.Remove(key); }
        }

        foreach (var member in definition.CalculatedMembers)
        {
            var axis = member.IsRow ? rows : columns;
            var insert = member.Insertion.Compile(false);
            var header = member.Header.Compile("");
            var groupValue = member.GroupValue.Compile("");
            foreach (var candidate in axis.ToArray())
            {
                if (candidate.Member is not null || candidate.Level != member.GroupLevel) continue;
                var index = axis.IndexOf(candidate);
                var context = Context(member.IsRow ? index : 0, member.IsRow ? 0 : index, 0);
                if (insert.Evaluate(context) is not true) continue;
                var label = Convert.ToString(header.Evaluate(context)) ?? "";
                var value = groupValue.IsEmpty ? label : groupValue.Evaluate(context);
                var path = candidate.Path.Take(member.GroupLevel - 1).Append(value).ToArray();
                var labels = candidate.Source.Labels.Take(member.GroupLevel - 1).Append(label).ToArray();
                axis.Insert(index + (member.InsertAfter ? 1 : 0), new(new(path, labels, false), member));
                if (columns.Count > 256 || (long)rows.Count * columns.Count * definition.Measures.Count > 2_000_000)
                    throw new InvalidDataException("Calculated cross-tab exceeds its cell limit.");
                cache.Clear();
            }
        }

        ReportNativeCrossTabTemplate? Template(int axis, int rowLevel, int colLevel, int summary = 0) => native?.Templates.FirstOrDefault(item =>
            item.Axis == axis && item.RowLevel == rowLevel && item.ColumnLevel == colLevel && item.Summary == summary);
        var appearance = native?.Appearance;
        var rowCount = definition.RowFields.Count;
        var columnMap = columns.SelectMany((axis, index) => Enumerable.Range(0, definition.Measures.Count).Select(summary => (Axis: axis, Index: index, Summary: summary)))
            .Where(item => (native?.SuppressRowGrandTotals != true || item.Axis.Level != 0)
                && (!item.Axis.Total || native?.SuppressedColumnSubtotals.Contains(item.Axis.Level) != true)).ToArray();
        var rowMap = rows.Select((axis, index) => (Axis: axis, Index: index)).Where(item => (native?.SuppressColumnGrandTotals != true || item.Axis.Level != 0)
            && (!item.Axis.Total || native?.SuppressedRowSubtotals.Contains(item.Axis.Level) != true)).ToArray();
        int Width(ReportNativeCrossTabTemplate? template, int fallback) => template is { Width: > 0 } ? template.Width + 2 * template.PaddingX : fallback;
        var renderedColumns = Enumerable.Range(0, rowCount).Select(level => new ReportTabularColumn("",
            Width(Template(211, level + 1, 0), definition.RowHeaderWidthTwips), true))
            .Concat(columnMap.Select(item => new ReportTabularColumn(item.Axis.Level == 0 && appearance is not null ? appearance.ColumnTotalCaption
                    : string.Join(" / ", item.Axis.Source.Labels) + (definition.Measures.Count > 1 ? " (" + definition.Measures[item.Summary].Caption + ")" : ""),
                Width(Template(216, rowCount, item.Axis.Level, item.Summary), definition.ValueColumnWidthTwips),
                false, item.Axis.Level == 0))).ToArray();
        var renderedRows = new List<ReportTabularRow>();
        var rowHeights = new List<int>();
        var cellStyles = new List<IReadOnlyList<ReportNativeCrossTabCellStyle?>>();
        var headerStyles = new List<ReportNativeCrossTabCellStyle?>(Enumerable.Repeat<ReportNativeCrossTabCellStyle?>(null, rowCount));
        ReportNativeCrossTabCellStyle? Styled(ReportNativeCrossTabCellStyle? style, int row, int column, int summary, object? value, ref string text)
        {
            if (style is null) return null;
            var applied = ReportCrossTabFormatting.Apply(style, text, attribute => Context(row, column, summary, value, attribute));
            if (applied.Display is not null) text = applied.Display;
            if (applied.Style.Suppressed) text = "";
            return applied.Style;
        }
        foreach (var item in columnMap)
        {
            var text = renderedColumns[headerStyles.Count].Caption;
            headerStyles.Add(Styled(Template(207, 0, item.Axis.Level)?.Style ?? native?.StyleFor(true, false, false, item.Axis.Level == 0),
                0, item.Index, item.Summary, item.Axis.Path.LastOrDefault(), ref text));
            renderedColumns[headerStyles.Count - 1] = renderedColumns[headerStyles.Count - 1] with { Caption = text };
        }
        foreach (var row in rowMap)
        {
            rowHeights.Add(native?.Templates.Where(template => template.Axis == 216 && template.RowLevel == row.Axis.Level)
                .Select(template => template.Height + 2 * template.PaddingY).DefaultIfEmpty(definition.RowHeightTwips).Max() ?? definition.RowHeightTwips);
            var values = new List<string>(); var styles = new List<ReportNativeCrossTabCellStyle?>();
            for (var level = 0; level < rowCount; level++)
            {
                var text = row.Axis.Level == 0 ? level == 0 ? appearance?.RowTotalCaption ?? "Total" : ""
                    : row.Axis.Source.Labels.ElementAtOrDefault(level) ?? "";
                var rowTemplate = Template(211, row.Axis.Level == 0 ? 0 : level + 1, 0);
                var style = rowTemplate?.Style ?? native?.StyleFor(false, true, row.Axis.Level == 0, false);
                if (style is not null && rowTemplate is { CellWidth: > 0 }) style = style with
                    { ClipWidthTwips = Math.Max(rowTemplate.CellWidth, rowTemplate.Width + 2 * rowTemplate.PaddingX) };
                styles.Add(Styled(style, row.Index, 0, 0, Part(row.Axis, level), ref text)); values.Add(text);
            }
            foreach (var column in columnMap)
            {
                var value = Value(row.Index, column.Index, column.Summary);
                var template = Template(216, row.Axis.Level, column.Axis.Level, column.Summary);
                var text = value is null or DBNull or "" ? "" : value is DateTime date && template?.DateTimeFormat is { } dateFormat
                    ? dateFormat.Format(date, System.Globalization.CultureInfo.CurrentCulture)
                    : template?.NumericFormat is { } number ? number.Format(value) : Format(value, definition.Measures[column.Summary].Format);
                styles.Add(Styled(template?.Style ?? native?.StyleFor(false, false, row.Axis.Level == 0, column.Axis.Level == 0),
                    row.Index, column.Index, column.Summary, value, ref text)); values.Add(text);
            }
            renderedRows.Add(new(values, row.Axis.Total)); cellStyles.Add(styles);
        }
        return new(renderedColumns, renderedRows, native is null ? rowCount : 0) { NativeCrossTab = native, NativeCellStyles = cellStyles, NativeHeaderStyles = headerStyles,
            NativeRowHeights = rowHeights, NativeGrandTotalRows = rowMap.Select(row => row.Axis.Level == 0).ToArray() };
    }

    private static GridAxis At(List<GridAxis> axis, int index) => index >= 0 && index < axis.Count ? axis[index]
        : throw new InvalidDataException("Cross-tab grid index is out of range.");
    private static bool Equal(object? left, object? right) => left is string a && right is string b
        ? a.TrimEnd().Equals(b.TrimEnd(), StringComparison.OrdinalIgnoreCase) : Equals(left, right)
        || left is IConvertible && right is IConvertible && CrystalFormula.Compare(left, right) == 0;
}
