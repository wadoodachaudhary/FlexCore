using System.Data;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Reports;

/// <summary>
/// Runs a report's query against tables held in memory, so a report can show records that are not in a database
/// (a saved analysis, a file, a service's result). A report names a data set as its table; this executor answers
/// the single-table query the report loader writes for it: the listed columns under their aliases, in the query's
/// ORDER BY order, rows with equal keys in the data set's own order.
/// <para>Pass it to one viewer through <see cref="ReportWriterControl.DataExecutor"/>. Only a single-table SELECT
/// of plain columns is answered; joins, WHERE clauses and expressions are refused with a message.</para>
/// </summary>
public sealed partial class ReportDataSetExecutor : IReportDataExecutor
{
    private readonly Dictionary<string, DataTable> _tables = new(StringComparer.OrdinalIgnoreCase);

    public ReportDataSetExecutor() { }

    public ReportDataSetExecutor(IEnumerable<DataTable> tables)
    {
        foreach (var table in tables) Set(table);
    }

    /// <summary>Adds or replaces the data set named by <see cref="DataTable.TableName"/>.</summary>
    public void Set(DataTable table)
    {
        if (string.IsNullOrWhiteSpace(table.TableName)) throw new ArgumentException("A data set needs a table name.", nameof(table));
        _tables[table.TableName] = table;
    }

    public IReadOnlyCollection<string> Names => _tables.Keys;

    public DataTable Execute(string sql, IDictionary<string, object>? parameters)
    {
        var match = Query().Match(sql ?? "");
        if (!match.Success)
            throw new NotSupportedException("A data set report reads one table with plain columns; this query is not of that form: " + sql);
        var name = Unquote(match.Groups["table"].Value.Split('.')[^1]);
        if (!_tables.TryGetValue(name, out var source))
            throw new InvalidOperationException($"The data set '{name}' is not available.");

        var result = new DataTable(source.TableName);
        var map = new List<(DataColumn From, DataColumn To)>();
        foreach (var item in SplitList(match.Groups["columns"].Value))
        {
            var column = Column().Match(item);
            if (!column.Success) throw new NotSupportedException("A data set report selects plain columns; not supported: " + item.Trim());
            var from = SourceColumn(source, column.Groups["name"].Value);
            var alias = column.Groups["alias"].Success ? Unquote(column.Groups["alias"].Value) : from.ColumnName;
            map.Add((from, result.Columns.Add(alias, from.DataType)));
        }

        IEnumerable<DataRow> rows = source.Rows.Cast<DataRow>();
        if (match.Groups["order"].Success)
        {
            IOrderedEnumerable<DataRow>? ordered = null;
            foreach (var item in SplitList(match.Groups["order"].Value))
            {
                var key = Order().Match(item);
                if (!key.Success) throw new NotSupportedException("A data set report sorts by plain columns; not supported: " + item.Trim());
                var column = SourceColumn(source, key.Groups["name"].Value);
                var descending = key.Groups["direction"].Value.Equals("DESC", StringComparison.OrdinalIgnoreCase);
                Func<DataRow, object?> value = row => row.IsNull(column) ? null : row[column];
                var comparer = Comparer<object?>.Create(Compare);
                ordered = ordered is null
                    ? (descending ? rows.OrderByDescending(value, comparer) : rows.OrderBy(value, comparer))
                    : (descending ? ordered.ThenByDescending(value, comparer) : ordered.ThenBy(value, comparer));
            }
            if (ordered is not null) rows = ordered;
        }

        foreach (var row in rows)
        {
            var copy = result.NewRow();
            foreach (var (from, to) in map) copy[to] = row[from];
            result.Rows.Add(copy);
        }
        return result;
    }

    private static int Compare(object? left, object? right)
    {
        if (left is null) return right is null ? 0 : -1;
        if (right is null) return 1;
        if (left is string a && right is string b) return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase);
        return left is IComparable comparable && left.GetType() == right.GetType()
            ? comparable.CompareTo(right)
            : string.Compare(left.ToString(), right.ToString(), StringComparison.CurrentCultureIgnoreCase);
    }

    private static DataColumn SourceColumn(DataTable source, string reference)
    {
        var name = Unquote(reference.Split('.')[^1]);
        return source.Columns[name] ?? throw new InvalidOperationException($"The data set '{source.TableName}' has no column '{name}'.");
    }

    private static string Unquote(string name) => name.Trim().Trim('[', ']', '"');

    private static IEnumerable<string> SplitList(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex(@"^\s*SELECT\s+(?<columns>.+?)\s+FROM\s+(?<table>[\[\]\w.""]+)\s*(?:ORDER\s+BY\s+(?<order>.+?))?\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Query();

    [GeneratedRegex(@"^(?<name>[\[\]\w.""]+)(?:\s+AS\s+(?<alias>[\[\]\w""]+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Column();

    [GeneratedRegex(@"^(?<name>[\[\]\w.""]+)(?:\s+(?<direction>ASC|DESC))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Order();
}
