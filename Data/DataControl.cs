using System.Collections;
using System.Data;
using System.Dynamic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Fx.ControlKit.Charts;
using Fx.ControlKit.Reports;

namespace Fx.ControlKit.Data;

/// <summary>
/// Data-aware table component inspired by pandas DataFrame and PowerBuilder DataWindow.
/// It wraps a <see cref="DataTable"/> and exposes data shaping, row/item access,
/// selection, export, and adapter methods for GridControl, ChartControl, and ReportWriterControl.
/// </summary>
public class DataControl
{
    private DataTable _table;
    private int _currentRowIndex = -1;
    private readonly HashSet<int> _selectedRowNumbers = new();

    public DataControl()
        : this(new DataTable())
    {
    }

    public DataControl(DataTable table)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        ApplyView();
    }

    /// <summary>
    /// Create a DataControl with a pandas DataFrame-style constructor signature.
    /// Accepts DataControl, DataTable, dictionaries, row objects, list-like values,
    /// rectangular arrays, or scalar constants.
    /// </summary>
    public DataControl(
        object? data,
        IEnumerable? index = null,
        IEnumerable? columns = null,
        Type? dtype = null,
        bool? copy = null)
    {
        _table = BuildFrame(data, index, columns, dtype, copy);
        ApplyView();
    }

    /// <summary>The underlying table. Assigning a new table clears selection and reapplies Filter/Sort.</summary>
    public DataTable Table
    {
        get => _table;
        set
        {
            _table = value ?? throw new ArgumentNullException(nameof(value));
            _selectedRowNumbers.Clear();
            _currentRowIndex = RowCount > 0 ? 0 : -1;
            ApplyView();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string TableName
    {
        get => Table.TableName;
        set => Table.TableName = value;
    }

    public string? Title { get; set; }
    public object? Tag { get; set; }
    public IReadOnlyList<object?> Index => CurrentRows().Select(GetRowLabel).ToList();

    /// <summary>DataView row filter expression, similar to DataWindow SetFilter/Filter.</summary>
    public string? Filter
    {
        get => Table.DefaultView.RowFilter;
        set
        {
            Table.DefaultView.RowFilter = value ?? "";
            ClampCurrentRow();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>DataView sort expression, similar to DataWindow SetSort/Sort.</summary>
    public string? Sort
    {
        get => Table.DefaultView.Sort;
        set
        {
            Table.DefaultView.Sort = value ?? "";
            ClampCurrentRow();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsDirty => Table.GetChanges() != null;
    public bool HasRows => RowCount > 0;
    public int RowCount => Table.DefaultView.Count;
    public int TotalRowCount => Table.Rows.Count;
    public int ColumnCount => Table.Columns.Count;
    public int CurrentRow => _currentRowIndex < 0 ? 0 : _currentRowIndex + 1;
    public int ModifiedCount => Table.Select(null, null, DataViewRowState.ModifiedCurrent).Length;
    public int InsertedCount => Table.Select(null, null, DataViewRowState.Added).Length;
    public int DeletedCount => Table.Select(null, null, DataViewRowState.Deleted).Length;
    public IReadOnlyList<DataControlColumn> Columns => Table.Columns.Cast<DataColumn>().Select(DataControlColumn.From).ToList();
    public IReadOnlyDictionary<string, Type> Dtypes =>
        Table.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName, column => column.DataType, StringComparer.OrdinalIgnoreCase);
    public DataControlInfo Info => new(TableName, RowCount, ColumnCount, Columns, Table.Locale);
    public object?[,] Values => BuildValuesArray();
    public IReadOnlyList<IReadOnlyList<object?>> Axes => new List<IReadOnlyList<object?>>
    {
        Index,
        Table.Columns.Cast<DataColumn>().Select(column => (object?)column.ColumnName).ToList()
    };
    public int Ndim => 2;
    public int Size => RowCount * ColumnCount;
    public DataControlShape Shape => new(RowCount, ColumnCount);
    public bool Empty => RowCount == 0 || ColumnCount == 0;
    public DataControlAtIndexer At => new(this);
    public DataControlIatIndexer Iat => new(this);
    public IReadOnlyCollection<int> SelectedRows => _selectedRowNumbers.OrderBy(row => row).ToList();

    public event EventHandler? DataChanged;
    public event EventHandler<DataControlRowChangedEventArgs>? RowChanged;

    public static DataControl FromDataTable(DataTable table) => new(table);

    public static DataControl DataFrame(
        object? data = null,
        IEnumerable? index = null,
        IEnumerable? columns = null,
        Type? dtype = null,
        bool? copy = null) =>
        new(data, index, columns, dtype, copy);

    public static DataControl FromRows(IEnumerable rows, string? tableName = null)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var table = new DataTable(tableName ?? "");
        foreach (var row in rows)
        {
            if (row == null) continue;
            EnsureColumns(table, row);
            var dataRow = table.NewRow();
            foreach (DataColumn column in table.Columns)
                dataRow[column.ColumnName] = GetValue(row, column.ColumnName) ?? DBNull.Value;
            table.Rows.Add(dataRow);
        }

        table.AcceptChanges();
        return new DataControl(table);
    }

    public async Task<int> RetrieveAsync(Func<CancellationToken, Task<DataTable>> retrieve, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retrieve);
        Table = await retrieve(cancellationToken).ConfigureAwait(false);
        Table.AcceptChanges();
        return RowCount;
    }

    public int SetData(DataTable table, bool acceptChanges = true)
    {
        Table = table;
        if (acceptChanges)
            Table.AcceptChanges();
        return RowCount;
    }

    public void Reset()
    {
        Table.Clear();
        _selectedRowNumbers.Clear();
        _currentRowIndex = -1;
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetUpdate()
    {
        Table.AcceptChanges();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public DataTable? GetChanges() => Table.GetChanges();

    public void RejectChanges()
    {
        Table.RejectChanges();
        ClampCurrentRow();
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    public int GetRow() => CurrentRow;

    public bool SetRow(int rowNumber)
    {
        if (rowNumber < 1 || rowNumber > RowCount)
            return false;

        _currentRowIndex = rowNumber - 1;
        return true;
    }

    public DataRow? GetCurrentRow() => CurrentRow == 0 ? null : GetRowByNumber(CurrentRow);

    public DataRow? GetRowData(int rowNumber) => GetRowByNumber(rowNumber);

    public object? GetItem(int rowNumber, string columnName) => NormalizeDbNull(GetRowByNumber(rowNumber)?[columnName]);

    public T? GetItem<T>(int rowNumber, string columnName)
    {
        var value = GetItem(rowNumber, columnName);
        if (value == null)
            return default;

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (targetType.IsInstanceOfType(value))
            return (T)value;

        return (T)Convert.ChangeType(value, targetType, CultureInfo.CurrentCulture);
    }

    public string? GetItemString(int rowNumber, string columnName) => GetItem(rowNumber, columnName)?.ToString();
    public decimal? GetItemDecimal(int rowNumber, string columnName) => GetItem<decimal?>(rowNumber, columnName);
    public double? GetItemDouble(int rowNumber, string columnName) => GetItem<double?>(rowNumber, columnName);
    public int? GetItemInt32(int rowNumber, string columnName) => GetItem<int?>(rowNumber, columnName);
    public DateTime? GetItemDateTime(int rowNumber, string columnName) => GetItem<DateTime?>(rowNumber, columnName);

    public bool SetItem(int rowNumber, string columnName, object? value)
    {
        var row = GetRowByNumber(rowNumber);
        if (row == null || !Table.Columns.Contains(columnName))
            return false;

        row[columnName] = value ?? DBNull.Value;
        RowChanged?.Invoke(this, new DataControlRowChangedEventArgs(rowNumber, row));
        DataChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public int InsertRow(int rowNumber = 0, IDictionary<string, object?>? values = null)
    {
        var row = Table.NewRow();
        if (values != null)
        {
            foreach (var (key, value) in values)
            {
                if (Table.Columns.Contains(key))
                    row[key] = value ?? DBNull.Value;
            }
        }

        var insertIndex = rowNumber <= 0 ? Table.Rows.Count : Math.Clamp(rowNumber - 1, 0, Table.Rows.Count);
        Table.Rows.InsertAt(row, insertIndex);
        _currentRowIndex = Math.Min(insertIndex, Math.Max(0, RowCount - 1));
        DataChanged?.Invoke(this, EventArgs.Empty);
        return CurrentRow;
    }

    public bool DeleteRow(int rowNumber)
    {
        var row = GetRowByNumber(rowNumber);
        if (row == null)
            return false;

        row.Delete();
        _selectedRowNumbers.Remove(rowNumber);
        ClampCurrentRow();
        DataChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public int RowsDiscard(int startRow, int count)
    {
        if (count <= 0)
            return 0;

        var rows = CurrentRows().Skip(Math.Max(0, startRow - 1)).Take(count).ToList();
        foreach (var row in rows)
            Table.Rows.Remove(row);

        ClampCurrentRow();
        DataChanged?.Invoke(this, EventArgs.Empty);
        return rows.Count;
    }

    public int RowsCopy(int startRow, int count, DataControl target, int targetRow = 0)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (count <= 0)
            return 0;

        var copied = 0;
        foreach (var row in CurrentRows().Skip(Math.Max(0, startRow - 1)).Take(count))
        {
            target.EnsureSchemaMatches(Table);
            var newRow = target.Table.NewRow();
            foreach (DataColumn column in Table.Columns)
                newRow[column.ColumnName] = row[column.ColumnName];

            if (targetRow <= 0)
                target.Table.Rows.Add(newRow);
            else
                target.Table.Rows.InsertAt(newRow, Math.Clamp(targetRow - 1 + copied, 0, target.Table.Rows.Count));

            copied++;
        }

        target.DataChanged?.Invoke(target, EventArgs.Empty);
        return copied;
    }

    public int RowsMove(int startRow, int count, DataControl target, int targetRow = 0)
    {
        var moved = RowsCopy(startRow, count, target, targetRow);
        RowsDiscard(startRow, moved);
        return moved;
    }

    public void SelectRow(int rowNumber, bool selected = true)
    {
        if (rowNumber < 1 || rowNumber > RowCount)
            return;

        if (selected)
            _selectedRowNumbers.Add(rowNumber);
        else
            _selectedRowNumbers.Remove(rowNumber);
    }

    public void ClearSelection() => _selectedRowNumbers.Clear();

    public bool IsSelected(int rowNumber) => _selectedRowNumbers.Contains(rowNumber);

    public DataTable SelectedData()
    {
        var clone = Table.Clone();
        foreach (var rowNumber in _selectedRowNumbers.OrderBy(row => row))
        {
            var row = GetRowByNumber(rowNumber);
            if (row != null)
                clone.ImportRow(row);
        }

        return clone;
    }

    public DataControl Head(int count = 5) => TakeRows(count);

    public DataControl Tail(int count = 5)
    {
        var clone = Table.Clone();
        var take = Math.Max(0, count);
        foreach (var row in CurrentRows().TakeLast(take))
            clone.ImportRow(row);
        ApplyIndex(clone, Index.TakeLast(take).ToList());
        clone.AcceptChanges();
        return new DataControl(clone);
    }

    public DataControl SelectDtypes(IEnumerable<Type>? include = null, IEnumerable<Type>? exclude = null)
    {
        var includeTypes = include?.Select(NormalizeType).ToHashSet() ?? new HashSet<Type>();
        var excludeTypes = exclude?.Select(NormalizeType).ToHashSet() ?? new HashSet<Type>();
        var names = Table.Columns.Cast<DataColumn>()
            .Where(column => includeTypes.Count == 0 || includeTypes.Contains(NormalizeType(column.DataType)))
            .Where(column => !excludeTypes.Contains(NormalizeType(column.DataType)))
            .Select(column => column.ColumnName)
            .ToArray();

        return SelectColumns(names);
    }

    public DataControl Astype(Type dtype) => ConvertColumns(Table.Columns.Cast<DataColumn>().ToDictionary(column => column.ColumnName, _ => dtype));

    public DataControl Astype(string columnName, Type dtype) => Astype(new Dictionary<string, Type> { [columnName] = dtype });

    public DataControl Astype(IDictionary<string, Type> dtypes) => ConvertColumns(dtypes);

    public DataControl ConvertDtypes() => ConvertColumns(InferDtypes(preferStringConversion: true));

    public DataControl InferObjects() => ConvertColumns(InferDtypes(preferStringConversion: false));

    public DataControl Copy(bool deep = true)
    {
        var table = deep ? Table.Copy() : Table;
        ApplyIndex(table, Index);
        return new DataControl(table);
    }

    public DataControl SelectColumns(params string[] columnNames)
    {
        var clone = new DataTable(TableName);
        foreach (var columnName in columnNames.Where(Table.Columns.Contains))
        {
            var source = Table.Columns[columnName]!;
            clone.Columns.Add(source.ColumnName, source.DataType);
        }

        foreach (var row in CurrentRows())
        {
            var newRow = clone.NewRow();
            foreach (DataColumn column in clone.Columns)
                newRow[column.ColumnName] = row[column.ColumnName];
            clone.Rows.Add(newRow);
        }

        ApplyIndex(clone, Index);
        clone.AcceptChanges();
        return new DataControl(clone);
    }

    public DataControl DropColumns(params string[] columnNames)
    {
        var clone = Table.Copy();
        foreach (var columnName in columnNames)
        {
            if (clone.Columns.Contains(columnName))
                clone.Columns.Remove(columnName);
        }
        clone.AcceptChanges();
        return new DataControl(clone);
    }

    public DataControl RenameColumn(string oldName, string newName)
    {
        if (Table.Columns.Contains(oldName))
        {
            Table.Columns[oldName]!.ColumnName = newName;
            DataChanged?.Invoke(this, EventArgs.Empty);
        }

        return this;
    }

    public DataControl AddColumn(string columnName, Type dataType, object? defaultValue = null)
    {
        var column = Table.Columns.Add(columnName, dataType);
        if (defaultValue != null)
            column.DefaultValue = defaultValue;

        foreach (DataRow row in Table.Rows)
            row[column] = defaultValue ?? DBNull.Value;

        DataChanged?.Invoke(this, EventArgs.Empty);
        return this;
    }

    public DataControl DropNulls(params string[] columnNames)
    {
        var names = columnNames.Length == 0
            ? Table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToArray()
            : columnNames;

        foreach (var row in Table.Rows.Cast<DataRow>().ToList())
        {
            if (names.Any(name => Table.Columns.Contains(name) && row.IsNull(name)))
                Table.Rows.Remove(row);
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
        return this;
    }

    public DataControl FillNulls(object value, params string[] columnNames)
    {
        var names = columnNames.Length == 0
            ? Table.Columns.Cast<DataColumn>().Select(column => column.ColumnName).ToArray()
            : columnNames;

        foreach (DataRow row in Table.Rows)
        {
            foreach (var name in names.Where(Table.Columns.Contains))
            {
                if (row.IsNull(name))
                    row[name] = value;
            }
        }

        DataChanged?.Invoke(this, EventArgs.Empty);
        return this;
    }

    public DataControl Query(string expression)
    {
        var clone = Table.Clone();
        foreach (var row in Table.Select(expression))
            clone.ImportRow(row);
        clone.AcceptChanges();
        return new DataControl(clone);
    }

    public DataControl Where(Func<DataRow, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var clone = Table.Clone();
        foreach (var row in CurrentRows().Where(predicate))
            clone.ImportRow(row);
        clone.AcceptChanges();
        return new DataControl(clone);
    }

    public DataControl SortBy(string columnName, bool descending = false)
    {
        Sort = $"{EscapeColumn(columnName)} {(descending ? "DESC" : "ASC")}";
        return this;
    }

    public IReadOnlyList<DataControlAggregateResult> GroupBy(string groupColumn, string valueColumn, DataControlAggregate aggregate)
    {
        if (!Table.Columns.Contains(groupColumn) || !Table.Columns.Contains(valueColumn))
            return Array.Empty<DataControlAggregateResult>();

        return CurrentRows()
            .GroupBy(row => NormalizeDbNull(row[groupColumn]))
            .Select(group => new DataControlAggregateResult(
                group.Key,
                aggregate,
                AggregateRows(group, valueColumn, aggregate),
                group.Count()))
            .ToList();
    }

    public object? Describe(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var parts = expression.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            return parts[0].ToLowerInvariant() switch
            {
                "rowcount" => RowCount,
                "columncount" => ColumnCount,
                "filter" => Filter,
                "sort" => Sort,
                "title" => Title,
                "tablename" => TableName,
                _ => Table.Columns.Contains(parts[0]) ? Table.Columns[parts[0]]?.DataType.Name : null
            };
        }

        if (parts.Length == 2 && parts[0].Equals("DataWindow", StringComparison.OrdinalIgnoreCase))
        {
            return parts[1].ToLowerInvariant() switch
            {
                "rowcount" => RowCount,
                "columncount" => ColumnCount,
                "filter" => Filter,
                "sort" => Sort,
                "firstrowonpage" => RowCount > 0 ? 1 : 0,
                "lastrowonpage" => RowCount,
                _ => null
            };
        }

        return null;
    }

    public bool Modify(string propertyName, object? value)
    {
        switch (propertyName.ToLowerInvariant())
        {
            case "filter":
            case "datawindow.filter":
                Filter = value?.ToString();
                return true;
            case "sort":
            case "datawindow.sort":
                Sort = value?.ToString();
                return true;
            case "title":
            case "datawindow.title":
                Title = value?.ToString();
                DataChanged?.Invoke(this, EventArgs.Empty);
                return true;
            default:
                return false;
        }
    }

    public int Find(string expression, int startRow = 1, int endRow = 0)
    {
        var rows = CurrentRows().ToList();
        var start = Math.Max(0, startRow - 1);
        var end = endRow <= 0 ? rows.Count - 1 : Math.Min(rows.Count - 1, endRow - 1);
        if (start > end)
            return 0;

        var matches = Table.Select(expression);
        for (var i = start; i <= end; i++)
        {
            if (matches.Contains(rows[i]))
                return i + 1;
        }

        return 0;
    }

    public IEnumerable<ExpandoObject> ToGridRows() => ToExpandoRows();

    public List<Dictionary<string, object?>> ToDictionaries() =>
        CurrentRows()
            .Select(row => Table.Columns.Cast<DataColumn>()
                .ToDictionary(column => column.ColumnName, column => NormalizeDbNull(row[column]), StringComparer.OrdinalIgnoreCase))
            .ToList();

    public List<ExpandoObject> ToExpandoRows()
    {
        var rows = new List<ExpandoObject>();
        foreach (var row in CurrentRows())
        {
            var expando = new ExpandoObject();
            var dict = (IDictionary<string, object?>)expando;
            foreach (DataColumn column in Table.Columns)
                dict[column.ColumnName] = NormalizeDbNull(row[column]);
            rows.Add(expando);
        }

        return rows;
    }

    public List<ChartSeries> ToChartSeries(string labelColumn, params string[] valueColumns)
    {
        var series = new List<ChartSeries>();
        foreach (var valueColumn in valueColumns.Where(Table.Columns.Contains))
        {
            var chartSeries = new ChartSeries { Name = valueColumn };
            foreach (var row in CurrentRows())
            {
                chartSeries.DataPoints.Add(new ChartDataPoint(
                    Convert.ToString(NormalizeDbNull(row[labelColumn]), CultureInfo.CurrentCulture) ?? "",
                    ToDouble(row[valueColumn])));
            }
            series.Add(chartSeries);
        }

        return series;
    }

    public List<ChartControl.ChartBar> ToChartBars(string labelColumn, string valueColumn, string? colorColumn = null) =>
        CurrentRows()
            .Select(row => new ChartControl.ChartBar(
                Convert.ToString(NormalizeDbNull(row[labelColumn]), CultureInfo.CurrentCulture) ?? "",
                ToDouble(row[valueColumn]),
                colorColumn != null && Table.Columns.Contains(colorColumn)
                    ? Convert.ToString(NormalizeDbNull(row[colorColumn]), CultureInfo.CurrentCulture)
                    : null))
            .ToList();

    public List<ReportColumn> ToReportColumns() =>
        Table.Columns.Cast<DataColumn>().Select(column => new ReportColumn
        {
            Field = column.ColumnName,
            HeaderText = column.Caption == column.ColumnName ? column.ColumnName : column.Caption,
            ColumnType = ToReportColumnType(column.DataType),
            Alignment = IsNumericType(column.DataType) ? ReportAlignment.Right : ReportAlignment.Left
        }).ToList();

    public ReportDefinition ToReportDefinition(string title)
    {
        return new ReportDefinition
        {
            ReportId = string.IsNullOrWhiteSpace(TableName) ? title : TableName,
            Title = title,
            Columns = ToReportColumns()
        };
    }

    public string SaveAs(DataControlSaveFormat format = DataControlSaveFormat.Csv) =>
        format switch
        {
            DataControlSaveFormat.Json => ToJson(),
            DataControlSaveFormat.Csv => ToCsv(),
            _ => ToCsv()
        };

    public string ToJson(JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(ToDictionaries(), options ?? new JsonSerializerOptions { WriteIndented = true });

    public string ToCsv()
    {
        var sb = new StringBuilder();
        var columns = Table.Columns.Cast<DataColumn>().ToList();
        sb.AppendLine(string.Join(",", columns.Select(column => CsvEscape(column.ColumnName))));
        foreach (var row in CurrentRows())
            sb.AppendLine(string.Join(",", columns.Select(column => CsvEscape(NormalizeDbNull(row[column])?.ToString() ?? ""))));

        return sb.ToString();
    }

    internal object? GetItemByLabel(object? rowLabel, string columnName)
    {
        var row = GetRowByLabel(rowLabel);
        return row == null || !Table.Columns.Contains(columnName) ? null : NormalizeDbNull(row[columnName]);
    }

    internal bool SetItemByLabel(object? rowLabel, string columnName, object? value)
    {
        var row = GetRowByLabel(rowLabel);
        if (row == null || !Table.Columns.Contains(columnName))
            return false;

        row[columnName] = value ?? DBNull.Value;
        DataChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    internal object? GetItemByPosition(int rowIndex, int columnIndex)
    {
        var row = GetRowByPosition(rowIndex);
        return row == null || columnIndex < 0 || columnIndex >= Table.Columns.Count ? null : NormalizeDbNull(row[columnIndex]);
    }

    internal bool SetItemByPosition(int rowIndex, int columnIndex, object? value)
    {
        var row = GetRowByPosition(rowIndex);
        if (row == null || columnIndex < 0 || columnIndex >= Table.Columns.Count)
            return false;

        row[columnIndex] = value ?? DBNull.Value;
        DataChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private DataControl TakeRows(int count)
    {
        var clone = Table.Clone();
        foreach (var row in CurrentRows().Take(Math.Max(0, count)))
            clone.ImportRow(row);
        ApplyIndex(clone, Index.Take(Math.Max(0, count)).ToList());
        clone.AcceptChanges();
        return new DataControl(clone);
    }

    private object?[,] BuildValuesArray()
    {
        var rows = CurrentRows().ToList();
        var values = new object?[rows.Count, Table.Columns.Count];
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < Table.Columns.Count; c++)
                values[r, c] = NormalizeDbNull(rows[r][c]);
        }

        return values;
    }

    private DataControl ConvertColumns(IDictionary<string, Type> targetTypes)
    {
        var table = new DataTable(TableName);
        foreach (DataColumn column in Table.Columns)
        {
            var targetType = targetTypes.TryGetValue(column.ColumnName, out var dtype)
                ? NormalizeType(dtype)
                : column.DataType;
            table.Columns.Add(column.ColumnName, targetType);
        }

        foreach (var sourceRow in CurrentRows())
        {
            var row = table.NewRow();
            foreach (DataColumn column in table.Columns)
                row[column] = CoerceValue(sourceRow[column.ColumnName], column.DataType);
            table.Rows.Add(row);
        }

        ApplyIndex(table, Index);
        table.AcceptChanges();
        return new DataControl(table);
    }

    private Dictionary<string, Type> InferDtypes(bool preferStringConversion)
    {
        var dtypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        foreach (DataColumn column in Table.Columns)
        {
            dtypes[column.ColumnName] = column.DataType == typeof(object) || (preferStringConversion && column.DataType == typeof(string))
                ? InferBestColumnType(column)
                : column.DataType;
        }

        return dtypes;
    }

    private Type InferBestColumnType(DataColumn column)
    {
        var values = CurrentRows()
            .Select(row => NormalizeDbNull(row[column]))
            .Where(value => value != null)
            .ToList();
        if (values.Count == 0)
            return typeof(object);

        if (values.All(value => value is bool || bool.TryParse(value?.ToString(), out _)))
            return typeof(bool);
        if (values.All(value => value is int || int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.CurrentCulture, out _)))
            return typeof(int);
        if (values.All(value => value is long || long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.CurrentCulture, out _)))
            return typeof(long);
        if (values.All(value => value is decimal || decimal.TryParse(value?.ToString(), NumberStyles.Number, CultureInfo.CurrentCulture, out _)))
            return typeof(decimal);
        if (values.All(value => value is DateTime || DateTime.TryParse(value?.ToString(), CultureInfo.CurrentCulture, DateTimeStyles.None, out _)))
            return typeof(DateTime);

        return values.Select(value => value!.GetType()).Distinct().Count() == 1 ? values[0]!.GetType() : typeof(object);
    }

    private static DataTable BuildFrame(object? data, IEnumerable? index, IEnumerable? columns, Type? dtype, bool? copy)
    {
        var indexLabels = ToLabelList(index);
        var columnLabels = ToLabelList(columns).Select(label => label?.ToString() ?? "").Where(label => label.Length > 0).ToList();
        if (indexLabels.Count == 0 && data is IEnumerable dictionaryData && IsDictionary(data))
            indexLabels = DeriveIndexFromDictionary(dictionaryData);

        var table = data switch
        {
            null => BuildEmptyFrame(indexLabels, columnLabels, dtype),
            DataControl frame => CopyDataTable(frame.Table, copy ?? false),
            DataTable dataTable => CopyDataTable(dataTable, copy ?? false),
            _ when IsDictionary(data) => BuildFromDictionary((IEnumerable)data, indexLabels, columnLabels, dtype),
            Array array when array.Rank == 2 => BuildFrom2dArray(array, indexLabels, columnLabels, dtype),
            string or not IEnumerable => BuildFromScalar(data, indexLabels, columnLabels, dtype),
            IEnumerable enumerable => BuildFromEnumerable(enumerable, indexLabels, columnLabels, dtype)
        };

        if (columnLabels.Count > 0)
            table = SelectOrCreateColumns(table, columnLabels, dtype);

        ApplyIndex(table, indexLabels);
        table.AcceptChanges();
        return table;
    }

    private static DataTable CopyDataTable(DataTable source, bool copy) => copy ? source.Copy() : source;

    private static DataTable BuildEmptyFrame(IReadOnlyList<object?> indexLabels, IReadOnlyList<string> columnLabels, Type? dtype)
    {
        var table = new DataTable();
        foreach (var columnName in columnLabels)
            table.Columns.Add(columnName, dtype ?? typeof(object));

        for (var i = 0; i < indexLabels.Count; i++)
        {
            var row = table.NewRow();
            foreach (DataColumn column in table.Columns)
                row[column] = DBNull.Value;
            table.Rows.Add(row);
        }

        return table;
    }

    private static DataTable BuildFromDictionary(IEnumerable dictionary, IReadOnlyList<object?> indexLabels, IReadOnlyList<string> columnLabels, Type? dtype)
    {
        var columns = DictionaryEntries(dictionary).ToList();
        var selectedColumns = columnLabels.Count == 0
            ? columns.Select(entry => entry.Key).ToList()
            : columnLabels;

        var rowCount = indexLabels.Count > 0
            ? indexLabels.Count
            : columns.Select(entry => ValueLength(entry.Value)).DefaultIfEmpty(1).Max();
        if (rowCount == 0)
            rowCount = 1;

        var table = new DataTable();
        foreach (var columnName in selectedColumns)
        {
            var value = columns.FirstOrDefault(entry => entry.Key == columnName).Value;
            table.Columns.Add(columnName, dtype ?? InferColumnType(value));
        }

        for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
        {
            var row = table.NewRow();
            foreach (DataColumn column in table.Columns)
            {
                var value = columns.FirstOrDefault(entry => entry.Key == column.ColumnName).Value;
                var rowLabel = indexLabels.Count > rowIndex ? indexLabels[rowIndex] : rowIndex;
                row[column] = CoerceValue(GetAlignedSequenceValue(value, rowIndex, rowLabel), column.DataType);
            }
            table.Rows.Add(row);
        }

        return table;
    }

    private static DataTable BuildFrom2dArray(Array array, IReadOnlyList<object?> indexLabels, IReadOnlyList<string> columnLabels, Type? dtype)
    {
        var rowCount = array.GetLength(0);
        var columnCount = array.GetLength(1);
        var table = new DataTable();
        var labels = columnLabels.Count > 0
            ? columnLabels
            : Enumerable.Range(0, columnCount).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();

        foreach (var label in labels.Take(columnCount))
            table.Columns.Add(label, dtype ?? typeof(object));

        for (var r = 0; r < rowCount; r++)
        {
            var row = table.NewRow();
            for (var c = 0; c < table.Columns.Count; c++)
                row[c] = CoerceValue(array.GetValue(r, c), table.Columns[c].DataType);
            table.Rows.Add(row);
        }

        return table;
    }

    private static DataTable BuildFromEnumerable(IEnumerable enumerable, IReadOnlyList<object?> indexLabels, IReadOnlyList<string> columnLabels, Type? dtype)
    {
        var items = enumerable.Cast<object?>().Where(item => item != null).ToList();
        if (items.Count == 0)
            return BuildEmptyFrame(indexLabels, columnLabels, dtype);

        if (items.All(IsDictionary))
            return BuildFromRowDictionaries(items!, columnLabels, dtype);

        if (items.All(item => item is not string && item is IEnumerable))
            return BuildFromRowsOfValues(items!, columnLabels, dtype);

        if (items.All(item => item!.GetType().IsPrimitive || item is string || item is decimal || item is DateTime || item is DateOnly))
            return BuildFromSingleSeries(items!, columnLabels, dtype);

        return BuildFromObjects(items!, columnLabels, dtype);
    }

    private static DataTable BuildFromRowDictionaries(IEnumerable<object> rows, IReadOnlyList<string> columnLabels, Type? dtype)
    {
        var dictionaries = rows.Select(row => DictionaryEntries((IEnumerable)row).ToList()).ToList();
        var labels = columnLabels.Count > 0
            ? columnLabels
            : dictionaries.SelectMany(row => row.Select(entry => entry.Key)).Distinct().ToList();
        var table = new DataTable();

        foreach (var label in labels)
        {
            var sample = dictionaries.SelectMany(row => row).FirstOrDefault(entry => entry.Key == label).Value;
            table.Columns.Add(label, dtype ?? InferColumnType(sample));
        }

        foreach (var dict in dictionaries)
        {
            var row = table.NewRow();
            foreach (DataColumn column in table.Columns)
                row[column] = CoerceValue(dict.FirstOrDefault(entry => entry.Key == column.ColumnName).Value, column.DataType);
            table.Rows.Add(row);
        }

        return table;
    }

    private static DataTable BuildFromRowsOfValues(IEnumerable<object> rows, IReadOnlyList<string> columnLabels, Type? dtype)
    {
        var rowValues = rows.Select(row => ((IEnumerable)row).Cast<object?>().ToList()).ToList();
        var columnCount = Math.Max(columnLabels.Count, rowValues.Select(row => row.Count).DefaultIfEmpty(0).Max());
        var labels = columnLabels.Count > 0
            ? columnLabels
            : Enumerable.Range(0, columnCount).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();
        var table = new DataTable();

        foreach (var label in labels)
            table.Columns.Add(label, dtype ?? typeof(object));

        foreach (var values in rowValues)
        {
            var row = table.NewRow();
            for (var i = 0; i < table.Columns.Count; i++)
                row[i] = CoerceValue(i < values.Count ? values[i] : null, table.Columns[i].DataType);
            table.Rows.Add(row);
        }

        return table;
    }

    private static DataTable BuildFromSingleSeries(IEnumerable<object> values, IReadOnlyList<string> columnLabels, Type? dtype)
    {
        var label = columnLabels.Count > 0 ? columnLabels[0] : "0";
        var table = new DataTable();
        table.Columns.Add(label, dtype ?? InferColumnType(values.FirstOrDefault()));
        foreach (var value in values)
        {
            var row = table.NewRow();
            row[0] = CoerceValue(value, table.Columns[0].DataType);
            table.Rows.Add(row);
        }

        return table;
    }

    private static DataTable BuildFromObjects(IEnumerable<object> rows, IReadOnlyList<string> columnLabels, Type? dtype)
    {
        var items = rows.ToList();
        var labels = columnLabels.Count > 0
            ? columnLabels
            : items.SelectMany(item => item.GetType().GetProperties().Select(property => property.Name)).Distinct().ToList();
        var table = new DataTable();

        foreach (var label in labels)
            table.Columns.Add(label, dtype ?? InferPropertyType(items, label));

        foreach (var item in items)
        {
            var row = table.NewRow();
            foreach (DataColumn column in table.Columns)
                row[column] = CoerceValue(item.GetType().GetProperty(column.ColumnName)?.GetValue(item), column.DataType);
            table.Rows.Add(row);
        }

        return table;
    }

    private static DataTable BuildFromScalar(object? value, IReadOnlyList<object?> indexLabels, IReadOnlyList<string> columnLabels, Type? dtype)
    {
        var labels = columnLabels.Count > 0 ? columnLabels : new List<string> { "0" };
        var rows = indexLabels.Count > 0 ? indexLabels.Count : 1;
        var table = new DataTable();
        foreach (var label in labels)
            table.Columns.Add(label, dtype ?? value?.GetType() ?? typeof(object));

        for (var i = 0; i < rows; i++)
        {
            var row = table.NewRow();
            foreach (DataColumn column in table.Columns)
                row[column] = CoerceValue(value, column.DataType);
            table.Rows.Add(row);
        }

        return table;
    }

    private static DataTable SelectOrCreateColumns(DataTable source, IReadOnlyList<string> columnLabels, Type? dtype)
    {
        var table = new DataTable(source.TableName);
        foreach (var label in columnLabels)
        {
            var sourceColumn = source.Columns.Contains(label) ? source.Columns[label] : null;
            table.Columns.Add(label, dtype ?? sourceColumn?.DataType ?? typeof(object));
        }

        foreach (DataRow sourceRow in source.Rows)
        {
            var row = table.NewRow();
            foreach (DataColumn column in table.Columns)
            {
                row[column] = source.Columns.Contains(column.ColumnName)
                    ? CoerceValue(sourceRow[column.ColumnName], column.DataType)
                    : DBNull.Value;
            }
            table.Rows.Add(row);
        }

        return table;
    }

    private static void ApplyIndex(DataTable table, IReadOnlyList<object?> indexLabels)
    {
        if (indexLabels.Count == 0)
        {
            table.ExtendedProperties["Index"] = Enumerable.Range(0, table.Rows.Count).Cast<object?>().ToList();
            return;
        }

        table.ExtendedProperties["Index"] = indexLabels.ToList();
    }

    private static object? GetRowLabel(DataRow row)
    {
        if (row.Table.ExtendedProperties["Index"] is IReadOnlyList<object?> labels)
        {
            var index = row.Table.Rows.IndexOf(row);
            if (index >= 0 && index < labels.Count)
                return labels[index];
        }

        return row.Table.Rows.IndexOf(row);
    }

    private static List<object?> ToLabelList(IEnumerable? labels) =>
        labels == null ? new List<object?>() : labels.Cast<object?>().ToList();

    private static bool IsDictionary(object? value) =>
        value is IDictionary || value is IEnumerable<KeyValuePair<string, object?>> || value is IEnumerable<KeyValuePair<string, object>>;

    private static IEnumerable<(string Key, object? Value)> DictionaryEntries(IEnumerable dictionary)
    {
        if (dictionary is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
                yield return (entry.Key?.ToString() ?? "", entry.Value);
            yield break;
        }

        foreach (var item in dictionary)
        {
            if (item == null)
                continue;

            var type = item.GetType();
            var key = type.GetProperty("Key")?.GetValue(item)?.ToString();
            if (key == null)
                continue;

            yield return (key, type.GetProperty("Value")?.GetValue(item));
        }
    }

    private static int ValueLength(object? value)
    {
        if (value is null || value is string)
            return 1;
        if (value is DataControl frame)
            return frame.RowCount;
        if (value is DataTable table)
            return table.Rows.Count;
        if (value is IEnumerable enumerable)
            return enumerable.Cast<object?>().Count();

        return 1;
    }

    private static List<object?> DeriveIndexFromDictionary(IEnumerable dictionary)
    {
        foreach (var (_, value) in DictionaryEntries(dictionary))
        {
            if (value is DataControl frame && frame.Index.Count > 0)
                return frame.Index.ToList();
            if (value is DataTable table && table.ExtendedProperties["Index"] is IReadOnlyList<object?> labels)
                return labels.ToList();
        }

        return new List<object?>();
    }

    private static object? GetSequenceValue(object? value, int rowIndex)
    {
        value = NormalizeDbNull(value);
        if (value is null || value is string)
            return value;
        if (value is DataControl frame)
            return frame.RowCount > rowIndex && frame.ColumnCount > 0 ? frame.GetItem(rowIndex + 1, frame.Table.Columns[0].ColumnName) : null;
        if (value is DataTable table)
            return table.Rows.Count > rowIndex && table.Columns.Count > 0 ? NormalizeDbNull(table.Rows[rowIndex][0]) : null;
        if (value is IEnumerable enumerable)
            return enumerable.Cast<object?>().Skip(rowIndex).FirstOrDefault();

        return value;
    }

    private static object? GetAlignedSequenceValue(object? value, int rowIndex, object? rowLabel)
    {
        value = NormalizeDbNull(value);
        if (value is DataControl frame)
        {
            var alignedIndex = frame.Index
                .Select((label, index) => (label, index))
                .FirstOrDefault(item => Equals(item.label, rowLabel)).index;
            if (alignedIndex > 0 || (frame.Index.Count > 0 && Equals(frame.Index[0], rowLabel)))
                return frame.ColumnCount > 0 ? frame.GetItem(alignedIndex + 1, frame.Table.Columns[0].ColumnName) : null;
        }

        if (value is DataTable table && table.ExtendedProperties["Index"] is IReadOnlyList<object?> labels)
        {
            for (var i = 0; i < labels.Count && i < table.Rows.Count; i++)
            {
                if (Equals(labels[i], rowLabel))
                    return table.Columns.Count > 0 ? NormalizeDbNull(table.Rows[i][0]) : null;
            }
        }

        return GetSequenceValue(value, rowIndex);
    }

    private static Type InferColumnType(object? value)
    {
        value = GetSequenceValue(value, 0);
        value = NormalizeDbNull(value);
        return value?.GetType() ?? typeof(object);
    }

    private static Type InferPropertyType(IEnumerable<object> rows, string propertyName)
    {
        foreach (var row in rows)
        {
            var property = row.GetType().GetProperty(propertyName);
            if (property != null)
                return Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        }

        return typeof(object);
    }

    private static object CoerceValue(object? value, Type targetType)
    {
        value = NormalizeDbNull(value);
        if (value == null)
            return DBNull.Value;

        targetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (targetType == typeof(object) || targetType.IsInstanceOfType(value))
            return value;
        if (targetType.IsEnum)
            return value is string text ? Enum.Parse(targetType, text, ignoreCase: true) : Enum.ToObject(targetType, value);
        if (targetType == typeof(Guid))
            return value is Guid guid ? guid : Guid.Parse(value.ToString() ?? "");
        if (targetType == typeof(DateOnly))
        {
            if (value is DateOnly dateOnly)
                return dateOnly;
            if (value is DateTime dateTime)
                return DateOnly.FromDateTime(dateTime);
            return DateOnly.Parse(value.ToString() ?? "", CultureInfo.CurrentCulture);
        }

        return Convert.ChangeType(value, targetType, CultureInfo.CurrentCulture);
    }

    private void ApplyView()
    {
        if (!string.IsNullOrWhiteSpace(Table.DefaultView.RowFilter))
            Table.DefaultView.RowFilter = Table.DefaultView.RowFilter;
        if (!string.IsNullOrWhiteSpace(Table.DefaultView.Sort))
            Table.DefaultView.Sort = Table.DefaultView.Sort;
        ClampCurrentRow();
    }

    private void ClampCurrentRow()
    {
        _currentRowIndex = RowCount == 0 ? -1 : Math.Clamp(_currentRowIndex < 0 ? 0 : _currentRowIndex, 0, RowCount - 1);
    }

    private IEnumerable<DataRow> CurrentRows() => Table.DefaultView.Cast<DataRowView>().Select(view => view.Row);

    private DataRow? GetRowByNumber(int rowNumber) =>
        rowNumber < 1 || rowNumber > RowCount ? null : Table.DefaultView[rowNumber - 1].Row;

    private DataRow? GetRowByPosition(int rowIndex) =>
        rowIndex < 0 || rowIndex >= RowCount ? null : Table.DefaultView[rowIndex].Row;

    private DataRow? GetRowByLabel(object? rowLabel)
    {
        var rows = CurrentRows().ToList();
        var labels = Index;
        for (var i = 0; i < rows.Count && i < labels.Count; i++)
        {
            if (Equals(labels[i], rowLabel))
                return rows[i];
        }

        return null;
    }

    private void EnsureSchemaMatches(DataTable source)
    {
        foreach (DataColumn column in source.Columns)
        {
            if (!Table.Columns.Contains(column.ColumnName))
                Table.Columns.Add(column.ColumnName, column.DataType);
        }
    }

    private static void EnsureColumns(DataTable table, object row)
    {
        if (row is IDictionary<string, object?> dict)
        {
            foreach (var (key, value) in dict)
            {
                if (!table.Columns.Contains(key))
                    table.Columns.Add(key, value?.GetType() ?? typeof(object));
            }
            return;
        }

        foreach (var property in row.GetType().GetProperties())
        {
            if (!table.Columns.Contains(property.Name))
                table.Columns.Add(property.Name, Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType);
        }
    }

    private static object? GetValue(object row, string columnName)
    {
        if (row is IDictionary<string, object?> dict && dict.TryGetValue(columnName, out var value))
            return value;

        return row.GetType().GetProperty(columnName)?.GetValue(row);
    }

    private static object? NormalizeDbNull(object? value) => value == DBNull.Value ? null : value;

    private static Type NormalizeType(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static string EscapeColumn(string columnName) =>
        columnName.Contains(' ') || columnName.Contains('.') ? $"[{columnName}]" : columnName;

    private static double ToDouble(object? value)
    {
        value = NormalizeDbNull(value);
        if (value == null)
            return 0;

        try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static object? AggregateRows(IEnumerable<DataRow> rows, string valueColumn, DataControlAggregate aggregate)
    {
        var values = rows.Select(row => ToDouble(row[valueColumn])).ToList();
        return aggregate switch
        {
            DataControlAggregate.Count => values.Count,
            DataControlAggregate.Sum => values.Sum(),
            DataControlAggregate.Average => values.Count == 0 ? 0 : values.Average(),
            DataControlAggregate.Min => values.Count == 0 ? 0 : values.Min(),
            DataControlAggregate.Max => values.Count == 0 ? 0 : values.Max(),
            _ => null
        };
    }

    private static bool IsNumericType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte)
            || type == typeof(short)
            || type == typeof(int)
            || type == typeof(long)
            || type == typeof(float)
            || type == typeof(double)
            || type == typeof(decimal);
    }

    private static ReportColumnType ToReportColumnType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(DateTime))
            return ReportColumnType.DateTime;
        if (type == typeof(DateOnly))
            return ReportColumnType.Date;
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
            return ReportColumnType.Decimal;
        if (IsNumericType(type))
            return ReportColumnType.Integer;
        if (type == typeof(bool))
            return ReportColumnType.Boolean;
        return ReportColumnType.Text;
    }

    private static string CsvEscape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}

public sealed record DataControlColumn(
    string Name,
    string Caption,
    Type DataType,
    bool AllowNull,
    bool ReadOnly,
    int Ordinal)
{
    public static DataControlColumn From(DataColumn column) =>
        new(column.ColumnName, column.Caption, column.DataType, column.AllowDBNull, column.ReadOnly, column.Ordinal);
}

public sealed record DataControlInfo(
    string TableName,
    int RowCount,
    int ColumnCount,
    IReadOnlyList<DataControlColumn> Columns,
    CultureInfo Locale)
{
    public IReadOnlyDictionary<string, Type> Dtypes =>
        Columns.ToDictionary(column => column.Name, column => column.DataType, StringComparer.OrdinalIgnoreCase);
}

public sealed record DataControlShape(int Rows, int Columns);

public sealed class DataControlAtIndexer(DataControl owner)
{
    public object? this[object? rowLabel, string columnName]
    {
        get => owner.GetItemByLabel(rowLabel, columnName);
        set => owner.SetItemByLabel(rowLabel, columnName, value);
    }
}

public sealed class DataControlIatIndexer(DataControl owner)
{
    public object? this[int rowIndex, int columnIndex]
    {
        get => owner.GetItemByPosition(rowIndex, columnIndex);
        set => owner.SetItemByPosition(rowIndex, columnIndex, value);
    }
}

public sealed record DataControlAggregateResult(
    object? Key,
    DataControlAggregate Aggregate,
    object? Value,
    int Count);

public sealed class DataControlRowChangedEventArgs(int rowNumber, DataRow row) : EventArgs
{
    public int RowNumber { get; } = rowNumber;
    public DataRow Row { get; } = row;
}

public enum DataControlAggregate
{
    Count,
    Sum,
    Average,
    Min,
    Max
}

public enum DataControlSaveFormat
{
    Csv,
    Json
}
