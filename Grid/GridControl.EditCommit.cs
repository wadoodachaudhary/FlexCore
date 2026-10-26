namespace Fx.ControlKit.Grid;

public partial class GridControl<TValue>
{
    /// <summary>Includes browser-buffered editors that have not yet committed a value.</summary>
    public bool HasActiveEdit => _batchEditItem is not null || _isEditing || _typeAheadBuffer.Length > 0;

    /// <summary>Flushes the active editor and reports validation refusal to the calling form.</summary>
    public async Task<bool> TryEndEditAsync()
    {
        await EndEditAsync();
        return !HasActiveEdit;
    }
}
