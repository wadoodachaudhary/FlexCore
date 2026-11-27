using System.Data;
using System.Globalization;
using Fx.ControlKit.Data;
using Fx.ControlKit.Logging;
using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Grid;

/// <summary>
/// Shows the result of one SQL query. The control is given the query text and a way to run it;
/// every column, format and layout choice comes from the query's result and its text.
/// <para>
/// A plain query is a table with search and sort. A grouping query (a <c>GROUP BY</c>, or a
/// <see cref="Grouping"/> the host supplies) also offers a crosstab: a pivot table the user builds
/// by placing the result's columns on rows, columns and values.
/// </para>
/// <para>
/// The control runs whatever text it is given. Checking that the text is safe to run, and binding
/// any parameters, is the host's job inside <see cref="Executor"/>.
/// </para>
/// </summary>
public partial class SQLTableControl : IDisposable
{
    private static readonly IReadOnlyList<SelectionItem> ViewItems = new[]
    {
        new SelectionItem(nameof(SqlTableView.Table), "Table"),
        new SelectionItem(nameof(SqlTableView.Crosstab), "Crosstab")
    };

    // ── Parameters ──────────────────────────────────────────────────

    /// <summary>The query to show. A change runs the new query.</summary>
    [Parameter] public string? Sql { get; set; }

    /// <summary>
    /// Runs the query and returns its result. An executor may record each column's provider type
    /// name in <c>DataColumn.ExtendedProperties["SqlTypeName"]</c>; money columns then show as currency.
    /// </summary>
    [Parameter] public Func<string, CancellationToken, Task<DataTable>>? Executor { get; set; }

    /// <summary>Runs the query when no <see cref="Executor"/> is given.</summary>
    [Parameter] public DbControl? Database { get; set; }

    /// <summary>Any value; a change runs <see cref="Sql"/> again even when its text is the same.</summary>
    [Parameter] public object? QueryKey { get; set; }

    /// <summary>Optional outline for the table view. It also makes the crosstab available.</summary>
    [Parameter] public SqlTableGrouping? Grouping { get; set; }

    /// <summary>
    /// The view a result opens in. Unset: a <c>GROUP BY</c> query without a <see cref="Grouping"/>
    /// opens as a crosstab, anything else as a table.
    /// </summary>
    [Parameter] public SqlTableView? InitialView { get; set; }

    /// <summary>False keeps every result in the table view.</summary>
    [Parameter] public bool AllowCrosstab { get; set; } = true;

    [Parameter] public bool ShowToolbar { get; set; } = true;
    [Parameter] public bool ShowSearch { get; set; } = true;
    [Parameter] public bool AllowSorting { get; set; } = true;
    /// <summary>Shows the table view's per-column filter bar.</summary>
    [Parameter] public bool AllowFiltering { get; set; } = true;
    [Parameter] public string Height { get; set; } = "100%";
    /// <summary>Starting width of each table column.</summary>
    [Parameter] public string ColumnWidth { get; set; } = "150px";

    /// <summary>
    /// Sizes the table's columns to their content when a result opens, as the header menu's Best Fit All Columns
    /// does, so the table is as wide as its columns instead of stretched across the control.
    /// </summary>
    [Parameter] public bool BestFitColumns { get; set; }

    /// <summary>Raised after a query's result is loaded.</summary>
    [Parameter] public EventCallback<SqlTableResult> OnLoaded { get; set; }

    /// <summary>Raised when a query fails. A host that handles it shows the failure itself.</summary>
    [Parameter] public EventCallback<Exception> OnError { get; set; }

    [Parameter] public EventCallback<SqlTableView> ViewChanged { get; set; }

    // ── State ───────────────────────────────────────────────────────

    private GridControl<SqlTableRow>? _grid;
    private PivotControl<SqlTableRow>? _pivot;
    private SqlTableResult? _result;
    private List<SqlTableRow> _allRows = new();
    private List<SqlTableRow> _viewRows = new();
    private string[]? _searchIndex;
    private string _search = "";
    private SqlTableView _view;
    private bool _tableOpened;
    private bool _crosstabOpened;
    private int _resultVersion;
    private bool _loading;
    private Exception? _error;

    private bool _started;
    private string? _loadedSql;
    private object? _loadedKey;
    private string? _builtSql;
    private SqlTableGrouping? _appliedGrouping;
    private int _loadVersion;
    private CancellationTokenSource? _cancel;

    private bool _hasGrouping;
    private bool _groupingExpanded;
    private List<string> _groupColumns = new();
    private List<AggregateRow> _aggregateRows = new();
    private SqlTableColumn? _runColumn;
    private readonly List<string> _gridGroups = new();
    private GridControlEvents<SqlTableRow>? _gridEvents;
    private readonly FilterSettings _filterSettings = new() { Type = FilterType.FilterBar };
    private readonly SelectionSettings _selectionSettings = new() { Type = SelectionType.Single, Mode = SelectionMode.Row };

    private IReadOnlyList<string> _pivotFields = Array.Empty<string>();
    private Dictionary<string, string> _pivotLabels = new();
    private Dictionary<string, PivotValueConfig> _pivotDefaults = new();
    private IReadOnlyList<string> _pivotRows = Array.Empty<string>();
    private IReadOnlyList<string> _pivotColumns = Array.Empty<string>();
    private IReadOnlyList<PivotValueConfig> _pivotValues = Array.Empty<PivotValueConfig>();

    // ── Public surface ──────────────────────────────────────────────

    /// <summary>The grid of the table view; null while the crosstab shows or nothing is loaded.</summary>
    public GridControl<SqlTableRow>? Grid => _view == SqlTableView.Table ? _grid : null;

    /// <summary>The pivot table of the crosstab view; null while the table shows or nothing is loaded.</summary>
    public PivotControl<SqlTableRow>? Crosstab => _view == SqlTableView.Crosstab ? _pivot : null;

    /// <summary>The loaded result; null before the first query finishes and after a failure.</summary>
    public SqlTableResult? Result => _result;

    public SqlTableView View => _view;

    /// <summary>True when the loaded result can be shown as a crosstab.</summary>
    public bool CrosstabAvailable =>
        AllowCrosstab && _result is { Columns.Count: >= 2 } && (_result.Shape.HasGroupBy || _hasGrouping);

    /// <summary>The rows the search leaves, in result order.</summary>
    public IReadOnlyList<SqlTableRow> VisibleRows => _viewRows;

    /// <summary>Runs the current query again.</summary>
    public async Task ReloadAsync()
    {
        await LoadAsync();
        StateHasChanged();
    }

    public async Task SetViewAsync(SqlTableView view)
    {
        if (view == SqlTableView.Crosstab && !CrosstabAvailable) view = SqlTableView.Table;
        if (view == _view) return;
        _view = view;
        MarkViewOpened();
        RecordControlAction("ViewChanged", view.ToString(), null);
        await ViewChanged.InvokeAsync(view);
        StateHasChanged();
    }

    /// <summary>Applies a search text: a row stays when every word appears in one of its values.</summary>
    public Task SearchAsync(string? text)
    {
        var next = text?.Trim() ?? "";
        if (string.Equals(next, _search, StringComparison.Ordinal)) return Task.CompletedTask;
        _search = next;
        ApplySearch();
        RecordControlAction("Search", null, $"Rows={_viewRows.Count}");
        StateHasChanged();
        return Task.CompletedTask;
    }

    // ── Lifecycle ───────────────────────────────────────────────────

    protected override async Task OnParametersSetAsync()
    {
        if (!_started || !string.Equals(Sql, _loadedSql, StringComparison.Ordinal) || !Equals(QueryKey, _loadedKey))
        {
            _started = true;
            await LoadAsync();
        }
        else if (_result != null && !Equals(Grouping, _appliedGrouping))
        {
            // A new outline is a new table: the grid is rebuilt so no earlier grouping carries over.
            ApplyGrouping();
            BuildCrosstabSeed();
            _resultVersion++;
            if (_view == SqlTableView.Crosstab && !CrosstabAvailable) _view = SqlTableView.Table;
            _tableOpened = _crosstabOpened = false;
            MarkViewOpened();
        }
    }

    private void MarkViewOpened()
    {
        if (_view == SqlTableView.Table) _tableOpened = true;
        else _crosstabOpened = true;
    }

    public void Dispose()
    {
        _loadVersion++; // a query still running must not report into a control that is gone
        _cancel?.Cancel();
        _cancel?.Dispose();
        _cancel = null;
    }

    private FlexControlLocation ChildLocation => GetOrCreateChildLocation("SQLTableControl", () => ResolveControlIdentifier() ?? "SqlTable");

    private string RootCssClass => string.IsNullOrWhiteSpace(CssClass) ? "fx-sqltable" : $"fx-sqltable {CssClass.Trim()}";

    private string RootStyle
    {
        get
        {
            var height = string.IsNullOrWhiteSpace(Height) ? "" : $"height:{Height};";
            return height + ResolvedStyle;
        }
    }

    private string CountText
    {
        get
        {
            var total = _allRows.Count.ToString("N0", CultureInfo.CurrentCulture);
            var noun = _allRows.Count == 1 ? "row" : "rows";
            return _viewRows.Count == _allRows.Count
                ? $"{total} {noun}"
                : $"{_viewRows.Count.ToString("N0", CultureInfo.CurrentCulture)} of {total} {noun}";
        }
    }

    /// <summary>The table view's grid settings: this control's own, then whatever the host passed for the grid.</summary>
    private Dictionary<string, object> GridAttributes
    {
        get
        {
            var attributes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(GridControl<SqlTableRow>.AllowSorting)] = AllowSorting,
                [nameof(GridControl<SqlTableRow>.AllowResizing)] = true,
                [nameof(GridControl<SqlTableRow>.AllowSelection)] = true,
                [nameof(GridControl<SqlTableRow>.AllowFiltering)] = AllowFiltering,
                [nameof(GridControl<SqlTableRow>.EnableHover)] = true,
                [nameof(GridControl<SqlTableRow>.FilterSettingsRef)] = _filterSettings,
                [nameof(GridControl<SqlTableRow>.SelectionSettingsRef)] = _selectionSettings,
                [nameof(GridControl<SqlTableRow>.GroupItemTextColor)] = "inherit",
                [nameof(GridControl<SqlTableRow>.GroupTotalTextColor)] = "inherit",
                // The query-order outline keeps its column, so each line still shows the value it is grouped on.
                // Any grouping the user makes is the grid's own: the grouped column moves to the far left.
                [nameof(GridControl<SqlTableRow>.HideGroupedColumns)] = !RunOutlineOnly,
                [nameof(GridControl<SqlTableRow>.EventsRef)] = _gridEvents ??= new GridControlEvents<SqlTableRow>
                {
                    Grouped = EventCallback.Factory.Create<GroupEventArgs>(this, OnGridGrouped),
                    Ungrouped = EventCallback.Factory.Create<GroupEventArgs>(this, OnGridUngrouped)
                },
                // A result's columns are the query's, so there is none to insert.
                [nameof(GridControl<SqlTableRow>.AllowColumnInsert)] = false,
                [nameof(GridControl<SqlTableRow>.ColumnMenuOnCells)] = true
            };
            // Only the opening fit: the grid keeps the control's width, so the scroll area, the wheel and the
            // fit's widest column still span the control while the table is as wide as its columns.
            if (BestFitColumns)
                attributes[nameof(GridControl<SqlTableRow>.BestFitOnLoad)] = true;
            if (AdditionalAttributes != null)
                foreach (var pair in AdditionalAttributes)
                    attributes[pair.Key] = GridValue(pair.Key, pair.Value);
            return attributes;
        }
    }

    private static readonly Dictionary<string, Type> GridParameterTypes = typeof(GridControl<SqlTableRow>)
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(property => property.IsDefined(typeof(ParameterAttribute), inherit: true))
        .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.First().PropertyType, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A grid setting written on this control as plain text (PageWidth="true") arrives as a string,
    /// because the markup compiler cannot see the grid's parameter type. It is given that type here.
    /// </summary>
    private static object GridValue(string name, object value)
    {
        if (value is not string text || !GridParameterTypes.TryGetValue(name, out var type)) return value;
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type == typeof(object)) return value;
        try
        {
            if (type == typeof(bool)) return bool.Parse(text);
            if (type.IsEnum) return Enum.Parse(type, text, ignoreCase: true);
            if (typeof(IConvertible).IsAssignableFrom(type)) return Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException($"SQLTableControl: '{text}' is not a valid {type.Name} for the grid setting {name}.", ex);
        }
        return value;
    }

    // ── Loading ─────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        _loadedSql = Sql;
        _loadedKey = QueryKey;
        _cancel?.Cancel();
        _cancel?.Dispose();
        var cancel = _cancel = new CancellationTokenSource();
        var version = ++_loadVersion;
        var sql = Sql;
        _error = null;
        _loading = true;

        try
        {
            DataTable? table = null;
            if (!string.IsNullOrWhiteSpace(sql))
            {
                if (Executor != null) table = await Executor(sql, cancel.Token);
                else if (Database is { } database) table = await Task.Run(() => database.SqlExec(sql), cancel.Token);
                else throw new InvalidOperationException("SQLTableControl needs an Executor or a Database to run its query.");
            }
            if (version != _loadVersion) return;

            Build(sql ?? "", table);
            _loading = false;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // Superseded by a later query, or the control is gone.
            return;
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return;
            _loading = false;
            _error = ex;
            _result = null;
            _builtSql = null;
            _allRows = new();
            _viewRows = new();
            _searchIndex = null;
            _grid = null;
            _pivot = null;
            _resultVersion++;
        }

        // The host is told outside the try: a failure in its own handler is not a failed query.
        if (_error is { } failure)
        {
            RecordControlAction("QueryFailed", null, failure.GetType().Name);
            await OnError.InvokeAsync(failure);
        }
        else
        {
            RecordControlAction("QueryLoaded", null, $"Columns={_result!.Columns.Count}; Rows={_allRows.Count}");
            await OnLoaded.InvokeAsync(_result);
        }
    }

    private void Build(string sql, DataTable? table)
    {
        var shape = SqlQueryShape.Parse(sql);
        var columns = new List<SqlTableColumn>();
        var rows = new List<SqlTableRow>();

        if (table != null)
        {
            var count = table.Columns.Count;
            for (var i = 0; i < count; i++)
                columns.Add(DescribeColumn(table.Columns[i], i, count, shape));

            rows.Capacity = table.Rows.Count;
            foreach (DataRow source in table.Rows)
            {
                var row = new SqlTableRow(count);
                for (var i = 0; i < count; i++)
                {
                    var value = source[i];
                    row[columns[i].Field] = value == DBNull.Value ? null : value;
                }
                rows.Add(row);
            }
        }

        var sameQuery = string.Equals(sql, _builtSql, StringComparison.Ordinal);
        _builtSql = sql;
        _result = new SqlTableResult { Sql = sql, Shape = shape, Columns = columns, Rows = rows };
        _allRows = rows;
        _searchIndex = null;
        _grid = null;
        _pivot = null;
        _resultVersion++;
        if (!sameQuery) _search = "";
        ApplySearch();
        ApplyGrouping();
        BuildCrosstabSeed();

        // A re-run of the same query keeps the view the user chose.
        if (!sameQuery || (_view == SqlTableView.Crosstab && !CrosstabAvailable))
        {
            var wanted = InitialView ?? (shape.HasGroupBy && !_hasGrouping ? SqlTableView.Crosstab : SqlTableView.Table);
            _view = wanted == SqlTableView.Crosstab && CrosstabAvailable ? SqlTableView.Crosstab : SqlTableView.Table;
        }
        // The new result's views are built afresh; only the one showing is opened.
        _tableOpened = _crosstabOpened = false;
        MarkViewOpened();
    }

    private static SqlTableColumn DescribeColumn(DataColumn source, int ordinal, int count, SqlQueryShape shape)
    {
        var type = source.DataType;
        var sqlType = source.ExtendedProperties["SqlTypeName"] as string;
        var isFraction = type == typeof(decimal) || type == typeof(double) || type == typeof(float);
        var isWhole = type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte);
        var isMoney = string.Equals(sqlType, "money", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sqlType, "smallmoney", StringComparison.OrdinalIgnoreCase);
        var item = shape.ItemFor(ordinal, source.ColumnName, count);
        var numeric = isFraction || isWhole;

        return new SqlTableColumn
        {
            Ordinal = ordinal,
            Field = "Col" + ordinal.ToString(CultureInfo.InvariantCulture),
            Name = source.ColumnName,
            DataType = type,
            SqlTypeName = sqlType,
            IsNumeric = numeric,
            Format = isFraction ? (isMoney ? "C2" : "N2") : type == typeof(DateTime) ? "d" : "",
            TextAlign = numeric ? TextAlign.Right : TextAlign.Left,
            ColumnType = numeric ? ColumnType.Number : type == typeof(DateTime) ? ColumnType.Date : ColumnType.Text,
            SqlAggregate = item?.Aggregate,
            IsMeasure = numeric && item is { IsAggregate: true },
            // Summing the per-group counts gives the count of the wider group.
            Aggregate = item?.Aggregate switch
            {
                "MIN" => AggregateType.Min,
                "MAX" => AggregateType.Max,
                "AVG" => AggregateType.Average,
                _ => AggregateType.Sum
            }
        };
    }

    // ── Search ──────────────────────────────────────────────────────

    private Task OnSearchAsync(string? text) => SearchAsync(text);

    private void ApplySearch()
    {
        var words = _search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || _result == null)
        {
            _viewRows = _allRows;
            return;
        }

        _searchIndex ??= BuildSearchIndex(_result.Columns, _allRows);
        var kept = new List<SqlTableRow>();
        for (var i = 0; i < _allRows.Count; i++)
        {
            var text = _searchIndex[i];
            var matches = true;
            foreach (var word in words)
            {
                if (text.Contains(word, StringComparison.CurrentCultureIgnoreCase)) continue;
                matches = false;
                break;
            }
            if (matches) kept.Add(_allRows[i]);
        }
        _viewRows = kept;
    }

    /// <summary>Each row's values as shown and as stored, so "1,012.50" and "1012.5" both find the amount.</summary>
    private static string[] BuildSearchIndex(IReadOnlyList<SqlTableColumn> columns, List<SqlTableRow> rows)
    {
        var index = new string[rows.Count];
        var text = new System.Text.StringBuilder();
        for (var r = 0; r < rows.Count; r++)
        {
            text.Clear();
            foreach (var column in columns)
            {
                var value = rows[r][column.Field];
                if (value == null) continue;
                var plain = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
                text.Append(plain).Append('\u001f');
                if (column.Format.Length > 0 && value is IFormattable formattable)
                {
                    var shown = formattable.ToString(column.Format, CultureInfo.CurrentCulture);
                    if (!string.Equals(shown, plain, StringComparison.Ordinal)) text.Append(shown).Append('\u001f');
                }
            }
            index[r] = text.ToString();
        }
        return index;
    }

    // ── Table outline ───────────────────────────────────────────────

    private void ApplyGrouping()
    {
        _appliedGrouping = Grouping;
        _hasGrouping = false;
        _groupingExpanded = false;
        _groupColumns = new();
        _aggregateRows = new();
        _runColumn = null;
        _gridGroups.Clear();
        if (_result == null || Grouping is not { } grouping) return;
        var columns = _result.Columns;
        if (grouping.GroupColumn < 0 || grouping.GroupColumn >= columns.Count) return;

        _hasGrouping = true;
        _groupingExpanded = grouping.Expanded;
        var groupColumn = columns[grouping.GroupColumn];
        if (grouping.InQueryOrder)
        {
            _runColumn = groupColumn;
            NumberRuns(groupColumn.Field);
            _groupColumns = new List<string> { RunField };
        }
        else
        {
            _groupColumns = new List<string> { groupColumn.Field };
        }
        _gridGroups.AddRange(_groupColumns);
        if (grouping.Aggregate is not { } aggregate) return;

        var totals = TotalledColumns(grouping, columns)
            .Select(column => new AggregateColumn
            {
                Field = column.Field,
                Type = aggregate,
                Format = aggregate == AggregateType.Count ? "N0" : column.Format
            })
            .ToList();
        if (totals.Count == 0) return;

        _aggregateRows = new List<AggregateRow>
        {
            new() { ShowInGroupFooter = false, ShowInFooter = false, ShowInGroupHeader = true, Columns = totals }
        };
    }

    /// <summary>True while the table shows the query-order outline alone, as it opened.</summary>
    private bool RunOutlineOnly => _runColumn != null && _gridGroups.Count == 1 && _gridGroups[0] == RunField;

    /// <summary>
    /// False while the outline is grouped together with a column the user added: its group column then
    /// stands at the far left like any grouped column, and a second copy beside it would repeat it.
    /// </summary>
    private bool ShowRunValueColumn => RunOutlineOnly || !_gridGroups.Contains(RunField);

    private void OnGridGrouped(GroupEventArgs args)
    {
        if (!_gridGroups.Contains(args.Field)) _gridGroups.Add(args.Field);
    }

    private void OnGridUngrouped(GroupEventArgs args) => _gridGroups.Remove(args.Field);

    /// <summary>The row key that holds a row's run when the outline follows the query's order.</summary>
    public const string RunField = "OutlineRun";

    /// <summary>Numbers each run of consecutive rows that share a value, so the grid groups on the run.</summary>
    private void NumberRuns(string field)
    {
        var run = -1;
        object? last = null;
        foreach (var row in _allRows)
        {
            row.TryGetValue(field, out var value);
            if (run < 0 || !Equals(value, last))
            {
                run++;
                last = value;
            }
            row[RunField] = run.ToString("D9", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>A run's header shows the value its rows share, as the column shows it.</summary>
    private string RunHeaderText(string field, string key, IReadOnlyList<SqlTableRow> rows)
    {
        if (_runColumn is not { } column || !string.Equals(field, RunField, StringComparison.Ordinal) || rows.Count == 0)
            return key;
        rows[0].TryGetValue(column.Field, out var value);
        var text = value is IFormattable formattable && column.Format.Length > 0
            ? formattable.ToString(column.Format, CultureInfo.CurrentCulture)
            : Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
        // A blank value still names its group: the grid would show the run number for an empty caption.
        return string.IsNullOrWhiteSpace(text) ? "\u200B" : text;
    }

    private static IEnumerable<SqlTableColumn> TotalledColumns(SqlTableGrouping grouping, IReadOnlyList<SqlTableColumn> columns)
    {
        if (grouping.ValueColumn is { } value && value >= 0 && value < columns.Count)
            return new[] { columns[value] };
        return columns.Where(column => column.Ordinal != grouping.GroupColumn && column.IsNumeric);
    }

    // ── Crosstab ────────────────────────────────────────────────────

    private async Task OnViewPickedAsync(string value)
    {
        if (Enum.TryParse<SqlTableView>(value, out var view)) await SetViewAsync(view);
    }

    /// <summary>
    /// The crosstab a result opens with: the grouping column (or the query's first plain column)
    /// down the side, and the columns the query aggregates as values. The user rearranges from there.
    /// </summary>
    private void BuildCrosstabSeed()
    {
        var columns = _result?.Columns ?? Array.Empty<SqlTableColumn>();
        _pivotFields = columns.Select(column => column.Field).ToList();
        _pivotLabels = columns.ToDictionary(column => column.Field, column => column.Name, StringComparer.Ordinal);
        _pivotColumns = Array.Empty<string>();
        _pivotRows = Array.Empty<string>();
        _pivotValues = Array.Empty<PivotValueConfig>();
        _pivotDefaults = new(StringComparer.Ordinal);
        if (columns.Count == 0) return;

        var grouping = Grouping is { } g && g.GroupColumn >= 0 && g.GroupColumn < columns.Count ? g : null;
        var totalled = grouping is { Aggregate: not null }
            ? TotalledColumns(grouping, columns).ToList()
            : new List<SqlTableColumn>();

        foreach (var column in columns)
        {
            var aggregate = !column.IsNumeric ? AggregateType.Count
                : grouping?.Aggregate is { } hostAggregate && totalled.Contains(column) ? hostAggregate
                : column.Aggregate;
            _pivotDefaults[column.Field] = ValueConfig(column, aggregate);
        }

        var rowColumn = grouping != null
            ? columns[grouping.GroupColumn]
            : columns.FirstOrDefault(column => !column.IsMeasure) ?? columns[0];
        _pivotRows = new[] { rowColumn.Field };

        var values = (totalled.Count > 0 ? totalled : columns.Where(column => column.IsMeasure))
            .Where(column => column != rowColumn)
            .ToList();
        _pivotValues = values.Count > 0
            ? values.Select(column => _pivotDefaults[column.Field]).ToList()
            : new[] { new PivotValueConfig { Field = rowColumn.Field, Aggregation = AggregateType.Count, Format = "N0", Label = "Count" } };
    }

    private static PivotValueConfig ValueConfig(SqlTableColumn column, AggregateType aggregate) => new()
    {
        Field = column.Field,
        Aggregation = aggregate,
        Format = aggregate == AggregateType.Count ? "N0"
            : column.Format.Length > 0 ? column.Format
            : column.IsNumeric ? "N0" : "",
        Label = $"{aggregate} of {column.Name}"
    };
}
