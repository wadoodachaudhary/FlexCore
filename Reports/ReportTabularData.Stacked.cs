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
            var heading = total ? "" : path.Last();
            if (!total && definition.ColumnFields.LastOrDefault() is {} dateField && definition.GroupKinds.GetValueOrDefault(dateField)==7
                && DateTime.TryParse(heading, out var year)) heading=year.ToString("yyyy");
            columns.Add(new(total ? (native.ParentHeaderHeight > 0 ? "" : native.Appearance!.ColumnTotalCaption) : heading, total ? native.TotalWidth : definition.ValueColumnWidthTwips, false, total));
            parents.Add((total ? native.Appearance!.ColumnTotalCaption : path.First()).ReplaceLineEndings("\n"));
            starts.Add(i);
        }
        var formats = native.StackFormats.Split(';').Select(group => group.Split(',')).ToArray();
        var rows = new List<ReportTabularRow>();
        var backgrounds = new List<IReadOnlyList<IReadOnlyList<string>>>();
        var templates = native.Templates.Where(t => t.Axis == 216)
            .ToDictionary(t => (t.RowLevel,t.ColumnLevel,t.Summary));
        var stackHasTemplates = templates.Count > 0;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            if (row.IsBold && native.SuppressColumnGrandTotals) continue;
            var cells = new List<string> { (row.IsBold ? native.Appearance!.RowTotalCaption : Format(row.Values[0], "")).ReplaceLineEndings("\n") };
            var fills = new List<IReadOnlyList<string>> { Array.Empty<string>() };
            for (var c = 0; c < starts.Count; c++)
            {
                var mask = formats[(row.IsBold ? 2 : 0) + (columns[c + 1].IsGrandTotal ? 1 : 0)];
                var variables = new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
                var colors = new List<string>(); var lines = new List<string>();
                for (var m=0;m<count;m++)
                {
                    var value = pivot.ExportCellValue(r, starts[c]+m);
                    var display = FormatSummary(value, m < mask.Length ? mask[m] : definition.Measures[m].Format);
                    var template=templates.GetValueOrDefault((row.IsBold ? 0 : definition.RowFields.Count,
                        columns[c+1].IsGrandTotal ? 0 : definition.ColumnFields.Count,m));
                    var background = template?.Style.Background ?? "transparent";
                    if (template is not null)
                    {
                        var ordinary=template.Style with { Conditions=template.Style.Conditions.Where(condition=>condition.Property!="CurrencySymbol").ToArray() };
                        CrystalFormulaContext Context(object? attribute) => new() { CurrentFieldValue=()=>value,
                            DefaultAttribute=()=>attribute, GlobalVariables=variables };
                        var formatted=ReportCrossTabFormatting.Apply(ordinary,display,Context);
                        background=formatted.Style.Background;
                        if(template.NumericFormat is {} number && value is not null and not DBNull and not "")
                        {
                            var symbol=template.Style.Conditions.FirstOrDefault(condition=>condition.Property=="CurrencySymbol");
                            if(symbol is not null){
                                var evaluated=symbol.Formula.Compile(number.CurrencySymbol).Evaluate(Context(number.CurrencySymbol));
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
                    }
                    colors.Add(background);lines.Add(display);
                }
                fills.Add(colors); cells.Add(string.Join("\n",lines));
            }
            rows.Add(new(cells, row.IsBold)); backgrounds.Add(fills);
        }
        return new(columns, rows, 1) { NativeCrossTab = native, StackBackgrounds = stackHasTemplates ? backgrounds : null, ParentHeadings = native.ParentHeaderHeight > 0 ? parents : null };
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
