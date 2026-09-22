namespace Fx.ControlKit.Reports;

public sealed record ReportPageSnapshot(string Title, int Number, ReportDesignerPage Size, IReadOnlyList<ReportPageBand> Bands);
public sealed record ReportPageBand(string Name, int TopTwips, int HeightTwips, string Background, IReadOnlyList<ReportPageObject> Objects);
public sealed record ReportPageObject(ReportDesignerElement Element, string FormattedContent, ReportAnalysisSnapshot? Analysis)
{
    public ReportTableFragment? Table { get; init; }
}
