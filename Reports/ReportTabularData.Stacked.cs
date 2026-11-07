using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

public sealed partial record ReportTabularData
{
    private static ReportTabularData CreateStackedCrossTab(ReportAnalysisDefinition definition, ReportNativeCrossTabLayout native,
        PivotControl<Dictionary<string, object>> pivot, GridExportTable table)
    {
        var count = definition.Measures.Count;
        // In a single-level stack the real group heading can be taller than the summary placeholder.
        var columnTemplate = native.Templates.FirstOrDefault(t => t.Axis == 207 && t.ColumnLevel == 1 && t.HeaderText is null);
        if (native.ParentHeaderHeight == 0 && columnTemplate is not null && !columnTemplate.Style.Suppressed)
            native = native with { HeaderHeight = Math.Max(native.HeaderHeight, columnTemplate.Height + 2 * columnTemplate.PaddingY),
                ColumnStyle = columnTemplate.Style };
        var labelWidth = native.Templates.Where(t => t.Axis == 211 && !t.Style.Suppressed
            && (t.RowLevel != 0 || !native.SuppressColumnGrandTotals)).Select(t => t.Width + 2 * t.PaddingX)
            .DefaultIfEmpty(definition.RowHeaderWidthTwips).Max();
        var columns = new List<ReportTabularColumn> { new("", Math.Max(labelWidth, definition.RowHeaderWidthTwips), true) };
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
            var heading = total ? "" : path.Last();
            if (!total && definition.ColumnFields.LastOrDefault() is {} dateField && definition.GroupKinds.GetValueOrDefault(dateField)==7
                && DateTime.TryParse(heading, out var year)) heading=year.ToString("yyyy");
            columns.Add(new(total ? (native.ParentHeaderHeight > 0 ? "" : native.Appearance!.ColumnTotalCaption) : heading, total ? native.TotalWidth : definition.ValueColumnWidthTwips, false, total));
            parents.Add((total ? native.Appearance!.ColumnTotalCaption : path.First()).ReplaceLineEndings("\n"));
            starts.Add(i);
        }
        // "Show Labels": the summaries' labels print in a column of their own after the row labels, one line per summary.
        var labels = native.SummaryLabels.Count == count ? native.SummaryLabels : null;
        var summaryLabelStyle = labels?[0].Style;
        var offset = 1;
        if (labels is not null) { columns.Insert(1, new("", labels.Max(label => label.Width) + 2 * native.PaddingX, true)); parents.Insert(1, ""); offset = 2; }
        var formats = native.StackFormats.Split(';').Select(group => group.Split(',')).ToArray();
        var rows = new List<ReportTabularRow>();
        var backgrounds = new List<IReadOnlyList<IReadOnlyList<string>>>();
        var templates = native.Templates.Where(t => t.Axis == 216)
            .ToDictionary(t => (t.RowLevel,t.ColumnLevel,t.Summary));
        var stackHasTemplates = templates.Count > 0;
        // Crystal formats a stacked grid in print order, each row's label and then its cells left to right, each summary top to
        // bottom, over one set of variables: a label that advances a counter colours the cells of its row that read it. The
        // same order runs again where the grid prints, with the report's own variables (PrintCellFormulas), and the backgrounds
        // it decides then stand; display strings and suppression are decided here.
        var printOrder = definition.GridPrintFormulas().Select(pair => pair.Rule).ToHashSet();
        var variables = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var replay = new List<Action<IDictionary<string, object?>>>();
        List<Dictionary<string, object>>? records = null;
        // An aggregate with the field alone is over the records the grid summarizes; one over a group needs the report's groups.
        object? Summarize(string op, string field, string group)
        {
            if (group.Length > 0) throw new NotSupportedException($"{op} ({field}, {group}) requires the report's groups, which a cross-tab condition does not provide.");
            records ??= pivot.DataSource.ToList();
            var present = records.Select(record => record.GetValueOrDefault(field)).Where(value => value is not null and not DBNull).ToArray();
            return op.ToLowerInvariant() switch
            {
                "count" => (decimal)present.Length,
                "distinctcount" => (decimal)present.Distinct().Count(),
                "minimum" => present.Length == 0 ? null : ReportValueFormatting.ExtendedAggregate(present, ReportAggregateType.Min),
                "maximum" => present.Length == 0 ? null : ReportValueFormatting.ExtendedAggregate(present, ReportAggregateType.Max),
                "sum" => present.Sum(value => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)),
                "average" or "avg" => present.Length == 0 ? null : present.Sum(value => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture)) / present.Length,
                _ => throw new NotSupportedException($"Summary operation '{op}' is not implemented.")
            };
        }
        CrystalFormulaContext Context(object? value, object? attribute, IDictionary<string, object?> globals) => new()
            { CurrentFieldValue = () => value, DefaultAttribute = () => attribute, GlobalVariables = globals, Aggregate = Summarize };
        var columnTotals = new Dictionary<int, object?>();
        IEnumerable<int> printRows = Enumerable.Range(0, table.Rows.Count);
        if (native.RowTotalsBefore) printRows = printRows.OrderByDescending(r => table.Rows[r].IsBold);
        foreach (var r in printRows)
        {
            var row = table.Rows[r];
            if (row.IsBold && native.SuppressColumnGrandTotals) continue;
            var caption = (row.IsBold ? native.Appearance!.RowTotalCaption : Format(row.Values[0], "")).ReplaceLineEndings("\n");
            var labelFill = new List<string>();
            // A label's own conditions run before the row's cells when the grid's cells could carry them; others keep the authored label.
            if (stackHasTemplates && (row.IsBold ? native.RowTotalLabelStyle : native.RowStyle) is { Conditions.Count: > 0 } labelStyle
                && labelStyle.Conditions.All(ReportAnalysisDefinition.StackLabelCondition))
            {
                var labelValue = row.IsBold ? caption : row.Values[0];
                var authored = caption;
                string Label(IDictionary<string, object?> globals, out string text)
                {
                    var formatted = ReportCrossTabFormatting.Apply(labelStyle, authored, attribute => Context(labelValue, attribute, globals));
                    text = formatted.Style.Suppressed ? "" : formatted.Display ?? authored;
                    return formatted.Style.Background;
                }
                labelFill.Add(Label(variables, out caption));
                if (labelStyle.Conditions.Any(condition => printOrder.Contains(condition.Formula)))
                    replay.Add(globals => labelFill[0] = Label(globals, out _));
            }
            var cells = new List<string> { caption };
            var fills = new List<IReadOnlyList<string>> { labelFill };
            if (labels is not null) { cells.Add(string.Join("\n", labels.Select(label => label.HeaderText ?? ""))); fills.Add([]); }
            for (var c = 0; c < starts.Count; c++)
            {
                var mask = formats[(row.IsBold ? 2 : 0) + (columns[c + offset].IsGrandTotal ? 1 : 0)];
                var colors = new List<string>(); var lines = new List<string>();
                for (var m=0;m<count;m++)
                {
                    var value = CellValue(definition, pivot, starts[c] + m, pivot.ExportCellValue(r, starts[c] + m), columnTotals);
                    var display = FormatSummary(value, m < mask.Length ? mask[m] : definition.Measures[m].Format);
                    var template=templates.GetValueOrDefault((row.IsBold ? 0 : definition.RowFields.Count,
                        columns[c + offset].IsGrandTotal ? 0 : definition.ColumnFields.Count,m));
                    var background = template?.Style.Background ?? "transparent";
                    if (template is not null)
                    {
                        var ordinary=template.Style with { Conditions=template.Style.Conditions.Where(condition=>condition.Property!="CurrencySymbol").ToArray() };
                        // Crystal formats a summary cell twice, laying the grid out and then printing it, and a row label once: a
                        // counter a cell advances moves two steps per cell and one per label, so a label and a first summary that
                        // both add one highlight every cell of every other row (sample "Cross-tab that highlights alternate rows").
                        var printed=ordinary.Conditions.Any(condition=>printOrder.Contains(condition.Formula));
                        (ReportNativeCrossTabCellStyle Style, string? Display) Formatted(IDictionary<string, object?> globals, string shown)
                        {
                            var applied=ReportCrossTabFormatting.Apply(ordinary,shown,attribute=>Context(value,attribute,globals));
                            return printed ? ReportCrossTabFormatting.Apply(ordinary,shown,attribute=>Context(value,attribute,globals)) : applied;
                        }
                        var formatted=Formatted(variables,display);
                        background=formatted.Style.Background;
                        if(template.NumericFormat is {} number && value is not null and not DBNull and not "")
                        {
                            var symbol=template.Style.Conditions.FirstOrDefault(condition=>condition.Property=="CurrencySymbol");
                            if(symbol is not null){
                                var evaluated=symbol.Formula.Compile(number.CurrencySymbol).Evaluate(Context(value,number.CurrencySymbol,variables));
                                var text=evaluated is string replacement ? replacement : "";
                                // With an empty zero mask the authored currency symbol is the whole display.
                                if(Convert.ToDecimal(value)==0 && number.ZeroValue=="") display=text;
                                else{
                                    var effective=number with { CurrencySymbol=text,HasConditionalFormatting=false,
                                        CurrencySymbolType=number.CurrencySymbolType==1?2:number.CurrencySymbolType };
                                    if(effective.UnsupportedReason is null) display=effective.Format(value);
                                }
                            }
                            else if(number.UnsupportedReason is null) display=number.Format(value);
                        }
                        display=formatted.Style.Suppressed?"":formatted.Display??display;
                        if (printed)
                        {
                            var index = colors.Count; var shown = display;
                            replay.Add(globals => colors[index] = Formatted(globals, shown).Style.Background);
                        }
                    }
                    colors.Add(background);lines.Add(display);
                }
                fills.Add(colors); cells.Add(string.Join("\n",lines));
            }
            rows.Add(new(cells, row.IsBold)); backgrounds.Add(fills);
        }
        return new(columns, rows, offset) { NativeCrossTab = native, StackBackgrounds = stackHasTemplates ? backgrounds : null, ParentHeadings = native.ParentHeaderHeight > 0 ? parents : null,
            NativeCellStyles = summaryLabelStyle is null ? null : rows.Select(_ => (IReadOnlyList<ReportNativeCrossTabCellStyle?>)[null, summaryLabelStyle]).ToArray(),
            PrintCellFormulas = replay.Count == 0 ? null : globals => { foreach (var step in replay) step(globals); } };
    }

    private static string FormatSummary(object? value, string format)
    {
        if (value is decimal or double or float && format.Length > 1 && format[0] is 'N' or 'F'
            && int.TryParse(format.AsSpan(1), out var places) && places is >= 0 and <= 28)
            value = decimal.Round(Convert.ToDecimal(value), places, MidpointRounding.AwayFromZero);
        return Format(value, format);
    }

    // Crystal's percentage of column total: a summary's share of its column's total of the denominator summary.
    private static object Percent(object? value, object? total)
    {
        // An empty average or weighted average has a blank, null share.
        if (value is "" or null or DBNull) return DBNull.Value;
        var amount = total is "" ? 0m : CrystalFormula.Number(total);
        return amount == 0 ? 0m : CrystalFormula.Number(value) / amount * 100m;
    }

    private static object? CellValue(ReportAnalysisDefinition definition, PivotControl<Dictionary<string, object>> pivot,
        int column, object? value, Dictionary<int, object?> columnTotals)
    {
        var summary = (column - definition.RowFields.Count) % definition.Measures.Count;
        if (definition.Measures[summary].PercentOfColumnTotal is not { } denominator) return value;
        var source = column - summary + denominator;
        if (!columnTotals.TryGetValue(source, out var total)) columnTotals[source] = total = pivot.ExportColumnTotal(source);
        return Percent(value, total);
    }

    /// <summary>One column-group heading row; a null heading continues the caption in the row above,
    /// and a continued column shares the heading cell at its left.</summary>
    private sealed record ParentHeadingRow(IReadOnlyList<string?> Headings, IReadOnlyList<ReportNativeCrossTabCellStyle?>? Styles, int Height,
        IReadOnlyList<bool>? Continued = null);

    private IEnumerable<ReportTableFragment> ParentHeaders(List<int> indexes, ReportTabularColumn[] columns, int pane, ReportNativeCrossTabLayout native)
    {
        var rows = ParentHeadingRows ?? [new(ParentHeadings!, ParentHeaderStyles, native.ParentHeaderHeight)];
        for (var row = 0; row < rows.Count; row++)
        {
            var cells = new List<ReportTableCellFragment>();
            for (var i = 0; i < columns.Length;)
            {
                var value = rows[row].Headings[indexes[i]];
                var end = i + 1;
                // A heading spans the columns of its own group, so blank headings keep their own cells;
                // without group spans it spans its equal neighbours inside the span of the heading above it.
                while (!columns[i].IsRowHeader && end < columns.Length && (native.SummaryHeaderHeight > 0 ? columns[end].IsGrandTotal == columns[i].IsGrandTotal : !columns[end].IsGrandTotal)
                    && (rows[row].Continued is { } continued ? indexes[end] == indexes[end - 1] + 1 && continued[indexes[end]]
                        : rows.Take(row + 1).All(r => r.Headings[indexes[end]] == r.Headings[indexes[i]]))) end++;
                cells.Add(new(i, end - i, value ?? "", null, value is not null || row == 0,
                    row == rows.Count - 1 || rows[row + 1].Headings[indexes[i]] is not null, -2, indexes[i]));
                i = end;
            }
            var styles = rows[row].Styles;
            // Each heading row keeps its own line, so it prints as its own item.
            yield return new(columns, cells.Select(c => c.Text).ToArray(), pane, -2, row, true, false, rows[row].Height)
            { LayoutCells = cells, CellStyles = styles is null ? null : indexes.Select(i => styles[i]).ToArray(), NativeCrossTab = native with { ColumnStyle = native.ParentHeaderStyle } };
        }
    }
}
