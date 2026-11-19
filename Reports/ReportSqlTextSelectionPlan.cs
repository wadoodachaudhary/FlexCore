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
    /// <summary>One emitted selector: the parameter names it reads, the guarded clause as generated and the direct comparison.
    /// <see cref="Members"/> holds the values of a list the request reads back from one value, whose names it no longer binds.
    /// <see cref="Column"/> is the table or view column the direct comparison reads; without one the selector stays guarded.</summary>
    internal sealed record Binding(string[] Names, string GuardedClause, string DirectClause, IReadOnlyList<string>? Members = null)
    {
        internal ReportTextColumn? Column { get; init; }
    }

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
            ? binding with { Names = names, GuardedClause = pattern.Replace(binding.GuardedClause, replacement), DirectClause = pattern.Replace(binding.DirectClause, replacement) }
            : binding).ToList();
        return new(expandedBindings, TemplateSql);
    }

    /// <summary>The plan after the list whose members are <paramref name="names"/> was bound as one value: its selector's clauses
    /// read the list as <paramref name="rewrite"/> rewrote the query, and the list's <paramref name="values"/> decide the direct
    /// comparison. A selector that reads other names too keeps its guarded form.</summary>
    internal ReportSqlTextSelectionPlan WithList(IReadOnlySet<string> names, Func<string, string> rewrite, IReadOnlyList<string> values)
        => new(bindings.Where(binding => !binding.Names.Any(names.Contains) || binding.Names.All(names.Contains))
            .Select(binding => binding.Names.Length > 0 && binding.Names.All(names.Contains)
                ? binding with { Names = [], GuardedClause = rewrite(binding.GuardedClause), DirectClause = rewrite(binding.DirectClause), Members = values } : binding)
            .ToList(), TemplateSql);

    /// <summary>
    /// <paramref name="sql"/> with each selector whose values are all printable ASCII strings compared directly.
    /// <paramref name="template"/> is the generated SQL the plan belongs to; caller-replaced SQL is left untouched. Only a selector
    /// on a table or view column that <paramref name="caseInsensitive"/> accepts (any, when null) compares directly.
    /// </summary>
    internal string Prepare(string sql, string template, IDictionary<string, object>? parameters, Func<ReportTextColumn, bool>? caseInsensitive = null)
    {
        if (template != TemplateSql || parameters is null) return sql;
        // Every caller key by its SQL-sanitized name, built once: the first key and how many keys share that name.
        var sanitized = new Dictionary<string, (int Count, KeyValuePair<string, object> First)>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parameters)
        {
            var key = ReportSqlSelectionPlan.Sanitized(pair.Key);
            sanitized[key] = sanitized.TryGetValue(key, out var seen) ? (seen.Count + 1, seen.First) : (1, pair);
        }
        foreach (var binding in bindings)
        {
            var direct = binding.Column is { } column && (caseInsensitive?.Invoke(column) ?? true) && (binding.Members?.All(PrintableAscii) ?? true);
            foreach (var name in binding.Members is null ? binding.Names : [])
            {
                // SQL sanitization must not turn a second caller key into this name.
                if (!sanitized.TryGetValue(name, out var match) || match.Count != 1 || !match.First.Key.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || match.First.Value is not string value || !PrintableAscii(value)) { direct = false; break; }
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

}
