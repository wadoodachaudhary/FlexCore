using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public sealed record ReportGridFormula(string Text, string Syntax = "Crystal")
{
    internal CrystalFormula Compile(object? defaultValue = null) => CrystalFormula.Compile(Text, Syntax, defaultValue);
    internal XElement ToXml(string name) => new(name, new XAttribute("Syntax", Syntax), Text);
    internal static ReportGridFormula Read(XElement? xml) => new(xml?.Value ?? "", (string?)xml?.Attribute("Syntax") ?? "Crystal");
}

/// <summary>A native calculated row or column. Totals retain their original summary scope.</summary>
public sealed record ReportCalculatedMember(string Name, int GroupLevel, bool IsRow, bool InsertAfter,
    ReportGridFormula GroupValue, ReportGridFormula Header, ReportGridFormula Insertion, IReadOnlyList<ReportGridFormula> Values)
{
    internal IEnumerable<ReportGridFormula> Formulas => new[] { GroupValue, Header, Insertion }.Concat(Values);
    internal XElement ToXml() => new("Member", new XAttribute("Name", Name), new XAttribute("GroupLevel", GroupLevel),
        new XAttribute("IsRow", IsRow), new XAttribute("InsertAfter", InsertAfter), GroupValue.ToXml("GroupValue"),
        Header.ToXml("Header"), Insertion.ToXml("Insertion"), new XElement("Values", Values.Select(value => value.ToXml("Formula"))));
    internal static ReportCalculatedMember Read(XElement xml) => new((string?)xml.Attribute("Name") ?? "", (int?)xml.Attribute("GroupLevel") ?? 0,
        (bool?)xml.Attribute("IsRow") ?? false, (bool?)xml.Attribute("InsertAfter") ?? true,
        ReportGridFormula.Read(xml.Element("GroupValue")), ReportGridFormula.Read(xml.Element("Header")),
        ReportGridFormula.Read(xml.Element("Insertion")), xml.Element("Values")?.Elements("Formula").Select(ReportGridFormula.Read).ToArray() ?? []);
}
