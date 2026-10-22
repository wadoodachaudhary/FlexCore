namespace Fx.ControlKit.Reports;

public sealed record ReportPageSnapshot(string Title, int Number, ReportDesignerPage Size, IReadOnlyList<ReportPageBand> Bands);
public sealed record ReportPageBand(string Name, int TopTwips, int HeightTwips, string Background, IReadOnlyList<ReportPageObject> Objects)
{
    /// <summary>A section printed in a multiple-column block starts <see cref="LeftTwips"/> across and is <see cref="WidthTwips"/> wide; 0 spans the page.</summary>
    public int LeftTwips { get; init; }
    public int WidthTwips { get; init; }
    /// <summary>Native group targets attached to this band's physical page, independent of printed page-number resets.</summary>
    public IReadOnlyList<string> NavigationAnchors { get; init; } = [];
}
public sealed record ReportPageObject(ReportDesignerElement Element, string FormattedContent, ReportAnalysisSnapshot? Analysis)
{
    public ReportTableFragment? Table { get; init; }
    /// <summary>UTF-16 line ranges from completed browser measurement. PDF export retains these
    /// breaks when realizing native fonts at the smaller legacy PDF size.</summary>
    public IReadOnlyList<ReportTextLine>? TextLines { get; init; }
}
