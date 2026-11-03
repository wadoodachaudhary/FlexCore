using System.Globalization;
using Fx.ControlKit.Reports;

namespace Fx.ControlKit.Charts;

// Classic category keys and plot geometry shared by preview and native PDF output.
internal static class PrintedCategoryBarChart
{
    private static readonly string[] DefaultPalette = ["#0060ff", "#ff0000", "#ffff00", "#40ff00", "#00ffff", "#ff00ff"];
    internal static bool Supports(ChartType type) => type is ChartType.Bar or ChartType.GroupedBar or ChartType.StackedBar or ChartType.StackedBar100;

    internal static ReportVectorImage Draw(ChartType type, IReadOnlyList<ChartSeries> series, string title,
        double width, double height, bool legend, string[]? palette = null, ChartAxis? xAxis = null,
        ChartAxis? yAxis = null, bool showAxisLabels = true)
    {
        if (!Supports(type)) throw new NotSupportedException("This chart type has no classic category-bar adapter.");
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Chart dimensions must be finite and positive.");
        var image = new ReportVectorImage { Width = width, Height = height };
        var groups = series?.Where(group => group is not null).ToArray() ?? [];
        var count = groups.Select(group => group.DataPoints?.Count ?? 0).DefaultIfEmpty().Max();
        var colors = palette?.Select(PrintedBarChart3D.NormalizeColor).Where(color => color is not null).Select(color => color!).ToArray();
        if (colors is not { Length: > 0 }) colors = DefaultPalette;
        var font = Math.Min(width / 48, height / 31.69735);
        ChartDataPoint? Point(int group, int category) => groups[group].DataPoints is { } points && category < points.Count ? points[category] : null;
        static bool Present(ChartDataPoint? point) => point is { IsEmpty: false } && double.IsFinite(point.Value);
        var labels = Enumerable.Range(0, count).Select(category => groups.Select((_, group) => Point(group, category))
            .FirstOrDefault(point => point is not null)?.Label ?? (category + 1).ToString(CultureInfo.CurrentCulture)).ToArray();
        string Color(int group, int category) => PrintedBarChart3D.NormalizeColor(Point(group, category)?.Color)
            ?? PrintedBarChart3D.NormalizeColor(groups[group].Color) ?? colors[(groups.Length == 1 ? category : group) % colors.Length];
        var entries = new List<(string Label, string Color)>();
        if (legend && groups.Length == 1) entries.AddRange(labels.Select((label, category) => (label, Color(0, category))));
        else if (legend) entries.AddRange(groups.Select((group, index) => (group.Name ?? "", PrintedBarChart3D.NormalizeColor(group.Color) ?? colors[index % colors.Length])));
        var showLegend = entries.Count > 0;
        void Box(double x, double y, double w, double h, string fill, string stroke = "none", double thickness = .12) =>
            image.Shapes.Add(new("Rectangle", [x, y, w, h], fill, stroke, thickness));
        void Line(double x1, double y1, double x2, double y2, double stroke = .36) =>
            image.Shapes.Add(new("Polyline", [x1, y1, x2, y2], "none", "#000000", stroke));
        void Text(string value, double x, double y, double size, string anchor = "start", double angle = 0)
        {
            var radians = angle * Math.PI / 180;
            image.Shapes.Add(new("Text", [0, 0], "#000000", "none", 0,
                Transform: [Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), x, y],
                Text: new(value, "Arial", size, Anchor: anchor)));
        }
        Box(0, 0, width, height, "#ffffff");
        if (!string.IsNullOrEmpty(title)) Text(title, width / 2, height * .08, font * 1.6, "middle");

        var stacked = type is ChartType.StackedBar or ChartType.StackedBar100;
        var values = new double[groups.Length, count];
        var present = new bool[groups.Length, count];
        var heights = new List<double>();
        for (var category = 0; category < count; category++)
        {
            var largest = groups.Select((_, group) => Point(group, category)).Where(Present)
                .Select(point => Math.Abs(point!.Value)).DefaultIfEmpty().Max();
            var divisor = type == ChartType.StackedBar100 && largest > 0
                ? groups.Select((_, group) => Point(group, category)).Where(Present).Sum(point => Math.Abs(point!.Value) / largest) : 1;
            var positive = 0d; var negative = 0d; var categoryPresent = false;
            for (var group = 0; group < groups.Length; group++)
            {
                var point = Point(group, category);
                if (!Present(point)) continue;
                var value = type == ChartType.StackedBar100 && largest > 0 ? point!.Value / largest / divisor * 100 : point!.Value;
                values[group, category] = value; present[group, category] = true; categoryPresent = true;
                if (!stacked) heights.Add(value);
                else if (value >= 0) positive += value; else negative += value;
            }
            if (!double.IsFinite(positive) || !double.IsFinite(negative)) throw new InvalidDataException("Stacked chart totals exceed the finite numeric range.");
            if (stacked && categoryPresent)
            {
                if (positive > 0 || negative == 0) heights.Add(positive);
                if (negative < 0) heights.Add(negative);
            }
        }
        var minimum = heights.DefaultIfEmpty().Min(); var maximum = heights.DefaultIfEmpty().Max();
        (double Minimum, double Maximum, int Ticks) range;
        if (minimum == 0 && maximum == 0) range = (-6, 6, 7);
        else if (type == ChartType.StackedBar100) range = (minimum < 0 ? -100 : 0, maximum > 0 ? 100 : 0, minimum < 0 && maximum > 0 ? 11 : 6);
        else if (minimum == maximum)
            range = BarPrintScale.Range(minimum - Math.Abs(minimum) * .5, maximum + Math.Abs(maximum) * .5, targetIntervals: 8);
        else
        {
            range = BarPrintScale.Range(Math.Min(0, minimum), Math.Max(0, maximum), targetIntervals: 8);
            if (range.Maximum == maximum)
                range = (range.Minimum, range.Maximum + (range.Maximum - range.Minimum) / (range.Ticks - 1), range.Ticks + 1);
        }
        var low = yAxis?.Min ?? range.Minimum; var high = yAxis?.Max ?? range.Maximum;
        if (!double.IsFinite(low) || !double.IsFinite(high)) throw new InvalidDataException("Chart axis limits must be finite.");
        if (high <= low)
        {
            if (yAxis?.Min is not null && yAxis.Max is not null) throw new InvalidDataException("Chart axis maximum must be greater than its minimum.");
            if (yAxis?.Min is not null) high = low + Math.Max(1, Math.Abs(low) * .5);
            else low = high - Math.Max(1, Math.Abs(high) * .5);
        }
        if (!double.IsFinite(high - low) || high <= low) throw new InvalidDataException("Chart axis span exceeds the finite numeric range.");
        var ticks = yAxis?.TickCount is { } tickCount ? Math.Clamp(tickCount, 2, 1000) : range.Ticks;
        string Tick(double value)
        {
            try { return value.ToString(string.IsNullOrEmpty(yAxis?.Format) ? "G6" : yAxis.Format, CultureInfo.CurrentCulture); }
            catch (FormatException error) { throw new InvalidDataException("Invalid chart axis number format.", error); }
        }
        var legendFont = font;
        var legendHeight = legendFont * (entries.Count * 1.21333 + 1.126);
        if (showLegend && legendHeight > height * .72) legendFont *= height * .72 / legendHeight;
        var legendWidth = showLegend ? Math.Max(legendFont * 7, entries.Max(entry => ChartAxisLabelLayout.MeasureWidth(entry.Label, legendFont)) + legendFont * 3.9085) : 0;
        if (legendWidth > width * .38) { legendFont *= width * .38 / legendWidth; legendWidth = width * .38; }
        legendHeight = legendFont * (entries.Count * 1.21333 + 1.126);
        var legendLeft = Math.Min(width * .753846, width * .996 - legendFont * .47347 - legendWidth);
        var left = width * .160661125;
        var right = showLegend ? Math.Min(width * .7495306, legendLeft - width * .004315) : width * .955;
        var top = height * .18085648; var bottom = height * .78056;
        var slot = (right - left) / Math.Max(1, count);
        double Y(double value) => bottom - (Math.Clamp(value, low, high) - low) / (high - low) * (bottom - top);
        Box(left, top, right - left, bottom - top, "none", "#000000", .36);
        if (xAxis?.ShowGridLines != false)
            for (var category = 1; category < count; category++) Line(left + category * slot, top, left + category * slot, bottom);
        for (var tick = 0; tick < ticks; tick++)
        {
            var value = low + (high - low) * (tick / (ticks - 1d));
            var y = Y(value);
            if (yAxis?.ShowGridLines != false && tick > 0 && tick < ticks - 1) Line(left, y, right, y);
            if (showAxisLabels) Text(Tick(value), left - font * .49, y + font * .352, font, "end");
        }
        Line(left, top, left, bottom, .6); Line(left, bottom, right, bottom, .6);
        for (var category = 0; category < count; category++)
        {
            var positive = 0d; var negative = 0d;
            var grouped = !stacked && groups.Length > 1;
            var barWidth = grouped ? slot * .8113 / groups.Length : slot * .81;
            var gap = grouped ? slot * .0556 / groups.Length : 0;
            var groupWidth = grouped ? barWidth * groups.Length + gap * (groups.Length - 1) : barWidth;
            for (var group = 0; group < groups.Length; group++)
            {
                if (!present[group, category]) continue;
                var value = values[group, category];
                var baseline = stacked ? value >= 0 ? positive : negative : 0;
                var end = baseline + value;
                if (stacked) { if (value >= 0) positive = end; else negative = end; }
                if (Math.Max(baseline, end) < low || Math.Min(baseline, end) > high) continue;
                var x = grouped ? left + category * slot + (slot - groupWidth) / 2 + group * (barWidth + gap)
                    : left + category * slot + slot * .095;
                Box(x, Math.Min(Y(baseline), Y(end)), barWidth, Math.Abs(Y(end) - Y(baseline)), Color(group, category), "#000000");
            }
        }
        if (showAxisLabels)
        {
            var labelLines = labels.Select(label => Wrap(label, slot * 1.2, font)).ToArray();
            var labelBaseline = bottom + font * 1.153;
            var lineStep = font * 1.4142135623730951;
            double X(int index) => left + (index + .5) * slot + font * .64;
            var visible = SelectLabels(labelLines, X, font, lineStep, labelBaseline, width, height, xAxis);
            foreach (var category in visible.Order())
                for (var line = 0; line < labelLines[category].Length; line++)
                    Text(labelLines[category][line], X(category) + line * font, labelBaseline + line * font, font, "end", -45);
            if (!string.IsNullOrEmpty(xAxis?.Title)) Text(xAxis.Title, (left + right) / 2, height - font * .2, font, "middle");
            if (!string.IsNullOrEmpty(yAxis?.Title)) Text(yAxis.Title, font * 1.5, (top + bottom) / 2, font, "middle", -90);
        }
        if (showLegend)
        {
            var legendTop = Math.Max(font, Math.Min(height - legendHeight - font, height * .387783 - legendHeight / 2));
            Box(legendLeft + legendFont * .47347, legendTop + legendFont * .3341, legendWidth, legendHeight, "#000000");
            Box(legendLeft, legendTop, legendWidth, legendHeight, "#ffffff", "#000000");
            for (var index = 0; index < entries.Count; index++)
            {
                var y = legendTop + legendFont * (.721 + index * 1.21333);
                Box(legendLeft + legendFont * .8593, y, legendFont * .912, legendFont * .914, entries[index].Color, "#000000");
                Text(entries[index].Label, legendLeft + legendFont * 2.3919, legendTop + legendFont * (1.5123 + index * 1.21333), legendFont);
            }
        }
        image.Validate();
        return image;
    }

    private static string[] Wrap(string value, double available, double font)
    {
        value = value.ReplaceLineEndings(" ").Trim();
        if (ChartAxisLabelLayout.MeasureWidth(value, font) <= available) return [value];
        var words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return [value];
        var split = Enumerable.Range(1, words.Length - 1).OrderBy(index => Math.Max(
            ChartAxisLabelLayout.MeasureWidth(string.Join(" ", words.Take(index)), font),
            ChartAxisLabelLayout.MeasureWidth(string.Join(" ", words.Skip(index)), font))).First();
        return [string.Join(" ", words.Take(split)), string.Join(" ", words.Skip(split))];
    }

    // Measure the complete rotated block before thinning labels; wrapping never removes a data point.
    private static HashSet<int> SelectLabels(IReadOnlyList<string[]> labels, Func<int, double> position,
        double font, double lineStep, double baseline, double width, double height, ChartAxis? axis)
    {
        if (labels.Count == 0) return [];
        if (axis?.AutoSkipLabels == false) return Enumerable.Range(0, labels.Count).ToHashSet();
        var limit = Math.Max(1, Math.Min(labels.Count, axis?.TickCount ?? int.MaxValue));
        var gap = axis?.MinimumLabelGap ?? 4;
        if (!double.IsFinite(gap) || gap < 0) gap = 4;
        var cosine = Math.Sqrt(.5); var sine = -cosine;
        var bounds = labels.Select((lines, index) =>
        {
            var points = lines.SelectMany((line, row) =>
            {
                var measured = ChartAxisLabelLayout.MeasureWidth(line, font);
                return new[] { (-measured, row * lineStep - font), (0d, row * lineStep - font),
                    (-measured, row * lineStep + font * .3), (0d, row * lineStep + font * .3) };
            }).Select(point => (X: position(index) + point.Item1 * cosine - point.Item2 * sine,
                Y: baseline + point.Item1 * sine + point.Item2 * cosine)).ToArray();
            return (Left: points.Min(point => point.X), Right: points.Max(point => point.X), Bottom: points.Max(point => point.Y));
        }).ToArray();
        var eligible = bounds.Select(bound => bound.Left >= 0 && bound.Right <= width && bound.Bottom <= height).ToArray();
        var rectangles = labels.Select((lines, index) => lines.Select((line, row) =>
        {
            var anchorX = position(index) * cosine;
            var anchorY = -position(index) * sine;
            return (Left: anchorX - ChartAxisLabelLayout.MeasureWidth(line, font), Right: anchorX,
                Top: anchorY + row * lineStep - font, Bottom: anchorY + row * lineStep + font * .3);
        }).ToArray()).ToArray();
        bool Fits(IEnumerable<int> indices)
        {
            var chosen = indices.ToArray();
            for (var first = 0; first < chosen.Length; first++)
            for (var second = first + 1; second < chosen.Length; second++)
            foreach (var a in rectangles[chosen[first]])
            foreach (var b in rectangles[chosen[second]])
                if (a.Right + gap > b.Left && b.Right + gap > a.Left
                    && a.Bottom + gap > b.Top && b.Bottom + gap > a.Top) return false;
            return true;
        }
        for (var stride = Math.Max(1, (int)Math.Ceiling((double)labels.Count / limit)); stride <= labels.Count; stride++)
        {
            var chosen = Enumerable.Range(0, (labels.Count - 1) / stride + 1).Select(index => index * stride).Where(index => eligible[index]).ToHashSet();
            if (chosen.Count == 0 || !Fits(chosen)) continue;
            var last = labels.Count - 1;
            if (eligible[last] && chosen.Count < limit && !chosen.Contains(last) && Fits(chosen.Append(last))) chosen.Add(last);
            return chosen;
        }
        var first = Array.FindIndex(eligible, value => value);
        return first < 0 ? [] : [first];
    }
}
