using System.Text.RegularExpressions;

namespace Fx.ControlKit.Reports;

public static class MultiValueParameterRewriter
{
    public static Dictionary<string, object> Apply(
        ReportDefinition definition,
        IReadOnlyDictionary<string, IReadOnlyList<string>> selections,
        IReadOnlyDictionary<string, object>? extraSqlParameters = null)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        selections ??= new Dictionary<string, IReadOnlyList<string>>();

        var sqlParams = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (extraSqlParameters != null)
        {
            foreach (var kv in extraSqlParameters) sqlParams[kv.Key] = kv.Value;
        }

        foreach (var kv in selections)
        {
            var name = kv.Key;
            var values = kv.Value ?? Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(name) || values.Count == 0) continue;

            if (values.Count == 1)
            {
                sqlParams[name] = values[0];
                continue;
            }

            var pattern = new Regex(
                $@"=\s*@{Regex.Escape(name)}\b",
                RegexOptions.IgnoreCase);

            var placeholders = string.Join(", ",
                Enumerable.Range(0, values.Count).Select(i => $"@{name}_{i}"));
            var replacement = $"IN ({placeholders})";

            if (!string.IsNullOrEmpty(definition.Sql))
                definition.Sql = pattern.Replace(definition.Sql, replacement);

            if (!string.IsNullOrEmpty(definition.TreeSql))
                definition.TreeSql = pattern.Replace(definition.TreeSql, replacement);

            for (var i = 0; i < values.Count; i++)
                sqlParams[$"{name}_{i}"] = values[i];

            sqlParams[name] = values[0];
        }

        var rewrittenAllNames = new HashSet<string>(selections.Keys, StringComparer.OrdinalIgnoreCase);
        definition.Parameters = definition.Parameters
            .Where(p => !rewrittenAllNames.Contains(p.Name))
            .ToList();

        return sqlParams;
    }
}
