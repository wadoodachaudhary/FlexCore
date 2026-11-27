using System.Data;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

/// <summary>
/// ADO.NET (XML) tables for the report designer and the positioned runtime.
/// The host registers one instance as <see cref="IReportDataExecutor"/>. The designer loads a DataSet XML file;
/// preview joins those tables and lets the existing Crystal formula engine apply record selection.
/// </summary>
public sealed class ReportBoundDataSource : IReportDefinitionDataExecutor
{
    public const int MaxRows = 100_000;
    /// <summary>The catalog most recently loaded, used when the renderer's executor instance has not been loaded itself.</summary>
    public static ReportBoundDataSource? Ambient { get; private set; }

    private readonly Dictionary<string, DataTable> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReportDesignerDataTable> _schema = [];
    public string? SourcePath { get; private set; }
    public bool HasData => _tables.Count > 0;
    public IReadOnlyList<ReportDesignerDataTable> Tables => _schema;

    public void LoadFile(string path)
    {
        LoadXml(File.ReadAllText(path), Path.GetFullPath(path));
    }

    public void LoadXml(string xml, string? sourcePath = null)
    {
        if (xml.Length > 32 * 1024 * 1024) throw new InvalidDataException("ADO.NET XML data is limited to 32 MB.");
        var set = new DataSet { Locale = CultureInfo.InvariantCulture };
        using (var reader = new StringReader(xml))
            set.ReadXml(reader, XmlReadMode.ReadSchema);
        if (set.Tables.Count == 0)
        {
            using var reader = new StringReader(xml);
            set.ReadXml(reader, XmlReadMode.InferSchema);
        }
        if (set.Tables.Count is < 1 or > 256) throw new InvalidDataException("ADO.NET XML data must contain 1 to 256 tables.");
        _tables.Clear();
        _schema.Clear();
        SourcePath = string.IsNullOrWhiteSpace(sourcePath) ? null : Path.GetFullPath(sourcePath);
        foreach (DataTable table in set.Tables)
        {
            if (table.Columns.Count is < 1 or > 2048 || table.Rows.Count > MaxRows)
                throw new InvalidDataException($"Table '{table.TableName}' exceeds the column or row limit.");
            var copy = table.Copy();
            foreach (DataColumn column in copy.Columns.Cast<DataColumn>().ToList())
            {
                var name = XmlConvert.DecodeName(column.ColumnName);
                if (!string.Equals(name, column.ColumnName, StringComparison.Ordinal))
                {
                    if (copy.Columns.Contains(name)) throw new InvalidDataException($"Table '{table.TableName}' has two columns named '{name}'.");
                    column.ColumnName = name;
                }
            }
            copy.TableName = XmlConvert.DecodeName(copy.TableName);
            if (_tables.ContainsKey(copy.TableName)) throw new InvalidDataException($"Duplicate table '{copy.TableName}'.");
            KeepWallClockDates(xml, copy);
            _tables[copy.TableName] = copy;
            _schema.Add(new ReportDesignerDataTable
            {
                Name = copy.TableName,
                SourceName = copy.TableName,
                DataFile = SourcePath ?? "",
                Fields = copy.Columns.Cast<DataColumn>().Select(column => new ReportDesignerField
                {
                    Name = column.ColumnName,
                    Table = copy.TableName,
                    LongName = copy.TableName + "." + column.ColumnName,
                    Type = CrystalType(column.DataType)
                }).ToList()
            });
        }
        Ambient = this;
    }

    public DataTable Execute(string sql, IDictionary<string, object>? parameters) =>
        ReportBoundSql.Execute(sql, parameters, Active().Catalog());

    public DataTable Execute(ReportDefinition definition, IDictionary<string, object>? parameters)
    {
        var source = Active();
        source.EnsureFromReport(definition);
        var layout = definition.PositionedLayout;
        if (layout is not null && layout.Document.DataSources.Any(table => source._tables.ContainsKey(table.Name)))
            return source.Project(layout);
        if (!string.IsNullOrWhiteSpace(definition.Sql))
            return ReportBoundSql.Execute(definition.Sql, parameters, source._tables);
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add("__fxStaticReport", typeof(int));
        table.Rows.Add(1);
        return table;
    }

    public static IReadOnlyList<ReportDesignerField> FieldsFrom(DataTable table) =>
        table.Columns.Cast<DataColumn>().Select(column => new ReportDesignerField
        {
            Name = column.ColumnName,
            Type = CrystalType(column.DataType)
        }).ToList();

    private ReportBoundDataSource Active() => HasData ? this : Ambient is { HasData: true } ambient ? ambient : this;

    private IReadOnlyDictionary<string, DataTable> Catalog() => _tables;

    private void EnsureFromReport(ReportDefinition definition)
    {
        if (HasData) return;
        var reportPath = definition.SourceRptFile;
        if (string.IsNullOrWhiteSpace(reportPath) || !File.Exists(reportPath)) return;
        var report = XDocument.Load(reportPath).Root;
        var linked = report?.Descendants("ConnectionInfo")
            .Select(info => (string?)info.Attribute("File_Path_"))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path!));
        if (linked is null)
        {
            var sibling = report?.Descendants("ConnectionInfo")
                .Select(info => (string?)info.Attribute("File_Path_"))
                .Select(path => string.IsNullOrWhiteSpace(path) ? "" : Path.GetFileName(path))
                .FirstOrDefault(name => name.Length > 0);
            if (!string.IsNullOrWhiteSpace(sibling))
            {
                var beside = Path.Combine(Path.GetDirectoryName(reportPath) ?? "", sibling);
                if (File.Exists(beside)) linked = beside;
            }
        }
        if (linked is not null) LoadFile(linked);
    }

    private DataTable Project(ReportPositionedLayout layout)
    {
        var rows = Join(layout.Document);
        var bindings = layout.Bindings.Where(pair => ReportBoundSql.TryParseField(pair.Key) is not null).ToList();
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var pair in bindings)
            table.Columns.Add(pair.Value, typeof(object));
        if (table.Columns.Count == 0)
        {
            table.Columns.Add("__fxStaticReport", typeof(int));
            table.Rows.Add(1);
            return table;
        }
        foreach (var row in rows)
        {
            var values = bindings.Select(pair =>
            {
                var field = ReportBoundSql.TryParseField(pair.Key)!.Value;
                return row.TryGetValue(field.Table + "." + field.Field, out var value) ? value ?? DBNull.Value : DBNull.Value;
            }).ToArray();
            table.Rows.Add(values);
        }
        return table;
    }

    private List<Dictionary<string, object>> Join(ReportDesignerDocument document)
    {
        var tables = document.DataSources.Where(table => _tables.ContainsKey(table.Name)).ToList();
        if (tables.Count == 0) throw new InvalidDataException("The report's tables are not in the loaded ADO.NET XML data.");
        var remaining = tables.Skip(1).ToList();
        var joined = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { tables[0].Name };
        var rows = Load(tables[0].Name, tables[0].Name);
        var budget = new BoundJoinBudget();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(table => document.Links.Any(link => Connects(link, table.Name, joined))) ?? remaining[0];
            var links = document.Links.Where(link => Connects(link, next.Name, joined)).ToList();
            rows = links.Count == 0
                ? Cross(rows, next.Name, budget)
                : ApplyJoin(rows, next.Name, links, budget);
            joined.Add(next.Name);
            remaining.Remove(next);
        }
        if (rows.Count > MaxRows) throw new InvalidDataException($"The joined data has {rows.Count} rows, above the {MaxRows} row limit.");
        return rows;
    }

    private List<Dictionary<string, object>> ApplyJoin(List<Dictionary<string, object>> left, string rightName, List<ReportDesignerDataLink> links, BoundJoinBudget budget)
    {
        var right = Load(rightName, rightName);
        var (index, keyLink) = LinkIndex(links, right);
        var kinds = links.Select(link => Oriented(link, rightName)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var inner = kinds.Contains("inner");
        var full = !inner && kinds.Contains("full");
        var rightOuter = !inner && !full && kinds.Contains("right");
        var matchedRight = new bool[right.Count];
        var result = new List<Dictionary<string, object>>();
        foreach (var leftRow in left)
        {
            var hits = new List<int>();
            foreach (var candidate in index is null ? Enumerable.Range(0, right.Count) : index.Candidates(Cell(leftRow, keyLink!.LeftTable, keyLink.LeftField)))
            {
                budget.Spend();
                if (links.All(link => ValuesEqual(Cell(leftRow, link.LeftTable, link.LeftField), Cell(right[candidate], link.RightTable, link.RightField))))
                    hits.Add(candidate);
            }
            if (hits.Count == 0)
            {
                if (!inner && !rightOuter) Add(result, Extend(leftRow, rightName, null));
                continue;
            }
            foreach (var hit in hits)
            {
                matchedRight[hit] = true;
                Add(result, Extend(leftRow, rightName, right[hit]));
            }
        }
        if (rightOuter || full)
        {
            var blank = Empty(left);
            for (var row = 0; row < right.Count; row++)
                if (!matchedRight[row]) Add(result, Extend(blank, rightName, right[row]));
        }
        return result;
    }

    private List<Dictionary<string, object>> Cross(List<Dictionary<string, object>> left, string rightName, BoundJoinBudget budget)
    {
        var right = Load(rightName, rightName);
        var result = new List<Dictionary<string, object>>();
        foreach (var leftRow in left)
            foreach (var rightRow in right)
            {
                budget.Spend();
                Add(result, Extend(leftRow, rightName, rightRow));
            }
        return result;
    }

    // The row limit holds while the join runs, so an oversized join stops before it fills the server's memory.
    private static void Add(List<Dictionary<string, object>> rows, Dictionary<string, object> row)
    {
        if (rows.Count == MaxRows) throw new InvalidDataException($"The joined data has more than {MaxRows} rows, above the {MaxRows} row limit.");
        rows.Add(row);
    }

    // Right rows by one link's value, as ValuesEqual compares them: the link whose values tell the most rows apart, so a
    // link on a field every row shares (a division) does not decide the work. Null when no link's column types allow it.
    private (BoundJoinIndex? Index, ReportDesignerDataLink? Link) LinkIndex(List<ReportDesignerDataLink> links, List<Dictionary<string, object>> right)
    {
        (BoundJoinIndex? Index, ReportDesignerDataLink? Link) best = (null, null);
        foreach (var link in links)
        {
            var leftType = ColumnType(link.LeftTable, link.LeftField);
            var rightType = ColumnType(link.RightTable, link.RightField);
            var family = BoundJoinIndex.FamilyOf(leftType, rightType);
            if (family == BoundJoinIndex.Family.None) continue;
            var index = BoundJoinIndex.TryCreate(family, false, leftType, rightType, right.Select(row => Cell(row, link.RightTable, link.RightField)).ToList());
            if (index is not null && (best.Index is null || index.Keys > best.Index.Keys)) best = (index, link);
        }
        return best;
    }

    // A link to a table or field the data does not hold reads DBNull on every row, which never matches.
    private Type ColumnType(string table, string field) =>
        _tables.TryGetValue(table, out var data) && data.Columns.Contains(field) ? data.Columns[field]!.DataType : typeof(string);

    private List<Dictionary<string, object>> Load(string tableName, string alias)
    {
        if (!_tables.TryGetValue(tableName, out var table))
            throw new InvalidDataException($"Table '{tableName}' is not in the loaded ADO.NET XML data.");
        var rows = new List<Dictionary<string, object>>(table.Rows.Count);
        foreach (DataRow data in table.Rows)
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (DataColumn column in table.Columns)
                row[alias + "." + column.ColumnName] = data[column] is DBNull ? DBNull.Value : data[column];
            rows.Add(row);
        }
        return rows;
    }

    private static Dictionary<string, object> Extend(Dictionary<string, object> left, string rightAlias, Dictionary<string, object>? right)
    {
        var row = new Dictionary<string, object>(left, StringComparer.OrdinalIgnoreCase);
        if (right is null)
        {
            foreach (var key in left.Keys.Where(key => key.StartsWith(rightAlias + ".", StringComparison.OrdinalIgnoreCase)).ToList())
                row[key] = DBNull.Value;
            return row;
        }
        foreach (var pair in right)
            if (pair.Key.StartsWith(rightAlias + ".", StringComparison.OrdinalIgnoreCase))
                row[pair.Key] = pair.Value;
        return row;
    }

    private static Dictionary<string, object> Empty(IEnumerable<Dictionary<string, object>> rows)
    {
        var blank = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in rows.SelectMany(row => row.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            blank[key] = DBNull.Value;
        return blank;
    }

    private static object Cell(Dictionary<string, object> row, string table, string field) =>
        row.TryGetValue(table + "." + field, out var value) ? value : DBNull.Value;

    private static bool Connects(ReportDesignerDataLink link, string table, HashSet<string> joined) =>
        (link.LeftTable.Equals(table, StringComparison.OrdinalIgnoreCase) && joined.Contains(link.RightTable))
        || (link.RightTable.Equals(table, StringComparison.OrdinalIgnoreCase) && joined.Contains(link.LeftTable));

    private static string Oriented(ReportDesignerDataLink link, string newTable)
    {
        var kind = link.JoinType.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "leftouter" => "left",
            "rightouter" => "right",
            "fullouter" => "full",
            _ => "inner"
        };
        if (!link.LeftTable.Equals(newTable, StringComparison.OrdinalIgnoreCase)) return kind;
        return kind switch { "left" => "right", "right" => "left", _ => kind };
    }

    internal static bool ValuesEqual(object? left, object? right)
    {
        if (left is null or DBNull || right is null or DBNull) return false;
        if (IsNumber(left) && IsNumber(right))
            return Convert.ToDecimal(left, CultureInfo.InvariantCulture) == Convert.ToDecimal(right, CultureInfo.InvariantCulture);
        if (left is DateTime leftDate && right is DateTime rightDate) return leftDate == rightDate;
        if (left is bool leftFlag && right is bool rightFlag) return leftFlag == rightFlag;
        return string.Equals(Convert.ToString(left, CultureInfo.InvariantCulture)?.TrimEnd(),
            Convert.ToString(right, CultureInfo.InvariantCulture)?.TrimEnd(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumber(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    // ADO.NET converts 2003-01-01T00:00:00-08:00 into the machine zone. Crystal prints the clock time in the file.
    private static void KeepWallClockDates(string xml, DataTable table)
    {
        if (!table.Columns.Cast<DataColumn>().Any(column => column.DataType == typeof(DateTime))) return;
        var rows = XDocument.Parse(xml).Root?.Elements().Where(element => XmlConvert.DecodeName(element.Name.LocalName).Equals(table.TableName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (rows is null) return;
        for (var index = 0; index < rows.Count && index < table.Rows.Count; index++)
        {
            foreach (DataColumn column in table.Columns)
            {
                if (column.DataType != typeof(DateTime)) continue;
                var cell = rows[index].Elements().FirstOrDefault(element => XmlConvert.DecodeName(element.Name.LocalName).Equals(column.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (cell is null || string.IsNullOrWhiteSpace(cell.Value)) continue;
                table.Rows[index][column] = WallClock(cell.Value);
            }
        }
    }

    private static DateTime WallClock(string text)
    {
        var zone = text.Length > 11 ? text.LastIndexOfAny(['+', '-']) : -1;
        if (zone > 10) text = text[..zone];
        if (text.EndsWith('Z')) text = text[..^1];
        return DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None), DateTimeKind.Unspecified);
    }

    public static string CrystalType(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.Boolean => "Xsd:booleanField",
        TypeCode.DateTime => "Xsd:dateTimeField",
        TypeCode.Decimal or TypeCode.Double or TypeCode.Single => "Xsd:decimalField",
        TypeCode.Byte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64
            or TypeCode.SByte or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64 => "Xsd:longField",
        _ => "Xsd:stringField"
    };
}

/// <summary>Executes the bracketed SELECT/JOIN/WHERE/ORDER BY shape the designer and the Crystal loader emit, against in-memory tables.</summary>
internal static class ReportBoundSql
{
    internal static (string Table, string Field)? TryParseField(string reference)
    {
        var match = System.Text.RegularExpressions.Regex.Match(reference.Trim(), @"^\{([^.}]+)\.([^}]+)\}$");
        return match.Success ? (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim()) : null;
    }

    internal static DataTable Execute(string sql, IDictionary<string, object>? parameters, IReadOnlyDictionary<string, DataTable> tables)
    {
        var query = new Parser(sql).Parse();
        // Rows stream from the joins through WHERE, DISTINCT and TOP, so a TOP query stops joining once it has its rows.
        IEnumerable<Dictionary<string, object>> rows = query.From is null
            ? [new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)]
            : Run(query, tables, new BoundJoinBudget());
        if (query.Where is { } where)
            rows = rows.Where(row => Truth(where.Eval(row, parameters)));
        if (query.Order.Count > 0)
        {
            var held = Hold(rows);
            var indexes = Enumerable.Range(0, held.Count).ToList();
            indexes.Sort(new RowOrder(held, query.Order, parameters));
            rows = indexes.Select(index => held[index]);
        }
        if (query.Distinct)
            rows = rows.DistinctBy(row => string.Join('\u001f', query.Select.Select(item => Convert.ToString(item.Value.Eval(row, parameters), CultureInfo.InvariantCulture))));
        if (query.Top is { } top) rows = rows.Take(top);
        var result = new DataTable { Locale = CultureInfo.InvariantCulture };
        foreach (var item in query.Select)
            result.Columns.Add(item.Alias, typeof(object));
        foreach (var row in rows)
        {
            if (result.Rows.Count == ReportBoundDataSource.MaxRows) throw TooManyRows();
            result.Rows.Add(query.Select.Select(item => item.Value.Eval(row, parameters) ?? DBNull.Value).ToArray());
        }
        return result;
    }

    private static List<Dictionary<string, object>> Hold(IEnumerable<Dictionary<string, object>> rows)
    {
        var held = new List<Dictionary<string, object>>();
        foreach (var row in rows)
        {
            if (held.Count == ReportBoundDataSource.MaxRows) throw TooManyRows();
            held.Add(row);
        }
        return held;
    }

    private static InvalidDataException TooManyRows() =>
        new($"The query returns more than {ReportBoundDataSource.MaxRows} rows, above the {ReportBoundDataSource.MaxRows} row limit.");

    private static IEnumerable<Dictionary<string, object>> Run(Query query, IReadOnlyDictionary<string, DataTable> tables, BoundJoinBudget budget)
    {
        var from = query.From ?? throw new InvalidDataException("SQL has no FROM clause.");
        var first = Load(tables, from);
        IEnumerable<Dictionary<string, object>> rows = first.Rows;
        var leftTypes = new Dictionary<string, Type>(first.Types, StringComparer.OrdinalIgnoreCase);
        foreach (var join in query.Joins)
        {
            var right = Load(tables, join.Table);
            if (join.Kind == "inner" && join.On is null) throw new InvalidDataException("JOIN requires ON.");
            rows = join.Kind == "cross"
                ? Cross(rows, right, budget)
                : Match(rows, new Dictionary<string, Type>(leftTypes, StringComparer.OrdinalIgnoreCase), right, join, budget);
            foreach (var pair in right.Types) leftTypes[pair.Key] = pair.Value;
        }
        return rows;
    }

    private static IEnumerable<Dictionary<string, object>> Cross(IEnumerable<Dictionary<string, object>> left, BoundTable right, BoundJoinBudget budget)
    {
        foreach (var leftRow in left)
            foreach (var rightRow in right.Rows)
            {
                budget.Spend();
                yield return Merge(leftRow, rightRow);
            }
    }

    private static IEnumerable<Dictionary<string, object>> Match(IEnumerable<Dictionary<string, object>> left, Dictionary<string, Type> leftTypes, BoundTable right, Join join, BoundJoinBudget budget)
    {
        var keepRight = join.Kind is "right" or "full";
        var keepLeft = join.Kind is "left" or "full";
        var matched = new bool[right.Rows.Count];
        var on = join.On;
        var index = on is null ? null : EqualityIndex(on, leftTypes, right);
        foreach (var leftRow in left)
        {
            var found = false;
            if (on is not null && right.Rows.Count > 0)
            {
                // One scratch row per left row: each candidate's columns are written over it, and ON is read from it.
                var probe = new Dictionary<string, object>(leftRow, StringComparer.OrdinalIgnoreCase);
                var candidates = index is null ? Enumerable.Range(0, right.Rows.Count) : index.Value.Index.Candidates(leftRow[index.Value.LeftColumn]);
                foreach (var candidate in candidates)
                {
                    budget.Spend();
                    foreach (var pair in right.Rows[candidate]) probe[pair.Key] = pair.Value;
                    if (!Truth(on.Eval(probe, null))) continue;
                    found = true;
                    matched[candidate] = true;
                    yield return Merge(leftRow, right.Rows[candidate]);
                }
            }
            if (!found && keepLeft) yield return Merge(leftRow, right.Nulls);
        }
        if (!keepRight) yield break;
        var leftNulls = Nulls(leftTypes.Keys);
        for (var row = 0; row < right.Rows.Count; row++)
            if (!matched[row]) yield return Merge(leftNulls, right.Rows[row]);
    }

    // Uses one "left = right" column equality of ON (a term of its top-level AND) to pick the candidate rows; every
    // candidate is still checked against the whole ON. Not used when ON reads a parameter or a column the rows lack,
    // so the errors those raise are the same.
    private static (BoundJoinIndex Index, string LeftColumn)? EqualityIndex(Expr on, Dictionary<string, Type> leftTypes, BoundTable right)
    {
        if (!Indexable(on, leftTypes, right.Types)) return null;
        (BoundJoinIndex Index, string LeftColumn)? best = null;
        foreach (var term in Terms(on))
        {
            if (term is not CompareExpr { Operator: "=", LeftOperand: ColumnExpr one, RightOperand: ColumnExpr other }) continue;
            foreach (var (leftColumn, rightColumn) in new[] { (one.ColumnName, other.ColumnName), (other.ColumnName, one.ColumnName) })
            {
                if (right.Types.ContainsKey(leftColumn) || !right.Types.TryGetValue(rightColumn, out var rightType)) continue;
                var leftType = leftTypes[leftColumn];
                // CompareValues also counts two strings equal when the current culture compares them equal.
                var culture = leftType == typeof(string) && rightType == typeof(string);
                var family = BoundJoinIndex.FamilyOf(leftType, rightType);
                if (family == BoundJoinIndex.Family.None) continue;
                // Two values of one other type also compare equal through their own CompareTo; only types whose CompareTo
                // equality is text equality can be keyed as text (a DateTimeOffset is equal at another offset).
                if (family == BoundJoinIndex.Family.Text && leftType == rightType && !culture && !BoundJoinIndex.TextKeyed(leftType)) continue;
                var index = BoundJoinIndex.TryCreate(family, culture, leftType, rightType, right.Rows.Select(row => row[rightColumn]).ToList());
                // The term whose values tell the most rows apart, so a term on a field every row shares does not decide the work.
                if (index is not null && (best is null || index.Keys > best.Value.Index.Keys)) best = (index, leftColumn);
            }
        }
        return best;
    }

    private static IEnumerable<Expr> Terms(Expr expression)
    {
        if (expression is BoolExpr { Operator: "AND" } both)
        {
            foreach (var term in Terms(both.LeftOperand)) yield return term;
            foreach (var term in Terms(both.RightOperand)) yield return term;
        }
        else yield return expression;
    }

    private static bool Indexable(Expr expression, Dictionary<string, Type> leftTypes, Dictionary<string, Type> rightTypes) => expression switch
    {
        ColumnExpr column => leftTypes.ContainsKey(column.ColumnName) || rightTypes.ContainsKey(column.ColumnName),
        LiteralExpr => true,
        CompareExpr compare => Indexable(compare.LeftOperand, leftTypes, rightTypes) && Indexable(compare.RightOperand, leftTypes, rightTypes),
        BoolExpr both => Indexable(both.LeftOperand, leftTypes, rightTypes) && Indexable(both.RightOperand, leftTypes, rightTypes),
        NotExpr not => Indexable(not.Operand, leftTypes, rightTypes),
        _ => false
    };

    private sealed record BoundTable(List<Dictionary<string, object>> Rows, Dictionary<string, Type> Types, Dictionary<string, object> Nulls);

    private static BoundTable Load(IReadOnlyDictionary<string, DataTable> tables, TableRef table)
    {
        var found = tables.FirstOrDefault(item => item.Key.Equals(table.Name, StringComparison.OrdinalIgnoreCase));
        if (found.Value is null) throw new InvalidDataException($"Table '{table.Name}' is not in the loaded ADO.NET XML data.");
        var types = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (DataColumn column in found.Value.Columns)
            types[table.Alias + "." + column.ColumnName] = column.DataType;
        var rows = new List<Dictionary<string, object>>();
        foreach (DataRow data in found.Value.Rows)
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (DataColumn column in found.Value.Columns)
                row[table.Alias + "." + column.ColumnName] = data[column] is DBNull ? DBNull.Value : data[column];
            rows.Add(row);
        }
        return new BoundTable(rows, types, Nulls(types.Keys));
    }

    private static Dictionary<string, object> Merge(Dictionary<string, object> left, Dictionary<string, object> right)
    {
        var row = new Dictionary<string, object>(left, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in right) row[pair.Key] = pair.Value;
        return row;
    }

    // An outer join's missing side: every column of that side, DBNull (from the schema, so an empty table has its columns too).
    private static Dictionary<string, object> Nulls(IEnumerable<string> keys)
    {
        var blank = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys) blank[key] = DBNull.Value;
        return blank;
    }

    private static bool Truth(object? value) => value is bool flag && flag;

    private sealed class RowOrder(List<Dictionary<string, object>> rows, List<(Expr Expr, bool Descending)> order, IDictionary<string, object>? parameters) : IComparer<int>
    {
        public int Compare(int x, int y)
        {
            foreach (var key in order)
            {
                var compared = CompareValues(key.Expr.Eval(rows[x], parameters), key.Expr.Eval(rows[y], parameters));
                if (compared != 0) return key.Descending ? -compared : compared;
            }
            return x.CompareTo(y);
        }
    }

    private static int CompareValues(object? left, object? right)
    {
        var leftNull = left is null or DBNull;
        var rightNull = right is null or DBNull;
        if (leftNull || rightNull) return leftNull == rightNull ? 0 : leftNull ? -1 : 1;
        if (ReportBoundDataSource.ValuesEqual(left, right)) return 0;
        if (left is IComparable comparable && left.GetType() == right!.GetType()) return comparable.CompareTo(right);
        if (IsNumber(left!) && IsNumber(right!)) return Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture));
        return string.Compare(Convert.ToString(left, CultureInfo.InvariantCulture), Convert.ToString(right, CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNumber(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private sealed record Query(List<SelectItem> Select, TableRef? From, List<Join> Joins, Expr? Where, List<(Expr Expr, bool Descending)> Order, int? Top, bool Distinct);
    private sealed record SelectItem(Expr Value, string Alias);
    private sealed record TableRef(string Name, string Alias);
    private sealed record Join(string Kind, TableRef Table, Expr? On);

    private abstract class Expr
    {
        public abstract object? Eval(Dictionary<string, object> row, IDictionary<string, object>? parameters);
    }

    private sealed class ColumnExpr : Expr
    {
        public ColumnExpr(string column) => ColumnName = column;
        public string ColumnName { get; }
        public override object? Eval(Dictionary<string, object> row, IDictionary<string, object>? parameters) =>
            row.TryGetValue(ColumnName, out var value) ? value is DBNull ? null : value : throw new InvalidDataException($"SQL column '{ColumnName}' was not found.");
    }

    private sealed class LiteralExpr(object? Value) : Expr
    {
        public override object? Eval(Dictionary<string, object> row, IDictionary<string, object>? parameters) => Value;
    }

    private sealed class ParameterExpr(string Name) : Expr
    {
        public override object? Eval(Dictionary<string, object> row, IDictionary<string, object>? parameters)
        {
            if (parameters is null) throw new InvalidDataException($"SQL parameter @{Name} has no value.");
            var pair = parameters.FirstOrDefault(item => item.Key.TrimStart('@').Equals(Name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(pair.Key) && !parameters.ContainsKey(Name)) throw new InvalidDataException($"SQL parameter @{Name} has no value.");
            return pair.Value is DBNull ? null : pair.Value;
        }
    }

    private sealed class CompareExpr(Expr Left, string Op, Expr Right) : Expr
    {
        public Expr LeftOperand => Left;
        public string Operator => Op;
        public Expr RightOperand => Right;

        public override object? Eval(Dictionary<string, object> row, IDictionary<string, object>? parameters)
        {
            var left = Left.Eval(row, parameters);
            var right = Right.Eval(row, parameters);
            if (Op is "IS" or "IS NOT")
            {
                var isNull = left is null or DBNull;
                return Op == "IS" ? isNull : !isNull;
            }
            if (left is null or DBNull || right is null or DBNull) return false;
            var order = CompareValues(left, right);
            return Op switch
            {
                "=" => order == 0,
                "<>" or "!=" => order != 0,
                "<" => order < 0,
                ">" => order > 0,
                "<=" => order <= 0,
                ">=" => order >= 0,
                _ => throw new InvalidDataException("Unsupported SQL operator " + Op)
            };
        }
    }

    private sealed class BoolExpr(Expr Left, string Op, Expr Right) : Expr
    {
        public Expr LeftOperand => Left;
        public string Operator => Op;
        public Expr RightOperand => Right;

        public override object? Eval(Dictionary<string, object> row, IDictionary<string, object>? parameters)
        {
            var left = Truth(Left.Eval(row, parameters));
            if (Op == "OR" && left) return true;
            if (Op == "AND" && !left) return false;
            return Truth(Right.Eval(row, parameters));
        }
    }

    private sealed class NotExpr(Expr Inner) : Expr
    {
        public Expr Operand => Inner;

        public override object? Eval(Dictionary<string, object> row, IDictionary<string, object>? parameters) => !Truth(Inner.Eval(row, parameters));
    }

    private sealed class Parser
    {
        private readonly List<(string Kind, string Text)> _tokens = [];
        private int _index;
        public Parser(string sql) => Lex(sql);

        public Query Parse()
        {
            Expect("SELECT");
            var distinct = AcceptWord("DISTINCT");
            int? top = AcceptWord("TOP") ? int.Parse(ExpectKind("num").Text, CultureInfo.InvariantCulture) : null;
            var select = new List<SelectItem>();
            do select.Add(ParseSelectItem());
            while (AcceptOp(","));
            TableRef? from = null;
            var joins = new List<Join>();
            if (AcceptWord("FROM"))
            {
                from = ParseTable();
                while (true)
                {
                    var kind = PeekJoin();
                    if (kind is null) break;
                    ConsumeJoinWords();
                    var table = ParseTable();
                    Expr? on = null;
                    if (kind != "cross")
                    {
                        ExpectWord("ON");
                        on = ParseOr();
                    }
                    joins.Add(new Join(kind, table, on));
                }
            }
            Expr? where = AcceptWord("WHERE") ? ParseOr() : null;
            var order = new List<(Expr, bool)>();
            if (AcceptWord("ORDER"))
            {
                ExpectWord("BY");
                do
                {
                    var expr = ParseValue();
                    var descending = AcceptWord("DESC") || AcceptWord("DESCENDING");
                    if (!descending) AcceptWord("ASC");
                    order.Add((expr, descending));
                }
                while (AcceptOp(","));
            }
            if (_index < _tokens.Count) throw new InvalidDataException("SQL could not be read near '" + _tokens[_index].Text + "'.");
            return new Query(select, from, joins, where, order, top, distinct);
        }

        private SelectItem ParseSelectItem()
        {
            if (AcceptOp("*")) return new SelectItem(new LiteralExpr(1), "All");
            var value = ParseValue();
            var alias = AcceptWord("AS") ? ExpectKind("id").Text : value is ColumnExpr column ? column.ColumnName.Replace('.', '_') : "Value";
            return new SelectItem(value, alias);
        }

        private TableRef ParseTable()
        {
            var name = ExpectKind("id").Text;
            while (AcceptOp(".")) name = ExpectKind("id").Text;
            var alias = AcceptWord("AS") ? ExpectKind("id").Text : name;
            return new TableRef(name, alias);
        }

        private string? PeekJoin()
        {
            var word = _index < _tokens.Count && _tokens[_index].Kind == "id" ? _tokens[_index].Text : "";
            return word.ToUpperInvariant() switch
            {
                "LEFT" => "left", "RIGHT" => "right", "FULL" => "full", "INNER" => "inner", "CROSS" => "cross", "JOIN" => "inner",
                _ => null
            };
        }

        private void ConsumeJoinWords()
        {
            AcceptWord("LEFT"); AcceptWord("RIGHT"); AcceptWord("FULL"); AcceptWord("INNER"); AcceptWord("CROSS");
            AcceptWord("OUTER");
            ExpectWord("JOIN");
        }

        private Expr ParseOr()
        {
            var left = ParseAnd();
            while (AcceptWord("OR")) left = new BoolExpr(left, "OR", ParseAnd());
            return left;
        }

        private Expr ParseAnd()
        {
            var left = ParseNot();
            while (AcceptWord("AND")) left = new BoolExpr(left, "AND", ParseNot());
            return left;
        }

        private Expr ParseNot() => AcceptWord("NOT") ? new NotExpr(ParseNot()) : ParseComparison();

        private Expr ParseComparison()
        {
            if (AcceptOp("("))
            {
                var inner = ParseOr();
                ExpectOp(")");
                return inner;
            }
            var left = ParseValue();
            if (AcceptWord("IS"))
            {
                var not = AcceptWord("NOT");
                ExpectWord("NULL");
                return new CompareExpr(left, not ? "IS NOT" : "IS", new LiteralExpr(null));
            }
            var op = _index < _tokens.Count ? _tokens[_index].Text : "";
            if (op is "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=")
            {
                _index++;
                return new CompareExpr(left, op, ParseValue());
            }
            return left;
        }

        private Expr ParseValue()
        {
            if (AcceptOp("("))
            {
                var inner = ParseOr();
                ExpectOp(")");
                return inner;
            }
            var token = Next();
            if (token.Kind == "num") return new LiteralExpr(decimal.Parse(token.Text, CultureInfo.InvariantCulture));
            if (token.Kind == "str") return new LiteralExpr(token.Text);
            if (token.Kind == "param") return new ParameterExpr(token.Text);
            if (token.Kind != "id") throw new InvalidDataException("SQL expression expected near '" + token.Text + "'.");
            var name = token.Text;
            if (AcceptOp(".")) name = name + "." + ReadColumn();
            return new ColumnExpr(name);
        }

        private string ReadColumn()
        {
            if (_index < _tokens.Count && _tokens[_index].Kind == "id") return Next().Text;
            throw new InvalidDataException("SQL column name expected.");
        }

        private void Lex(string sql)
        {
            var index = 0;
            while (index < sql.Length)
            {
                var character = sql[index];
                if (char.IsWhiteSpace(character)) { index++; continue; }
                if (character == '[')
                {
                    var end = index + 1;
                    var text = "";
                    while (end < sql.Length)
                    {
                        if (sql[end] == ']' && end + 1 < sql.Length && sql[end + 1] == ']') { text += ']'; end += 2; continue; }
                        if (sql[end] == ']') break;
                        text += sql[end++];
                    }
                    if (end >= sql.Length || sql[end] != ']') throw new InvalidDataException("Unclosed SQL identifier.");
                    _tokens.Add(("id", text));
                    index = end + 1;
                    continue;
                }
                if (character is '\'' || character == 'N' && index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    if (character == 'N') index++;
                    var end = ++index;
                    var text = "";
                    while (end < sql.Length)
                    {
                        if (sql[end] == '\'' && end + 1 < sql.Length && sql[end + 1] == '\'') { text += '\''; end += 2; continue; }
                        if (sql[end] == '\'') break;
                        text += sql[end++];
                    }
                    _tokens.Add(("str", text));
                    index = end + 1;
                    continue;
                }
                if (character == '@')
                {
                    var end = index + 1;
                    while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                    _tokens.Add(("param", sql[(index + 1)..end]));
                    index = end;
                    continue;
                }
                if (char.IsDigit(character) || character == '.' && index + 1 < sql.Length && char.IsDigit(sql[index + 1]))
                {
                    var end = index;
                    while (end < sql.Length && (char.IsDigit(sql[end]) || sql[end] == '.')) end++;
                    _tokens.Add(("num", sql[index..end]));
                    index = end;
                    continue;
                }
                if (char.IsLetter(character) || character == '_')
                {
                    var end = index + 1;
                    while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_')) end++;
                    // An unbracketed column may contain spaces ("Customer ID") until the next operator.
                    if (index > 0 && sql[index - 1] == '.')
                    {
                        while (end < sql.Length && sql[end] == ' ')
                        {
                            var look = end;
                            while (look < sql.Length && sql[look] == ' ') look++;
                            if (look >= sql.Length || !(char.IsLetter(sql[look]) || sql[look] == '_')) break;
                            var wordEnd = look + 1;
                            while (wordEnd < sql.Length && (char.IsLetterOrDigit(sql[wordEnd]) || sql[wordEnd] == '_')) wordEnd++;
                            var word = sql[look..wordEnd];
                            if (IsClause(word)) break;
                            end = wordEnd;
                        }
                    }
                    _tokens.Add(("id", sql[index..end].Trim()));
                    index = end;
                    continue;
                }
                var two = index + 1 < sql.Length ? sql.Substring(index, 2) : "";
                if (two is ">=" or "<=" or "<>" or "!=") { _tokens.Add(("op", two)); index += 2; continue; }
                _tokens.Add(("op", character.ToString()));
                index++;
            }
        }

        private static bool IsClause(string word) => word.ToUpperInvariant() is "AS" or "FROM" or "WHERE" or "AND" or "OR" or "ON" or "JOIN"
            or "LEFT" or "RIGHT" or "INNER" or "FULL" or "CROSS" or "OUTER" or "ORDER" or "GROUP" or "SELECT" or "IS" or "NOT" or "NULL";

        private (string Kind, string Text) Next() => _index < _tokens.Count ? _tokens[_index++] : throw new InvalidDataException("Unexpected end of SQL.");
        private (string Kind, string Text) ExpectKind(string kind)
        {
            var token = Next();
            if (token.Kind != kind) throw new InvalidDataException($"SQL expected {kind} near '{token.Text}'.");
            return token;
        }
        private void Expect(string word) => ExpectWord(word);
        private void ExpectWord(string word) { if (!AcceptWord(word)) throw new InvalidDataException("SQL expected " + word + "."); }
        private void ExpectOp(string op) { if (!AcceptOp(op)) throw new InvalidDataException("SQL expected '" + op + "'."); }
        private bool AcceptWord(string word)
        {
            if (_index < _tokens.Count && _tokens[_index].Kind == "id" && _tokens[_index].Text.Equals(word, StringComparison.OrdinalIgnoreCase)) { _index++; return true; }
            return false;
        }
        private bool AcceptOp(string op)
        {
            if (_index < _tokens.Count && _tokens[_index].Kind == "op" && _tokens[_index].Text == op) { _index++; return true; }
            return false;
        }
    }
}

/// <summary>Bounds the work of one in-memory join, so a join too large for the server is refused before it runs out of memory.</summary>
internal sealed class BoundJoinBudget
{
    /// <summary>Most row pairs one query may compare or join.</summary>
    internal const long MaxWork = 5_000_000;
    private long _spent;

    internal void Spend()
    {
        if (++_spent > MaxWork)
            throw new InvalidDataException("The tables are too large to join: more than " + MaxWork.ToString("N0", CultureInfo.InvariantCulture)
                + " row comparisons. Link them on a key field, or load fewer rows.");
    }
}

/// <summary>
/// Right-hand rows by the value of one join column, so a left row is compared only with rows that can equal it.
/// The keys follow <see cref="ReportBoundDataSource.ValuesEqual"/>: two numbers compare as decimals, two dates and two
/// flags as themselves, every other pair as invariant text without trailing spaces, case ignored.
/// </summary>
internal sealed class BoundJoinIndex
{
    internal enum Family { None, Number, Date, Flag, Text }

    private readonly Family _family;
    private readonly Type _leftType;
    private readonly Type _rightType;
    private readonly Dictionary<object, List<int>> _byValue;
    private readonly Dictionary<string, List<int>>? _byCulture;
    private readonly int _count;

    /// <param name="culture">Also index strings by their current-culture sort key (two strings that compare equal there).</param>
    /// <param name="leftType">The left column's type: a left value of another type is compared with every row.</param>
    /// <param name="rightType">The right column's type: a right value of another type stops the index.</param>
    /// <exception cref="InvalidCastException">A right value whose type is not the column's.</exception>
    /// <exception cref="OverflowException">A number that ValuesEqual could not compare either.</exception>
    internal BoundJoinIndex(Family family, bool culture, Type leftType, Type rightType, IReadOnlyList<object?> values)
    {
        _family = family;
        _leftType = leftType;
        _rightType = rightType;
        _count = values.Count;
        _byValue = new Dictionary<object, List<int>>(family == Family.Text ? TextKeys.Instance : EqualityComparer<object>.Default);
        _byCulture = culture ? new Dictionary<string, List<int>>(StringComparer.Ordinal) : null;
        for (var row = 0; row < values.Count; row++)
        {
            if (Key(values[row], _rightType) is not { } key) continue;
            Add(_byValue, key, row);
            if (_byCulture is not null && values[row] is string text) Add(_byCulture, CultureKey(text), row);
        }
    }

    /// <summary>The index, or null when a right value cannot be keyed as its column's type compares it (the join then scans).</summary>
    internal static BoundJoinIndex? TryCreate(Family family, bool culture, Type leftType, Type rightType, IReadOnlyList<object?> values)
    {
        try
        {
            return new BoundJoinIndex(family, culture, leftType, rightType, values);
        }
        catch (Exception error) when (error is OverflowException or FormatException or InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>How many distinct keys the right rows have: the more, the fewer rows a left value is compared with.</summary>
    internal int Keys => _byValue.Count;

    /// <summary>A type whose CompareTo finds two values equal only when their invariant text is equal.</summary>
    internal static bool TextKeyed(Type type) => type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(char);

    /// <summary>How two columns of these types compare; None when a column can hold values of any type.</summary>
    internal static Family FamilyOf(Type left, Type right)
    {
        if (left == typeof(object) || right == typeof(object)) return Family.None;
        if (IsNumber(left) && IsNumber(right)) return Family.Number;
        if (left == typeof(DateTime) && right == typeof(DateTime)) return Family.Date;
        if (left == typeof(bool) && right == typeof(bool)) return Family.Flag;
        return Family.Text;
    }

    /// <summary>The rows that may equal <paramref name="value"/>, in row order; every row when the value cannot be keyed.</summary>
    internal IEnumerable<int> Candidates(object? value)
    {
        object? key;
        try
        {
            key = Key(value, _leftType);
        }
        catch (Exception error) when (error is OverflowException or FormatException or InvalidCastException)
        {
            return Enumerable.Range(0, _count);
        }
        if (key is null) return [];
        var byValue = _byValue.TryGetValue(key, out var rows) ? rows : null;
        var byCulture = _byCulture is not null && value is string text && _byCulture.TryGetValue(CultureKey(text), out var more) ? more : null;
        if (byCulture is null) return byValue ?? [];
        if (byValue is null) return byCulture;
        return Union(byValue, byCulture);
    }

    // A value of another type than its column's (two columns whose names differ only in case share one cell) is compared
    // by ValuesEqual as that type, so it has no key here.
    private object? Key(object? value, Type columnType)
    {
        if (value is null or DBNull) return null;
        if (value.GetType() != columnType) throw new InvalidCastException("The value's type is not its column's.");
        return _family switch
        {
            Family.Number => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            Family.Text => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").TrimEnd(),
            _ => value
        };
    }

    private static string CultureKey(string text) =>
        Convert.ToBase64String(CultureInfo.CurrentCulture.CompareInfo.GetSortKey(text, CompareOptions.None).KeyData);

    private static void Add<TKey>(Dictionary<TKey, List<int>> index, TKey key, int row) where TKey : notnull
    {
        if (!index.TryGetValue(key, out var rows)) index[key] = rows = [];
        rows.Add(row);
    }

    private static IEnumerable<int> Union(List<int> first, List<int> second)
    {
        int a = 0, b = 0;
        while (a < first.Count || b < second.Count)
        {
            if (b == second.Count || (a < first.Count && first[a] < second[b])) yield return first[a++];
            else if (a == first.Count || second[b] < first[a]) yield return second[b++];
            else
            {
                yield return first[a++];
                b++;
            }
        }
    }

    private static bool IsNumber(Type type) =>
        type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint)
        || type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double) || type == typeof(decimal);

    private sealed class TextKeys : IEqualityComparer<object>
    {
        internal static readonly TextKeys Instance = new();
        public new bool Equals(object? x, object? y) => string.Equals(x as string, y as string, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(object obj) => StringComparer.OrdinalIgnoreCase.GetHashCode((string)obj);
    }
}
