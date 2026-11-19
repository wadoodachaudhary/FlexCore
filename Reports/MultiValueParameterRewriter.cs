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
        // The original engine's query carries no empty-string member of a multi-value string prompt: the blank choice
        // stays in the prompt's list and in the runtime formula, and only the server query leaves it out.
        var sqlSelections = selections.ToDictionary(pair => pair.Key, pair => SqlValues(definition, pair.Key, pair.Value));
        definition.PositionedSqlSelection?.ConfigureLists(sqlSelections, extraSqlParameters);
        var integerSelection = definition.RestoreIntegerSelectionForRewrite();
        var textSelection = definition.RestoreTextSelectionForRewrite();

        var sqlParams = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var lists = definition.SqlLists.ToList();
        if (extraSqlParameters != null)
        {
            foreach (var kv in extraSqlParameters) sqlParams[kv.Key] = kv.Value;
        }

        foreach (var kv in sqlSelections)
        {
            var name = kv.Key;
            var values = kv.Value ?? Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(name) || values.Count == 0) continue;
            // A prompt's choices travel as text whatever the prompt's type (numeric choices arrive as
            // invariant text such as "-1.0"). SQL Server cannot convert '-1.0' to an int column, so each member is bound
            // with the prompt's declared type; a string prompt keeps its text.
            var parameterType = definition.Parameters
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.ParameterType;

            if (values.Count == 1)
            {
                // Scalar — leave the SQL alone; just bind the single value.
                // The Crystal-emitted "= @Name" already matches; no rewrite
                // needed.
                sqlParams[name] = TypedValue(values[0], parameterType);
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

            // Positioned text selectors retain a guarded predicate over a parameter
            // rowset. Expand that rowset so non-ASCII fallbacks apply to every value.
            var rowset = $"SELECT @{name} AS [Value]";
            var expandsRowset = definition.PositionedLayout?.SqlStringParameters.Contains(name, StringComparer.OrdinalIgnoreCase) == true
                && definition.Sql.Contains(rowset, StringComparison.OrdinalIgnoreCase);
            if (expandsRowset)
            {
                definition.Sql = definition.Sql.Replace(rowset,
                    string.Join(" UNION ALL ", Enumerable.Range(0, values.Count).Select(i => $"SELECT @{name}_{i} AS [Value]")),
                    StringComparison.OrdinalIgnoreCase);
                definition.ExpandedSqlStringParameters = definition.ExpandedSqlStringParameters
                    .Concat(Enumerable.Range(0, values.Count).Select(i => $"{name}_{i}")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }

            if (!string.IsNullOrEmpty(definition.Sql))
                definition.Sql = pattern.Replace(definition.Sql, replacement);
            textSelection = textSelection?.Expand(name, values.Count);

            if (!string.IsNullOrEmpty(definition.TreeSql))
                definition.TreeSql = pattern.Replace(definition.TreeSql, replacement);

            for (var i = 0; i < values.Count; i++)
                sqlParams[$"{name}_{i}"] = TypedValue(values[i], parameterType);
            // The request that runs this SQL decides, against its final parameters, whether the list still binds per member.
            lists.RemoveAll(list => list.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            lists.Add(new ReportSqlList(name, values.ToArray(), parameterType, expandsRowset));

            // Crystal formulas sometimes reference the same parameter both as
            // an IN-rewrite target AND as a sentinel predicate test (e.g.
            // a report's "IF {?Assembly Type} = -1
            // THEN ... ELSE ... AND tbl.AssemblyType = {?Assembly Type}"). The
            // sentinel reference becomes "@AssemblyType = -1" in SQL, and
            // without a scalar @Name binding SQL Server fails with
            // "Must declare the scalar variable '@Name'". Bind to the first
            // picked value — preserves the user's selection in the cond test.
            sqlParams[name] = TypedValue(values[0], parameterType);
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

        // A trusted list expansion changes the generated template, not the
        // integral clauses. Never revive a plan for caller-replaced SQL.
        definition.SqlLists = lists;
        definition.PositionedIntegerSelection = integerSelection?.WithTemplate(definition.Sql);
        definition.PositionedTextSelection = textSelection?.WithTemplate(definition.Sql);
        return sqlParams;
    }

    /// <summary>The prefix of the parameter that carries a list bound as one value.</summary>
    internal const string ListParameterPrefix = "__fxList_";

    /// <summary>
    /// A list as one parameter value and the rowset that reads its members back. Text is joined with a control character no
    /// member contains and split by the server, so the members come back in the type the parameter is bound with, as each member
    /// would bind on its own. Other members are a JSON array read in the type each would bind with (bigint, bit, decimal,
    /// datetime); members that share no type are read as text.
    /// </summary>
    internal static (string Value, string Source) ListSource(IReadOnlyList<string> values, ReportParameterType? type, string parameter)
    {
        var typed = values.Select(value => TypedValue(value, type)).ToArray();
        (string, string) Json(string json, string sqlType) => (json, $"OPENJSON(@{parameter}) WITH ([value] {sqlType} '$')");
        if (typed.All(value => value is long)) return Json(System.Text.Json.JsonSerializer.Serialize(typed.Cast<long>()), "bigint");
        if (typed.All(value => value is bool)) return Json(System.Text.Json.JsonSerializer.Serialize(typed.Cast<bool>()), "bit");
        if (typed.All(value => value is decimal))
        {
            var numbers = typed.Cast<decimal>().ToArray();
            var scale = numbers.Max(number => (int)number.Scale);
            var digits = numbers.Max(number => decimal.Truncate(decimal.Abs(number)).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            if (digits + scale <= 38)
                return Json("[" + string.Join(",", numbers.Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]", $"decimal(38, {scale})");
        }
        if (typed.All(value => value is DateTime))
        {
            // The client sends a DateTime as datetime, rounded to its 1/300-second step; the text carries that value.
            var dates = typed.Select(value => IsoDate((DateTime)value)).ToArray();
            if (dates.All(date => date is not null)) return Json(System.Text.Json.JsonSerializer.Serialize(dates), "datetime");
        }
        // Text of every member; a date member as ISO 8601 text, which converts the same under any SET DATEFORMAT.
        var text = Text(typed.Select((value, index) => value is DateTime date && IsoDate(date) is { } iso ? iso : values[index]).ToArray());
        return (text.Value, text.Source(parameter));
    }

    /// <summary>The prefix of each parameter of a list bound as one value per SQL type (<see cref="ListParts"/>): the part's number,
    /// an underscore and the list's name. No list's one-value parameter (<see cref="ListParameterPrefix"/>) takes this form.</summary>
    internal const string ListPartPrefix = "__fxPart";

    /// <summary><paramref name="name"/> as a parameter name SQL Server accepts (127 characters after the '@'): a longer name keeps
    /// its start and ends with a hash of the whole name.</summary>
    internal static string FitName(string name)
    {
        const int Limit = 127;
        if (name.Length <= Limit) return name;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)), 0, 8);
        return name[..(Limit - hash.Length - 1)] + "_" + hash;
    }

    internal sealed record ListPart(string Parameter, string Value, string Source);

    /// <summary>
    /// A list bound as one value per SQL type its members bind with one by one: whole numbers (bigint), decimals (one part per
    /// scale when one decimal type cannot hold every member), floating-point numbers (each sent as its exact binary mantissa
    /// and exponent), date-times (datetime, as ISO 8601 text), Booleans, identifiers, date-time offsets, times, binary values
    /// and text. Text is split into ASCII, Windows-1252 and other text, the classes by which a client binds a value as varchar
    /// or nvarchar, so each part binds as each of its members would. Null members are left out: they match nothing in an IN
    /// list. <paramref name="name"/> names part i of n. Null when a member has no exact one-value form (a date-time outside the
    /// datetime range, a value of another type).
    /// </summary>
    internal static IReadOnlyList<ListPart>? ListParts(IReadOnlyList<object?> members, Func<int, int, string> name)
    {
        var groups = new SortedDictionary<int, List<object>>();
        foreach (var member in members)
        {
            if (member is null or DBNull) continue;
            var (kind, item) = member switch
            {
                sbyte or byte or short or ushort or int or uint or long => (0, (object)System.Convert.ToInt64(member, System.Globalization.CultureInfo.InvariantCulture)),
                ulong whole when whole <= long.MaxValue => (0, (long)whole),
                decimal number => (1, number),
                double real when double.IsFinite(real) => (2, real),
                float real when float.IsFinite(real) => (2, (double)real),
                DateTime date => (3, date),
                bool flag => (4, flag),
                Guid id => (5, id),
                DateTimeOffset offset => (6, offset),
                TimeSpan time when time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) => (7, time),
                byte[] bytes => (8, bytes),
                string text => (9 + TextClass(text), text),
                char character => (9 + TextClass(character.ToString()), character.ToString()),
                char[] characters => (9 + TextClass(new string(characters)), new string(characters)),
                _ => (-1, member)
            };
            if (kind < 0) return null;
            if (!groups.TryGetValue(kind, out var group)) groups[kind] = group = [];
            group.Add(item);
        }
        // Each part's value and its rowset, given the parameter's name.
        var parts = new List<(string Value, Func<string, string> Source)>();
        string Json<T>(IEnumerable<T> items) => System.Text.Json.JsonSerializer.Serialize(items);
        void Rows(string json, string sqlType) => parts.Add((json, name => $"OPENJSON(@{name}) WITH ([value] {sqlType} '$')"));
        foreach (var (kind, items) in groups)
        {
            switch (kind)
            {
                case 0: Rows(Json(items.Cast<long>()), "bigint"); break;
                case 1:
                    // One decimal type for every member when it holds them all, else one per scale (a scale always holds its members).
                    var numbers = items.Cast<decimal>().ToArray();
                    var digits = numbers.Max(number => decimal.Truncate(decimal.Abs(number)).ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
                    var scales = digits + numbers.Max(number => (int)number.Scale) <= 38
                        ? [numbers] : numbers.GroupBy(number => number.Scale).OrderBy(scale => scale.Key).Select(scale => scale.ToArray()).ToArray();
                    foreach (var scale in scales)
                        Rows("[" + string.Join(",", scale.Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]",
                            $"decimal(38, {scale.Max(number => (int)number.Scale)})");
                    break;
                case 2:
                    // A double is its mantissa times a power of two, both exact on the server: text would lose subnormal values.
                    parts.Add((Json(items.Cast<double>().Select(Binary)), name =>
                        $"(SELECT CONVERT(float, [m]) * POWER(CONVERT(float, 2), [e]) AS [value] FROM OPENJSON(@{name}) WITH ([m] bigint '$[0]', [e] int '$[1]')) AS [{name}]"));
                    break;
                case 3:
                    // The client sends a DateTime as datetime, rounded to its 1/300-second step; the text carries that value.
                    var dates = new List<string>();
                    foreach (var date in items.Cast<DateTime>())
                    {
                        if (IsoDate(date) is not { } iso) return null;
                        dates.Add(iso);
                    }
                    Rows(Json(dates), "datetime");
                    break;
                case 4: Rows(Json(items.Cast<bool>()), "bit"); break;
                case 5: Rows(Json(items.Cast<Guid>().Select(id => id.ToString("D"))), "uniqueidentifier"); break;
                case 6: Rows(Json(items.Cast<DateTimeOffset>().Select(offset => offset.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", System.Globalization.CultureInfo.InvariantCulture))), "datetimeoffset(7)"); break;
                case 7: Rows(Json(items.Cast<TimeSpan>().Select(time => time.ToString("hh\\:mm\\:ss\\.fffffff", System.Globalization.CultureInfo.InvariantCulture))), "time(7)"); break;
                case 8:
                    parts.Add((Json(items.Cast<byte[]>().Select(bytes => "0x" + Convert.ToHexString(bytes))),
                        name => $"(SELECT CONVERT(varbinary(max), [text], 1) AS [value] FROM OPENJSON(@{name}) WITH ([text] varchar(max) '$')) AS [{name}]"));
                    break;
                default:
                    var text = Text(items.Cast<string>().ToArray());
                    parts.Add((text.Value, text.Source));
                    break;
            }
        }
        if (parts.Count == 0) Rows("[]", "nvarchar(1)");
        return parts.Select((part, index) => name(index, parts.Count) is var parameter ? new ListPart(parameter, part.Value, part.Source(parameter)) : null!).ToList();
    }

    // A finite double as [mantissa, exponent]: value = mantissa * 2^exponent, the mantissa a signed integer of at most 53 bits.
    private static long[] Binary(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xF_FFFF_FFFF_FFFFL;
        if (exponent == 0) exponent = 1; else mantissa |= 1L << 52;
        return [bits < 0 ? -mantissa : mantissa, exponent - 1075];
    }

    // A DateTime as the datetime the client would send, in ISO 8601 text (read the same under any SET DATEFORMAT); null outside
    // the datetime range.
    private static string? IsoDate(DateTime date) =>
        date < System.Data.SqlTypes.SqlDateTime.MinValue.Value || date > System.Data.SqlTypes.SqlDateTime.MaxValue.Value ? null
            : new System.Data.SqlTypes.SqlDateTime(date).Value.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    // Text joined with a control character no member contains and split by the server, in the type the parameter binds with.
    private static (string Value, Func<string, string> Source) Text(IReadOnlyList<string> values)
    {
        for (var separator = (char)31; separator >= (char)1; separator--)
            if (!values.Any(value => value.Contains(separator)))
            {
                var code = (int)separator;
                return (string.Join(separator, values), name => $"STRING_SPLIT(@{name}, CHAR({code}))");
            }
        return (System.Text.Json.JsonSerializer.Serialize(values), name => $"OPENJSON(@{name}) WITH ([value] nvarchar(max) '$')");
    }

    // 0: ASCII; 1: Windows-1252 beyond ASCII; 2: other text.
    private static int TextClass(string text)
    {
        if (System.Text.Ascii.IsValid(text)) return 0;
        foreach (var character in text)
            if (character is not (< '\u0080' or >= '\u00A0' and <= '\u00FF' or '\u20AC' or '\u201A' or '\u0192' or '\u201E' or '\u2026' or '\u2020' or '\u2021'
                or '\u02C6' or '\u2030' or '\u0160' or '\u2039' or '\u0152' or '\u017D' or '\u2018' or '\u2019' or '\u201C' or '\u201D' or '\u2022'
                or '\u2013' or '\u2014' or '\u02DC' or '\u2122' or '\u0161' or '\u203A' or '\u0153' or '\u017E' or '\u0178'))
                return 2;
        return 1;
    }

    /// <summary>
    /// The members of a list a request bound as one value, read back from its parameter: <paramref name="name"/> is the prompt
    /// whose list it is (its start and a hash when the parameter's name was too long for SQL Server, <see cref="FitName"/>). A
    /// list bound per SQL type has one such parameter per part. Null for any other parameter. Text members are exact when none
    /// holds a control character.
    /// </summary>
    public static IReadOnlyList<string>? ListMembers(string parameter, object? value, out string name)
    {
        name = "";
        if (value is not string text) return null;
        var bare = parameter.TrimStart('@');
        if (bare.StartsWith(ListParameterPrefix, StringComparison.OrdinalIgnoreCase)) name = bare[ListParameterPrefix.Length..];
        else if (System.Text.RegularExpressions.Regex.Match(bare, "^" + ListPartPrefix + "[0-9]+_(.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } part)
            name = part.Groups[1].Value;
        else return null;
        if (text.StartsWith('['))
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(text);
                return json.RootElement.EnumerateArray().Select(item => item.ValueKind == System.Text.Json.JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText()).ToArray();
            }
            catch (System.Text.Json.JsonException) { }
        }
        var separator = text.Where(character => character is >= (char)1 and <= (char)31).DefaultIfEmpty('\0').Max();
        return separator == '\0' ? [text] : text.Split(separator);
    }

    /// <summary>The members of a selection that the server query receives: a multi-value string prompt drops its empty-string members.</summary>
    private static IReadOnlyList<string> SqlValues(ReportDefinition definition, string name, IReadOnlyList<string>? values)
    {
        values ??= [];
        // Only a positioned query re-evaluates the complete list in its runtime formula; a tabular query is filtered by its SQL alone.
        if (values.Count < 2 || definition.PositionedLayout is null) return values;
        var type = definition.Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.ParameterType;
        if (type is not (null or ReportParameterType.String)) return values;
        var kept = values.Where(value => value.Length > 0).ToArray();
        return kept.Length == 0 || kept.Length == values.Count ? values : kept;
    }

    /// <summary>The SQL value for one prompt choice: numbers, dates and booleans by the prompt's declared
    /// type (invariant text, as the converter writes defaults), anything else as the text it arrived as.</summary>
    internal static object TypedValue(string value, ReportParameterType? type) => type switch
    {
        ReportParameterType.Integer when long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var integer) => integer,
        ReportParameterType.Integer when decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var whole) && whole == decimal.Truncate(whole) => (long)whole,
        ReportParameterType.Decimal when decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) => number,
        ReportParameterType.Boolean when bool.TryParse(value, out var flag) => flag,
        ReportParameterType.Date when DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) => date,
        _ => value
    };
}
