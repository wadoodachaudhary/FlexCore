using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public static partial class ReportDesignerXmlSerializer
{
    // A custom query's result schema is not a physical database table.
    private static void ApplySqlResultFields(XElement root, ReportDesignerDocument document)
    {
        var fields = document.Fields.Where(f => !f.IsFormula && string.IsNullOrWhiteSpace(f.Table)).ToArray();
        var existing = root.Element(MetadataElementName)?.Element("Sql")?.Element("ResultFields");
        if (fields.Length == 0 || string.IsNullOrWhiteSpace(document.CustomSql)) { existing?.Remove(); return; }
        var replacement = new XElement("ResultFields", fields.Select(f => new XElement("Field",
            new XAttribute("Name", f.Name), new XAttribute("Type", f.Type))));
        if (XNode.DeepEquals(existing, replacement)) return;
        if (existing is not null) existing.ReplaceWith(replacement);
        else EnsureChild(EnsureChild(root, MetadataElementName), "Sql").Add(replacement);
    }
}
