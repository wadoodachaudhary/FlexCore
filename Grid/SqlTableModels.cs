namespace Fx.ControlKit.Grid;

/// <summary>How <see cref="SQLTableControl"/> shows a query result.</summary>
public enum SqlTableView
{
    /// <summary>One line per result row, with search and sort.</summary>
    Table,
    /// <summary>A pivot table the user builds from the result's columns.</summary>
    Crosstab
}

/// <summary>One result row, keyed by <see cref="SqlTableColumn.Field"/>. A database null is a null value.</summary>
public sealed class SqlTableRow : Dictionary<string, object?>
{
    public SqlTableRow() : base(StringComparer.Ordinal) { }
    public SqlTableRow(int capacity) : base(capacity, StringComparer.Ordinal) { }
}

/// <summary>One column of a query result, described from the result's schema and the query text.</summary>
public sealed class SqlTableColumn
{
    /// <summary>Zero-based position in the result.</summary>
    public int Ordinal { get; init; }
    /// <summary>The key of this column in each <see cref="SqlTableRow"/> and its grid field (Col0, Col1, ...).</summary>
    public string Field { get; init; } = "";
    /// <summary>The column name the query returned.</summary>
    public string Name { get; init; } = "";
    public Type DataType { get; init; } = typeof(object);
    /// <summary>The provider's type name (money, varchar, ...) when the executor recorded it.</summary>
    public string? SqlTypeName { get; init; }
    public bool IsNumeric { get; init; }
    public string Format { get; init; } = "";
    public TextAlign TextAlign { get; init; } = TextAlign.Left;
    public ColumnType ColumnType { get; init; } = ColumnType.Text;
    /// <summary>The aggregate function the query computes this column with, or null.</summary>
    public string? SqlAggregate { get; init; }
    /// <summary>True for a numeric column the query aggregates, or one a grouping totals: a crosstab value.</summary>
    public bool IsMeasure { get; set; }
    /// <summary>How a crosstab combines this column's values.</summary>
    public AggregateType Aggregate { get; set; } = AggregateType.Sum;
}

/// <summary>
/// An outline for the table view: rows grouped on one column, each group showing a total line.
/// Ordinals are zero-based result positions.
/// </summary>
/// <param name="GroupColumn">The column to group on.</param>
/// <param name="Aggregate">The total each group shows; null for none.</param>
/// <param name="ValueColumn">The column totalled; null totals every numeric column.</param>
/// <param name="Expanded">True to start with every group open.</param>
/// <param name="InQueryOrder">
/// True keeps the query's row order: a group is a run of consecutive rows with the same value, so a
/// value that comes back later starts another group. False gathers equal values into one group and
/// lists the groups in order.
/// </param>
public sealed record SqlTableGrouping(int GroupColumn, AggregateType? Aggregate = null, int? ValueColumn = null, bool Expanded = false, bool InQueryOrder = false);

/// <summary>A query result as <see cref="SQLTableControl"/> loaded it.</summary>
public sealed class SqlTableResult
{
    public string Sql { get; init; } = "";
    public SqlQueryShape Shape { get; init; } = SqlQueryShape.Unknown;
    public IReadOnlyList<SqlTableColumn> Columns { get; init; } = Array.Empty<SqlTableColumn>();
    public IReadOnlyList<SqlTableRow> Rows { get; init; } = Array.Empty<SqlTableRow>();
}
