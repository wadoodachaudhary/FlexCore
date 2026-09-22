namespace Fx.ControlKit.Reports;

/// <summary>A zero-based body-cell rectangle in the evaluated table/matrix. A nested region uses
/// the union of source records represented by that rectangle, never the whole report implicitly.</summary>
public sealed class ReportMatrixCell
{
    public int Row { get; set; }
    public int Column { get; set; }
    public int RowSpan { get; set; } = 1;
    public int ColumnSpan { get; set; } = 1;
    public string? Text { get; set; }
    public string RegionKind { get; set; } = "Table";
    public ReportAnalysisDefinition? Region { get; set; }
    internal ReportMatrixCell Copy(ReportAnalysisDefinition? region) => new()
    { Row = Row, Column = Column, RowSpan = RowSpan, ColumnSpan = ColumnSpan, Text = Text, RegionKind = RegionKind, Region = region };
}

public sealed partial class ReportAnalysisDefinition
{
    internal IEnumerable<ReportAnalysisDefinition> Tree()
    {
        var active = new HashSet<ReportAnalysisDefinition>(ReferenceEqualityComparer.Instance);
        var count = 0;
        return Walk(this, 0);
        IEnumerable<ReportAnalysisDefinition> Walk(ReportAnalysisDefinition node, int depth)
        {
            if (depth >= 16 || ++count > 4096 || node.Cells.Count > 4096)
                throw new InvalidDataException("Nested report regions exceed the 16-level/4096-region safety limit.");
            if (!active.Add(node)) throw new InvalidDataException("Nested report regions contain a cycle.");
            yield return node;
            foreach (var cell in node.Cells)
                if (cell.Region is { } region)
                    foreach (var child in Walk(region, depth + 1)) yield return child;
            active.Remove(node);
        }
    }
}
