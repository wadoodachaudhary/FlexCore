using System.Globalization;
using System.Xml.Linq;
using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

public sealed class ReportAnalysisSpecifiedGroup
{
    public string Name { get; set; } = "";
    public string Expression { get; set; } = "";
    public ReportAnalysisSpecifiedGroup Clone() => (ReportAnalysisSpecifiedGroup)MemberwiseClone();
}

/// <summary>Crystal's specified-order groups, or first-seen (original) order, for one cross-tab axis.</summary>
public sealed class ReportAnalysisGroup
{
    public string Field { get; set; } = "";
    public bool OriginalOrder { get; set; }
    /// <summary>0: merge unmatched values; 1: discard; 2: retain as separate groups after named groups.</summary>
    public int UnspecifiedValues { get; set; } = 2;
    public string OthersName { get; set; } = "Others";
    public List<ReportAnalysisSpecifiedGroup> SpecifiedGroups { get; set; } = [];
    public ReportAnalysisGroup Clone() => new() { Field = Field, OriginalOrder = OriginalOrder,
        UnspecifiedValues = UnspecifiedValues, OthersName = OthersName,
        SpecifiedGroups = SpecifiedGroups.Select(group => group.Clone()).ToList() };

    internal XElement ToXml() => new("Group", new XAttribute("Field", Field), new XAttribute("OriginalOrder", OriginalOrder),
        new XAttribute("UnspecifiedValues", UnspecifiedValues), new XAttribute("OthersName", OthersName),
        SpecifiedGroups.Select(group => new XElement("Specified", new XAttribute("Name", group.Name), new XElement("Expression", group.Expression))));

    internal static ReportAnalysisGroup Read(XElement xml) => new()
    {
        Field = (string?)xml.Attribute("Field") ?? "", OriginalOrder = (bool?)xml.Attribute("OriginalOrder") ?? false,
        UnspecifiedValues = (int?)xml.Attribute("UnspecifiedValues") ?? 2, OthersName = (string?)xml.Attribute("OthersName") ?? "Others",
        SpecifiedGroups = xml.Elements("Specified").Select(group => new ReportAnalysisSpecifiedGroup
        { Name = (string?)group.Attribute("Name") ?? "", Expression = (string?)group.Element("Expression") ?? "" }).ToList()
    };
}

internal sealed class ReportAnalysisGrouping
{
    private readonly Dictionary<Dictionary<string, object>, Dictionary<string, PivotGroupValue>> _keys = new(ReferenceEqualityComparer.Instance);
    internal List<Dictionary<string, object>> Rows { get; } = [];

    internal ReportAnalysisGrouping(ReportAnalysisDefinition definition, IEnumerable<Dictionary<string, object>> rows)
    {
        var rules = definition.Groups.Select(group => (Group: group,
            Formulas: group.SpecifiedGroups.Select(item => CrystalFormula.Compile(item.Expression)).ToArray(),
            Original: new Dictionary<object, int>())).ToArray();
        foreach (var row in rows)
        {
            var keys = new Dictionary<string, PivotGroupValue>(StringComparer.OrdinalIgnoreCase);
            var discard = false;
            foreach (var (group, formulas, original) in rules)
            {
                var raw = row.GetValueOrDefault(group.Field);
                var value = raw is null or DBNull ? "" : raw;
                var label = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
                if (group.OriginalOrder)
                {
                    if (!original.TryGetValue(value, out var order)) original[value] = order = original.Count;
                    keys[group.Field] = new(order, label, order);
                    continue;
                }
                var context = new CrystalFormulaContext { Resolve = name => row.TryGetValue(name, out var found) ? found
                    : row.TryGetValue(name.Trim('{', '}'), out found) ? found
                    : throw new InvalidDataException($"Missing cross-tab group field {name}.") };
                var match = Array.FindIndex(formulas, formula => formula.Evaluate(context) is true);
                if (match >= 0) keys[group.Field] = new(match, group.SpecifiedGroups[match].Name, match);
                else if (group.UnspecifiedValues == 1) { discard = true; break; }
                else if (group.UnspecifiedValues == 0) keys[group.Field] = new(-1, group.OthersName, formulas.Length);
                else keys[group.Field] = new(value, label, formulas.Length + 1, value);
            }
            if (discard) continue;
            Rows.Add(row);
            _keys.Add(row, keys);
        }
    }

    internal object? Value(Dictionary<string, object> row, string field) => _keys[row].TryGetValue(field, out var key) ? key : row.GetValueOrDefault(field);
}
