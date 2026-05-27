namespace Fx.ControlKit.Layout;

/// <summary>
/// Runtime config cascaded from <see cref="DashboardLayoutControl"/> down to each
/// <see cref="DashboardLayoutPanel"/>. Panels use this to compute their grid
/// placement (column span, row span, aspect ratio).
/// </summary>
public sealed record DashboardLayoutConfig(int Columns, double[] CellSpacing, double CellAspectRatio);
