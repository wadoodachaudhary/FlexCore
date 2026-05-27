namespace Fx.ControlKit.Charts;

public class ChartSeries
{
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public ChartType Type { get; set; } = ChartType.Bar;
    public List<ChartDataPoint> DataPoints { get; set; } = new();
}

public class ChartDataPoint
{
    public string Label { get; set; } = "";
    public double Value { get; set; }
    public double? Value2 { get; set; } // For bubble (size), box plot (high), range charts
    public double? Value3 { get; set; } // For box plot (Q1)
    public double? Value4 { get; set; } // For box plot (Q3)
    public double? Value5 { get; set; } // For box plot (median)
    public string? Color { get; set; }
    public string? Category { get; set; } // For grouping (heatmap row, gantt resource)
    public DateTime? DateValue { get; set; } // For time-series

    public ChartDataPoint() { }
    public ChartDataPoint(string label, double value, string? color = null)
    {
        Label = label;
        Value = value;
        Color = color;
    }
}

public class ChartAxis
{
    public string? Title { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    public string? Format { get; set; }
    public bool ShowGridLines { get; set; } = true;
    public int? TickCount { get; set; }
}

public class ChartLegend
{
    public bool Visible { get; set; } = true;
    public LegendPosition Position { get; set; } = LegendPosition.Bottom;
}

public enum LegendPosition { Top, Bottom, Left, Right }
