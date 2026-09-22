namespace Fx.ControlKit.Reports;

public partial class ReportDesignerControl
{
    private async Task AddAnalysisAsync(string kind)
    {
        var section = GetActiveSection();
        if (section.ReadOnly) return;
        var element = new ReportDesignerElement
        {
            SectionId = section.Id, Name = NextObjectName(kind), Kind = kind,
            WidthTwips = Math.Min(7200, _document.Page.ContentWidthTwips), HeightTwips = 3600,
            Analysis = new() { Measures = [new()] }
        };
        if (kind == "Table")
        {
            element.Analysis.Measures.Clear();
            element.Analysis.TableColumns = _document.Fields.Where(field => !field.IsFormula).Take(3)
                .Select(field => new ReportTableColumn { Field = field.Reference, Caption = field.DisplayName }).ToList();
            if (element.Analysis.TableColumns.Count == 0) element.Analysis.TableColumns.Add(new());
        }
        section.Elements.Add(element);
        section.HeightTwips = Math.Max(section.HeightTwips, element.HeightTwips);
        SelectElement(section, element);
        await MarkDirtyAsync();
    }
}
