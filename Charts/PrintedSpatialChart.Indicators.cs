using System.Globalization;
using Fx.ControlKit.Reports;

namespace Fx.ControlKit.Charts;

internal static partial class PrintedSpatialChart
{
    // Each summary is a needle on the same scale for this category.
    private static void SummaryGauge(Canvas g, ReportAnalysisDefinition d, IReadOnlyList<ChartSeries> series)
    {
        var count = series.Select(s => s.DataPoints.Count).DefaultIfEmpty().Max();
        if (count == 0) return;
        var largest = series.SelectMany(s => s.DataPoints).Where(p => !p.IsEmpty).Select(p => p.Value).DefaultIfEmpty().Max();
        // Keep headroom at the end of the sweep for the highest needle.
        var scale = BarPrintScale.Range(0, Math.Max(1, largest) * 1.02, 8);
        var max = scale.Maximum;
        var step = max / (scale.Ticks - 1);
        var cols = (int)Math.Ceiling(Math.Sqrt(count));
        var rows = (count + cols - 1) / cols;
        var radius = Math.Min(g.Width / cols * .465, g.Height / rows * .72);
        for (var group = 0; group < count; group++)
        {
            var cx = (group % cols + .5) * g.Width / cols;
            var cy = (group / cols + .64) * g.Height / rows;
            (double X, double Y) At(double v, double r) => (cx - r * Math.Cos(Math.PI * v / max), cy - r * Math.Sin(Math.PI * v / max));
            foreach (var r in new[] { radius, radius * .976 })
            {
                var path = new List<double>();
                for (var k = 0; k <= 64; k++) { var p = At(max * k / 64, r); path.Add(p.X); path.Add(p.Y); }
                g.Image.Shapes.Add(new("Polyline", path.ToArray(), "none", "#000000", .5));
            }
            for (var k = 0; k < scale.Ticks; k++)
            {
                var v = k * step; var a = At(v, radius); var b = At(v, radius * .94); var label = At(v, radius * .86);
                g.Line(a.X, a.Y, b.X, b.Y);
                var size = Math.Min(g.Font * 2.25, radius * .107);
                g.Text(label.X, label.Y + size * .32, v.ToString("0.##", CultureInfo.CurrentCulture), size);
            }
            for (var j = 0; j < series.Count; j++)
            {
                if (group >= series[j].DataPoints.Count || series[j].DataPoints[group].IsEmpty) continue;
                var tip = At(Math.Clamp(series[j].DataPoints[group].Value, 0, max), radius);
                g.Image.Shapes.Add(new("Polyline", [cx, cy, tip.X, tip.Y], "none", g.Color(j), 1.25));
            }
            g.Image.Shapes.Add(new("Ellipse", [cx - 2, cy - 2, 4, 4], "#000000", "none", 0));
        }
    }

    private static void StatusLines(Canvas g, ReportAnalysisDefinition d, IReadOnlyList<ChartSeries> series)
    {
        var points = series.FirstOrDefault()?.DataPoints;
        if (points is null || points.Count == 0) return;
        var style = d.SpatialStyle!;
        var horizontal = style.Horizontal;
        var labelSize = g.Font * 1.75;
        var frame = style.Frame;
        var left = g.Width * (frame?.Left ?? (horizontal ? .38 : .05));
        var right = g.Width * (frame?.Right ?? .98);
        var top = g.Height * (frame?.Top ?? .09);
        var bottom = g.Height * (frame?.Bottom ?? .78);
        var values = series.SelectMany(s => s.DataPoints).Where(p => !p.IsEmpty).Select(p => p.Value).ToArray();
        var scale = Range(Math.Min(0, values.DefaultIfEmpty().Min()), Math.Max(1, values.DefaultIfEmpty().Max()), true);
        double Value(double v) => (v - scale.Min) / (scale.Max - scale.Min);
        double Category(int i) => (i + .5) / points.Count;
        if (!horizontal) g.Box(left, top, right - left, bottom - top, "none", "#000000");
        for (var i = 0; i < points.Count; i++)
        {
            if (horizontal)
            {
                var y = top + Category(i) * (bottom - top); g.Line(left, y, right, y);
                g.Text(left - g.Font, y + g.Font * .35, points[i].Label, labelSize, "end");
            }
            else g.Text(left + Category(i) * (right - left), bottom + g.Font * .65, points[i].Label, labelSize, "end", angle: -90);
        }
        var ticks = (int)Math.Round((scale.Max - scale.Min) / scale.Step);
        for (var j = 0; j <= ticks; j++)
        {
            var v = scale.Min + j * scale.Step;
            var str = v.ToString(horizontal ? "0.0" : "0.##", CultureInfo.CurrentCulture);
            if (horizontal) g.Text(left + Value(v) * (right - left), bottom + g.Font * .6, str, labelSize, "start", angle: 90);
            else
            {
                var y = bottom - Value(v) * (bottom - top); g.Line(left, y, right, y);
                g.Text(left - g.Font * .7, y + g.Font * .4, str, labelSize, "end");
            }
        }
        for (var s = 0; s < series.Count; s++)
        {
            var path = new List<double>(); var markers = new List<(double X, double Y)>();
            for (var i = 0; i < series[s].DataPoints.Count; i++)
            {
                var p = series[s].DataPoints[i]; if (p.IsEmpty) continue;
                var x = left + (horizontal ? Value(p.Value) : Category(i)) * (right - left);
                var y = horizontal ? top + Category(i) * (bottom - top) : bottom - Value(p.Value) * (bottom - top);
                path.Add(x); path.Add(y); markers.Add((x, y));
            }
            if (!horizontal && path.Count >= 4) g.Image.Shapes.Add(new("Polyline", path.ToArray(), "none", g.Color(s), .7));
            foreach (var (x, y) in markers)
            {
                var radius = g.Font * (horizontal ? .85 : .55);
                if (s % 2 == 1) g.Image.Shapes.Add(new("Ellipse", [x - radius, y - radius, radius * 2, radius * 2], g.Color(s), horizontal ? "#000000" : "none", .5));
                else
                {
                    var star = new List<double>();
                    for (var k = 0; k < 8; k++) { var a = (k + 1) * Math.PI / 4; var r = radius * (k % 2 == 0 ? 1 : .28); star.Add(x + Math.Cos(a) * r); star.Add(y + Math.Sin(a) * r); }
                    g.Poly(star.ToArray(), g.Color(s), horizontal ? "#000000" : "none");
                }
            }
        }
    }
}
