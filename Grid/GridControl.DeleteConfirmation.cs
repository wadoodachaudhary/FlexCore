namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    private List<(TValue Item, int RowIndex)>? _pendingRowDeletion;
    private bool _rowDeletionBusy;
    private string? _rowDeletionError;

    private async Task RequestRowDeletionAsync(IEnumerable<(TValue Item, int RowIndex)> records)
    {
        if (EditSettingsRef?.AllowDeleting != true || _pendingRowDeletion != null || _rowDeletionBusy)
            return;
        var snapshot = records.DistinctBy(record => record.Item).ToList();
        if (snapshot.Count == 0) return;
        if (EditSettingsRef.ShowConfirmDialog)
        {
            _rowDeletionError = null;
            _pendingRowDeletion = snapshot;
            await InvokeAsync(StateHasChanged);
        }
        else
        {
            await DeleteRowsCoreAsync(snapshot);
        }
    }

    private void CancelRowDeletion()
    {
        if (_rowDeletionBusy) return;
        _pendingRowDeletion = null;
        _rowDeletionError = null;
    }

    private async Task ConfirmRowDeletionAsync()
    {
        if (_pendingRowDeletion == null || _rowDeletionBusy) return;
        _rowDeletionBusy = true;
        try
        {
            // Recheck permissions: the host can change them while the dialog is open.
            if (EditSettingsRef?.AllowDeleting == true)
                await DeleteRowsCoreAsync(_pendingRowDeletion);
            _pendingRowDeletion = null;
        }
        catch (Exception ex)
        {
            // Keep the remaining records reviewable if a host callback fails mid-batch.
            _rowDeletionError = ex.Message;
        }
        finally
        {
            _rowDeletionBusy = false;
        }
    }

    private async Task DeleteRowsCoreAsync(List<(TValue Item, int RowIndex)> records)
    {
        var selectionChanged = false;
        try
        {
            foreach (var record in records.ToArray())
            {
                if (!UsesItemsProvider && DataSource is ICollection<TValue> existing && !existing.Contains(record.Item))
                {
                    records.Remove(record);
                    continue;
                }
                if (EventsRef?.RowDeleting.HasDelegate == true)
                {
                    var args = new RowEditEventArgs<TValue> { Data = record.Item, RowIndex = record.RowIndex };
                    await EventsRef.RowDeleting.InvokeAsync(args);
                    if (args.Cancel)
                    {
                        records.Remove(record);
                        continue;
                    }
                }

                if (!UsesItemsProvider && DataSource is ICollection<TValue> list)
                {
                    if (list.IsReadOnly)
                        throw new InvalidOperationException("The grid data source is read-only.");
                    list.Remove(record.Item);
                }
                selectionChanged |= _selectedItems.Remove(record.Item);
                // Remove before the post-delete callback so retry cannot delete twice.
                records.Remove(record);
                ClearPassViewMemos();
                InvalidateBlazorServerOptimizationCaches();
                ClearTransientSelectionState(clearRows: false);
                ClearKeyboardRangeSelectionAnchor();
                if (EventsRef?.RowDeleted.HasDelegate == true)
                    await EventsRef.RowDeleted.InvokeAsync(new RowEditEventArgs<TValue>
                    { Data = record.Item, RowIndex = record.RowIndex });
            }
        }
        finally
        {
            if (selectionChanged)
                await NotifySelectionChangedAsync(GridSelectionChangeSource.Programmatic);
            if (UsesItemsProvider)
                await ReloadItemsAsync();
            await InvokeAsync(StateHasChanged);
        }
    }
}
