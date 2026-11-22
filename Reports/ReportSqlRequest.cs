using System.Text.RegularExpressions;

namespace Fx.ControlKit.Reports;

/// <summary>A list the rewriter bound one parameter per member: <c>IN (@Name_0, …)</c>, and for a positioned text selector the
/// rowset <c>SELECT @Name_0 AS [Value] UNION ALL …</c>. <see cref="Typed"/> holds members bound as values rather than prompt
/// text (a subreport link's list).</summary>
internal sealed record ReportSqlList(string Name, string[] Values, ReportParameterType? Type, bool Rowset)
{
    internal object?[]? Typed { get; init; }
    internal string Member(int index) => Name + "_" + index;
    internal IEnumerable<string> Members => Enumerable.Range(0, Values.Length).Select(Member);
    internal string InList => "IN (" + string.Join(", ", Members.Select(member => "@" + member)) + ")";
    internal string RowsetUnion => string.Join(" UNION ALL ", Members.Select(member => $"SELECT @{member} AS [Value]"));
    internal object?[] Bound => Typed ?? Values.Select(value => (object?)MultiValueParameterRewriter.TypedValue(value, Type)).ToArray();

    /// <summary><paramref name="clause"/> as the rewriter expands it for this list.</summary>
    internal string Expand(string clause)
    {
        if (Rowset) clause = clause.Replace($"SELECT @{Name} AS [Value]", RowsetUnion, StringComparison.OrdinalIgnoreCase);
        var inList = InList;
        return new Regex($@"=\s*@{Regex.Escape(Name)}\b", RegexOptions.IgnoreCase).Replace(clause, _ => inList);
    }
}

/// <summary>
/// Fits one request to SQL Server: at most <see cref="ReportSqlSelectionPlan.MaxRequestParameters"/> parameters, every name the
/// SQL reads bound. It runs when the request is prepared, against its final parameters (prompts, session values, ranges, links,
/// constants and the selection's reserved names), so every consumer of a definition decides for its own request. A request
/// within the limit is sent as it is. Past it, names the library derived and the SQL does not read are left out, then the
/// longest lists bind as one value per SQL type (<see cref="MultiValueParameterRewriter.ListParts"/>) and the server reads them
/// back, every filter staying in the query, until it fits.
/// </summary>
internal static class ReportSqlRequest
{
    private static readonly Regex Reference = new(@"@([\p{L}\p{N}_#$]+)", RegexOptions.CultureInvariant);

    internal static string Fit(ReportDefinition copy, string sql, IDictionary<string, object> parameters, bool sendsOnlyRead = false)
    {
        var tree = copy.TreeSql ?? "";
        HashSet<string>? referenced = null;
        HashSet<string> Referenced() => referenced ??= References(sql + "\n" + tree);
        var keys = new HashSet<string>(parameters.Keys, StringComparer.OrdinalIgnoreCase);

        // A member the SQL reads is bound from the list itself, whatever request supplied the other values.
        foreach (var list in copy.SqlLists)
            for (var index = 0; index < list.Values.Length; index++)
                if (!keys.Contains(list.Member(index)) && Referenced().Contains(list.Member(index)))
                {
                    parameters[list.Member(index)] = list.Typed is { } typed ? typed[index] ?? DBNull.Value : MultiValueParameterRewriter.TypedValue(list.Values[index], list.Type);
                    keys.Add(list.Member(index));
                }
        int Sent() => Count(copy, parameters, sendsOnlyRead ? sql + "\n" + tree : null);
        if (Sent() <= ReportSqlSelectionPlan.MaxRequestParameters) return sql;

        foreach (var name in DerivedNames(copy, parameters).Where(name => !Referenced().Contains(name)).ToArray())
            Remove(parameters, name);
        foreach (var list in copy.SqlLists.Where(list => list.Values.Length > 1).OrderByDescending(list => list.Values.Length).ThenBy(list => list.Name, StringComparer.Ordinal).ToArray())
        {
            if (Sent() <= ReportSqlSelectionPlan.MaxRequestParameters) break;
            Pack(copy, list, ref sql, ref tree, parameters);
        }
        if (copy.TreeSql is { Length: > 0 }) copy.TreeSql = tree;
        return sql;
    }

    /// <summary>The parameters the request sends: the caller's, or only those <paramref name="readBy"/> names when given, and the
    /// selection's reserved names it adds when validated.</summary>
    internal static int Count(ReportDefinition definition, IDictionary<string, object> parameters, string? readBy = null)
    {
        var read = readBy is null ? null : ReportSqlParameters.Names(readBy);
        var sent = read is null ? parameters.Count : parameters.Keys.Count(key => read.Contains(key.TrimStart('@')));
        var generated = definition.PositionedSqlSelection?.GeneratedNames().ToArray() ?? [];
        if (generated.Length == 0) return sent;
        var keys = new HashSet<string>(parameters.Keys, StringComparer.OrdinalIgnoreCase);
        return sent + generated.Distinct(StringComparer.OrdinalIgnoreCase).Count(name => !keys.Contains(name) && (read is null || read.Contains(name)));
    }

    internal static HashSet<string> References(string sql)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Reference.Matches(sql)) names.Add(match.Groups[1].Value);
        return names;
    }

    // Names this library derived from a value: a list's first member under its own name, its members, the list bound as one
    // value and range bounds.
    private static IEnumerable<string> DerivedNames(ReportDefinition definition, IDictionary<string, object> parameters)
    {
        foreach (var list in definition.SqlLists)
        {
            yield return list.Name;
            foreach (var member in list.Members) yield return member;
            yield return MultiValueParameterRewriter.FitName(MultiValueParameterRewriter.ListParameterPrefix + list.Name);
        }
        foreach (var name in ReportRangeParameters.BoundNames(parameters)) yield return name;
    }

    // The operand of an IN list the rewriter expanded: a column (dotted, bracketed or quoted parts), optionally with a collation,
    // at the start of a condition (OperandOf).
    private const string Part = @"(?:\[(?:[^\]]|\]\])*\]|""[^""]*""|[\p{L}_#][\p{L}\p{N}_@$#]*)";
    private static readonly Regex Operand = new($@"{Part}(?:\s*\.\s*{Part})*(?:\s+COLLATE\s+[\p{{L}}\p{{N}}_]+)?\s*$", RegexOptions.RightToLeft | RegexOptions.CultureInvariant);
    private static readonly Regex ConditionStart = new(@"(?:^|\(|\b(?:WHERE|AND|OR|NOT|ON|WHEN|HAVING))\s*$", RegexOptions.IgnoreCase | RegexOptions.RightToLeft | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Keywords = new(["NOT", "AND", "OR", "WHERE", "ON", "WHEN", "HAVING", "SELECT", "CASE", "THEN", "ELSE", "END", "IS", "NULL", "EXISTS"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The operand that ends <paramref name="before"/>, the text before an expanded IN list, at the start of a condition: a column,
    /// or a parenthesized expression, a function call or a CASE expression, each optionally with a collation. Null for any other
    /// operand (an arithmetic or concatenated expression, text the tokens do not close), which is not repeated.
    /// </summary>
    private static (int Index, string Text)? OperandOf(string before)
    {
        if (Operand.Match(before) is { Success: true } column && !Keywords.Contains(column.Value.Trim()) && ConditionStart.IsMatch(before[..column.Index]))
            return (column.Index, column.Value.TrimEnd());
        if (Tokens(before) is not { Count: > 0 } tokens) return null;
        var core = tokens.Count - 1;
        if (core >= 2 && tokens[core - 1].Text.Equals("COLLATE", StringComparison.OrdinalIgnoreCase) && IsName(tokens[core])) core -= 2;
        var first = tokens[core].Open;
        if (first < 0) return null;
        // A function's name, of one or more dotted parts, before its parenthesis.
        if (tokens[core].Text == ")")
            for (var k = first - 1; k >= 0 && IsName(tokens[k]) && !Keywords.Contains(tokens[k].Text); k -= 2)
            {
                first = k;
                if (k < 1 || tokens[k - 1].Text != ".") break;
            }
        var index = tokens[first].Start;
        return ConditionStart.IsMatch(before[..index]) ? (index, before[index..].TrimEnd()) : null;
    }

    private sealed class Token(int start, string text)
    {
        public int Start { get; } = start;
        public string Text { get; } = text;
        /// <summary>For a closing parenthesis or END: the index of the token it closes; otherwise -1.</summary>
        public int Open { get; set; } = -1;
    }
    private static bool IsName(Token token) => token.Text.Length > 0 && (char.IsLetter(token.Text[0]) || token.Text[0] is '_' or '[' or '"');

    // SQL tokens: quoted text, bracketed and quoted names, words, single characters; comments are skipped. A closing parenthesis
    // or END records the token it closes. Null when a quote or a comment does not close.
    private static List<Token>? Tokens(string text)
    {
        var tokens = new List<Token>();
        var open = new Stack<int>();
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && next == '-') { var end = text.IndexOf('\n', i); i = end < 0 ? text.Length : end; continue; }
            if (c == '/' && next == '*') { var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) return null; i = end + 2; continue; }
            var start = i;
            if (c is '\'' or '"' or '[' || c is 'N' or 'n' && next == '\'')
            {
                var close = c switch { '[' => ']', '"' => '"', _ => '\'' };
                i += c is 'N' or 'n' ? 2 : 1;
                while (true)
                {
                    if (i >= text.Length) return null;
                    if (text[i++] != close) continue;
                    if (i < text.Length && text[i] == close) { i++; continue; }
                    break;
                }
            }
            else if (char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$')
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '@' or '#' or '$')) i++;
            else i++;
            var token = new Token(start, text[start..i]);
            tokens.Add(token);
            var opens = token.Text == "(" || token.Text.Equals("CASE", StringComparison.OrdinalIgnoreCase);
            var closes = token.Text == ")" || token.Text.Equals("END", StringComparison.OrdinalIgnoreCase);
            if (opens) open.Push(tokens.Count - 1);
            else if (closes)
            {
                // A close that does not match the innermost open leaves everything before it unmatched.
                if (open.Count > 0 && (tokens[open.Peek()].Text == "(") == (token.Text == ")")) token.Open = open.Pop();
                else open.Clear();
            }
        }
        return tokens;
    }

    /// <summary>
    /// Binds <paramref name="list"/> as one value per SQL type and reads it back on the server. Each <c>IN (@Name_0, …)</c> reads
    /// the rowset of every part: with one part, <c>IN (SELECT [value] FROM …)</c>; with several, the operand is compared with each
    /// part (<c>(x IN (…) OR x IN (…))</c>), so every member compares in the type it binds with on its own; the operand is a column,
    /// a parenthesized expression, a function call or a CASE expression (<see cref="OperandOf"/>). Any other operand reads one text
    /// rowset of every member, whose type is that of the widest member, as a union of the members bound one by one would be (a date
    /// as ISO 8601 text); a list with no text form then stays per member. A positioned text selector's rowset reads that
    /// text rowset too.
    /// </summary>
    private static void Pack(ReportDefinition copy, ReportSqlList list, ref string sql, ref string tree, IDictionary<string, object> parameters)
    {
        var members = list.Members.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parameter = MultiValueParameterRewriter.FitName(MultiValueParameterRewriter.ListParameterPrefix + list.Name);
        // Each part takes a name no parameter of the request has and the SQL does not read: the list's one-value name when it
        // has one part, else the part prefix, a number and the list's name (FitName shortens either past SQL Server's limit).
        var taken = new HashSet<string>(parameters.Keys, StringComparer.OrdinalIgnoreCase);
        taken.UnionWith(References(sql + "\n" + tree));
        taken.Remove(parameter);
        var parts = MultiValueParameterRewriter.ListParts(list.Bound, (index, count) =>
        {
            if (count == 1 && !taken.Contains(parameter)) return parameter;
            for (var number = index; ; number++)
                if (MultiValueParameterRewriter.FitName($"{MultiValueParameterRewriter.ListPartPrefix}{number}_{list.Name}") is var name && taken.Add(name)) return name;
        });
        // One rowset of every member as text: the single part of a one-type list, else the members' text joined.
        MultiValueParameterRewriter.ListPart? single = parts is { Count: 1 } ? parts[0] : null;
        if (single is null && list.Typed is null)
        {
            var (value, source) = MultiValueParameterRewriter.ListSource(list.Values, list.Type, parameter);
            single = new(parameter, value, source);
        }
        var bind = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var kept = false;
        // A guarded record-selection term of the list takes its membership form, which reads the text rowset and its key.
        var terms = single is null ? [] : (copy.PositionedSqlSelection?.Strings ?? [])
            .Where(binding => binding.Name.Equals(list.Name, StringComparison.OrdinalIgnoreCase) && binding.Guarded is { Length: > 0 } && binding.Membership is { Length: > 0 })
            .Select(binding => (Guarded: list.Expand(binding.Guarded!), Membership: new Regex($@"=\s*@{Regex.Escape(list.Name)}\b", RegexOptions.IgnoreCase)
                // The replacement is SQL, not a substitution pattern: a source such as OPENJSON's '$' path stays as written.
                .Replace(binding.Membership!, _ => $"IN (SELECT [value] FROM {single.Source} UNION ALL SELECT {ReportSelectionSql.MembershipKey})"))).ToArray();
        string Rewrite(string text)
        {
            if (text.Length == 0) return text;
            foreach (var (guarded, membership) in terms)
                if (text.Contains(guarded, StringComparison.Ordinal))
                {
                    text = text.Replace(guarded, membership, StringComparison.Ordinal);
                    bind[single!.Parameter] = single.Value;
                }
            if (list.Rowset && text.Contains(list.RowsetUnion, StringComparison.Ordinal))
            {
                if (single is null) kept = true;
                else
                {
                    text = text.Replace(list.RowsetUnion, $"SELECT [value] AS [Value] FROM {single.Source}", StringComparison.Ordinal);
                    bind[single.Parameter] = single.Value;
                }
            }
            var builder = new System.Text.StringBuilder();
            var at = 0;
            for (var found = text.IndexOf(list.InList, StringComparison.Ordinal); found >= 0; found = text.IndexOf(list.InList, at, StringComparison.Ordinal))
            {
                var before = text[at..found];
                if (parts is { Count: > 1 } && OperandOf(before) is { } operand)
                {
                    var column = operand.Text;
                    builder.Append(before, 0, operand.Index).Append('(')
                        .Append(string.Join(" OR ", parts.Select(part => $"{column} IN (SELECT [value] FROM {part.Source})"))).Append(')');
                    foreach (var part in parts) bind[part.Parameter] = part.Value;
                }
                else if (single is not null)
                {
                    builder.Append(before).Append($"IN (SELECT [value] FROM {single.Source})");
                    bind[single.Parameter] = single.Value;
                }
                else { builder.Append(before).Append(list.InList); kept = true; }
                at = found + list.InList.Length;
            }
            return at == 0 ? text : builder.Append(text, at, text.Length - at).ToString();
        }
        var (packedSql, packedTree) = (Rewrite(sql), Rewrite(tree));
        // A list that cannot be read back somewhere (no text form and no plain operand) stays bound per member.
        if (kept) return;
        (sql, tree) = (packedSql, packedTree);
        if (copy.PositionedSqlSelection is { } plan)
            foreach (var binding in plan.Strings.Where(binding => binding.Expanded && binding.Members is null && binding.Name.Equals(list.Name, StringComparison.OrdinalIgnoreCase)))
            {
                binding.SqlNames = [];
                binding.Members = list.Values;
            }
        // The selector's direct comparison reads the list the same way, so a list of printable ASCII text still compares the column directly.
        copy.PositionedTextSelection = copy.PositionedTextSelection?.WithList(members, Rewrite, list.Values);
        foreach (var member in members) Remove(parameters, member);
        foreach (var (name, value) in bind) parameters[name] = value;
        copy.ExpandedSqlStringParameters = copy.ExpandedSqlStringParameters.Where(name => !members.Contains(name)).ToArray();
    }

    private static void Remove(IDictionary<string, object> parameters, string name)
    {
        if (parameters.Remove(name)) return;
        foreach (var key in parameters.Keys.Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray()) parameters.Remove(key);
    }
}
