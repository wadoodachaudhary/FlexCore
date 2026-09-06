namespace Fx.ControlKit.Charts;
public sealed record GaugeRange(double From, double To, string Color, string? Label = null);
public sealed record GaugePointer(double Value, string Color = "#dc2626", string? Label = null);
public sealed record GaugeValueContext(double Value, double Min, double Max);
