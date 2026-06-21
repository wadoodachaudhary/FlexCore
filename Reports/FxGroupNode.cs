namespace Fx.ControlKit.Reports;

public class FxGroupNode
{
    public string Label { get; set; } = "";

    public string Value { get; set; } = "";

    public string Field { get; set; } = "";

    public int Level { get; set; }

    public int PageIndex { get; set; }

    public string AnchorId { get; set; } = "";

    public List<FxGroupNode> Children { get; set; } = new();

    public bool Expanded { get; set; }
}
