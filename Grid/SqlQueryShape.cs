namespace Fx.ControlKit.Grid;

/// <summary>One item of a SELECT list, as written in the query text.</summary>
/// <param name="Expression">The item's text.</param>
/// <param name="Name">Its alias, or the column name of a plain column reference; null when the text gives none.</param>
/// <param name="Aggregate">The aggregate function it calls (SUM, COUNT, AVG, MIN, MAX, ...), "MIXED" for several, or null.</param>
/// <param name="IsStar">True for <c>*</c> and <c>alias.*</c>.</param>
public sealed record SqlSelectItem(string Expression, string? Name, string? Aggregate, bool IsStar)
{
    public bool IsAggregate => Aggregate != null;
}

/// <summary>
/// What a SELECT statement's text says about its result: the select list, which items are
/// aggregates, and whether the query groups. It reads the text only (no database, no provider
/// parser), so it is a description to lay a result out by, never a validation of the query.
/// </summary>
public sealed class SqlQueryShape
{
    public static readonly SqlQueryShape Unknown = new();

    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUM", "COUNT", "COUNT_BIG", "AVG", "MIN", "MAX", "STDEV", "STDEVP", "VAR", "VARP",
        "STRING_AGG", "GROUP_CONCAT", "CHECKSUM_AGG", "APPROX_COUNT_DISTINCT"
    };

    private static readonly HashSet<string> NotAnAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        "END", "NULL", "AND", "OR", "NOT", "LIKE", "IN", "IS", "BETWEEN", "THEN", "ELSE", "WHEN", "CASE", "AS"
    };

    private static readonly HashSet<string> ClauseStart = new(StringComparer.OrdinalIgnoreCase)
    {
        "FROM", "INTO", "WHERE", "HAVING", "UNION", "EXCEPT", "INTERSECT", "OPTION", "WINDOW"
    };

    /// <summary>True when the text holds a SELECT statement.</summary>
    public bool IsSelect { get; private init; }

    /// <summary>True when the statement has a GROUP BY clause.</summary>
    public bool HasGroupBy { get; private init; }

    /// <summary>True when the select list holds <c>*</c>, so its items do not line up with the result's columns.</summary>
    public bool HasStar { get; private init; }

    public IReadOnlyList<SqlSelectItem> SelectItems { get; private init; } = Array.Empty<SqlSelectItem>();

    public IReadOnlyList<string> GroupByExpressions { get; private init; } = Array.Empty<string>();

    public bool HasAggregates => SelectItems.Any(item => item.IsAggregate);

    /// <summary>
    /// The select item that produced result column <paramref name="ordinal"/> named
    /// <paramref name="columnName"/>, or null when the text does not tell.
    /// </summary>
    public SqlSelectItem? ItemFor(int ordinal, string columnName, int columnCount)
    {
        if (!HasStar && SelectItems.Count == columnCount && ordinal >= 0 && ordinal < SelectItems.Count)
            return SelectItems[ordinal];
        return SelectItems.FirstOrDefault(item =>
            !item.IsStar && string.Equals(item.Name, columnName, StringComparison.OrdinalIgnoreCase));
    }

    public static SqlQueryShape Parse(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return Unknown;
        var tokens = Lex(sql);

        // The result comes from the last statement that selects.
        var statement = new List<Token>();
        List<Token>? selected = null;
        foreach (var token in tokens)
        {
            if (token is { Kind: Kind.Symbol, Text: ";", Depth: 0 })
            {
                if (statement.Any(IsTopSelect)) selected = statement;
                statement = new List<Token>();
            }
            else statement.Add(token);
        }
        if (statement.Any(IsTopSelect)) selected = statement;
        if (selected == null) return Unknown;

        var start = selected.FindIndex(IsTopSelect) + 1;
        start = SkipSelectPrefix(selected, start);
        var end = start;
        while (end < selected.Count && !EndsSelectList(selected, end)) end++;

        var items = Split(selected, start, end).Select(item => ParseItem(sql, item)).ToList();

        var groupBy = new List<string>();
        var hasGroupBy = false;
        for (var i = end; i < selected.Count - 1; i++)
        {
            if (!IsTopWord(selected[i], "GROUP") || !IsTopWord(selected[i + 1], "BY")) continue;
            hasGroupBy = true;
            var from = i + 2;
            var to = from;
            while (to < selected.Count && !EndsGroupBy(selected, to)) to++;
            groupBy.AddRange(Split(selected, from, to).Select(part => sql[part[0].Start..part[^1].End]));
            break;
        }

        return new SqlQueryShape
        {
            IsSelect = true,
            HasGroupBy = hasGroupBy,
            HasStar = items.Any(item => item.IsStar),
            SelectItems = items,
            GroupByExpressions = groupBy
        };
    }

    // ── Statement structure ─────────────────────────────────────────

    private static bool IsTopSelect(Token token) => IsTopWord(token, "SELECT");

    private static bool IsTopWord(Token token, string word) =>
        token.Kind == Kind.Word && token.Depth == 0 && token.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool IsSymbol(Token token, string text) => token.Kind == Kind.Symbol && token.Text == text;

    private static int SkipSelectPrefix(List<Token> tokens, int index)
    {
        if (index < tokens.Count && (IsTopWord(tokens[index], "DISTINCT") || IsTopWord(tokens[index], "ALL"))) index++;
        if (index >= tokens.Count || !IsTopWord(tokens[index], "TOP")) return index;
        index++;
        if (index < tokens.Count && IsSymbol(tokens[index], "(")) index = Close(tokens, index) + 1;
        else if (index < tokens.Count && tokens[index].Kind == Kind.Number) index++;
        if (index < tokens.Count && IsTopWord(tokens[index], "PERCENT")) index++;
        if (index + 1 < tokens.Count && IsTopWord(tokens[index], "WITH") && IsTopWord(tokens[index + 1], "TIES")) index += 2;
        return index;
    }

    private static bool EndsSelectList(List<Token> tokens, int index)
    {
        var token = tokens[index];
        if (token.Kind != Kind.Word || token.Depth != 0) return false;
        if (ClauseStart.Contains(token.Text)) return true;
        // "WITHIN GROUP (ORDER BY ...)" belongs to an ordered-set aggregate in the select list.
        return (IsTopWord(token, "GROUP") || IsTopWord(token, "ORDER"))
            && index + 1 < tokens.Count && IsTopWord(tokens[index + 1], "BY");
    }

    private static bool EndsGroupBy(List<Token> tokens, int index)
    {
        var token = tokens[index];
        if (token.Kind != Kind.Word || token.Depth != 0) return false;
        return ClauseStart.Contains(token.Text) || IsTopWord(token, "ORDER") || IsTopWord(token, "FOR");
    }

    /// <summary>Index of the parenthesis closing the one at <paramref name="open"/>.</summary>
    private static int Close(List<Token> tokens, int open)
    {
        for (var i = open + 1; i < tokens.Count; i++)
            if (IsSymbol(tokens[i], ")") && tokens[i].Depth == tokens[open].Depth) return i;
        return tokens.Count - 1;
    }

    private static List<List<Token>> Split(List<Token> tokens, int start, int end)
    {
        var parts = new List<List<Token>>();
        var current = new List<Token>();
        for (var i = start; i < end; i++)
        {
            if (tokens[i] is { Kind: Kind.Symbol, Text: ",", Depth: 0 })
            {
                if (current.Count > 0) parts.Add(current);
                current = new List<Token>();
            }
            else current.Add(tokens[i]);
        }
        if (current.Count > 0) parts.Add(current);
        return parts;
    }

    // ── Select items ────────────────────────────────────────────────

    private static SqlSelectItem ParseItem(string sql, List<Token> item)
    {
        var expression = sql[item[0].Start..item[^1].End];
        var isStar = IsSymbol(item[^1], "*") && (item.Count == 1 || (item.Count == 3 && IsSymbol(item[1], ".")));
        return new SqlSelectItem(expression, isStar ? null : NameOf(item), AggregateOf(item), isStar);
    }

    private static bool IsIdentifier(Token token) =>
        token.Kind == Kind.Quoted || (token.Kind == Kind.Word && !NotAnAlias.Contains(token.Text));

    private static string? NameOf(List<Token> item)
    {
        var last = item[^1];
        if (item.Count == 1) return IsIdentifier(last) ? last.Text : null;

        // T-SQL "alias = expression".
        if (item.Count >= 3 && IsSymbol(item[1], "=") && item[0].Depth == 0
            && (IsIdentifier(item[0]) || item[0].Kind == Kind.String))
            return item[0].Text;

        if (last.Depth != 0 || !(IsIdentifier(last) || last.Kind == Kind.String)) return null;
        var before = item[^2];
        if (IsTopWord(before, "AS")) return last.Text;
        if (IsSymbol(before, ".")) return last.Kind == Kind.String ? null : last.Text;
        if (IsSymbol(before, ")")) return last.Text;
        return before.Kind switch
        {
            Kind.Word => NotAnAlias.Contains(before.Text) && !before.Text.Equals("END", StringComparison.OrdinalIgnoreCase)
                ? null
                : last.Text,
            Kind.Quoted or Kind.Number or Kind.String => last.Text,
            _ => null
        };
    }

    private static string? AggregateOf(List<Token> item)
    {
        string? found = null;
        for (var i = 0; i < item.Count; i++)
        {
            // An aggregate inside a scalar subquery belongs to that subquery.
            if (IsSymbol(item[i], "(") && i + 1 < item.Count
                && item[i + 1].Kind == Kind.Word && item[i + 1].Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                i = Close(item, i);
                continue;
            }
            if (item[i].Kind != Kind.Word || !AggregateFunctions.Contains(item[i].Text)
                || i + 1 >= item.Count || !IsSymbol(item[i + 1], "(")) continue;

            var close = Close(item, i + 1);
            var isWindow = close + 1 < item.Count && item[close + 1].Kind == Kind.Word
                && item[close + 1].Text.Equals("OVER", StringComparison.OrdinalIgnoreCase);
            if (!isWindow)
            {
                var name = item[i].Text.ToUpperInvariant();
                found = found == null || found == name ? name : "MIXED";
            }
            i = close;
        }
        return found;
    }

    // ── Lexer ───────────────────────────────────────────────────────

    private enum Kind { Word, Quoted, String, Number, Symbol }

    /// <summary>Text holds the unquoted name for Quoted and the unquoted value for String.</summary>
    private readonly record struct Token(Kind Kind, string Text, int Depth, int Start, int End);

    private static List<Token> Lex(string sql)
    {
        var tokens = new List<Token>();
        var depth = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c) || c == '\0') { i++; continue; }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var nesting = 1;
                i += 2;
                while (i < sql.Length && nesting > 0)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { nesting++; i += 2; }
                    else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { nesting--; i += 2; }
                    else i++;
                }
                continue;
            }

            var start = i;
            if (c == '\'' || ((c == 'N' || c == 'n') && i + 1 < sql.Length && sql[i + 1] == '\''))
            {
                if (c != '\'') i++;
                tokens.Add(new Token(Kind.String, ReadQuoted(sql, ref i, '\''), depth, start, i));
            }
            else if (c == '[') tokens.Add(new Token(Kind.Quoted, ReadQuoted(sql, ref i, ']'), depth, start, i));
            else if (c == '"' || c == '`') tokens.Add(new Token(Kind.Quoted, ReadQuoted(sql, ref i, c), depth, start, i));
            else if (char.IsLetter(c) || c == '_' || c == '@' || c == '#')
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '#' or '$')) i++;
                tokens.Add(new Token(Kind.Word, sql[start..i], depth, start, i));
            }
            else if (char.IsDigit(c))
            {
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '.')) i++;
                tokens.Add(new Token(Kind.Number, sql[start..i], depth, start, i));
            }
            else if (c == '(')
            {
                tokens.Add(new Token(Kind.Symbol, "(", depth, start, ++i));
                depth++;
            }
            else if (c == ')')
            {
                if (depth > 0) depth--;
                tokens.Add(new Token(Kind.Symbol, ")", depth, start, ++i));
            }
            else tokens.Add(new Token(Kind.Symbol, c.ToString(), depth, start, ++i));
        }
        return tokens;
    }

    /// <summary>Reads a quoted run starting at its opening quote; a doubled closing quote is a literal one.</summary>
    private static string ReadQuoted(string sql, ref int i, char close)
    {
        var text = new System.Text.StringBuilder();
        i++;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close) { text.Append(close); i += 2; continue; }
                i++;
                break;
            }
            text.Append(sql[i++]);
        }
        return text.ToString();
    }
}
