using System.Globalization;

namespace Fx.ControlKit.Reports;

/// <summary>Execution-local specialization of complete, AST-proven integral equalities.</summary>
internal sealed class ReportSqlIntegerSelectionPlan(
    IReadOnlyList<ReportSqlIntegerSelectionPlan.Binding> bindings, string templateSql)
{
    internal sealed record Binding(string Name, string GuardedClause, string DirectClause, decimal Minimum, decimal Maximum);

    internal string TemplateSql { get; } = templateSql;

    internal ReportSqlIntegerSelectionPlan WithTemplate(string sql) => new(bindings, sql);

    internal string Prepare(string sql, IDictionary<string, object>? parameters)
    {
        if (sql != TemplateSql || parameters is null) return sql;
        foreach (var binding in bindings)
        {
            // SQL sanitization must not turn a second caller key into this name.
            var matches = parameters.Where(pair => Sanitized(pair.Key).Equals(binding.Name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length != 1 || !matches[0].Key.Equals(binding.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (matches[0].Value is not (sbyte or byte or short or ushort or int or uint or long or ulong or decimal)) continue;
            var value = Convert.ToDecimal(matches[0].Value, CultureInfo.InvariantCulture);
            if (value != decimal.Truncate(value) || value < binding.Minimum || value > binding.Maximum) continue;
            // Another authored Command table could contain identical text in a
            // literal, comment or predicate. Its SQL must remain untouched: only
            // specialize an unambiguous single emitted clause in this template.
            var start = sql.IndexOf(binding.GuardedClause, StringComparison.Ordinal);
            if (start < 0 || sql.IndexOf(binding.GuardedClause, start + binding.GuardedClause.Length, StringComparison.Ordinal) >= 0) continue;
            sql = sql[..start] + binding.DirectClause + sql[(start + binding.GuardedClause.Length)..];
        }
        return sql;
    }

    private static string Sanitized(string name) => new(name.Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray());
}
