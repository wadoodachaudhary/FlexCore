namespace Fx.ControlKit.Charts;
public sealed class ArcGaugeControl : GaugeControl { protected override ChartType GaugeType => ChartType.ArcGauge; }
public sealed class CircularGaugeControl : GaugeControl { protected override ChartType GaugeType => ChartType.ArcGauge; protected override bool IsCircular => true; }
public sealed class LinearGaugeControl : GaugeControl { protected override ChartType GaugeType => ChartType.LinearGauge; }
public sealed class RadialGaugeControl : GaugeControl { protected override ChartType GaugeType => ChartType.RadialGauge; }
