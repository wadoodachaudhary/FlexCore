using System.Globalization;
using Fx.ControlKit.Reports;

namespace Fx.ControlKit.Charts;

// Shared print geometry for the chart control and the native PDF writer.
internal static class PrintedStageChart
{
    internal static ReportVectorImage Draw(ChartType type, IReadOnlyList<ChartSeries> series, string title,
        double width, double height, bool legend, string[] palette, bool reportGroupChart = false, ChartAxis? xAxis = null)
    {
        if (type == ChartType.Pie && series is { Count: 1 } && series[0].DataPoints is { Count: > 0 } zeroPoints
            && zeroPoints.All(point => point is not null && (point.IsEmpty || point.Value == 0)))
            return DrawZeroPie(series[0], title, width, height, legend);
        if (type is ChartType.Line or ChartType.Area) return DrawLine(type, series, title, width, height, legend, palette, reportGroupChart, xAxis);
        var image = new ReportVectorImage { Width = width, Height = height };
        void Text(double x, double y, string text, double size, string anchor = "middle", bool italic = false) =>
            image.Shapes.Add(new("Text", [x,y], "#000000", "none", 0, Text: new(text, "Arial", size, 700, Italic: italic, Anchor: anchor)));
        void Box(double x, double y, double w, double h, string fill, string stroke = "none") =>
            image.Shapes.Add(new("Rectangle", [x,y,w,h], fill, stroke, .65));
        var font = Math.Min(width / 40, height / 24);
        Box(0, 0, width, height, "#ffffff");
        Text(width / 2, font * 1.6, title, font * 1.5);
        if (series.Count == 0 || series[0].DataPoints.Count == 0) return image;
        var points = series[0].DataPoints;
        var plotWidth = width * (legend ? type == ChartType.Gauge ? .78 : .72 : 1);
        var yStart = height * .153;
        var plotHeight = height - yStart - height * .075;
        var entries = new List<(string Text, string Color)>();
        if (type == ChartType.Surface)
        {
            var values = series.SelectMany(group => group.DataPoints).Where(point => !point.IsEmpty)
                .Select(point => point.Value).ToArray();
            var minimum = Math.Min(0, values.DefaultIfEmpty().Min());
            var maximum = Math.Max(1, values.DefaultIfEmpty().Max());
            var axis = BarPrintScale.Range(minimum, maximum, targetIntervals: 8);
            var axisLabelSize = font * .867;

            // The wall projection includes its inset and depth. The exposed floor carries the category axes.
            double[] Project(double column, double row, double value)
            {
                var x = (1-column)*(1-row)*.164 + column*(1-row)*.523
                    + column*row*.837 + (1-column)*row*.510;
                var y = (1-column)*(1-row)*.591 + column*(1-row)*.836
                    + column*row*.578 + (1-column)*row*.384;
                var depth = (value-axis.Minimum)/(axis.Maximum-axis.Minimum);
                return [width*(x+depth*(x-.5)*.10),
                    height*(y-depth*(.236+.07*Math.Abs(x-.51)))];
            }
            double[] Floor(double column, double row) =>
                [width*((1-column)*(1-row)*.143 + column*(1-row)*.523
                    + column*row*.858 + (1-column)*row*.489),
                 height*((1-column)*(1-row)*.578 + column*(1-row)*.836
                    + column*row*.560 + (1-column)*row*.374)];
            void AxisLine(double[] start, double[] end) =>
                image.Shapes.Add(new("Polyline", [..start, ..end], "none", "#000000", .65));

            var floor = new[] { Floor(0,0), Floor(1,0), Floor(1,1), Floor(0,1) };
            image.Shapes.Add(new("Polygon", floor.SelectMany(point => point).ToArray(), "#ffffff", "#000000", .5));
            for (var side = 0; side < 2; side++)
            {
                var start = floor[side];
                var end = floor[side+1];
                image.Shapes.Add(new("Polygon", [..start, ..end, end[0], end[1]+height*.035, start[0], start[1]+height*.035],
                    side == 0 ? "#cccccc" : "#999999", "#000000", .35));
            }
            var walls = new[] {
                new[] { Project(0,0,axis.Minimum), Project(0,1,axis.Minimum), Project(0,1,axis.Maximum), Project(0,0,axis.Maximum) },
                new[] { Project(0,1,axis.Minimum), Project(1,1,axis.Minimum), Project(1,1,axis.Maximum), Project(0,1,axis.Maximum) } };
            for (var side = 0; side < walls.Length; side++)
                image.Shapes.Add(new("Polygon", walls[side].SelectMany(point => point).ToArray(),
                    side == 0 ? "#999999" : "#cccccc", "#000000", .5));

            for (var index = 0; index < axis.Ticks; index++)
            {
                var value = axis.Minimum + (axis.Maximum-axis.Minimum)*index/(axis.Ticks-1);
                var left = Project(0,0,value);
                var back = Project(0,1,value);
                var right = Project(1,1,value);
                image.Shapes.Add(new("Polyline", [..left, ..back, ..right], "none", "#000000", .5));
                var caption = value.ToString("G6", CultureInfo.CurrentCulture);
                Text(left[0]-axisLabelSize*3.4, left[1], caption, axisLabelSize, "end", italic: true);
                Text(right[0]+axisLabelSize*3.4, right[1], caption, axisLabelSize, "start", italic: true);
            }
            for (var row = 0; row < series.Count-1; row++)
            for (var column = 0; column < points.Count-1; column++)
            {
                if (column+1 >= series[row].DataPoints.Count || column+1 >= series[row+1].DataPoints.Count) continue;
                var a = series[row].DataPoints[column];
                var b = series[row].DataPoints[column+1];
                var c = series[row+1].DataPoints[column+1];
                var d = series[row+1].DataPoints[column];
                if (a.IsEmpty || b.IsEmpty || c.IsEmpty || d.IsEmpty) continue;
                var columns = Math.Max(1, points.Count-1);
                var rows = Math.Max(1, series.Count-1);
                image.Shapes.Add(new("Polygon", [..Project((double)column/columns,(double)row/rows,a.Value),
                    ..Project((double)(column+1)/columns,(double)row/rows,b.Value),
                    ..Project((double)(column+1)/columns,(double)(row+1)/rows,c.Value),
                    ..Project((double)column/columns,(double)(row+1)/rows,d.Value)], palette[row%palette.Length], "#000000", .4));
            }

            // Paint axes after the surface faces so their strokes and ticks remain visible.
            AxisLine(floor[0],floor[1]);
            AxisLine(floor[1],floor[2]);
            AxisLine(Project(0,0,axis.Minimum),Project(0,0,axis.Maximum));
            AxisLine(Project(1,1,axis.Minimum),Project(1,1,axis.Maximum));
            var dateLabels = ChartAxisLabelLayout.Select(points.Select(point => point.Label).ToArray(),
                index => Floor((double)index/Math.Max(1,points.Count-1),0)[0], axisLabelSize,
                bold: true, italic: true, axis: xAxis, maximumCount: 4);
            foreach (var index in dateLabels.Order())
            {
                var anchor = Floor((double)index/Math.Max(1,points.Count-1),0);
                AxisLine(anchor,[anchor[0]-axisLabelSize*.15,anchor[1]+axisLabelSize*.5]);
                Text(anchor[0],anchor[1]+axisLabelSize*2.75,points[index].Label,axisLabelSize,italic: true);
            }
            for (var index = 0; index < series.Count; index++)
            {
                var position = (index+.5)/series.Count;
                var tick = Floor(1,position);
                AxisLine(tick,[tick[0]+axisLabelSize*.15,tick[1]+axisLabelSize*.5]);
                var anchor = Project(1,position,axis.Minimum);
                Text(anchor[0]+axisLabelSize*.35,anchor[1]+axisLabelSize*2.3,series[index].Name,
                    axisLabelSize,"start",italic: true);
            }
            return image;
        }
        if (type == ChartType.Ohlc)
        {
            if (series.Count != 4) throw new InvalidDataException("OHLC charts require four measures in open, high, low, close order.");
            var values = series.SelectMany(group => group.DataPoints).Where(point => !point.IsEmpty)
                .Select(point => point.Value).ToArray();
            var minimum = Math.Min(0, values.DefaultIfEmpty().Min());
            var maximum = Math.Max(1, values.DefaultIfEmpty().Max());
            var step = Math.Pow(10, Math.Floor(Math.Log10(maximum-minimum)));
            var range = (Minimum: Math.Floor(minimum/step)*step, Maximum: Math.Ceiling(maximum/step)*step,
                Ticks: 1+(int)Math.Ceiling((maximum-minimum)/step));
            var axisFont = font * 1.18;
            const double labelAngle = -45;
            var captions = points.Select(point => point.Label).ToArray();
            var widest = captions.Max(caption => ChartAxisLabelLayout.MeasureWidth(caption, axisFont, true, true));
            var labelDepth = (widest + axisFont) / Math.Sqrt(2);
            var left = width*.10;
            var right = legend ? plotWidth - 3 : width*.96;
            var labelBand = Math.Min(labelDepth+axisFont*1.5, height*.45);
            var bottom = Math.Min(height*.80, height-labelBand);
            bottom = Math.Max(yStart+axisFont*2, bottom);
            double X(int index) => left+(index+.5)*(right-left)/points.Count;
            double Y(double value) => bottom-(value-range.Minimum)/(range.Maximum-range.Minimum)*(bottom-yStart);
            Box(left, yStart, right-left, bottom-yStart, "#d9d9d9", "#000000");
            for (var index = 0; index < range.Ticks; index++)
            {
                var value = range.Minimum+(range.Maximum-range.Minimum)*index/(range.Ticks-1);
                image.Shapes.Add(new("Polyline", [left,Y(value),right,Y(value)], "none", "#000000", .5));
                Text(left-3, Y(value)+axisFont*.3, value.ToString("C0"), axisFont, "end", italic: true);
            }
            for (var index = 0; index < points.Count; index++)
            {
                if (series.Any(group => index >= group.DataPoints.Count || group.DataPoints[index].IsEmpty)) continue;
                var x = X(index);
                var tick = Math.Min(3,(right-left)/points.Count*.3);
                var open = points[index].Value;
                var high = series[1].DataPoints[index].Value;
                var low = series[2].DataPoints[index].Value;
                var close = series[3].DataPoints[index].Value;
                image.Shapes.Add(new("Polyline", [x,Y(low),x,Y(high)], "none", palette[0], .7));
                image.Shapes.Add(new("Polyline", [x-tick,Y(open),x,Y(open)], "none", palette[0], .7));
                image.Shapes.Add(new("Polyline", [x,Y(close),x+tick,Y(close)], "none", palette[0], .7));
            }
            var visibleLabels = ChartAxisLabelLayout.Select(captions, X, axisFont, labelAngle, "end",
                bold: true, italic: true, axis: xAxis, minimumX: 0, maximumX: width,
                maximumDepth: height-bottom-axisFont*.9);
            var angle = labelAngle*Math.PI/180;
            foreach (var index in visibleLabels.Order())
                image.Shapes.Add(new("Text", [0,0], "#000000", "none", 0,
                    Transform: [Math.Cos(angle),Math.Sin(angle),-Math.Sin(angle),Math.Cos(angle),X(index),bottom+axisFont*.9],
                    Text: new(captions[index], "Arial", axisFont, 700, Italic: true, Anchor: "end")));
            if (legend)
            {
                var caption = series[0].Name;
                var keyFont = Math.Min(axisFont, (width-plotWidth-8) / Math.Max(2, caption.Length*.6+1.5));
                var keyY = (yStart+bottom)/2;
                Box(plotWidth, keyY-keyFont, width-plotWidth-2, keyFont*2, "#ffffff", "#000000");
                Box(plotWidth+3, keyY-keyFont*.35, keyFont*.7, keyFont*.7, palette[0]);
                Text(plotWidth+5+keyFont*.7, keyY+keyFont*.32, caption, keyFont, "start", italic:true);
            }
            return image;
        }
        if (type == ChartType.HorizontalBar)
        {
            var low = Math.Min(0, points.Min(p => p.Value));
            var high = Math.Max(0, points.Max(p => p.Value));
            if (high == low) high = low + 1;
            double X(double value) => width * (.17 + .77 * (value-low)/(high-low));
            var rowHeight = height * .68 / points.Count;
            for (var i = 0; i <= 8; i++)
            {
                var value = low + (high-low)*i/8;
                image.Shapes.Add(new("Polyline", [X(value), yStart, X(value), yStart + rowHeight*points.Count], "none", "#000000", .55));
                Text(X(value), yStart + rowHeight*points.Count + font, value.ToString("0.##"), font*.75);
            }
            string[] colors = ["#0066ff", "#ff0000", "#ffff00"];
            for (var i = 0; i < points.Count; i++)
            {
                var p = points[i]; var y = yStart + i*rowHeight;
                Text(X(low)-2,y+rowHeight*.53,p.Label,font*.8,"end");
                if (!p.IsEmpty) Box(Math.Min(X(0),X(p.Value)),y+rowHeight*.1,Math.Abs(X(p.Value)-X(0)),rowHeight*.8,p.Color ?? colors[i%colors.Length],"#000000");
            }
            return image;
        }
        if (type == ChartType.Radar)
        {
            var count = points.Count;
            plotWidth = width * (legend ? .72 : 1);
            var cx = plotWidth / 2; var cy = yStart + plotHeight / 2;
            var radius = Math.Min(plotWidth * .33, plotHeight * .4);
            var all = series.SelectMany(s => s.DataPoints).Where(p => !p.IsEmpty).Select(p => p.Value).ToArray();
            var min = Math.Min(0, all.DefaultIfEmpty().Min());
            var max = Math.Max(1, all.DefaultIfEmpty().Max());
            var magnitude = Math.Pow(10, Math.Floor(Math.Log10((max-min)/6)));
            var step = Math.Ceiling((max-min)/6/magnitude)*magnitude;
            min = Math.Floor(min/step)*step; max = Math.Ceiling(max/step)*step;
            double[] Point(int i, double distance)
            {
                var angle = (i+.5)*2*Math.PI/count;
                return [cx+Math.Cos(angle)*distance,cy-Math.Sin(angle)*distance];
            }
            for (var tick = min+step; tick <= max+step*.01; tick += step)
            {
                var distance = (tick-min)/(max-min)*radius;
                image.Shapes.Add(new("Ellipse", [cx-distance,cy-distance,2*distance,2*distance],"none","#000000",.6));
                Text(cx+distance,cy+font,tick.ToString("G3"),font*.65);
            }
            for (int i=0;i<count;i++)
            {
                image.Shapes.Add(new("Polyline",[cx,cy,..Point(i,radius)],"none","#000000",.6));
                var pos = Point(i,radius+font*1.5);
                Text(pos[0],pos[1]+font*.3,points[i].Label,font*.78);
            }
            for (int i=0;i<series.Count;i++)
            {
                var color = series[i].Color ?? palette[i%palette.Length];
                var path = new List<double>();
                for (int j=0;j<series[i].DataPoints.Count;j++)
                {
                    var pt = series[i].DataPoints[j];
                    if (pt.IsEmpty) continue;
                    var xy = Point(j,(pt.Value-min)/(max-min)*radius); path.AddRange(xy);
                    Box(xy[0]-1.5,xy[1]-1.5,3,3,color);
                }
                if (path.Count >= 4) image.Shapes.Add(new("Polygon",path.ToArray(),"none",color,.85));
                entries.Add((series[i].Name,color));
            }
        }
        else if (type is ChartType.Scatter or ChartType.Bubble)
        {
            var coordinates = series.SelectMany((group, index) => group.DataPoints.Where(point => !point.IsEmpty)
                .Select(point => (X: double.Parse(point.Label, CultureInfo.InvariantCulture), Y: point.Value, Size: point.Value2 ?? 0,
                    Color: point.Color ?? group.Color ?? palette[index % palette.Length]))).ToArray();
            if (coordinates.Length == 0) return image;
            static (double Min, double Max, double Step) Axis(double low, double high, bool includeZero)
            {
                if (low == high)
                {
                    var span = Math.Max(1, Math.Abs(low) * .5);
                    low -= span; high += includeZero ? span * .1 : span;
                }
                if (includeZero) { low = Math.Min(low, 0); high = Math.Max(high, 0); }
                var raw = (high - low) / 6;
                var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
                var step = magnitude * (raw / magnitude <= 1 ? 1 : raw / magnitude <= 2 ? 2 : raw / magnitude <= 5 ? 5 : 10);
                return (Math.Floor(low / step) * step, Math.Ceiling(high / step) * step, step);
            }
            var xx = Axis(coordinates.Min(p => p.X), coordinates.Max(p => p.X), false);
            var yy = Axis(coordinates.Min(p => p.Y), coordinates.Max(p => p.Y), true);
            double left = width * .08, right = width * (legend ? .82 : .98), bottom = height * .91;
            double X(double value) => left + (value - xx.Min) / (xx.Max - xx.Min) * (right - left);
            double Y(double value) => bottom - (value - yy.Min) / (yy.Max - yy.Min) * (bottom - yStart);
            string Tick(double value, double step) => step >= 1000 ? (value / 1000).ToString("0.##") + "K" : value.ToString("G4");
            for (int i = 0; i <= Math.Round((xx.Max - xx.Min) / xx.Step); i++)
            {
                var v = xx.Min + i * xx.Step;
                image.Shapes.Add(new("Polyline", [X(v), yStart, X(v), bottom], "none", "#000000", .55));
                Text(X(v), bottom + font, Tick(v, xx.Step), font * .83);
            }
            for (int i = 0; i <= Math.Round((yy.Max - yy.Min) / yy.Step); i++)
            {
                var v = yy.Min + i * yy.Step;
                image.Shapes.Add(new("Polyline", [left, Y(v), right, Y(v)], "none", "#000000", .55));
                Text(left - 2, Y(v) + font * .3, Tick(v, yy.Step), font * .83, "end");
            }
            var maxSize = coordinates.Max(p => Math.Abs(p.Size));
            foreach (var point in coordinates)
            {
                var radius = type == ChartType.Scatter ? font * .33 : 1 + (coordinates.Length == 1 || maxSize == 0
                    ? 0 : Math.Sqrt(Math.Abs(point.Size) / maxSize) * Math.Min(right-left, bottom-yStart) * .08);
                image.Shapes.Add(new(type == ChartType.Scatter ? "Rectangle" : "Ellipse",
                    [X(point.X)-radius, Y(point.Y)-radius, 2*radius, 2*radius], point.Color, "none", 0));
            }
            plotWidth = width * .86;
            entries.AddRange(series.Select((group, i) => (group.Name, group.Color ?? palette[i % palette.Length])));
        }
        else if (type is ChartType.Pie or ChartType.Donut)
        {
            var total = points.Where(p => !p.IsEmpty).Sum(p => p.Value);
            var cx = plotWidth / 2;
            var cy = yStart + plotHeight / 2;
            var radius = Math.Min(plotWidth, plotHeight) * .43;
            var angle = -Math.PI / 2;
            foreach (var point in points.Where(p => !p.IsEmpty))
            {
                var share = total > 0 ? point.Value / total : 0;
                var color = point.Color ?? palette[entries.Count % palette.Length];
                var segments = Math.Max(1, (int)Math.Ceiling(120 * share));
                var sector = new List<double> { cx, cy };
                for (var i = 0; i <= segments; i++)
                {
                    var theta = angle + 2 * Math.PI * share * i / segments;
                    sector.Add(cx + radius * Math.Cos(theta));
                    sector.Add(cy + radius * Math.Sin(theta));
                }
                if (share > 0) image.Shapes.Add(new("Polygon", sector.ToArray(), color, "none", 0));
                angle += 2 * Math.PI * share;
                entries.Add(($"{point.Label}   {share * 100:F1}%", color));
            }
            if (type == ChartType.Donut)
            {
                image.Shapes.Add(new("Ellipse", [cx-radius*.68,cy-radius*.68,radius*1.36,radius*1.36], "#ffffff", "none", 0));
                Text(cx, cy + font*.3, total.ToString("C0", CultureInfo.CurrentCulture), font);
            }
            entries.Add(($"Total:   {(total > 0 ? 100 : 0):F1}%", "none"));
        }
        else if (type == ChartType.Funnel)
        {
            if (series.Count != 1 || points.Any(p => p.Value < 0))
                throw new InvalidDataException("A funnel requires one nonnegative measure.");
            var sum = points.Where(p => !p.IsEmpty).Sum(p => p.Value);
            var fraction = 0d;
            var center = legend ? width * .265 : width / 2;
            double HalfWidth(double depth) => plotWidth * Math.Max(.035, .325 * (1 - depth));
            foreach (var point in points.Where(p => !p.IsEmpty))
            {
                var share = sum == 0 ? 0 : point.Value / sum;
                var color = point.Color ?? palette[entries.Count % palette.Length];
                var end = fraction + share;
                var y = yStart + fraction * plotHeight;
                var bottom = yStart + end * plotHeight;
                var polygon = new List<double> { center - HalfWidth(fraction), y, center + HalfWidth(fraction), y };
                const double neck = 1 - .035 / .325;
                if (fraction < neck && end > neck) polygon.AddRange([center + HalfWidth(neck), yStart + neck * plotHeight]);
                polygon.AddRange([center + HalfWidth(end), bottom, center - HalfWidth(end), bottom]);
                if (fraction < neck && end > neck) polygon.AddRange([center - HalfWidth(neck), yStart + neck * plotHeight]);
                if (share > 0) image.Shapes.Add(new("Polygon", polygon.ToArray(), color, "none", 0));
                entries.Add(($"{point.Label}   {share * 100:F1}%", color));
                fraction = end;
            }
            entries.Add(($"Total:   {fraction * 100:F1}%", "none"));
        }
        else
        {
            var maximum = Math.Max(1, series.SelectMany(s => s.DataPoints).Where(p => !p.IsEmpty).Select(p => p.Value).DefaultIfEmpty().Max());
            var minimum = Math.Min(0, series.SelectMany(s => s.DataPoints).Where(p => !p.IsEmpty).Select(p => p.Value).DefaultIfEmpty().Min());
            var columns = Math.Clamp((int)Math.Round(Math.Sqrt(points.Count * plotWidth / (plotHeight * 2))), 1, points.Count);
            var rows = (int)Math.Ceiling((double)points.Count / columns);
            var cellWidth = plotWidth / columns;
            var cellHeight = plotHeight / rows;
            var radius = Math.Min(cellWidth * .43, cellHeight * .65);
            var tickFont = Math.Min(font * .84, radius * .18);
            for (var i = 0; i < points.Count; i++)
            {
                var cx = cellWidth * (i % columns + .5);
                var cy = yStart + cellHeight * (i / columns + .73);
                double[] Point(double fraction, double r) => [cx - r * Math.Cos(fraction * Math.PI), cy - r * Math.Sin(fraction * Math.PI)];
                void Line(double[] a, double[] b, string color, double thickness) =>
                    image.Shapes.Add(new("Polyline", [..a, ..b], "none", color, thickness));
                foreach (var arc in new[] { radius, radius * .80 })
                    image.Shapes.Add(new("Polyline", Enumerable.Range(0, 41).SelectMany(k => Point(k / 40d, arc)).ToArray(), "none", "#000000", .55));
                for (var k = 0; k <= 8; k++)
                {
                    var f = k / 8d;
                    Line(Point(f, radius * .80), Point(f, radius), "#000000", .45);
                    var label = Point(f, radius * .65);
                    Text(label[0], label[1], (minimum + (maximum - minimum) * f).ToString("0.##", CultureInfo.CurrentCulture), tickFont);
                }
                for (var s = 0; s < series.Count; s++)
                {
                    if (i >= series[s].DataPoints.Count || series[s].DataPoints[i].IsEmpty) continue;
                    Line([cx,cy], Point((series[s].DataPoints[i].Value - minimum) / (maximum - minimum), radius * .82),
                        palette[s % palette.Length], 1.9);
                }
                image.Shapes.Add(new("Ellipse", [cx-1.5,cy-1.5,3,3], "#000000","none",0));
                Text(cx, cy + tickFont * 1.5, points[i].Label, tickFont);
            }
            entries.AddRange(series.Select((s,i) => (s.Name, palette[i % palette.Length])));
        }
        if (legend)
        {
            var legendFont = Math.Min(font * .84, Math.Min(plotHeight / (entries.Count + 2), (width-plotWidth) / Math.Max(1, entries.Max(e => e.Text.Length) * .56 + 2)));
            var line = legendFont * 1.32;
            var y = yStart + (plotHeight - entries.Count * line) / 2;
            Box(plotWidth, y-line*.3, width-plotWidth-1, (entries.Count+.6)*line, "#ffffff", "#000000");
            foreach (var entry in entries)
            {
                if (entry.Color != "none") Box(plotWidth+2, y+line*.2, legendFont*.65, legendFont*.65, entry.Color);
                Text(plotWidth+3+legendFont*.65, y+line*.8, entry.Text, legendFont, "start");
                y += line;
            }
        }
        return image;
    }

    // A zero-only pie has no sector; keep its compact percentage key without inventing a slice.
    internal static ReportVectorImage DrawZeroPie(ChartSeries series, string title, double width, double height, bool legend)
    {
        var image = new ReportVectorImage { Width = width, Height = height };
        var font = Math.Min(height / 32.064, width / 16.5);
        void Box(double x, double y, double w, double h, string fill, string stroke = "none") =>
            image.Shapes.Add(new("Rectangle", [x, y, w, h], fill, stroke, .12));
        void Text(string value, double x, double y, string anchor = "start") =>
            image.Shapes.Add(new("Text", [x, y], "#000000", "none", 0, Text: new(value, "Arial", font, Anchor: anchor)));
        Box(0, 0, width, height, "#ffffff");
        if (!string.IsNullOrEmpty(title)) Text(title, width / 2, font * 1.6, "middle");
        if (!legend) return image;
        var left = width - font * 15.037;
        var top = height * .01191;
        var keyWidth = font * 12.613; var keyHeight = font * 4.725;
        Box(left + font, top + font / 3, keyWidth, keyHeight, "#000000");
        Box(left, top, keyWidth, keyHeight, "#ffffff", "#000000");
        var pointColor = series.DataPoints.FirstOrDefault(point => point is { IsEmpty: false })?.Color;
        var color = PrintedBarChart3D.NormalizeColor(pointColor) ?? PrintedBarChart3D.NormalizeColor(series.Color) ?? "#0060ff";
        Box(left + font * 1.4755, top + font * 1.3364, font * .9044, font * .9307, color, "#000000");
        Text("0.0%", left + font * 10.565, top + font * 2.1478, "end");
        image.Shapes.Add(new("Polyline", [left + font * 3.0035, top + font * 2.482, left + font * 10.543, top + font * 2.482], "none", "#000000", .12));
        Text("Total:", left + font * 3.0035, top + font * 3.508);
        Text("100.0%", left + font * 10.565, top + font * 3.508, "end");
        return image;
    }

    private static ReportVectorImage DrawLine(ChartType type, IReadOnlyList<ChartSeries> series, string title, double width, double height, bool legend, string[] palette, bool reportGroupChart, ChartAxis? xAxis)
    {
        var image = new ReportVectorImage { Width = width, Height = height };
        image.Shapes.Add(new("Rectangle", [0,0,width,height], "#ffffff", "none", 0));
        void Text(string label, double x, double y, double size, double angle = 0, string anchor = "middle", bool heading = false)
        {
            var radians = angle * Math.PI / 180;
            image.Shapes.Add(new("Text", [0,0], heading && !legend && !reportGroupChart ? "#ff0000" : "#000000", "none", 0,
                Transform: [Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), x, y],
                Text: new(label, "Arial", size, heading || legend ? 700 : 400, Anchor: anchor)));
        }
        void Line(double x, double y, double endX, double endY, string color, double stroke) =>
            image.Shapes.Add(new("Polyline", [x,y,endX,endY], "none", color, stroke));
        var font = Math.Min(height / 25, width / 55);
        Text(title, width/2, height*.08, font*1.6, heading: true);
        var values = series.SelectMany(s => s.DataPoints).Where(p => !p.IsEmpty).Select(p => p.Value).ToArray();
        if (!series.Any(s => s.DataPoints.Count > 0)) return image;
        var low = Math.Min(0, values.DefaultIfEmpty().Min()); var high = Math.Max(0, values.DefaultIfEmpty().Max());
        if (low == high) { low = -6; high = 6; }
        if (reportGroupChart && values.Length > 0 && low != high)
        {
            var range = BarPrintScale.Range(low,high);
            low=range.Minimum; high=range.Maximum;
        }
        var left = width * (reportGroupChart ? .045 : .105); var right = width * (reportGroupChart ? .85 : legend ? .64 : .945);
        var top = height * (reportGroupChart ? .133 : .19); var bottom = height * (reportGroupChart ? .747 : .785);
        double Y(double v) => bottom - (v-low)/(high-low)*(bottom-top);
        for (int i=0;i<=6;i++)
        {
            var v = low + (high-low)*i/6;
            Line(left,Y(v),right,Y(v),"#000000",.6);
            Text(v.ToString("0.##", CultureInfo.CurrentCulture),left-4,Y(v)+2,font*(reportGroupChart ? 1 : .75),anchor: "end");
        }
        Line(left,top,left,bottom,"#000000",.7); Line(right,top,right,bottom,"#000000",.7);
        var labels = series.OrderByDescending(s => s.DataPoints.Count).First().DataPoints;
        double X(int i) => left + (i+.5)*(right-left)/labels.Count;
        var captions = labels.Select(point => DateTime.TryParse(point.Label, out var date)
            && point.Label.Contains(':') && date.TimeOfDay == TimeSpan.Zero ? date.ToShortDateString() : point.Label).ToArray();
        var labelAngle = legend || reportGroupChart ? 0 : -45;
        var labelAnchor = legend || reportGroupChart ? "middle" : "end";
        var labelSize = font*(reportGroupChart ? 1 : .85);
        var visibleLabels = ChartAxisLabelLayout.Select(captions, X, labelSize, labelAngle, labelAnchor,
            bold: legend, axis: xAxis);
        foreach (var index in visibleLabels.Order())
            Text(captions[index],X(index),bottom+font*(reportGroupChart ? 2 : 1),labelSize,labelAngle,labelAnchor);
        var axes = title.Split(" / ", 2);
        if (axes.Length == 2)
        {
            Text(axes[0],font*1.5,(top+bottom)/2,font*.88,-90);
            Text(axes[1],(left+right)/2,height-2,font*.88);
        }
        for (int s=0;s<series.Count;s++)
        {
            var color = series[s].Color ?? (s == 0 && !reportGroupChart ? "#0060ff" : palette[s%palette.Length]);
            if (legend)
            {
                var boxTop = (top+bottom)/2 + (s-series.Count*.5)*font*2;
                image.Shapes.Add(new("Rectangle", [right+3,boxTop,width-right-5,font*2], "#ffffff","#000000",.6));
                Line(right+5,boxTop+font,right+10,boxTop+font,color,1);
                Text(series[s].Name,right+12,boxTop+font*1.35,font*.85,anchor:"start");
            }
            var path = new List<double>();
            foreach (var (point,index) in series[s].DataPoints.Select((p,i)=>(p,i)))
            {
                if (point.IsEmpty) continue;
                path.Add(X(index)); path.Add(Y(point.Value));
            }
            if (type == ChartType.Area && path.Count >= 2)
            {
                if (path.Count == 2) Line(path[0], Y(0), path[0], path[1], color, 1);
                else image.Shapes.Add(new("Polygon", [path[0], Y(0), ..path, path[^2], Y(0)], color, color, 1));
            }
            else if (path.Count >= 4) image.Shapes.Add(new("Polyline",path.ToArray(),"none",color,1));
            else if (path.Count == 2) image.Shapes.Add(new("Ellipse",[path[0]-1,path[1]-1,2,2],color,"none",0));
        }
        return image;
    }
}
