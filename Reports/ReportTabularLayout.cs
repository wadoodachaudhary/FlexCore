using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;

namespace Fx.ControlKit.Reports;

/// <summary>Row-local authored detail suppression. Visibility does not remove source rows or change group and total inputs.</summary>
public sealed class ReportTabularLayout
{
    internal sealed record Suppression(bool Fixed, CrystalFormula? Formula);
    private readonly Suppression _area;
    private readonly Suppression _section;
    private readonly IReadOnlyDictionary<string, CrystalFormula> _formulas;
    private readonly IReadOnlyDictionary<string, ReportParameter> _parameters;
    private readonly IReadOnlyDictionary<string, CrystalValueType?> _types;

    /// <summary>Crystal field reference to result-column alias, including dependencies not displayed as columns.</summary>
    public IReadOnlyDictionary<string, string> FieldBindings { get; }

    internal ReportTabularLayout(Suppression area, Suppression section, Dictionary<string, string> bindings,
        Dictionary<string, CrystalFormula> formulas, Dictionary<string, ReportParameter> parameters,
        Dictionary<string, CrystalValueType?> types)
    {
        _area = area; _section = section;
        FieldBindings = new ReadOnlyDictionary<string, string>(bindings);
        _formulas = formulas; _parameters = parameters; _types = types;
    }

    /// <summary>Evaluates the authored area/section conditions once for a source row. Missing context leaves it visible and reports why.</summary>
    public bool IsDetailVisible(DataRow row, IReadOnlyDictionary<string, object>? parameters,
        int drillDownGroupLevel = 0, Action<string>? diagnostic = null)
    {
        try
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            CrystalFormulaContext? context = null;
            object? Resolve(string reference)
            {
                reference = reference.Trim();
                if (values.TryGetValue(reference, out var cached)) return cached;
                object? value;
                if (_formulas.TryGetValue(reference, out var formula)) value = formula.Evaluate(context!);
                else if (FieldBindings.TryGetValue(reference, out var alias))
                {
                    if (!row.Table.Columns.Contains(alias)) throw new InvalidDataException($"Missing detail-suppression result column '{alias}'.");
                    value = row[alias] is DBNull ? null : row[alias];
                }
                else if (_parameters.TryGetValue(reference, out var parameter))
                {
                    var supplied = parameters?.Where(p => p.Key.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray() ?? [];
                    if (supplied.Length > 1) throw new InvalidDataException($"Ambiguous detail-suppression parameter '{parameter.Name}'.");
                    if (supplied.Length == 1) value = TypedParameter(supplied[0].Value, _types.GetValueOrDefault(reference), false);
                    else if (!string.IsNullOrEmpty(parameter.DefaultValue)) value = TypedParameter(parameter.DefaultValue, _types.GetValueOrDefault(reference), true);
                    else throw new InvalidDataException($"Missing detail-suppression parameter '{parameter.Name}'.");
                }
                else throw new InvalidDataException($"Unresolved detail-suppression reference '{reference}'.");
                values[reference] = value;
                return value;
            }
            context = new CrystalFormulaContext
            {
                Resolve = Resolve, ReferenceType = reference => _types.GetValueOrDefault(reference.Trim()),
                DrillDownGroupLevel = drillDownGroupLevel
            };
            bool Suppressed(Suppression rule) => rule.Formula is null ? rule.Fixed : CrystalFormula.Boolean(rule.Formula.Evaluate(context));
            return !Suppressed(_area) && !Suppressed(_section);
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArgumentException or FormatException or ArithmeticException)
        {
            diagnostic?.Invoke("Tabular detail suppression was not applied: " + error.Message);
            return true;
        }
    }

    private static object? TypedParameter(object? value, CrystalValueType? type, bool isDefault)
    {
        if (value is null or DBNull) return null;
        if (type is null) throw new NotSupportedException("The detail-suppression parameter has no supported declared type.");
        if (type.IsArray)
        {
            var elementType = type with { IsArray = false };
            if (value is IEnumerable items && value is not string)
                return items.Cast<object?>().Select(item => TypedParameter(item, elementType, isDefault)).ToArray();
            return new[] { TypedParameter(value, elementType, isDefault) };
        }
        if (type.IsRange)
        {
            if (value is not CrystalRange range) throw new InvalidDataException("A range parameter requires its complete range value.");
            var elementType = type with { IsRange = false };
            return range with { Start = TypedParameter(range.Start, elementType, isDefault), End = TypedParameter(range.End, elementType, isDefault) };
        }
        var culture = isDefault ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture;
        return type.BaseType switch
        {
            CrystalBaseType.String when value is string => value,
            CrystalBaseType.Boolean when value is bool => value,
            CrystalBaseType.Boolean when value is string text && bool.TryParse(text, out var flag) => flag,
            CrystalBaseType.Number or CrystalBaseType.Currency when value is string text
                => decimal.Parse(text, NumberStyles.Number, culture),
            CrystalBaseType.Number or CrystalBaseType.Currency when value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal => value,
            CrystalBaseType.Date or CrystalBaseType.DateTime when value is DateTime => value,
            CrystalBaseType.Date or CrystalBaseType.DateTime when value is string text => DateTime.Parse(text, culture, DateTimeStyles.AllowWhiteSpaces),
            CrystalBaseType.Time when value is TimeSpan or DateTime => value,
            CrystalBaseType.Time when value is string text => TimeSpan.Parse(text, culture),
            _ => throw new InvalidDataException("A detail-suppression parameter value does not match its declared type.")
        };
    }
}
