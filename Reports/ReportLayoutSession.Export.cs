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
        string[]? headers = null;
        if (_layout.UseVisualExportHeaders)
        {
            columns = columns.OrderBy(e => e.LeftTwips).ToList();
            var headings = _layout.Document.Sections.Where(s => s.Kind is "ReportHeader" or "PageHeader")
                .SelectMany(s => s.Elements).Where(e => e.Kind == "FieldHeading" && !e.IsSuppressed)
                .OrderBy(e => e.LeftTwips).ToArray();
            if (headings.Length != columns.Count || headings.Select(e => e.SectionId).Distinct().Count() != 1)
                throw new InvalidOperationException("Visual detail export requires one heading per field in a single header band.");
            headers = headings.Select(e => e.Text).ToArray();
        }
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            table.Columns.Add(new(headers?[i] ?? column.Name, column.FormatString, width: column.WidthTwips / 15d));
        }
        foreach (var row in details.GroupBy(b => b.Row))
        {
            var export = new GridExportRow();
            export.XlsxValues.EnsureCapacity(columns.Count); export.XlsxFormats.EnsureCapacity(columns.Count); export.Values.EnsureCapacity(columns.Count);
            foreach (var column in columns)
            {
                var visible = row.SelectMany(b => b.Items).Any(i => i.Element.Binding == column.Binding);
                var (text, value, format) = visible ? ExportCell(column, row.Key) : ("", null, column.FormatString);
                export.XlsxValues.Add(value); export.XlsxFormats.Add(format); export.Values.Add(text);
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
                // Named as the group tree names it. A date group's name is text ("10/2026"); its cell holds the period's date in a
                // format that reads the same, so it sorts and filters as a date. A group-name formula keeps its own value; CSV the text.
                var value = GroupNameOrKey(depth, group.Key);
                var (date, kind) = GroupPeriodDate(depth, group.Key);
                export.XlsxValues.Add(date ?? value); export.XlsxFormats.Add(date is null ? null : GroupDateExportFormat(kind));
                export.Values.Add(value?.ToString() ?? "");
            }
            var items = group.SelectMany(ExportItems).ToDictionary(i => i.Element.Id);
            foreach (var column in columns)
            {
                var present = items.TryGetValue(column.Id, out var item);
                var element = item?.Element ?? column;
                var (text, value, format) = present ? ExportCell(element, item!.Row) : ("", null, element.FormatString);
                export.Values.Add(text); export.XlsxValues.Add(value); export.XlsxFormats.Add(format);
            }
            table.Rows.Add(export);
        }
        return table;
    }

    // One exported field: its page's text (CSV) and its typed value and format (XLSX). A value its page prints as a formula error
    // (a failing formula or group name) exports that text in both, rather than failing the export or leaving a blank cell.
    private (string Text, object? Value, string? Format) ExportCell(ReportDesignerElement element, int row)
    {
        object? value = null;
        var captured = false;
        var text = Format(element.Binding, row, element.FormatString, element.NativeDateTimeFormat, element.NativeNumberFormat,
            element.NativeNumericFormat, result => { value = result; captured = true; });
        if (!captured) return (text, text, element.FormatString);
        // A date group's name field with its own date components captures the period's date. Without a field format it reads
        // as the group's name does ("1/2003", "2003"), never as a day its page does not print.
        return value is DateTime && string.IsNullOrEmpty(element.FormatString) && GroupPeriodDate(element.Binding, row) is ({ }, var kind)
            ? (text, value, GroupDateExportFormat(kind)) : (text, value, EffectiveValueFormat(element.FormatString, value));
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
