namespace Fx.ControlKit.Grid;

internal interface IGridOwner
{
    void RegisterColumnsContainer(GridColumnsBase container);
    void NotifyColumnsChanged();
}
