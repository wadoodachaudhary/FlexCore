namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    private readonly Dictionary<string, object?[]> _runningValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _runningEvaluation = new(StringComparer.OrdinalIgnoreCase);

    private object? RunningTotal(string name, ReportLayoutSummary total, int row)
    {
        if (row < 0 || _rows.Length == 0) return total.Operation is "Sum" or "Count" or "DistinctCount" ? 0m : null;
        if (_runningValues.TryGetValue(name, out var cached)) return cached[Math.Min(row, cached.Length - 1)];
        if (!_runningEvaluation.Add(name)) throw new InvalidDataException($"Circular running total '{name}'.");
        try
        {
            CheckReference(total.Field);
            Validate(total.Evaluation); Validate(total.Reset);
            var results = new object?[_rows.Length];
            var sum = 0m; var count = 0; object? extreme = null;
            var distinct = new HashSet<object>(TotalValueComparer.Instance);
            for (var index = 0; index < _rows.Length; index++)
            {
                if (Matches(total.Reset, index, false)) { sum = 0; count = 0; extreme = null; distinct.Clear(); }
                if (Matches(total.Evaluation, index, true))
                {
                    var value = Value(total.Field, index);
                    if (value is not (null or DBNull))
                    {
                        count++;
                        switch (total.Operation)
                        {
                            case "Sum": case "Average": sum += CrystalFormula.Number(value); break;
                            case "DistinctCount": distinct.Add(value); break;
                            case "Count": break;
                            case "Minimum": case "Maximum":
                                if (extreme is null || (CrystalFormula.Compare(value, extreme) < 0) == (total.Operation == "Minimum")) extreme = value;
                                break;
                            default: throw new NotSupportedException($"Running total operation '{total.Operation}' is not implemented.");
                        }
                    }
                }
                results[index] = total.Operation switch
                {
                    "Sum" => sum, "Average" => count == 0 ? null : sum / count,
                    "Count" => count, "DistinctCount" => distinct.Count, _ => extreme
                };
            }
            _runningValues[name] = results;
            return results[Math.Min(row, results.Length - 1)];
        }
        finally { _runningEvaluation.Remove(name); }

        void CheckReference(string reference)
        {
            if (reference.StartsWith("{#", StringComparison.Ordinal) || _layout.Summaries.ContainsKey(reference))
                throw new NotSupportedException($"Running total '{name}' must use first-pass fields, not another total.");
            if (_layout.Formulas.TryGetValue(reference, out var formula)) RequireReadTime(formula, "Running total");
        }
        void Validate(ReportRunningTotalCondition condition)
        {
            switch (condition.Type.ToLowerInvariant())
            {
                case "nocondition": break;
                case "onchangeoffield":
                    if (string.IsNullOrWhiteSpace(condition.Field)) throw new InvalidDataException($"Running total '{name}' is missing its condition field. Reconvert the RPT or supply the condition metadata.");
                    CheckReference(condition.Field); break;
                case "onchangeofgroup":
                    if (condition.Group < 1 || condition.Group > _layout.Document.Groups.Count) throw new InvalidDataException($"Running total '{name}' is missing a valid condition group. Reconvert the RPT or supply the condition metadata.");
                    break;
                case "useformula":
                    if (condition.Formula is null) throw new InvalidDataException($"Running total '{name}' is missing its condition formula.");
                    RequireReadTime(condition.Formula, "Running total condition"); break;
                default: throw new NotSupportedException($"Running total condition '{condition.Type}' is not implemented.");
            }
        }
        bool Matches(ReportRunningTotalCondition condition, int index, bool evaluation) => condition.Type.ToLowerInvariant() switch
        {
            "nocondition" => evaluation,
            "onchangeoffield" => index == 0 || CrystalFormula.Compare(Value(condition.Field, index), Value(condition.Field, index - 1)) != 0,
            "onchangeofgroup" => index == 0 || !SameGroup(index, index - 1, condition.Group - 1),
            "useformula" => CrystalFormula.Boolean(condition.Formula!.Evaluate(Context(index))),
            _ => false
        };
    }

    private sealed class TotalValueComparer : IEqualityComparer<object>
    {
        public static readonly TotalValueComparer Instance = new();
        public new bool Equals(object? x, object? y) => x is string a && y is string b
            ? StringComparer.OrdinalIgnoreCase.Equals(a, b) : object.Equals(Normalize(x), Normalize(y));
        public int GetHashCode(object value) => value is string text ? StringComparer.OrdinalIgnoreCase.GetHashCode(text) : Normalize(value)?.GetHashCode() ?? 0;
        private static object? Normalize(object? value) => value is byte or short or int or long or float or double or decimal ? CrystalFormula.Number(value) : value;
    }
}
