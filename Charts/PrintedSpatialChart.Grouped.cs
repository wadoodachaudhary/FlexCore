using System.Globalization;
using Fx.ControlKit.Reports;

namespace Fx.ControlKit.Charts;

internal static partial class PrintedSpatialChart
{
    // One ring per category, with slice colors shared across all categories and the key.
    private static void MultipleRings(Canvas g, ReportAnalysisDefinition d, IReadOnlyList<ChartSeries> series)
    {
        var count = series.Select(s => s.DataPoints.Count).DefaultIfEmpty().Max();
        if (count == 0) return;
        var totals = new double[count];
        foreach (var s in series)
            for (var i = 0; i < s.DataPoints.Count; i++)
                if (!s.DataPoints[i].IsEmpty) totals[i] += s.DataPoints[i].Value;
        var max = totals.Max();
        var rows = (count + 1) / 2;
        var cols = Math.Min(count, 2);
        var frame = d.SpatialStyle!.Frame ?? new ReportPlotFrame(.015, .13, d.ShowLegend ? .82 : .98, .93);
        var plotWidth = g.Width * (frame.Right - frame.Left);
        var rowHeight = g.Height * (frame.Bottom - frame.Top) / rows;
        var fullRadius = Math.Min(rowHeight * .435, plotWidth / cols * .42);
        for (var i = 0; i < count; i++)
        {
            var cx = g.Width * frame.Left + (i % cols + .5) * plotWidth / cols;
            var cy = g.Height * frame.Top + (i / cols + .5) * rowHeight;
            var radius = d.SpatialStyle!.ProportionalRings && max > 0 ? fullRadius * totals[i] / max : fullRadius;
            var angle = -Math.PI / 2;
            for (var j = 0; j < series.Count && totals[i] > 0; j++)
            {
                var points = series[j].DataPoints;
                if (i >= points.Count || points[i].IsEmpty || points[i].Value <= 0) continue;
                var sweep = 2 * Math.PI * points[i].Value / totals[i];
                var steps = Math.Max(2, (int)Math.Ceiling(sweep * 16));
                var xy = new List<double>();
                void Arc(int k, double r)
                {
                    var t = angle + sweep * k / steps;
                    xy.Add(cx + r * Math.Cos(t)); xy.Add(cy + r * Math.Sin(t));
                }
                for (var k = 0; k <= steps; k++) Arc(k, radius);
                for (var k = steps; k >= 0; k--) Arc(k, radius * .65);
                g.Poly(xy.ToArray(), g.Color(j), "none");
                angle += sweep;
            }
            var font = Math.Min(g.Font * 1.4, fullRadius * .63);
            g.Text(cx, cy + font * .32, totals[i].ToString("C0", CultureInfo.CurrentCulture), font, bold: true);
        }
        if (d.ShowLegend)
        {
            var names = series.Select(s => double.TryParse(s.Name, out var n) ? n.ToString("F2", CultureInfo.CurrentCulture) : s.Name ?? "").ToArray();
            var left = g.Width * frame.Right + g.Font;
            g.Legend(names, left, g.Height * (frame.Top + frame.Bottom) / 2, g.Width - left, rowHeight * rows * .82);
        }
    }

    private static void DepthArea(Canvas g, ReportAnalysisDefinition d, IReadOnlyList<ChartSeries> series)
    {
        var style = d.SpatialStyle!;
        var points = series.FirstOrDefault()?.DataPoints;
        if (points is null || points.Count == 0) return;
        var values = series.SelectMany(s => s.DataPoints).Where(p => !p.IsEmpty).Select(p => p.Value).ToArray();
        var range = BarPrintScale.Range(Math.Min(0, values.DefaultIfEmpty().Min()), Math.Max(1, values.DefaultIfEmpty().Max()), 6);
        var frame = style.Frame ?? new ReportPlotFrame(.14, .16, d.ShowLegend ? .76 : .94, .75);
        var left = g.Width * frame.Left; var right = g.Width * frame.Right;
        var top = g.Height * frame.Top; var bottom = g.Height * frame.Bottom;
        var dx = g.Font * 1.25; var dy = -dx * .62;
        double X(int i) => left + (i + .5) / points.Count * (right - left);
        double Y(double v) => bottom - (v - range.Minimum) / (range.Maximum - range.Minimum) * (bottom - top);
        g.Box(left + dx, top + dy, right - left, bottom - top, "none", "#000000");
        for (var i = 0; i < range.Ticks; i++)
        {
            var v = range.Minimum + i * (range.Maximum - range.Minimum) / (range.Ticks - 1);
            var y = Y(v); g.Line(left, y, left + dx, y + dy); g.Line(left + dx, y + dy, right + dx, y + dy);
            g.Text(left - g.Font * .7, y + g.Font * .3, v.ToString("0.##", CultureInfo.CurrentCulture), g.Font, "end");
        }
        g.Line(left, top, left, bottom); g.Line(left, bottom, right, bottom);
        for (var j = series.Count - 1; j >= 0; j--)
        {
            var band = new List<double>();
            void Flush()
            {
                if (band.Count >= 4)
                {
                    var face = new List<double> { band[0], Y(0) };
                    face.AddRange(band); face.AddRange([band[^2], Y(0)]);
                    g.Poly(face.ToArray(), g.Color(j), "none");
                }
                band.Clear();
            }
            for (var i = 0; i < series[j].DataPoints.Count; i++)
            {
                var p = series[j].DataPoints[i];
                if (p.IsEmpty) { Flush(); continue; }
                var x = X(i); var y = Y(p.Value);
                if (band.Count > 0)
                    g.Poly([band[^2], band[^1], band[^2] + dx, band[^1] + dy, x + dx, y + dy, x, y], Shade(g.Color(j), .70), "none");
                band.Add(x); band.Add(y);
            }
            Flush();
        }
        var stride = Math.Max(1, (int)Math.Ceiling(points.Count * g.Font * 1.9 / (right - left)));
        for (var i = 0; i < points.Count; i += stride) g.Text(X(i), bottom + g.Font, points[i].Label, g.Font * .87, "end", angle: -45);
        g.Text((left + right) / 2, g.Height * .97, style.CategoryTitle, g.Font, bold: true);
        g.Text(g.Font * 2, (top + bottom) / 2, style.ValueTitle, g.Font, bold: true, angle: -90);
        if (d.ShowLegend) g.Legend(d.Measures.Select(Caption).ToArray(), right + dx + g.Font, (top + bottom) / 2, g.Width - right - dx - g.Font, g.Height * .65);
    }
}
