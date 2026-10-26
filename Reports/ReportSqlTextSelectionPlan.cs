using System.Text.RegularExpressions;

namespace Fx.ControlKit.Reports;

/// <summary>
/// Execution-local form of the guarded text selectors. The generated SQL compares a text column through
/// <c>CONVERT … COLLATE</c> guards so that a value outside printable ASCII still reaches the runtime formula;
/// that form cannot use an index. When every value bound to a selector is printable ASCII, the guards decide
/// nothing, and the execution copy compares the column directly, as the original report engine's query does.
/// </summary>
internal sealed class ReportSqlTextSelectionPlan(IReadOnlyList<ReportSqlTextSelectionPlan.Binding> bindings, string templateSql)
{
    /// <summary>One emitted selector: the parameter names it reads, the guarded clause as generated and the direct comparison.</summary>
    internal sealed record Binding(string[] Names, string GuardedClause, string DirectClause);

    internal string TemplateSql { get; } = templateSql;

    internal IReadOnlyList<Binding> Bindings => bindings;

    internal ReportSqlTextSelectionPlan WithTemplate(string sql) => new(bindings, sql);

    /// <summary>The plan after the list rewriter expanded <c>= @name</c> into <c>IN (@name_0, …)</c>.</summary>
    internal ReportSqlTextSelectionPlan Expand(string name, int count)
    {
        var pattern = new Regex($@"=\s*@{Regex.Escape(name)}\b", RegexOptions.IgnoreCase);
        var names = Enumerable.Range(0, count).Select(index => $"{name}_{index}").ToArray();
        var replacement = "IN (" + string.Join(", ", names.Select(expanded => "@" + expanded)) + ")";
        // Both forms change together, or neither: a guarded clause the rewriter did not expand keeps its scalar direct form.
        var expandedBindings = bindings.Select(binding => binding.Names.Length == 1 && binding.Names[0].Equals(name, StringComparison.OrdinalIgnoreCase) && pattern.IsMatch(binding.GuardedClause)
            ? new Binding(names, pattern.Replace(binding.GuardedClause, replacement), pattern.Replace(binding.DirectClause, replacement))
            : binding).ToList();
        return new(expandedBindings, TemplateSql);
    }

    /// <summary>
    /// <paramref name="sql"/> with each selector whose values are all printable ASCII strings compared directly.
    /// <paramref name="template"/> is the generated SQL the plan belongs to; caller-replaced SQL is left untouched.
    /// </summary>
    internal string Prepare(string sql, string template, IDictionary<string, object>? parameters)
    {
        if (template != TemplateSql || parameters is null) return sql;
        foreach (var binding in bindings)
        {
            var direct = true;
            foreach (var name in binding.Names)
            {
                // SQL sanitization must not turn a second caller key into this name.
                var matches = parameters.Where(pair => Sanitized(pair.Key).Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (matches.Length != 1 || !matches[0].Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || matches[0].Value is not string value || !PrintableAscii(value)) { direct = false; break; }
            }
            if (!direct) continue;
            // Another authored Command table could contain identical text; only an unambiguous single emitted clause changes.
            var start = sql.IndexOf(binding.GuardedClause, StringComparison.Ordinal);
            if (start < 0 || sql.IndexOf(binding.GuardedClause, start + binding.GuardedClause.Length, StringComparison.Ordinal) >= 0) continue;
            sql = sql[..start] + binding.DirectClause + sql[(start + binding.GuardedClause.Length)..];
        }
        return sql;
    }

    private static bool PrintableAscii(string value)
    {
        foreach (var character in value)
            if (character is < ' ' or > '~') return false;
        return true;
    }

    private static string Sanitized(string name) => new(name.Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray());
}
