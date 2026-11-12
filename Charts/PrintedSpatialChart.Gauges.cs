using Fx.ControlKit.Reports;

namespace Fx.ControlKit.Charts;

internal static partial class PrintedSpatialChart
{
    private static void CategoryGauges(Canvas canvas, ReportAnalysisDefinition definition, IReadOnlyList<ChartSeries> series)
    {
        if (series.Count == 0 || series[0].DataPoints.Count == 0) return;
        var points = series[0].DataPoints;
        if (definition.SpatialStyle!.GaugeArc is { } arc && !definition.ShowLegend)
        {
            SweepGauges(canvas, definition, points, arc);
            return;
        }
        var frame = definition.SpatialStyle.Frame;
        var top = (frame?.Top ?? .12) * canvas.Height;
        var bottom = (frame?.Bottom ?? .92) * canvas.Height;
        var plotHeight = bottom - top;
        var font = canvas.Font;
        var margin = font;
        var legendFont = Math.Min(font, plotHeight / (points.Count + 1) / 1.42);
        var widestLabel = points.Max(p => ChartAxisLabelLayout.MeasureWidth(p.Label, 1));
        var legendWidth = Math.Min(canvas.Width * .4, (widestLabel + 4) * legendFont);
        legendFont = Math.Min(legendFont, legendWidth / (widestLabel + 4));
        var legendLeft = canvas.Width - margin - legendWidth;
        var plotRight = definition.ShowLegend ? legendLeft - margin * .5 : canvas.Width - margin;
        var plotWidth = plotRight - margin;
        var preferredRows = Math.Max(1, (int)Math.Floor(plotHeight / (4.25 * font)));
        var columns = (points.Count + preferredRows - 1) / preferredRows;
        var rows = (points.Count + columns - 1) / columns;
        var cellWidth = plotWidth / columns;
        var cellHeight = plotHeight / rows;
        var radius = Math.Min(cellWidth * .42, cellHeight * .412);
        var values = points.Where(p => !p.IsEmpty).Select(p => p.Value).ToArray();
        var range = BarPrintScale.Range(Math.Min(0, values.DefaultIfEmpty().Min()), Math.Max(1, values.DefaultIfEmpty().Max()), 8);
        var rowHeight = legendFont * 1.42;
        var boxHeight = (points.Count + .6) * rowHeight;
        var boxTop = (top + bottom - boxHeight) / 2;
        if (definition.ShowLegend)
            canvas.Box(legendLeft, boxTop, legendWidth, boxHeight, "#ffffff", "#000000");

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var color = canvas.Color(index);
            var cx = margin + (index % columns + .5) * cellWidth;
            var cy = top + (index / columns + .62) * cellHeight;
            var fraction = point.IsEmpty ? (double?)null : Math.Clamp((point.Value - range.Minimum) / (range.Maximum - range.Minimum), 0, 1);
            Dial(canvas, cx, cy, radius, fraction, color);
            var captionSize = Math.Min(Math.Min(font * 1.22, cellHeight * .29),
                cellWidth / Math.Max(1, ChartAxisLabelLayout.MeasureWidth(point.Label, 1, bold: true)));
            canvas.Text(cx, cy + captionSize * 1.22, point.Label, captionSize, bold: true);
            if (!definition.ShowLegend) continue;
            var y = boxTop + (index + .4) * rowHeight;
            canvas.Box(legendLeft + legendFont, y + .2 * legendFont, legendFont, legendFont, color);
            canvas.Text(legendLeft + 2.6 * legendFont, y + legendFont, point.Label, legendFont, "start");
        }
    }

    private static void SweepGauges(Canvas canvas, ReportAnalysisDefinition definition, IReadOnlyList<ChartDataPoint> points, ReportGaugeArc arc)
    {
        var frame = definition.SpatialStyle!.Frame ?? new ReportPlotFrame(0, 0, 1, 1);
        var top = frame.Top * canvas.Height;
        var height = (frame.Bottom - frame.Top) * canvas.Height;
        var width = (frame.Right - frame.Left) * canvas.Width;
        var cols = Math.Min(points.Count, Math.Max(1, (int)Math.Ceiling(Math.Sqrt(points.Count * width / height))));
        var rows = (points.Count + cols - 1) / cols;
        var radius = Math.Min(width / cols, height / rows) * .435;
        var values = points.Where(p => !p.IsEmpty).Select(p => p.Value).ToArray();
        var range = BarPrintScale.Range(Math.Min(0, values.DefaultIfEmpty().Min()), Math.Max(1, values.DefaultIfEmpty().Max()), arc.Intervals);
        for (var i = 0; i < points.Count; i++)
        {
            var cx = frame.Left * canvas.Width + (i % cols + .5) * width / cols;
            var cy = top + (i / cols + .5) * height / rows;
            (double X, double Y) At(double t, double r)
            {
                var angle = ((t - 1) * arc.End - t * arc.Start) * Math.PI / 180;
                return (cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));
            }
            foreach (var r in new[] { radius, radius * .83 })
            {
                var xy = new List<double>();
                for (var j = 0; j <= 80; j++)
                {
                    var pos = At(j / 80d, r);
                    xy.Add(pos.X); xy.Add(pos.Y);
                }
                canvas.Image.Shapes.Add(new("Polyline", xy.ToArray(), "none", "#000000", .55));
            }
            var size = Math.Min(canvas.Font * 1.71, radius * .13);
            for (var j = 0; j < range.Ticks; j++)
            {
                var fraction = (double)j / (range.Ticks - 1);
                var outer = At(fraction, radius);
                var inner = At(fraction, radius * .83);
                canvas.Line(inner.X, inner.Y, outer.X, outer.Y);
                var caption = At(fraction, radius * .72);
                var tick = range.Minimum + (range.Maximum - range.Minimum) * fraction;
                canvas.Text(caption.X, caption.Y + size * .34, tick.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture), size);
            }
            var point = points[i];
            if (!point.IsEmpty)
            {
                var tip = At(Math.Clamp((point.Value - range.Minimum) / (range.Maximum - range.Minimum), 0, 1), radius);
                canvas.Image.Shapes.Add(new("Polyline", [cx, cy, tip.X, tip.Y], "none", canvas.Color(i), 1.7));
                canvas.Image.Shapes.Add(new("Ellipse", [cx-1.8, cy-1.8, 3.6, 3.6], "#000000", "none", 0));
            }
            canvas.Text(cx, cy + size * 1.14, point.Label, size, bold: true, color: "#000080");
        }
    }

    private static void Dial(Canvas canvas, double cx, double cy, double radius, double? fraction, string color)
    {
        (double X, double Y) Tick(double position, double length) =>
            (cx - length * Math.Cos(position * Math.PI), cy - length * Math.Sin(position * Math.PI));
        var face = new List<double>();
        for (var i = 0; i <= 40; i++)
        {
            var point = Tick(i / 40d, radius);
            face.Add(point.X); face.Add(point.Y);
        }
        canvas.Poly(face.ToArray(), "#ffffff");
        for (var i = 0; i <= 8; i++)
        {
            var inner = Tick(i / 8d, radius * .45);
            var outer = Tick(i / 8d, radius);
            canvas.Line(inner.X, inner.Y, outer.X, outer.Y);
        }
        if (fraction is { } value)
        {
            var tip = Tick(value, radius);
            var dx = Math.Sin(value * Math.PI) * radius * .12;
            var dy = -Math.Cos(value * Math.PI) * radius * .12;
            canvas.Poly([cx - dx, cy - dy, tip.X, tip.Y, cx + dx, cy + dy], color, color);
        }
        canvas.Image.Shapes.Add(new("Ellipse", [cx - radius * .22, cy - radius * .22, radius * .44, radius * .44], "#000000", "none", 0));
    }
}
