using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
namespace Fx.ControlKit.Grid;
public partial class TreeGridControl<TValue>
{
    [Parameter] public bool AllowRowDragAndDrop { get; set; }
    [Parameter] public Func<TreeGridRowMoveEventArgs<TValue>, bool>? CanDrop { get; set; }
    [Parameter] public EventCallback<TreeGridRowMoveEventArgs<TValue>> RowMoving { get; set; }
    [Parameter] public EventCallback<TreeGridRowMoveEventArgs<TValue>> RowMoved { get; set; }
    private object? _dragRowId;
    private bool _moveDialog;
    private string _moveTarget = "";
    private TreeDropPosition _movePosition = TreeDropPosition.Inside;
    private IReadOnlyList<Choice<string>> MoveTargets => _flatNodes.Where(n => _selectedItem is null || !Equals(n.Id, RecordId(_selectedItem)))
        .Select(n => new Choice<string>(StateId(n.Id), new string(' ', n.Level * 2) + GetCellDisplayValue(n.Data, _columns.ElementAtOrDefault(TreeColumnIndex) ?? _columns[0]))).ToArray();
    public async Task<bool> MoveRowAsync(object sourceId, object targetId, TreeDropPosition position = TreeDropPosition.Inside)
    {
        if (!AllowRowDragAndDrop || _mutationBusy) return false;
        if (HasPendingChanges) return await OperationFailedAsync("Save or cancel pending edits before moving rows.");
        try
        {
            var source = _flatNodes.FirstOrDefault(n => Equals(n.Id, sourceId));
            var target = _flatNodes.FirstOrDefault(n => Equals(n.Id, targetId));
            if (source is null || target is null) return await OperationFailedAsync("The source or destination row no longer exists.");
            if (!Enum.IsDefined(position)) throw new ArgumentException("Unknown drop position.");
            if (position != TreeDropPosition.Inside && _sorts.Count > 0 && Equals(source.ParentId, target.ParentId))
                return await OperationFailedAsync("Clear sorting before reordering siblings.");
            // Validate cycles and permissions before starting a remote load or invoking the host.
            var args = new TreeGridRowMoveEventArgs<TValue>(source.Data, target.Data, position);
            if (CanDrop?.Invoke(args) == false) return await OperationFailedAsync("This move is not allowed.");
            TreeGridHierarchy.Move(LoadedRecords, sourceId, targetId, position, r => RecordId(r), RecordParent, CloneRecord, SetRecordParent);
            if (position == TreeDropPosition.Inside && NeedsChildLoad(target))
            { await SetNodeExpandedAsync(target, true); if (NeedsChildLoad(target)) return false; }
            var generation = _dataGeneration;
            _mutationBusy = true;
            try { await RowMoving.InvokeAsync(args); } finally { _mutationBusy = false; }
            if (args.Cancel || args.Error is not null) return await OperationFailedAsync(args.Error ?? "The move was cancelled.");
            if (generation != _dataGeneration || _disposed) return false;
            var records = TreeGridHierarchy.Move(LoadedRecords, sourceId, targetId, position, r => RecordId(r), RecordParent, CloneRecord, SetRecordParent).ToList();
            var moved = records.First(r => Equals(RecordId(r), sourceId));
            if (!await CommitRecordsAsync(records, [new(TreeGridChangeKind.Move, source.Data, moved)], false)) return false;
            if (position == TreeDropPosition.Inside && _flatNodes.FirstOrDefault(n => Equals(n.Id, targetId)) is {} parent) parent.IsExpanded = true;
            await RowMoved.InvokeAsync(new(moved, target.Data, position)); await InvokeAsync(StateHasChanged); return true;
        }
        catch (Exception ex) { return await OperationFailedAsync(ex.Message); }
    }
    public async Task<bool> IndentAsync(object id)
    {
        var node = _flatNodes.FirstOrDefault(n => Equals(n.Id, id)); if (node is null) return false;
        var previous = _flatNodes.TakeWhile(n => !Equals(n.Id, id)).LastOrDefault(n => Equals(n.ParentId, node.ParentId));
        return previous is not null && await MoveRowAsync(id, previous.Id!, TreeDropPosition.Inside);
    }
    public async Task<bool> OutdentAsync(object id)
    {
        var node = _flatNodes.FirstOrDefault(n => Equals(n.Id, id));
        return node?.ParentId is {} parent && await MoveRowAsync(id, parent, TreeDropPosition.After);
    }
    private async Task IndentSelectedAsync() { if (_selectedItem is not null) await IndentAsync(RecordId(_selectedItem)); }
    private async Task OutdentSelectedAsync() { if (_selectedItem is not null) await OutdentAsync(RecordId(_selectedItem)); }
    private void OpenMoveDialog()
    { _headerMenu = false; _operationError = null; _moveTarget = MoveTargets.FirstOrDefault()?.Value ?? ""; _moveDialog = _selectedItem is not null; }
    private async Task ApplyMoveDialogAsync()
    {
        var target = _flatNodes.FirstOrDefault(n => StateId(n.Id) == _moveTarget);
        if (_selectedItem is not null && target is not null && await MoveRowAsync(RecordId(_selectedItem), target.Id!, _movePosition)) _moveDialog = false;
    }
    private void StartRowDrag(TreeNode<TValue> node) { if (AllowRowDragAndDrop && !HasPendingChanges) _dragRowId = node.Id; }
    private async Task DropRowAsync(TreeNode<TValue> target, TreeDropPosition position)
    { var source = _dragRowId; _dragRowId = null; if (source is not null) await MoveRowAsync(source, target.Id!, position); }
}
