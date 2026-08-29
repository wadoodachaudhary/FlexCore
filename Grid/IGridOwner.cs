namespace Fx.ControlKit.Grid;

/// <summary>
/// Interface for GridControl to receive column registration from GridColumnsBase.
/// </summary>
internal interface IGridOwner
{
    void RegisterColumnsContainer(GridColumnsBase container);

    /// <summary>Raised from GridColumnsBase.Dispose. Reference-checked by the
    /// implementation: on a @key swap the NEW container has already overwritten
    /// the registration in the same batch, so the dying container's call is a
    /// no-op; only a columns block removed with NO replacement clears it.</summary>
    void UnregisterColumnsContainer(GridColumnsBase container);
    void NotifyColumnsChanged();

    /// <summary>Raised once per completed column-registration wave, AFTER the
    /// render that first showed the wave (OnAfterRender timing). Carries the
    /// container's column-set generation so duplicate completions coalesce.
    /// Bookkeeping only — implementations must not redraw from here, or the
    /// columns would slip into a second wire batch.</summary>
    void NotifyColumnsCompleted(int generation);
}
