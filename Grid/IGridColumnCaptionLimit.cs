namespace Fx.ControlKit.Grid;

/// <summary>
/// Optional host service: the longest column caption the host can store. When it is
/// registered, the header "Rename this column..." box never accepts more than this; a
/// grid in an app that registers none keeps <see cref="GridControl{TValue}.ColumnRenameMaxLength"/>.
/// The smaller of the two applies. Return null for "no limit".
/// </summary>
public interface IGridColumnCaptionLimit
{
    /// <summary>Longest caption the host stores for <paramref name="field"/>, or null for no limit.</summary>
    ValueTask<int?> GetMaxLengthAsync(string field, CancellationToken cancellationToken = default);
}
