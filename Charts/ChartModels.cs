namespace Fx.ControlKit.Charts;

public class ChartSeries
{
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public ChartType Type { get; set; } = ChartType.Bar;
    public List<ChartDataPoint> DataPoints { get; set; } = new();

    public ChartSeries() { }
    public ChartSeries(string name, ChartType type, IEnumerable<ChartDataPoint>? points = null, string? color = null)
    {
        Name = name;
        Type = type;
        Color = color;
        if (points != null)
            DataPoints.AddRange(points);
    }
}

public class ChartDataPoint
{
    public string Label { get; set; } = "";
    public double Value { get; set; }
    public double? Value2 { get; set; } // For bubble (size), box plot (high), financial (high), bullet (target), range (max)
    public double? Value3 { get; set; } // For box plot (Q1), financial (low)
    public double? Value4 { get; set; } // For box plot (Q3), financial (close)
    public double? Value5 { get; set; } // For box plot (median)
    public string? Color { get; set; }
    public string? Category { get; set; } // For grouping (heatmap row, gantt resource, sankey target)
    public DateTime? DateValue { get; set; } // For time-series

    // Financial Chart Helpers
    public double Open => Value;
    public double High => Value2 ?? Value;
    public double Low => Value3 ?? Value;
    public double Close => Value4 ?? Value;

    // Bullet Chart Helpers
    public double Actual => Value;
    public double Target => Value2 ?? Value;

    // Flow / Sankey Helpers
    public string Source => Label;
    public string Destination => Category ?? "";

    public ChartDataPoint() { }
    public ChartDataPoint(string label, double value, string? color = null)
    {
        Label = label;
        Value = value;
        Color = color;
    }

    public static ChartDataPoint Financial(string label, double open, double high, double low, double close, DateTime? date = null)
    {
        return new ChartDataPoint
        {
            Label = label,
            Value = open,
            Value2 = high,
            Value3 = low,
            Value4 = close,
            DateValue = date
        };
    }

    public static ChartDataPoint Bullet(string label, double actual, double target, string? category = null)
    {
        return new ChartDataPoint
        {
            Label = label,
            Value = actual,
            Value2 = target,
            Category = category
        };
    }

    public static ChartDataPoint Range(string label, double min, double max, string? color = null)
    {
        return new ChartDataPoint
        {
            Label = label,
            Value = min,
            Value2 = max,
            Color = color
        };
    }

    public static ChartDataPoint Sankey(string source, string target, double flow, string? color = null)
    {
        return new ChartDataPoint
        {
            Label = source,
            Category = target,
            Value = flow,
            Color = color
        };
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
