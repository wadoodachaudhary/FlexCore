namespace Fx.ControlKit.Grid;

/// <summary>
/// Interface for GridControl to receive column registration from GridColumnsBase.
/// </summary>
internal interface IGridOwner
{
    void RegisterColumnsContainer(GridColumnsBase container);
    void NotifyColumnsChanged();

    /// <summary>Raised once per completed column-registration wave, AFTER the
    /// render that first showed the wave (OnAfterRender timing). Carries the
    /// container's column-set generation so duplicate completions coalesce.
    /// Bookkeeping only — implementations must not redraw from here, or the
    /// columns would slip into a second wire batch.</summary>
    void NotifyColumnsCompleted(int generation);
}
