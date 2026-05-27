namespace Fx.ControlKit.Grid;

/// <summary>
/// Interface for GridControl to receive column registration from GridColumnsBase.
/// </summary>
internal interface IGridOwner
{
    void RegisterColumnsContainer(GridColumnsBase container);
    void NotifyColumnsChanged();
}
