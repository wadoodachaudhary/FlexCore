namespace Fx.ControlKit.Grid;

/// <summary>A direction for a pivot dimension or a rendered aggregate column.</summary>
public sealed record PivotSortDescriptor(string Field, SortDirection Direction);
