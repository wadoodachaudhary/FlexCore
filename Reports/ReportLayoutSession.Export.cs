using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    /// <summary>Selected/sorted detail data, not page furniture or a flattened image of the report.</summary>
    public GridExportTable CreateDataExport()
    {
        var columns = _layout.Document.Sections.Where(s => s.Kind == "Detail").SelectMany(s => s.Elements)
            .Where(e => e.Kind == "Field" && !e.IsSuppressed && !IsSpecial(e.Binding)).DistinctBy(e => e.Binding).ToList();
        if (columns.Count == 0) throw new InvalidOperationException("This layout has no exportable detail fields. Export HTML for its rendered pages.");
        if (columns.Any(e => DependsOn(e.Binding, false) || DependsOn(e.Binding, true)))
            throw new NotSupportedException("Data export cannot re-evaluate shared-state or page-dependent detail formulas. Export HTML for the rendered result.");
        if (_layout.SectionConditions.Values.Concat(_layout.AreaConditions.Values).Concat(_layout.ObjectConditions.Values).SelectMany(c => c.Values).Any(UsesPage))
            throw new NotSupportedException("Data export is unavailable for page-dependent visibility or formatting. Export HTML for the rendered result.");
        var table = new GridExportTable { Title = _layout.Document.Title, SheetName = _layout.Document.Title };
        foreach (var column in columns) table.Columns.Add(new(column.Name, column.FormatString, width: column.WidthTwips / 15d));
        foreach (var row in _bands.Where(b => b.Section.Kind == "Detail" && !b.Suppressed).GroupBy(b => b.Row))
        {
            var export = new GridExportRow();
            foreach (var column in columns)
            {
                var visible = row.SelectMany(b => b.Items).Any(i => i.Element.Binding == column.Binding);
                var value = visible ? Value(column.Binding, row.Key) : null;
                export.XlsxValues.Add(value); export.XlsxFormats.Add(column.FormatString);
                export.Values.Add(visible ? Format(column.Binding, row.Key, column.FormatString) : "");
            }
            table.Rows.Add(export);
        }
        return table;
    }
}
