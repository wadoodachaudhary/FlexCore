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
        internal bool Column { get; init; }
    }

    private static ReportTabularData CreateCalculatedCrossTab(ReportDesignerElement element, ReportAnalysisSnapshot snapshot,
        PivotControl<Dictionary<string, object>> pivot, GridExportTable table)
    {
        var definition = snapshot.Definition;
        if (definition.Validate("CrossTab") is { } error) throw new NotSupportedException(error);
        var native = definition.NativeCrossTab;
        var rows = pivot.ExportAxis(false, definition.ShowTotals).Select(axis => new GridAxis(axis)).ToList();
        var columns = pivot.ExportAxis(true, definition.ShowTotals).Select(axis => new GridAxis(axis) { Column = true }).ToList();
        if (native?.ColumnTotalsBefore == true) columns = columns.OrderBy(axis => axis.Level == 0 ? 0 : 1).ToList();
        if (native?.RowTotalsBefore == true) rows = rows.OrderBy(axis => axis.Level == 0 ? 0 : 1).ToList();
        if ((long)rows.Count * columns.Count * definition.Measures.Count > 2_000_000)
            throw new InvalidDataException("Cross-tab hierarchy exceeds its cell limit.");
        var cache = new Dictionary<(GridAxis Row, GridAxis Column, int Summary), object?>();
        var evaluating = new HashSet<(GridAxis Row, GridAxis Column, int Summary)>();
        var formulas = definition.CalculatedMembers.ToDictionary(member => member, member => member.Values.Select(value => value.Compile()).ToArray());
        var printFormulas = definition.GridPrintFormulas().ToDictionary(p => p.Rule, p => p.Formula);
        var assignments = new List<(CrystalFormula Formula, int Row, int Column, int Summary, object? Value, bool Attribute)>();
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
        var groupRules = new Dictionary<string, ReportAnalysisGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in definition.Groups) groupRules[group.Field] = group;
        ReportAnalysisGroup? Rule(GridAxis axis, int level) => groupRules.GetValueOrDefault((axis.Column ? definition.ColumnFields : definition.RowFields).ElementAtOrDefault(level) ?? "");
        // A plain date group gives formulas its typed period start; a specified-order or named group gives its group name.
        object? Part(GridAxis axis, int level) => axis.Path.ElementAtOrDefault(level) is PivotGroupValue group
            ? group.Identity is DateTime date && Rule(axis, level) is null ? date : group.Label : axis.Path.ElementAtOrDefault(level);
        // GridRowColumnValue keeps a date group's typed date, also under a group name formula; a specified-order group gives its group name.
        object? Typed(GridAxis axis, int level) => axis.Path.ElementAtOrDefault(level) is PivotGroupValue { Identity: DateTime date }
            && Rule(axis, level) is not { NameFormula: null, SpecifiedGroups.Count: > 0 } ? date : Part(axis, level);
        // A text lookup matches a group's printed name; any other value matches its typed value.
        bool Same(GridAxis item, int level, object? value) => Equal(value is string && item.Path.ElementAtOrDefault(level) is PivotGroupValue group
            ? group.Label : Typed(item, level), value);
        int Find(List<GridAxis> axis, IReadOnlyList<object?> path) => axis.FindIndex(item => item.Path.Count == path.Count
            && item.Path.Select((_, i) => Same(item, i, path[i])).All(value => value));
        object? Resolve(string reference, int row, int column)
        {
            var axis = aliases.FirstOrDefault(item => item.Name.Equals(reference.Trim('{', '}'), StringComparison.OrdinalIgnoreCase));
            if (axis.Name is not null) return Part(At(axis.Column ? columns : rows, axis.Column ? column : row), axis.Level);
            var source = reference.StartsWith("{?", StringComparison.Ordinal) ? snapshot.Rows
                : pivot.ExportAxisRows(At(rows, row).Source, At(columns, column).Source);
            var values = source.Select(record => record.GetValueOrDefault(reference)).Distinct().Take(2).ToArray();
            return values.Length <= 1 ? values.FirstOrDefault() : throw new InvalidDataException($"Cross-tab condition '{reference}' has no unique value in this grid scope.");
        }
        CrystalFormulaContext Context(int row, int column, int summary, object? current = null, object? attribute = null, IDictionary<string, object?>? globals = null) => new()
        {
            GlobalVariables = globals ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
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
                    var axis = At(a.Column ? columns : rows, index ?? (a.Column ? column : row));
                    return Typed(axis, a.Level);
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
                    return items.FindIndex(start, item => item.Level > a.Level && Same(item, a.Level, value));
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
                var value = member is null ? pivot.ExportAxisValue(r.Source, c.Source, summary)
                    : formulas[member][summary].Evaluate(Context(row, column, summary));
                if (member is null && definition.Measures[summary].PercentOfColumnTotal is { } denominatorIndex)
                {
                    value = Percent(value, pivot.ExportAxisValue(new([], [], true), c.Source, denominatorIndex));
                }
                return cache[key] = value;
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
                axis.Insert(index + (member.InsertAfter ? 1 : 0), new(new(path, labels, false), member) { Column = !member.IsRow });
                if (columns.Count > 256 || (long)rows.Count * columns.Count * definition.Measures.Count > 2_000_000)
                    throw new InvalidDataException("Calculated cross-tab exceeds its cell limit.");
                cache.Clear();
            }
        }

        ReportNativeCrossTabTemplate? Template(int axis, int rowLevel, int colLevel, int summary = 0) => native?.Templates.FirstOrDefault(item =>
            item.HeaderText is null && item.Axis == axis && item.RowLevel == rowLevel && item.ColumnLevel == colLevel && item.Summary == summary);
        var appearance = native?.Appearance;
        var rowCount = definition.RowFields.Count;
        var labelCount = native?.CompactRowHeaders == true ? 1 : rowCount;
        var summaryHeaders = native?.SummaryHeaderHeight > 0;
        var stack = native?.StackedRowGroups == true && !summaryHeaders && labelCount == rowCount;
        var slotsPerColumn = stack ? 1 : definition.Measures.Count;
        var parentHeadings = new List<string>(Enumerable.Repeat("", labelCount));
        var parentStyles = new List<ReportNativeCrossTabCellStyle?>(Enumerable.Repeat<ReportNativeCrossTabCellStyle?>(null, labelCount));
        var columnMap = columns.SelectMany((axis, index) => Enumerable.Range(0, slotsPerColumn).Select(summary => (Axis: axis, Index: index, Summary: summary)))
            .Where(item => (native?.SuppressRowGrandTotals != true || item.Axis.Level != 0)
                && (!item.Axis.Total || native?.SuppressedColumnSubtotals.Contains(item.Axis.Level) != true)).ToArray();
        var rowMap = rows.Select((axis, index) => (Axis: axis, Index: index)).Where(item => (native?.SuppressColumnGrandTotals != true || item.Axis.Level != 0)
            && (!item.Axis.Total || native?.SuppressedRowSubtotals.Contains(item.Axis.Level) != true)).ToArray();
        int Width(ReportNativeCrossTabTemplate? template, int fallback) => template is { Width: > 0 } ? template.Width + 2 * template.PaddingX : fallback;
        var renderedColumns = Enumerable.Range(0, labelCount).Select(level => new ReportTabularColumn("",
            native?.CompactRowHeaders == true ? native.Templates.Where(t => t.Axis == 216).Min(t => t.Left)
                : Width(Template(211, level + 1, 0), definition.RowHeaderWidthTwips), true))
            .Concat(columnMap.Select(item => new ReportTabularColumn(item.Axis.Level == 0 && appearance is not null ? appearance.ColumnTotalCaption
                    : string.Join(" / ", item.Axis.Source.Labels) + (slotsPerColumn > 1 ? " (" + definition.Measures[item.Summary].Caption + ")" : ""),
                Width(Template(216, rowCount, item.Axis.Level, item.Summary), definition.ValueColumnWidthTwips),
                false, item.Axis.Level == 0))).ToArray();
        var renderedRows = new List<ReportTabularRow>();
        var rowHeights = new List<int>();
        var cellStyles = new List<IReadOnlyList<ReportNativeCrossTabCellStyle?>>();
        var headerStyles = new List<ReportNativeCrossTabCellStyle?>(Enumerable.Repeat<ReportNativeCrossTabCellStyle?>(null, labelCount));
        ReportNativeCrossTabCellStyle? Styled(ReportNativeCrossTabCellStyle? style, int row, int column, int summary, object? value, ref string text)
        {
            if (style is null) return null;
            bool? Defer(ReportGridFormula rule, bool attribute)
            {
                if (!printFormulas.TryGetValue(rule, out var formula)) return null;
                assignments.Add((formula, row, column, summary, value, attribute));
                return formula.ConstantGridSuppression;
            }
            var applied = ReportCrossTabFormatting.Apply(style, text, attribute => Context(row, column, summary, value, attribute),
                printFormulas.Count == 0 ? null : Defer);
            if (applied.Display is not null) text = applied.Display;
            if (applied.Style.Suppressed) text = "";
            return applied.Style;
        }
        // One heading row per column group, outermost first; a null heading continues the caption above it,
        // and a continued column shares the heading cell at its left.
        var headingRows = Enumerable.Range(0, Math.Max(1, definition.ColumnFields.Count)).Select(_ => (Headings: new List<string?>(parentHeadings),
            Styles: new List<ReportNativeCrossTabCellStyle?>(parentStyles), Continued: new List<bool>(new bool[labelCount]))).ToArray();
        // A heading belongs to its own column at a total or calculated member, else to the group spanning its columns.
        bool SameHeading(int i, int level)
        {
            var (left, right) = (columnMap[i - 1], columnMap[i]);
            bool Own(GridAxis axis) => axis.Level <= level || axis.Member is not null && axis.Level - 1 <= level;
            return left.Index == right.Index || !Own(left.Axis) && !Own(right.Axis)
                && Enumerable.Range(0, level + 1).All(k => Equals(left.Axis.Path[k], right.Axis.Path[k]));
        }
        if (summaryHeaders)
        {
            for (var level = 0; level < headingRows.Length; level++)
            for (var i = 0; i < columnMap.Length; i++)
            {
                var (axis, index, summary) = columnMap[i];
                var (headings, styles, continued) = headingRows[level];
                continued.Add(i > 0 && SameHeading(i, level));
                // A heading spanning several columns is formatted once, at its first column.
                if (continued[^1]) { headings.Add(headings[^1]); styles.Add(styles[^1]); continue; }
                if (level > 0 && (axis.Level < level || axis.Level == level && !axis.Total))
                {
                    headings.Add(null); styles.Add(headingRows[level - 1].Styles[labelCount + i]); continue;
                }
                var total = axis.Level == level;
                // A group's subtotal prints that group's own caption in its own format.
                var caption = total && level > 0 ? native?.ColumnSubtotalCaptions.FirstOrDefault(c => c.Level == level) : null;
                var text = caption?.Text ?? (total ? appearance?.ColumnTotalCaption ?? "Total" : axis.Source.Labels.ElementAtOrDefault(level) ?? "");
                // A date group prints its period; a group name formula or a calculated member prints as authored.
                if (!total && Rule(axis, level)?.NameFormula is null && !(axis.Member is not null && level == axis.Level - 1)
                    && Part(axis, level) is { } part && DateTime.TryParse(Convert.ToString(part), out var groupDate)
                    && definition.GroupKinds.TryGetValue(definition.ColumnFields[level], out var dateKind))
                    text = groupDate.ToString(dateKind is >= 4 and <= 6 ? "M/yyyy" : dateKind == 7 ? "yyyy" : "d");
                styles.Add(Styled(caption?.Style ?? Template(207, 0, total ? level : level + 1)?.Style, 0, index, summary, Part(axis, total ? level - 1 : level), ref text));
                headings.Add(text);
            }
            parentHeadings.AddRange(headingRows[^1].Headings.Skip(labelCount).Select(heading => heading ?? ""));
            parentStyles.AddRange(headingRows[^1].Styles.Skip(labelCount));
        }
        foreach (var item in columnMap)
        {
            var text = renderedColumns[headerStyles.Count].Caption;
            if (summaryHeaders)
            {
                var heading = native!.Templates.FirstOrDefault(t => t.HeaderText is not null && t.ColumnLevel == item.Axis.Level && t.Summary == item.Summary);
                text = ReportNativeCrossTabText.Wrap(heading?.HeaderText ?? definition.Measures[item.Summary].Caption,
                    heading?.Style, renderedColumns[headerStyles.Count].WidthTwips);
                // A summary label keeps its own suppression and formatting conditions.
                headerStyles.Add(Styled(heading?.Style, 0, item.Index, item.Summary, heading?.HeaderText, ref text));
                renderedColumns[headerStyles.Count - 1] = renderedColumns[headerStyles.Count - 1] with { Caption = text };
                continue;
            }
            headerStyles.Add(Styled(Template(207, 0, item.Axis.Level)?.Style ?? native?.StyleFor(true, false, false, item.Axis.Level == 0),
                0, item.Index, item.Summary, Part(item.Axis, item.Axis.Level - 1), ref text));
            renderedColumns[headerStyles.Count - 1] = renderedColumns[headerStyles.Count - 1] with { Caption = text };
        }
        var labelRows = new List<IReadOnlyList<ReportTableCellFragment>>();
        foreach (var row in rowMap)
        {
            var visibleRow = renderedRows.Count;
            var labelCells = new List<ReportTableCellFragment>();
            var templates = native?.Templates.Where(t => t.Axis == 216 && t.RowLevel == row.Axis.Level);
            rowHeights.Add(templates?.GroupBy(t => t.ColumnLevel).Select(group => stack
                ? group.Max(t => t.Top + t.Height + t.PaddingY) - group.Min(t => t.Top - t.PaddingY)
                : group.Max(t => t.Height + 2 * t.PaddingY)).DefaultIfEmpty(definition.RowHeightTwips).Max() ?? definition.RowHeightTwips);
            var values = new List<string>(); var styles = new List<ReportNativeCrossTabCellStyle?>();
            for (var label = 0; label < labelCount; label++)
            {
                var level = native?.CompactRowHeaders == true ? Math.Max(0, row.Axis.Level - 1) : label;
                var text = row.Axis.Level == 0 ? level == 0 ? appearance?.RowTotalCaption ?? "Total" : ""
                    : row.Axis.Source.Labels.ElementAtOrDefault(level) ?? "";
                var rowTemplate = Template(211, row.Axis.Level == 0 ? 0 : level + 1, 0);
                var style = rowTemplate?.Style ?? native?.StyleFor(false, true, row.Axis.Level == 0, false);
                if (native?.CompactRowHeaders == true && style is not null && rowTemplate is not null)
                    style = style with { TextInsetTwips = rowTemplate.Left, ClipWidthTwips = renderedColumns[label].WidthTwips - rowTemplate.Left };
                else if (style is not null && rowTemplate is { CellWidth: > 0 }) style = style with
                    { ClipWidthTwips = Math.Max(rowTemplate.CellWidth, rowTemplate.Width + 2 * rowTemplate.PaddingX) };
                var first = true;
                var last = true;
                var span = 1;
                if (stack && level < row.Axis.Level)
                {
                    // The parent caption spans its visible children, including the parent subtotal.
                    bool SharesPrefix(int index) => index >= 0 && index < rowMap.Length && rowMap[index].Axis.Level > level
                        && row.Axis.Path.Take(level + 1).SequenceEqual(rowMap[index].Axis.Path.Take(level + 1));
                    first = !SharesPrefix(visibleRow - 1);
                    last = !SharesPrefix(visibleRow + 1);
                }
                else if (stack && level == row.Axis.Level)
                {
                    span = labelCount - level;
                    var caption = native!.Templates.FirstOrDefault(t => t.Axis == 211 && t.RowLevel == level && t.HeaderText is not null);
                    text = caption?.HeaderText ?? appearance?.RowTotalCaption ?? "Total";
                    style = caption?.Style ?? Template(211, level, 0)?.Style ?? style;
                }
                styles.Add(Styled(style, row.Index, 0, 0, Part(row.Axis, level), ref text));
                values.Add(first ? text : "");
                if (stack && level <= row.Axis.Level)
                    labelCells.Add(new(label, span, values[^1], null, first, last, visibleRow, label));
            }
            foreach (var column in columnMap)
            {
                string Summary(int summary, out ReportNativeCrossTabCellStyle? style)
                {
                    var value = Value(row.Index, column.Index, summary);
                    var template = Template(216, row.Axis.Level, column.Axis.Level, summary);
                    var text = value is null or DBNull or "" ? "" : value is DateTime date && template?.DateTimeFormat is { } dateFormat
                        ? dateFormat.Format(date, System.Globalization.CultureInfo.CurrentCulture)
                        : template?.NumericFormat is { } number ? number.Format(value) : Format(value, definition.Measures[summary].Format);
                    style = Styled(template?.Style ?? native?.StyleFor(false, false, row.Axis.Level == 0, column.Axis.Level == 0),
                        row.Index, column.Index, summary, value, ref text);
                    return text;
                }
                var text = Summary(column.Summary, out var style);
                if (stack)
                {
                    var lines = new string[definition.Measures.Count];
                    lines[0] = text;
                    for (var summary = 1; summary < lines.Length; summary++)
                        lines[summary] = Summary(summary, out _);
                    text = string.Join("\n", lines);
                }
                styles.Add(style);
                values.Add(text);
            }
            if (stack) labelRows.Add(labelCells);
            renderedRows.Add(new(values, row.Axis.Total)); cellStyles.Add(styles);
        }
        // All column captions share the tallest visible header cell, including the grand-total caption.
        if (!summaryHeaders && native is not null && definition.ColumnFields.Count == 1)
        {
            var headerHeight = columnMap.Select(c => Template(207, 0, c.Axis.Level)).OfType<ReportNativeCrossTabTemplate>()
                .Where(t => !t.Style.Suppressed).Select(t => t.Height + 2 * t.PaddingY).DefaultIfEmpty(0).Max();
            if (headerHeight > native.HeaderHeight) native = native with { HeaderHeight = headerHeight };
        }
        if (summaryHeaders)
        {
            var summaryHeight = native!.Templates.Where(t => t.Axis == 207 && t.HeaderText is not null && !t.Style.Suppressed)
                .Select(t => t.Height + 2 * t.PaddingY).DefaultIfEmpty(native.SummaryHeaderHeight).Max();
            native = native with { ParentHeaderHeight = native.HeaderHeight, HeaderHeight = summaryHeight,
                TotalWidth = renderedColumns.Where(c => c.IsGrandTotal).Sum(c => c.WidthTwips) };
        }
        void Print(IDictionary<string, object?> globals)
        {
            steps = 0;
            foreach (var cell in assignments)
                cell.Formula.Evaluate(Context(cell.Row, cell.Column, cell.Summary, cell.Value, cell.Attribute, globals));
        }
        var parentRows = summaryHeaders ? headingRows.Select((row, level) => new ParentHeadingRow(row.Headings, row.Styles,
            level < headingRows.Length - 1 && Template(207, 0, level + 1) is { Height: > 0 } outer ? outer.Height + 2 * outer.PaddingY : native!.ParentHeaderHeight,
            row.Continued)).ToArray() : null;
        return new(renderedColumns, renderedRows, native is null ? rowCount : 0) { PrintCellFormulas = assignments.Count == 0 ? null : Print, NativeCrossTab = native, NativeCellStyles = cellStyles, NativeHeaderStyles = headerStyles,
            ParentHeadingRows = parentRows,
            ParentHeadings = summaryHeaders ? parentHeadings : null, ParentHeaderStyles = summaryHeaders ? parentStyles : null,
            NativeRowLabelCells = stack ? labelRows : null, NativeRowHeights = rowHeights, NativeGrandTotalRows = rowMap.Select(row => row.Axis.Level == 0).ToArray() };
    }

    private static GridAxis At(List<GridAxis> axis, int index) => index >= 0 && index < axis.Count ? axis[index]
        : throw new InvalidDataException("Cross-tab grid index is out of range.");
    private static bool Equal(object? left, object? right) => left is string a && right is string b
        ? a.TrimEnd().Equals(b.TrimEnd(), StringComparison.OrdinalIgnoreCase) : Equals(left, right)
        || left is IConvertible && right is IConvertible && CrystalFormula.Compare(left, right) == 0;
}
