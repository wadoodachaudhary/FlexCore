using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    /// <summary>Selected/sorted detail data, or visible group rows in a summary-only view, without page furniture.</summary>
    public GridExportTable CreateDataExport()
    {
        var columns = _layout.Document.Sections.Where(s => s.Kind == "Detail").SelectMany(s => s.Elements)
            .Where(e => e.Kind == "Field" && !e.IsSuppressed && !IsSpecial(e.Binding)).DistinctBy(e => e.Binding).ToList();
        if (_layout.SectionConditions.Values.Concat(_layout.AreaConditions.Values).Concat(_layout.ObjectConditions.Values).SelectMany(c => c.Values).Any(UsesPage))
            throw new NotSupportedException("Data export is unavailable for page-dependent visibility or formatting. Export HTML for the rendered result.");
        var details = _bands.Where(b => b.Section.Kind == "Detail" && b.Row >= 0 && !b.Suppressed).ToList();
        if (!_bands.Any(b => b.Section.Kind == "Detail" && !b.Suppressed && ExportItems(b).Any())
            && CreateGroupDataExport() is { } grouped) return grouped;
        if (columns.Count == 0) throw new InvalidOperationException("This layout has no exportable detail or group fields. Export HTML for its rendered pages.");
        if (columns.Any(e => DependsOn(e.Binding, false) || DependsOn(e.Binding, true)))
            throw new NotSupportedException("Data export cannot re-evaluate shared-state or page-dependent detail formulas. Export HTML for the rendered result.");
        var table = new GridExportTable { Title = _layout.Document.Title, SheetName = _layout.Document.Title };
        foreach (var column in columns) table.Columns.Add(new(column.Name, column.FormatString, width: column.WidthTwips / 15d));
        foreach (var row in details.GroupBy(b => b.Row))
        {
            var export = new GridExportRow();
            foreach (var column in columns)
            {
                var visible = row.SelectMany(b => b.Items).Any(i => i.Element.Binding == column.Binding);
                var value = visible ? Value(column.Binding, row.Key) : null;
                export.XlsxValues.Add(value); export.XlsxFormats.Add(column.FormatString);
                export.Values.Add(visible ? Format(column.Binding, row.Key, column.FormatString, column.NativeDateTimeFormat, column.NativeNumberFormat, column.NativeNumericFormat) : "");
            }
            table.Rows.Add(export);
        }
        return table;
    }

    private IEnumerable<Item> ExportItems(Band band) => band.Items.Where(i => i.Owner == this
        && i.Element.Kind == "Field" && !i.Element.IsSuppressed && !IsSpecial(i.Element.Binding));

    // Hidden details are not the data of a summary-only view. Use the deepest visible group level, once per
    // group instance, merging its header/footer subsections. Parent names become filterable context columns;
    // parent totals, page repeats and inline subreport fields do not become extra records in this table.
    private GridExportTable? CreateGroupDataExport()
    {
        var bands = _bands.Where(b => !b.Suppressed && !b.EvaluationOnly && !b.RepeatedHeader
            && b.Section.Kind is "GroupHeader" or "GroupFooter" && ExportItems(b).Any())
            .Select(b => (Band: b, Level: ReportDesignerEditing.GetGroupNumber(_layout.Document, b.Section) - 1))
            .Where(b => b.Level >= 0).ToList();
        if (bands.Count == 0) return null;
        var level = bands.Max(b => b.Level);
        var selected = bands.Where(b => b.Level == level).Select(b => b.Band).ToList();
        var ids = selected.SelectMany(ExportItems).Select(i => i.Element.Id).ToHashSet();
        var columns = _layout.Document.Sections.SelectMany(s => s.Elements).Where(e => ids.Contains(e.Id)).ToList();
        foreach (var column in columns) RequireExportValue(column.Binding, []);
        for (var depth = 0; depth <= level; depth++) RequireExportGroup(depth, []);

        var table = new GridExportTable { Title = _layout.Document.Title, SheetName = _layout.Document.Title };
        for (var depth = 0; depth <= level; depth++) table.Columns.Add(new("Group: " + _layout.Document.Groups[depth].Condition));
        foreach (var column in columns) table.Columns.Add(new(column.Name, column.FormatString, width: column.WidthTwips / 15d));
        // Row -1 is empty-report furniture: it supplies the schema, never a fabricated data record.
        foreach (var group in selected.Where(b => b.Row >= 0).GroupBy(b => GroupStart(level, b.Row)).OrderBy(g => g.Key))
        {
            var export = new GridExportRow();
            for (var depth = 0; depth <= level; depth++)
            {
                var value = GroupNameValue(depth, group.Key);
                export.XlsxValues.Add(value); export.XlsxFormats.Add(null); export.Values.Add(value?.ToString() ?? "");
            }
            var items = group.SelectMany(ExportItems).ToDictionary(i => i.Element.Id);
            foreach (var column in columns)
            {
                object? value = null;
                var present = items.TryGetValue(column.Id, out var item);
                var element = item?.Element ?? column;
                var text = present ? Format(element.Binding, item!.Row, element.FormatString, element.NativeDateTimeFormat,
                    element.NativeNumberFormat, element.NativeNumericFormat, captured => value = captured) : "";
                export.Values.Add(text); export.XlsxValues.Add(value); export.XlsxFormats.Add(element.FormatString);
            }
            table.Rows.Add(export);
        }
        return table;
    }

    // Summary fields and group labels can indirectly read formulas too. Refuse print-state/page values rather
    // than exporting a final counter or a later group's value after pagination has advanced that state.
    private void RequireExportValue(string reference, HashSet<string> seen)
    {
        if (!seen.Add(reference)) return;
        if (DependsOn(reference, false) || DependsOn(reference, true))
            throw new NotSupportedException("Data export cannot re-evaluate shared-state or page-dependent group formulas. Export HTML for the rendered result.");
        if (_layout.Formulas.TryGetValue(reference, out var formula))
            foreach (var dependency in formula.References) RequireExportValue(dependency, seen);
        if (_layout.GroupNames.TryGetValue(reference, out var condition)) RequireExportGroup(GroupLevel(condition), seen);
        if (_layout.Summaries.TryGetValue(reference, out var summary) || _layout.RunningTotals.TryGetValue(reference, out summary))
        {
            RequireExportValue(summary.Field, seen); RequireExportValue(summary.WeightField, seen);
            RequireExportValue(summary.Evaluation.Field, seen); RequireExportValue(summary.Reset.Field, seen);
            foreach (var test in new[] { summary.Evaluation.Formula, summary.Reset.Formula })
                if (test is not null) RequireExportFormula(test, seen);
        }
    }

    private void RequireExportGroup(int level, HashSet<string> seen)
    {
        if (level < 0) return;
        RequireExportValue(_layout.Document.Groups[level].Condition, seen);
        if (Options(level)?.NameFormula is { } formula) RequireExportFormula(formula, seen);
    }

    private void RequireExportFormula(CrystalFormula formula, HashSet<string> seen)
    {
        if (formula.UsesPersistentVariables || formula.UsesPageContext)
            throw new NotSupportedException("Data export cannot re-evaluate shared-state or page-dependent group formulas. Export HTML for the rendered result.");
        foreach (var reference in formula.References) RequireExportValue(reference, seen);
    }
}
