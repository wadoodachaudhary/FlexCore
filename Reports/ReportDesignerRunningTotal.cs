using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public sealed class ReportDesignerRunningTotal : ReportDesignerSourceItem
{
    public string Name { get; set; } = "RunningTotal";
    public string Field { get; set; } = "";
    public string Operation { get; set; } = "Sum";
    public ReportDesignerTotalCondition Evaluation { get; set; } = new();
    public ReportDesignerTotalCondition Reset { get; set; } = new();
    public string Binding => "{#" + Name + "}";
}

public sealed class ReportDesignerTotalCondition
{
    public string Type { get; set; } = "NoCondition";
    public string Field { get; set; } = "";
    public int Group { get; set; }
    public string Formula { get; set; } = "";
    /// <summary>The syntax of <see cref="Formula"/>: "Crystal" or "Basic".</summary>
    public string FormulaSyntax { get; set; } = "Crystal";
}

public static partial class ReportDesignerXmlSerializer
{
    private static void ParseRunningTotals(XElement root, ReportDesignerDocument document)
    {
        foreach (var source in root.Element("DataDefinition")?.Element("RunningTotalFieldDefinitions")?.Elements("RunningTotalFieldDefinition") ?? [])
        {
            // Converted reports name a formula condition "OnFormula", the designer "UseFormula".
            ReportDesignerTotalCondition Condition(string prefix) => new()
            {
                Type = ((string?)source.Attribute(prefix + "ConditionType") ?? "NoCondition") is var type && type.Equals("OnFormula", StringComparison.OrdinalIgnoreCase) ? "UseFormula" : type,
                Field = (string?)source.Element("FlexKitRunningTotalConditions")?.Attribute(prefix + "Field") ?? "",
                Group = (int?)source.Element("FlexKitRunningTotalConditions")?.Attribute(prefix + "Group") ?? 0,
                Formula = (string?)source.Element("FlexKitRunningTotalConditions")?.Attribute(prefix + "Formula") ?? "",
                FormulaSyntax = (string?)source.Element("FlexKitRunningTotalConditions")?.Attribute(prefix + "FormulaSyntax") ?? "Crystal"
            };
            var total = new ReportDesignerRunningTotal
            {
                Name = (string?)source.Attribute("Name") ?? ((string?)source.Attribute("FormulaName") ?? "").Trim('{', '}', '#'),
                Field = (string?)source.Attribute("SummarizedField") ?? "", Operation = (string?)source.Attribute("Operation") ?? "Sum",
                Evaluation = Condition("Evaluation"), Reset = Condition("Reset")
            };
            document.RunningTotals.Add(total); document.SourceItems[total.Id] = source;
        }
    }

    private static XElement BuildRunningTotal(ReportDesignerRunningTotal total)
    {
        var conditions = new XElement("FlexKitRunningTotalConditions");
        foreach (var (prefix, condition) in new[] { ("Evaluation", total.Evaluation), ("Reset", total.Reset) })
        {
            conditions.SetAttributeValue(prefix + "Field", condition.Field);
            conditions.SetAttributeValue(prefix + "Group", condition.Group);
            conditions.SetAttributeValue(prefix + "Formula", condition.Formula);
            if (!CrystalFormula.IsCrystalSyntax(condition.FormulaSyntax)) conditions.SetAttributeValue(prefix + "FormulaSyntax", condition.FormulaSyntax);
        }
        return new("RunningTotalFieldDefinition", new XAttribute("Name", total.Name), new XAttribute("Kind", "RunningTotalField"),
            new XAttribute("FormulaName", total.Binding), new XAttribute("SummarizedField", total.Field), new XAttribute("Operation", total.Operation),
            new XAttribute("EvaluationConditionType", total.Evaluation.Type), new XAttribute("ResetConditionType", total.Reset.Type), conditions);
    }
}
