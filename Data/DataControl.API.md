# DataControl initial API

`DataControl` is a non-visual data-aware component in `Fx.ControlKit.Data`.
Rows are addressed with 1-based row numbers for the PowerBuilder/DataWindow-style methods.

## Properties

- `Table`
- `TableName`
- `Title`
- `Tag`
- `Index`
- `Filter`
- `Sort`
- `IsDirty`
- `HasRows`
- `RowCount`
- `TotalRowCount`
- `ColumnCount`
- `CurrentRow`
- `ModifiedCount`
- `InsertedCount`
- `DeletedCount`
- `Columns`
- `Dtypes`
- `Info`
- `Values`
- `Axes`
- `Ndim`
- `Size`
- `Shape`
- `Empty`
- `At`
- `Iat`
- `SelectedRows`

## Events

- `DataChanged`
- `RowChanged`

## Constructors and factories

- `DataControl()`
- `DataControl(DataTable table)`
- `DataControl(object? data, IEnumerable? index = null, IEnumerable? columns = null, Type? dtype = null, bool? copy = null)`
- `FromDataTable(DataTable table)`
- `DataFrame(object? data = null, IEnumerable? index = null, IEnumerable? columns = null, Type? dtype = null, bool? copy = null)`
- `FromRows(IEnumerable rows, string? tableName = null)`

## DataWindow-style methods

- `RetrieveAsync(Func<CancellationToken, Task<DataTable>> retrieve, CancellationToken cancellationToken = default)`
- `SetData(DataTable table, bool acceptChanges = true)`
- `Reset()`
- `ResetUpdate()`
- `GetChanges()`
- `RejectChanges()`
- `GetRow()`
- `SetRow(int rowNumber)`
- `GetCurrentRow()`
- `GetRowData(int rowNumber)`
- `GetItem(int rowNumber, string columnName)`
- `GetItem<T>(int rowNumber, string columnName)`
- `GetItemString(int rowNumber, string columnName)`
- `GetItemDecimal(int rowNumber, string columnName)`
- `GetItemDouble(int rowNumber, string columnName)`
- `GetItemInt32(int rowNumber, string columnName)`
- `GetItemDateTime(int rowNumber, string columnName)`
- `SetItem(int rowNumber, string columnName, object? value)`
- `InsertRow(int rowNumber = 0, IDictionary<string, object?>? values = null)`
- `DeleteRow(int rowNumber)`
- `RowsDiscard(int startRow, int count)`
- `RowsCopy(int startRow, int count, DataControl target, int targetRow = 0)`
- `RowsMove(int startRow, int count, DataControl target, int targetRow = 0)`
- `SelectRow(int rowNumber, bool selected = true)`
- `ClearSelection()`
- `IsSelected(int rowNumber)`
- `SelectedData()`
- `Describe(string expression)`
- `Modify(string propertyName, object? value)`
- `Find(string expression, int startRow = 1, int endRow = 0)`
- `SaveAs(DataControlSaveFormat format = DataControlSaveFormat.Csv)`

## DataFrame-style methods

- `Head(int count = 5)`
- `Tail(int count = 5)`
- `SelectDtypes(IEnumerable<Type>? include = null, IEnumerable<Type>? exclude = null)`
- `Astype(Type dtype)`
- `Astype(string columnName, Type dtype)`
- `Astype(IDictionary<string, Type> dtypes)`
- `ConvertDtypes()`
- `InferObjects()`
- `Copy(bool deep = true)`
- `SelectColumns(params string[] columnNames)`
- `DropColumns(params string[] columnNames)`
- `RenameColumn(string oldName, string newName)`
- `AddColumn(string columnName, Type dataType, object? defaultValue = null)`
- `DropNulls(params string[] columnNames)`
- `FillNulls(object value, params string[] columnNames)`
- `Query(string expression)`
- `Where(Func<DataRow, bool> predicate)`
- `SortBy(string columnName, bool descending = false)`
- `GroupBy(string groupColumn, string valueColumn, DataControlAggregate aggregate)`

## Control adapters

- `ToGridRows()`
- `ToDictionaries()`
- `ToExpandoRows()`
- `ToChartSeries(string labelColumn, params string[] valueColumns)`
- `ToChartBars(string labelColumn, string valueColumn, string? colorColumn = null)`
- `ToReportColumns()`
- `ToReportDefinition(string title)`
- `ToJson(JsonSerializerOptions? options = null)`
- `ToCsv()`

## Support types

- `DataControlColumn`
- `DataControlInfo`
- `DataControlShape`
- `DataControlAtIndexer`
- `DataControlIatIndexer`
- `DataControlAggregateResult`
- `DataControlRowChangedEventArgs`
- `DataControlAggregate`
- `DataControlSaveFormat`
