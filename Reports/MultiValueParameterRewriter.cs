using System.Text.RegularExpressions;

namespace Fx.ControlKit.Reports;

/// <summary>
/// Rewrites a Crystal-exported scalar parameter SQL ('… = @Name …') into a
/// multi-value <c>IN (@Name_0, @Name_1, …)</c> clause and supplies the
/// matching SQL parameter dictionary.
///
/// <para>
/// Generalizes the model-specific rewrite that used to live inside
/// FModelCostsReport: instead of hardcoded "Model" / "Community" patterns,
/// this helper takes the parameter selection dictionary produced by either
/// FReportParams (host-side state service) or
/// <see cref="ReportParamDialogControl"/> (when the dialog renders a
/// dual-list-box for an <c>AllowMultiple</c> parameter) and rewrites every
/// parameter the user supplied multiple values for.
/// </para>
///
/// <para>
/// Why the rewrite is needed: Crystal Reports exports a single-value
/// equality predicate per parameter, e.g.
/// <c>{tbl.col} = {?Model}</c>, which <c>CrystalXmlReportLoader</c>
/// translates to <c>tbl.col = @Model</c>. SQL Server can't accept a list
/// for a scalar parameter, so we expand the placeholder into an
/// <c>IN (...)</c> with one fresh parameter per value the user picked.
/// </para>
///
/// <para>
/// Idempotent — the regex anchors on <c>@Name</c> word boundaries, so a
/// second pass over an already-rewritten SQL is a no-op (the expanded
/// <c>@Name_0</c> placeholders don't match the original <c>@Name</c>
/// pattern). Safe to call on the main definition and on every drill-down
/// definition the writer loads.
/// </para>
/// </summary>
public static class MultiValueParameterRewriter
{
    /// <summary>
    /// Mutates <paramref name="definition"/> in place: expands every
    /// <c>= @Name</c> matching a multi-value <paramref name="selections"/>
    /// entry into <c>IN (@Name_0, …, @Name_{N-1})</c>, removes the
    /// now-rewritten Crystal parameter declarations so the writer doesn't
    /// re-prompt, and returns a fresh SQL parameter dictionary that
    /// includes the auto-injected session values plus the expanded
    /// multi-value placeholders.
    /// </summary>
    /// <param name="definition">The loaded report definition. <c>Sql</c>
    /// and <c>TreeSql</c> are rewritten in place; <c>Parameters</c> is
    /// pruned to drop the multi-value parameters that no longer have a
    /// scalar placeholder in the SQL.</param>
    /// <param name="selections">User's parameter selection — keyed by
    /// sanitized parameter name (the same name <c>CrystalXmlReportLoader</c>
    /// emits onto <c>ReportParameter.Name</c>). Single-value parameters
    /// (one entry in the list) are passed through as scalar <c>@Name</c>.
    /// Multi-value entries trigger the <c>IN (...)</c> rewrite.</param>
    /// <param name="extraSqlParameters">Additional scalar parameters (e.g.
    /// auto-injected DivisionID, UserId) to merge into the returned
    /// dictionary. May be null.</param>
    /// <returns>The merged parameter dictionary to pass into
    /// <c>ReportWriterControl.ShowSqlReport</c>.</returns>
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
                // Scalar — leave the SQL alone; just bind the single value.
                // The Crystal-emitted "= @Name" already matches; no rewrite
                // needed.
                sqlParams[name] = values[0];
                continue;
            }

            // Multi-value: expand "<lhs> = @Name" into "<lhs> IN (@Name_0, ...)".
            // Anchor on "= @Name" only — don't try to capture the LHS. The
            // LHS can be a bare column (t.Col), a bracketed alias ([tblFoo_Model]),
            // or a parenthesized expression like (CASE WHEN ... END) — that last
            // form is what Crystal formula-field references ({@MsgGroup} etc.)
            // expand into via CrystalXmlReportLoader.ConvertRecordSelectionFormula,
            // and an LHS capture that only allowed [\[\]\w\.] silently no-op'd on
            // those reports, leaving @Name unbound and SQL Server screaming
            // "Must declare the scalar variable '@Name'". The `\b` after the
            // parameter name keeps us from clipping "@Name" out of "@NameSuffix".
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

            // Crystal formulas sometimes reference the same parameter both as
            // an IN-rewrite target AND as a sentinel predicate test (e.g.
            // Master Assembly List - No Cost v1's "IF {?Assembly Type} = -1
            // THEN ... ELSE ... AND tbl.AssemblyType = {?Assembly Type}"). The
            // sentinel reference becomes "@AssemblyType = -1" in SQL, and
            // without a scalar @Name binding SQL Server fails with
            // "Must declare the scalar variable '@Name'". Bind to the first
            // picked value — preserves the user's selection in the cond test.
            sqlParams[name] = values[0];
        }

        // Drop the now-rewritten Crystal scalar parameters so neither
        // ReportWriterControl's parameter-resolution loop nor any prompt
        // dialog tries to bind a value for the bare @Name (its placeholder
        // no longer exists after the rewrite).
        //
        // Single-value selections are dropped too: their values are already
        // in sqlParams, and leaving them in Parameters would cause the
        // writer to prompt for them again when it can't auto-resolve.
        var rewrittenAllNames = new HashSet<string>(selections.Keys, StringComparer.OrdinalIgnoreCase);
        definition.Parameters = definition.Parameters
            .Where(p => !rewrittenAllNames.Contains(p.Name))
            .ToList();

        return sqlParams;
    }
}
