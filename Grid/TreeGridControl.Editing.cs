using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;

namespace Fx.ControlKit.Grid;

public partial class TreeGridControl<TValue>
{
    [Inject] private IServiceProvider EditServices { get; set; } = default!;
    [Parameter] public EditSettings? EditSettingsRef { get; set; }
    [Parameter] public Func<TValue>? NewItemFactory { get; set; }
    [Parameter] public Func<TValue, TValue>? CloneFactory { get; set; }
    [Parameter] public Func<TValue, IEnumerable<ValidationResult>>? Validator { get; set; }
    [Parameter] public EventCallback<IEnumerable<TValue>> DataSourceChanged { get; set; }
    [Parameter] public EventCallback<TreeGridEditEventArgs<TValue>> EditStarting { get; set; }
    [Parameter] public EventCallback<TreeGridDataChangeEventArgs<TValue>> DataChanging { get; set; }
    [Parameter] public EventCallback<TreeGridDataChangeEventArgs<TValue>> DataChanged { get; set; }
    [Parameter] public bool ShowEditToolbar { get; set; } = true;
    [Parameter] public RenderFragment<EditContext>? EditValidatorTemplate { get; set; }
    private readonly Dictionary<object, TreeGridEditSession<TValue>> _editSessions = [];
    private readonly HashSet<object> _deletedRows = [];
    private readonly Dictionary<object, bool> _deletedExpansion = [];
    private List<TValue>? _localRecords;
    private IEnumerable<TValue>? _publishedRecords;
    private TreeGridEditSession<TValue>? _activeEdit;
    private string? _editField, _operationError;
    private bool _mutationBusy, _editDialog, _deleteDialog;
    private object[] _deleteRequest = [];
    private FormItemControl? _focusEditor;
    private bool _focusEditorPending;
    public bool HasPendingChanges => _editSessions.Count > 0 || _deletedRows.Count > 0;
    public EditContext? CurrentEditContext => _activeEdit?.Context;
    public IReadOnlyList<TValue> GetCurrentRecords() => LoadedRecords.ToArray();
    private IEnumerable<TValue> LoadedRecords => (_localRecords ?? DataSource ?? []).Concat(_loadedChildren.Values.SelectMany(c => c));
    private IEnumerable<TValue> PreviewRecords => IncludeAddedRows(LoadedRecords.Where(r => !_deletedRows.Contains(RecordId(r))));
    private IEnumerable<TValue> IncludeAddedRows(IEnumerable<TValue> existing)
    {
        var added = _editSessions.Values.Where(s => s.IsNew && !_deletedRows.Contains(s.Id)).Select(s => s.Draft);
        return EditSettingsRef?.NewRowPosition == NewRowPosition.Bottom ? existing.Concat(added) : added.Concat(existing);
    }
    private object RecordId(TValue row) => GetPropertyValue(row, IdMapping) ?? throw new ArgumentException("A tree record needs a non-null ID.");
    private object? RecordParent(TValue row) => GetPropertyValue(row, ParentIdMapping);
    private TreeGridEditSession<TValue>? Session(TreeNode<TValue> node) => node.Id is not null ? _editSessions.GetValueOrDefault(node.Id) : null;
    private TValue DisplayRecord(TreeNode<TValue> node) => Session(node) is {} session ? session.Draft : node.Data;
    private bool CanEditColumn(TreeGridColumn col) => col.AllowEditing && !col.IsPrimaryKey && col.Field != IdMapping && col.Field != ParentIdMapping
        && typeof(TValue).GetProperty(col.Field)?.SetMethod?.IsPublic == true;
    private bool IsBuiltInEditor(TreeNode<TValue> node, TreeGridColumn col) => _activeEdit?.Id.Equals(node.Id) == true
        && !_editDialog && CanEditColumn(col) && (EditSettingsRef?.Mode != EditMode.Batch || _editField == col.Field);
    private TValue CloneRecord(TValue source)
    {
        if (typeof(TValue).IsValueType) throw new InvalidOperationException("Tree editing requires reference-type records.");
        var copy = CloneFactory is not null ? CloneFactory(source) : JsonSerializer.Deserialize<TValue>(JsonSerializer.Serialize(source));
        if (copy is null || ReferenceEquals(copy, source)) throw new InvalidOperationException("Provide a CloneFactory that returns an independent record.");
        return copy;
    }
    private void SetRecordParent(TValue item, object? value)
    {
        var property = typeof(TValue).GetProperty(ParentIdMapping) ?? throw new ArgumentException("Unknown parent mapping.");
        if (property.SetMethod?.IsPublic != true) throw new InvalidOperationException("The parent ID property must be writable.");
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (value is not null && !type.IsInstanceOfType(value)) value = type == typeof(Guid) ? Guid.Parse(value.ToString()!) : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        if (value is null && property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
            throw new InvalidOperationException("Use a nullable parent ID to move a row to the root.");
        property.SetValue(item, value);
    }
    private TreeGridEditSession<TValue> CreateSession(TValue? original, TValue draft, bool isNew)
    {
        var context = new EditContext(draft!);
        return new() { Id = RecordId(draft), Original = original, Draft = draft, Context = context,
            Messages = new(context), AnnotationSubscription = context.EnableDataAnnotationsValidation(EditServices), IsNew = isNew };
    }
    private bool ValidateSession(TreeGridEditSession<TValue> session)
    {
        if (!session.Id.Equals(RecordId(session.Draft)) || !session.IsNew && !Equals(RecordParent(session.Original!), RecordParent(session.Draft)))
        { _operationError = "ID and parent ID cannot be changed through a cell editor. Use the move commands."; return false; }
        return session.Validate(Validator);
    }
    private async Task<bool> FinishActiveEditorAsync()
    {
        if (_activeEdit is null) return true;
        if (!ValidateSession(_activeEdit)) { await InvokeAsync(StateHasChanged); return false; }
        if (EditSettingsRef?.Mode != EditMode.Batch) return await SaveChangesAsync();
        _activeEdit = null; _editField = null; _editDialog = false;
        return true;
    }
    public async Task<bool> BeginEditAsync(object id, string? field = null)
    {
        if (_mutationBusy || EditSettingsRef?.AllowEditing != true) return false;
        if (_activeEdit?.Id.Equals(id) == true && (_editField == field || EditSettingsRef.Mode != EditMode.Batch)) return true;
        if (!await FinishActiveEditorAsync()) return false;
        var row = _flatNodes.FirstOrDefault(n => Equals(n.Id, id));
        if (row is null || !VisibleNodes.Contains(row)) return await OperationFailedAsync("The row is hidden. Clear the filter or expand its parent before editing.");
        var column = field is null ? VisibleColumns.FirstOrDefault(CanEditColumn) : _columns.FirstOrDefault(c => c.Field == field && CanEditColumn(c));
        if (column is null) return false;
        if (!_editSessions.TryGetValue(id, out var session))
        {
            session = CreateSession(row.Data, CloneRecord(row.Data), false);
            var args = new TreeGridEditEventArgs<TValue>(row.Data, session.Draft, session.Context, false);
            var generation = _dataGeneration;
            _mutationBusy = true;
            try { await EditStarting.InvokeAsync(args); } catch { session.Dispose(); throw; } finally { _mutationBusy = false; }
            if (generation != _dataGeneration || _disposed) { session.Dispose(); return false; }
            if (args.Cancel) { session.Dispose(); return false; }
            _editSessions.Add(id, session);
        }
        _activeEdit = session; _editField = column.Field; _editDialog = EditSettingsRef.Mode == EditMode.Dialog;
        _operationError = null; _page = VisibleNodes.ToList().IndexOf(row) / PageSize + 1;
        _focusEditorPending = true; await InvokeAsync(StateHasChanged); return true;
    }
    public async Task<bool> AddRecordAsync(TValue item, object? parentId = null)
    {
        if (_mutationBusy || EditSettingsRef?.AllowAdding != true || !await FinishActiveEditorAsync()) return false;
        if (_loadingChildren.Count > 0) return await OperationFailedAsync("Wait for child loading to finish before adding a row.");
        if (parentId is not null)
        {
            var parent = _flatNodes.FirstOrDefault(n => Equals(n.Id, parentId));
            if (parent is null) return await OperationFailedAsync("Unknown parent row.");
            if (NeedsChildLoad(parent)) { await SetNodeExpandedAsync(parent, true); if (NeedsChildLoad(parent)) return false; }
        }
        var draft = CloneRecord(item); SetRecordParent(draft, parentId);
        var id = RecordId(draft);
        if (PreviewRecords.Any(r => Equals(RecordId(r), id))) return await OperationFailedAsync("The new row ID already exists. NewItemFactory must provide a unique ID.");
        var session = CreateSession(default, draft, true);
        var args = new TreeGridEditEventArgs<TValue>(default, draft, session.Context, true);
        var generation = _dataGeneration; _mutationBusy = true;
        try { await EditStarting.InvokeAsync(args); } catch { session.Dispose(); throw; } finally { _mutationBusy = false; }
        if (generation != _dataGeneration || _disposed) { session.Dispose(); return false; }
        if (args.Cancel) { session.Dispose(); return false; }
        _editSessions.Add(id, session); _activeEdit = session;
        _editField = VisibleColumns.FirstOrDefault(CanEditColumn)?.Field;
        // A new record needs every required field, including in Batch mode.
        _editDialog = EditSettingsRef.Mode is EditMode.Dialog or EditMode.Batch;
        RebuildPreservingExpansion();
        if (parentId is not null) _flatNodes.First(n => Equals(n.Id, parentId)).IsExpanded = true;
        _page = Math.Max(0, VisibleNodes.ToList().FindIndex(n => Equals(n.Id, id))) / PageSize + 1;
        _focusEditorPending = true; await InvokeAsync(StateHasChanged); return true;
    }
    private async Task BeginEditFromUiAsync(object id, string? field = null)
    { try { await BeginEditAsync(id, field); } catch (Exception ex) { await OperationFailedAsync(ex.Message); } }
    private Task EditSelectedAsync() => _selectedItem is null ? Task.CompletedTask : BeginEditFromUiAsync(RecordId(_selectedItem));
    private async Task HandleCellClickAsync(TreeNode<TValue> node, TreeGridColumn col, int rowIndex)
    {
        var index = VisibleColumns.IndexOf(col);
        var activate = EditSettingsRef?.EditOnActiveCellClick == true && _selectedItem is not null && Equals(RecordId(_selectedItem), node.Id) && index == CurrentCellColumnIndex;
        if (EditSettingsRef is null) { _activeCellColumnIndex = index; return; }
        if (!await FinishActiveEditorAsync()) return;
        _activeCellColumnIndex = index;
        await HandleRowClick(node, rowIndex);
        if (activate && CanEditColumn(col)) await BeginEditFromUiAsync(node.Id!, col.Field);
    }
    private Task EditDialogVisibleChangedAsync(bool visible) => visible ? Task.CompletedTask : CancelEditAsync();
    private async Task AddFromToolbarAsync(bool child)
    {
        try { await AddRecordAsync(NewItemFactory is null ? Activator.CreateInstance<TValue>()! : NewItemFactory(), child && _selectedItem is not null ? RecordId(_selectedItem) : null); }
        catch (Exception ex) { await OperationFailedAsync(ex.Message); }
    }
    public async Task<bool> SaveChangesAsync()
    {
        if (_mutationBusy || !HasPendingChanges) return !HasPendingChanges;
        try
        {
            var valid = true;
            foreach (var session in _editSessions.Values) if (!_deletedRows.Contains(session.Id)) valid &= ValidateSession(session);
            if (!valid) { await InvokeAsync(StateHasChanged); return false; }
            var records = IncludeAddedRows(LoadedRecords.Where(r => !_deletedRows.Contains(RecordId(r)))
                .Select(r => _editSessions.TryGetValue(RecordId(r), out var s) ? s.Draft : r)).ToList();
            var changes = LoadedRecords.Where(r => _deletedRows.Contains(RecordId(r))).Select(r => new TreeGridRowChange<TValue>(TreeGridChangeKind.Delete, r, default))
                .Concat(_editSessions.Values.Where(s => !_deletedRows.Contains(s.Id)).Select(s => new TreeGridRowChange<TValue>(s.IsNew ? TreeGridChangeKind.Add : TreeGridChangeKind.Update, s.Original, s.Draft))).ToArray();
            return await CommitRecordsAsync(records, changes, clearEdits: true);
        }
        catch (Exception ex) { return await OperationFailedAsync(ex.Message); }
    }
    private async Task<bool> CommitRecordsAsync(List<TValue> records, IReadOnlyList<TreeGridRowChange<TValue>> changes, bool clearEdits)
    {
        if (_loadingChildren.Count > 0) return await OperationFailedAsync("Wait for child loading to finish before changing the hierarchy.");
        TreeGridHierarchy.Order(records, r => RecordId(r), RecordParent);
        _mutationBusy = true; _operationError = null;
        var generation = _dataGeneration;
        try
        {
            var args = new TreeGridDataChangeEventArgs<TValue>(changes); await DataChanging.InvokeAsync(args);
            if (args.Cancel || args.Error is not null) return await OperationFailedAsync(args.Error ?? "The change was cancelled.");
            if (_disposed || generation != _dataGeneration) return await OperationFailedAsync("The data source changed while the operation was pending. Retry with the current rows.");
            // Validate again after host callbacks; they may inspect or amend drafts.
            TreeGridHierarchy.Order(records, r => RecordId(r), RecordParent);
            if (clearEdits && _editSessions.Values.Where(s => !_deletedRows.Contains(s.Id)).Any(s => !ValidateSession(s))) return false;
            var selectedId = _selectedItem is null ? null : RecordId(_selectedItem);
            if (clearEdits) ClearEditSessions();
            _completedChildLoads.UnionWith(_loadedChildren.Keys);
            _loadedChildren.Clear(); _localRecords = records; _dataGeneration++;
            RebuildPreservingExpansion();
            _selectedItem = _flatNodes.FirstOrDefault(n => Equals(n.Id, selectedId)) is {} selected ? selected.Data : default;
            RefreshHierarchyChecks(); await PublishChecksAsync();
            _publishedRecords = records; await DataSourceChanged.InvokeAsync(records);
            await DataChanged.InvokeAsync(args); _pendingTreeFocus = true;
            await InvokeAsync(StateHasChanged); return true;
        }
        finally { _mutationBusy = false; }
    }
    public Task CancelChangesAsync()
    {
        if (_mutationBusy) return Task.CompletedTask;
        var expansion = new Dictionary<object, bool>(_deletedExpansion);
        ClearEditSessions(); _operationError = null; RebuildPreservingExpansion();
        foreach (var node in _flatNodes) if (node.Id is {} id && expansion.TryGetValue(id, out var expanded)) node.IsExpanded = expanded;
        _pendingTreeFocus = true;
        return InvokeAsync(StateHasChanged);
    }
    public Task CancelEditAsync()
    {
        if (_mutationBusy || _activeEdit is null) return Task.CompletedTask;
        // Escape cancels only this row's draft. The toolbar Cancel discards the batch.
        var id = _activeEdit.Id; _activeEdit.Dispose(); _editSessions.Remove(id);
        _activeEdit = null; _editDialog = false; _editField = null; _operationError = null;
        RebuildPreservingExpansion(); _pendingTreeFocus = true;
        return InvokeAsync(StateHasChanged);
    }
    private void ClearEditSessions()
    {
        foreach (var session in _editSessions.Values) session.Dispose();
        _editSessions.Clear(); _deletedRows.Clear(); _deletedExpansion.Clear(); _focusEditor = null; _focusEditorPending = false; _activeEdit = null; _editField = null; _editDialog = false;
    }
    private async Task CommitEditorAsync()
    {
        if (_activeEdit is null || !ValidateSession(_activeEdit)) return;
        if (EditSettingsRef?.Mode == EditMode.Batch) { _activeEdit = null; _editField = null; _editDialog = false; _pendingTreeFocus = true; }
        else await SaveChangesAsync();
        await InvokeAsync(StateHasChanged);
    }
    private async Task EditorKeyDownAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Tab" && EditSettingsRef?.Mode == EditMode.Batch && !_editDialog && _activeEdit is {} active)
        {
            var columns = VisibleColumns.Where(CanEditColumn).ToList();
            var rows = VisibleNodes.ToList();
            var at = rows.FindIndex(n => Equals(n.Id, active.Id)) * columns.Count + columns.FindIndex(c => c.Field == _editField) + (e.ShiftKey ? -1 : 1);
            if (at >= 0 && at < rows.Count * columns.Count) await BeginEditFromUiAsync(rows[at / columns.Count].Id!, columns[at % columns.Count].Field);
            else await CommitEditorAsync();
            return;
        }
        if (e.Key == "Escape") await CancelEditAsync();
        else if (e.Key == "Enter" && !e.ShiftKey) await CommitEditorAsync();
    }
    private async Task<bool> OperationFailedAsync(string message)
    {
        _operationError = message; if (!_disposed) await InvokeAsync(StateHasChanged); return false;
    }
    public async Task<bool> DeleteRecordsAsync(IEnumerable<object> ids)
    {
        if (_mutationBusy || EditSettingsRef?.AllowDeleting != true || !await FinishActiveEditorAsync()) return false;
        var keys = ids.ToArray();
        foreach (var id in keys)
        {
            if (!PreviewRecords.Any(r => Equals(RecordId(r), id))) continue;
            var branch = TreeGridHierarchy.DescendantIds(PreviewRecords, id, r => RecordId(r), RecordParent);
            foreach (var node in _flatNodes.Where(n => n.Id is not null && branch.Contains(n.Id))) _deletedExpansion.TryAdd(node.Id!, node.IsExpanded);
            _deletedRows.UnionWith(branch);
        }
        if (EditSettingsRef.Mode != EditMode.Batch) return await SaveChangesAsync();
        RebuildPreservingExpansion(); await InvokeAsync(StateHasChanged); return true;
    }
    private async Task RequestDeleteAsync()
    {
        _deleteRequest = GetCheckedRecords().Select(RecordId).ToArray();
        if (_deleteRequest.Length == 0 && _selectedItem is not null) _deleteRequest = [RecordId(_selectedItem)];
        if (_deleteRequest.Length == 0) return;
        if (EditSettingsRef?.ShowConfirmDialog == true) _deleteDialog = true;
        else await DeleteRecordsAsync(_deleteRequest);
    }
    private async Task ConfirmDeleteAsync() { _deleteDialog = false; await DeleteRecordsAsync(_deleteRequest); }
}
