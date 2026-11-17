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
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ReportGridFormula? NameFormula { get; set; }

    internal Func<Dictionary<string, object>, string>? NameSelector()
    {
        if (NameFormula is null) return null;
        var formula = NameFormula.Compile();
        var labels = new Dictionary<object, string>();
        return row =>
        {
            var key = row.GetValueOrDefault(Field) ?? DBNull.Value;
            if (labels.TryGetValue(key, out var existing)) return existing;
            var value = formula.Evaluate(new CrystalFormulaContext { Resolve = field => row[field], CurrentFieldValue = () => key });
            return labels[key] = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
        };
    }
    /// <summary>0: merge unmatched values; 1: discard; 2: retain as separate groups after named groups.</summary>
    public int UnspecifiedValues { get; set; } = 2;
    public string OthersName { get; set; } = "Others";
    public List<ReportAnalysisSpecifiedGroup> SpecifiedGroups { get; set; } = [];
    public ReportAnalysisGroup Clone() => new() { Field = Field, OriginalOrder = OriginalOrder, NameFormula = NameFormula,
        UnspecifiedValues = UnspecifiedValues, OthersName = OthersName,
        SpecifiedGroups = SpecifiedGroups.Select(group => group.Clone()).ToList() };

    internal XElement ToXml() => new("Group", new XAttribute("Field", Field), new XAttribute("OriginalOrder", OriginalOrder),
        new XAttribute("UnspecifiedValues", UnspecifiedValues), new XAttribute("OthersName", OthersName),
        NameFormula?.ToXml("NameFormula"), SpecifiedGroups.Select(group => new XElement("Specified", new XAttribute("Name", group.Name), new XElement("Expression", group.Expression))));

    internal static ReportAnalysisGroup Read(XElement xml) => new()
    {
        Field = (string?)xml.Attribute("Field") ?? "", OriginalOrder = (bool?)xml.Attribute("OriginalOrder") ?? false,
        NameFormula = xml.Element("NameFormula") is { } caption ? ReportGridFormula.Read(caption) : null,
        UnspecifiedValues = (int?)xml.Attribute("UnspecifiedValues") ?? 2, OthersName = (string?)xml.Attribute("OthersName") ?? "Others",
        SpecifiedGroups = xml.Elements("Specified").Select(group => new ReportAnalysisSpecifiedGroup
        { Name = (string?)group.Attribute("Name") ?? "", Expression = (string?)group.Element("Expression") ?? "" }).ToList()
    };
}

internal sealed class ReportAnalysisGrouping
{
    private readonly Dictionary<Dictionary<string, object>, Dictionary<string, PivotGroupValue>> _keys = new(ReferenceEqualityComparer.Instance);
    internal List<Dictionary<string, object>> Rows { get; } = [];
    private readonly IReadOnlyDictionary<string, int> _groupKinds;

    internal ReportAnalysisGrouping(ReportAnalysisDefinition definition, IEnumerable<Dictionary<string, object>> rows)
    {
        _groupKinds = definition.GroupKinds;
        var rules = definition.Groups.Select(group => (Group: group,
            Formulas: group.SpecifiedGroups.Select(item => CrystalFormula.Compile(item.Expression)).ToArray(),
            Original: new Dictionary<object, int>(), Name: group.NameSelector())).ToArray();
        foreach (var row in rows)
        {
            var keys = new Dictionary<string, PivotGroupValue>(StringComparer.OrdinalIgnoreCase);
            var discard = false;
            foreach (var (group, formulas, original, name) in rules)
            {
                var raw = row.GetValueOrDefault(group.Field);
                var value = raw is null or DBNull ? "" : raw;
                var label = value is DateTime date ? date.ToString("d", CultureInfo.CurrentCulture) : Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
                if (name is not null)
                {
                    keys[group.Field] = new(value, name(row), 0, value);
                    continue;
                }
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

    // Rank the entire row group before partitioning it by columns. Fold the remaining
    // records into one axis key so every intersection and total uses the original records.
    internal void RankWithOthers(ReportAnalysisDefinition definition, ReportRowRanking rank)
    {
        var field = definition.RowFields.Single();
        var measure = definition.Measures.Single();
        var totals = Rows.GroupBy(row => Value(row, field) ?? DBNull.Value).Select(group => new
        {
            Key = group.Key,
            Amount = group.Sum(row => row.GetValueOrDefault(measure.Field) is { } value && value is not DBNull
                ? measure.Aggregate == ReportAggregateType.Count ? 1m : Convert.ToDecimal(value, CultureInfo.InvariantCulture) : 0m)
        });
        var ranked = rank.Bottom ? totals.OrderBy(g => g.Amount) : totals.OrderByDescending(g => g.Amount);
        var comparer = Comparer<object>.Create((a, b) => a is DBNull ? b is DBNull ? 0 : -1 : b is DBNull ? 1 : Comparer<object>.Default.Compare(a, b));
        var ordered = (definition.DescendingFields.Contains(field) ? ranked.ThenByDescending(g => g.Key, comparer)
            : ranked.ThenBy(g => g.Key, comparer)).ToArray();
        var keys = new Dictionary<object, PivotGroupValue>();
        for (int i = 0; i < ordered.Length; i++)
        {
            var group = ordered[i];
            if (i >= rank.Count && !(rank.WithTies && group.Amount == ordered[rank.Count - 1].Amount)) break;
            keys[group.Key] = new(i, Convert.ToString(group.Key, CultureInfo.CurrentCulture) ?? "", i);
        }
        var others = new PivotGroupValue(-1, rank.OthersCaption!, keys.Count);
        foreach (var row in Rows)
            _keys[row][field] = keys.GetValueOrDefault(Value(row, field) ?? DBNull.Value, others);
    }

    internal object? Value(Dictionary<string, object> row, string field) => _keys[row].TryGetValue(field, out var key) ? key : row.GetValueOrDefault(field) is DateTime date
            ? new PivotGroupValue(date, date.ToString(_groupKinds.GetValueOrDefault(field) == 7 ? "yyyy" : "d", CultureInfo.CurrentCulture), 0, date) : row.GetValueOrDefault(field);
}
