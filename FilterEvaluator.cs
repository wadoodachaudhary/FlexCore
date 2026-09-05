using System.Collections;
using System.Globalization;
using System.Reflection;
using Fx.ControlKit.Grid;

namespace Fx.ControlKit;

/// <summary>Typed, in-memory execution of DataFilterControl groups. Does not evaluate expression strings.</summary>
public static class FilterEvaluator
{
    public static bool NeedsValue(GridFilterOperator op) => op is not (GridFilterOperator.IsNull or GridFilterOperator.IsNotNull
        or GridFilterOperator.IsEmpty or GridFilterOperator.IsNotEmpty);
    public static bool IsActive(FilterCondition condition) => !string.IsNullOrWhiteSpace(condition.Field)
        && (!NeedsValue(condition.Operator) || !string.IsNullOrEmpty(condition.Value));

    public static IReadOnlyList<GridFilterOperator> GetOperators(Type declaredType)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        var result = new List<GridFilterOperator> { GridFilterOperator.Equals, GridFilterOperator.NotEquals };
        if (type == typeof(string))
            result.AddRange([GridFilterOperator.Contains, GridFilterOperator.DoesNotContain, GridFilterOperator.StartsWith,
                GridFilterOperator.EndsWith, GridFilterOperator.IsEmpty, GridFilterOperator.IsNotEmpty]);
        else if (type != typeof(bool) && !type.IsEnum && typeof(IComparable).IsAssignableFrom(type))
            result.AddRange([GridFilterOperator.GreaterThan, GridFilterOperator.GreaterThanOrEquals,
                GridFilterOperator.LessThan, GridFilterOperator.LessThanOrEquals]);
        if (!declaredType.IsValueType || Nullable.GetUnderlyingType(declaredType) != null)
            result.AddRange([GridFilterOperator.IsNull, GridFilterOperator.IsNotNull]);
        return result;
    }

    public static void Validate(FilterCondition condition, IEnumerable<FilterProperty> properties, CultureInfo? culture = null)
    {
        if (!IsActive(condition)) return;
        var property = FindProperty(condition, properties);
        if (!GetOperators(property.Type).Contains(condition.Operator))
            throw new FormatException($"{condition.Operator} is not supported for {property.Title ?? property.Property}.");
        if (NeedsValue(condition.Operator)) Parse(condition.Value, property.Type, culture ?? CultureInfo.CurrentCulture, property.Title ?? property.Property);
    }

    /// <summary>Compiles a snapshot; rebuild it after changing filter state. Empty drafts/groups are ignored.</summary>
    public static Func<T, bool> BuildPredicate<T>(FilterGroup group, IEnumerable<FilterProperty> properties,
        CultureInfo? culture = null, bool caseSensitive = false)
    {
        var schema = properties.ToArray();
        var format = CultureInfo.ReadOnly((CultureInfo)(culture ?? CultureInfo.CurrentCulture).Clone());
        var compare = format.CompareInfo;
        var options = caseSensitive ? CompareOptions.None : CompareOptions.IgnoreCase;
        Func<T, bool>? Compile(FilterGroup current, HashSet<FilterGroup> ancestors)
        {
            if (!ancestors.Add(current)) throw new FormatException("Filter groups cannot contain a cycle.");
            var tests = new List<Func<T, bool>>();
            foreach (var condition in current.Conditions.Where(IsActive))
            {
                Validate(condition, schema, format);
                var property = FindProperty(condition, schema);
                var field = condition.Field;
                var op = condition.Operator;
                var type = Nullable.GetUnderlyingType(property.Type) ?? property.Type;
                var target = NeedsValue(op) ? Parse(condition.Value, type, format, field) : null;
                // Resolve POCO paths once so misspelled paths fail before a grid is enumerated.
                var accessor = BuildAccessor(typeof(T), field);
                tests.Add(item =>
                {
                    var raw = accessor(item);
                    var actual = raw == DBNull.Value ? null : raw;
                    if (actual != null && !type.IsInstanceOfType(actual))
                        actual = Parse(Convert.ToString(actual, format) ?? "", type, format, field);
                    if (op == GridFilterOperator.IsNull) return actual == null;
                    if (op == GridFilterOperator.IsNotNull) return actual != null;
                    if (op == GridFilterOperator.IsEmpty) return actual is string { Length: 0 };
                    if (op == GridFilterOperator.IsNotEmpty) return actual is string { Length: > 0 };
                    if (actual == null) return op == GridFilterOperator.NotEquals;
                    if (type == typeof(string))
                    {
                        var a = (string)actual; var b = (string)target!;
                        return op switch
                        {
                            GridFilterOperator.Equals => compare.Compare(a, b, options) == 0,
                            GridFilterOperator.NotEquals => compare.Compare(a, b, options) != 0,
                            GridFilterOperator.Contains => compare.IndexOf(a, b, options) >= 0,
                            GridFilterOperator.DoesNotContain => compare.IndexOf(a, b, options) < 0,
                            GridFilterOperator.StartsWith => compare.IsPrefix(a, b, options),
                            GridFilterOperator.EndsWith => compare.IsSuffix(a, b, options),
                            _ => false
                        };
                    }
                    if (op == GridFilterOperator.Equals) return Equals(actual, target);
                    if (op == GridFilterOperator.NotEquals) return !Equals(actual, target);
                    var order = ((IComparable)actual).CompareTo(target);
                    return op switch
                    {
                        GridFilterOperator.GreaterThan => order > 0, GridFilterOperator.GreaterThanOrEquals => order >= 0,
                        GridFilterOperator.LessThan => order < 0, GridFilterOperator.LessThanOrEquals => order <= 0,
                        _ => false
                    };
                });
            }
            foreach (var child in current.Groups)
                if (Compile(child, ancestors) is { } test) tests.Add(test);
            ancestors.Remove(current);
            if (tests.Count == 0) return null;
            return current.LogicalOperator == LogicalFilterOperator.And
                ? item => tests.All(test => test(item)) : item => tests.Any(test => test(item));
        }
        return Compile(group, new()) ?? (_ => true);
    }

    private static FilterProperty FindProperty(FilterCondition condition, IEnumerable<FilterProperty> schema) =>
        schema.FirstOrDefault(p => p.Property == condition.Field)
        ?? throw new FormatException($"Unknown filter field '{condition.Field}'.");

    private static object Parse(string value, Type declaredType, CultureInfo culture, string field)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        try
        {
            if (type == typeof(string)) return value;
            if (type == typeof(DateOnly)) return DateOnly.Parse(value, culture);
            if (type == typeof(DateTime)) return DateTime.Parse(value, culture);
            if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(value, culture);
            if (type == typeof(TimeOnly)) return TimeOnly.Parse(value, culture);
            if (type == typeof(TimeSpan)) return TimeSpan.Parse(value, culture);
            if (type == typeof(Guid)) return Guid.Parse(value);
            if (type.IsEnum)
            {
                var parsed = Enum.Parse(type, value, true);
                if (!Enum.IsDefined(type, parsed)) throw new FormatException();
                return parsed;
            }
            var result = Convert.ChangeType(value, type, culture);
            if (result is double d && !double.IsFinite(d) || result is float f && !float.IsFinite(f)) throw new FormatException();
            return result;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or ArgumentException)
        { throw new FormatException($"Enter a valid {type.Name} value for {field}.", ex); }
    }

    private static Func<object?, object?> BuildAccessor(Type type, string field)
    {
        var steps = new List<Func<object, object?>>();
        foreach (var segment in field.Split('.'))
        {
            if (typeof(IDictionary).IsAssignableFrom(type) || typeof(IDictionary<string, object?>).IsAssignableFrom(type) || type == typeof(object))
            {
                steps.Add(value =>
                {
                    if (value is IDictionary dictionary)
                        return dictionary.Contains(segment) ? dictionary[segment] : throw new FormatException($"Unknown filter field '{field}'.");
                    if (value is IDictionary<string, object?> generic)
                        return generic.TryGetValue(segment, out var found) ? found : throw new FormatException($"Unknown filter field '{field}'.");
                    var property = value.GetType().GetProperty(segment, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                    if (property == null || property.GetIndexParameters().Length != 0)
                        throw new FormatException($"Unknown filter field '{field}'.");
                    return property.GetValue(value);
                });
                type = typeof(object);
            }
            else
            {
                var property = type.GetProperty(segment, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (property == null || property.GetIndexParameters().Length != 0)
                    throw new FormatException($"Unknown filter field '{field}'.");
                steps.Add(property.GetValue);
                type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            }
        }
        return value =>
        {
            foreach (var step in steps)
            {
                if (value == null) return null;
                value = step(value);
            }
            return value;
        };
    }
}
