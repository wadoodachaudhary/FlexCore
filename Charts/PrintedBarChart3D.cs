using System.Globalization;
using Fx.ControlKit.Reports;

namespace Fx.ControlKit.Charts;

// Shared vector scene: interactive previews and PDF use the same camera and data geometry.
internal static class PrintedBarChart3D
{
    private static readonly string[] DefaultPalette = ["#0060ff", "#ff0000", "#ffff00", "#40ff00", "#00ffff", "#ff00ff"];

    internal static bool Supports(ChartType type) => type is ChartType.Bar or ChartType.GroupedBar or ChartType.StackedBar or ChartType.StackedBar100;

    internal static ReportVectorImage Draw(ChartType type, IReadOnlyList<ChartSeries> series, string title,
        double width, double height, bool legend, string[]? palette = null, ChartAxis? xAxis = null,
        ChartAxis? yAxis = null, bool showAxisLabels = true, ReportPerspectiveBarStyle? style = null)
    {
        if (!Supports(type)) throw new NotSupportedException("This chart type has no three-dimensional bar adapter.");
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Chart dimensions must be finite and positive.");
        var image = new ReportVectorImage { Width = width, Height = height };
        var groups = series?.Where(group => group is not null).ToArray() ?? [];
        var count = groups.Select(group => group.DataPoints?.Count ?? 0).DefaultIfEmpty().Max();
        if (style is { Palette.Length: > 0 }) palette = style.Palette.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var colors = palette?.Select(NormalizeColor).Where(color => color is not null).Select(color => color!).ToArray();
        if (colors is not { Length: > 0 }) colors = DefaultPalette;
        var font = groups.Length > 1 ? Math.Min(width / 80, height / 31.93) : Math.Min(width / 118, height / 31.78);
        if (style is not null) font = Math.Min(width / 50, height / 32);
        ChartDataPoint? Point(int group, int category) => groups[group].DataPoints is { } points && category < points.Count ? points[category] : null;
        static bool Present(ChartDataPoint? point) => point is { IsEmpty: false } && double.IsFinite(point.Value);
        var labels = Enumerable.Range(0, count).Select(category => groups.Select((_, group) => Point(group, category))
            .FirstOrDefault(point => point is not null)?.Label ?? (category + 1).ToString(CultureInfo.CurrentCulture)).ToArray();
        string Color(int group, int category) => NormalizeColor(Point(group, category)?.Color) ?? NormalizeColor(groups[group].Color)
            ?? colors[(groups.Length == 1 ? category : group) % colors.Length];
        var entries = new List<(string Label, string Color)>();
        if (legend && groups.Length == 1)
            entries.AddRange(labels.Select((label, category) => (label, Color(0, category))));
        else if (legend)
            entries.AddRange(groups.Select((group, index) => (group.Name ?? "", NormalizeColor(group.Color) ?? colors[index % colors.Length])));
        var showLegend = entries.Count > 0;

        void Line(double x1, double y1, double x2, double y2, double stroke = .24) =>
            image.Shapes.Add(new("Polyline", [x1, y1, x2, y2], "none", "#000000", style is null ? stroke : Math.Max(stroke, .36)));
        void Polygon(double[] points, string fill = "none", double stroke = .24) =>
            image.Shapes.Add(new("Polygon", points, fill, "#000000", stroke));
        void Box(double x, double y, double w, double h, string fill, string stroke = "none", double thickness = .12) =>
            image.Shapes.Add(new("Rectangle", [x, y, w, h], fill, stroke, thickness));
        void Text(string text, double x, double y, double size, string anchor = "start", double angle = 0, string color = "#000000", bool bold = false)
        {
            var radians = angle * Math.PI / 180;
            image.Shapes.Add(new("Text", [0, 0], color, "none", 0,
                Transform: [Math.Cos(radians), Math.Sin(radians), -Math.Sin(radians), Math.Cos(radians), x, y],
                Text: new(text, "Arial", size, bold ? 700 : 400, Anchor: anchor)));
        }
        Box(0, 0, width, height, "#ffffff");
        if (!string.IsNullOrEmpty(title)) Text(title, width / 2, height * .08, font * (style is null ? 1.6 : 2),
            "middle", color: NormalizeColor(style?.TitleColor) ?? "#000000", bold: style is not null);

        // Percent stacks use absolute totals, so positive and negative values cannot cancel the denominator.
        var values = new double[groups.Length, count];
        var present = new bool[groups.Length, count];
        var stacked = type is ChartType.StackedBar or ChartType.StackedBar100;
        var minimum = 0d; var maximum = 0d;
        for (var category = 0; category < count; category++)
        {
            var largest = groups.Select((_, group) => Point(group, category)).Where(Present)
                .Select(point => Math.Abs(point!.Value)).DefaultIfEmpty().Max();
            var divisor = type == ChartType.StackedBar100 && largest > 0
                ? groups.Select((_, group) => Point(group, category)).Where(Present).Sum(point => Math.Abs(point!.Value) / largest) : 1;
            var positive = 0d; var negative = 0d;
            for (var group = 0; group < groups.Length; group++)
            {
                var point = Point(group, category);
                if (!Present(point)) continue;
                var value = type == ChartType.StackedBar100 && largest > 0 ? point!.Value / largest / divisor * 100 : point!.Value;
                present[group, category] = true; values[group, category] = value;
                if (stacked)
                {
                    if (value >= 0) positive += value; else negative += value;
                    if (!double.IsFinite(positive) || !double.IsFinite(negative)) throw new InvalidDataException("Stacked chart totals exceed the finite numeric range.");
                }
                else { minimum = Math.Min(minimum, value); maximum = Math.Max(maximum, value); }
            }
            if (stacked) { minimum = Math.Min(minimum, negative); maximum = Math.Max(maximum, positive); }
        }
        var allZero = minimum == 0 && maximum == 0;
        var range = allZero ? (Minimum: -6d, Maximum: 6d, Ticks: 7)
            : type == ChartType.StackedBar100 ? (Minimum: minimum < 0 ? -100d : 0d, Maximum: maximum > 0 ? 100d : 0d, Ticks: minimum < 0 && maximum > 0 ? 11 : 6)
            : BarPrintScale.Range(minimum, maximum);
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
        var legendHeight = legendFont * (entries.Count * 1.2 + (groups.Length > 1 ? 1.82 : 2.53));
        if (showLegend && legendHeight > height * .72) legendFont *= height * .72 / legendHeight;
        var legendWidth = showLegend ? Math.Max(legendFont * 9.153, entries.Max(entry => ChartAxisLabelLayout.MeasureWidth(entry.Label, legendFont)) + legendFont * (groups.Length > 1 ? 4.726 : 6.3)) : 0;
        if (legendWidth > width * .30) { legendFont *= width * .30 / legendWidth; legendWidth = width * .30; }
        legendHeight = legendFont * (entries.Count * 1.2 + (groups.Length > 1 ? 1.82 : 2.53));
        var legendLeft = Math.Min(width * (groups.Length > 1 ? .86187 : .86246), width * (groups.Length > 1 ? .995 : .97) - width * .009113 - legendWidth);
        var backLeft = width * .18250;
        var backRight = showLegend ? Math.Min(width * .77155, legendLeft - width * .09) : width * .955;
        var backTop = height * .15850; var backBottom = height * .75848;
        var dx = width * .022; var dy = -height * .0224;
        var frontLeft = backLeft - dx; var frontRight = backRight - dx;
        var frontTop = backTop - dy; var frontBottom = backBottom - dy;
        if (style?.Plot is { } frame)
        {
            frontLeft = width * frame.Left; frontRight = width * frame.Right;
            frontTop = height * frame.Top; frontBottom = height * frame.Bottom;
            backLeft = frontLeft + dx; backRight = frontRight + dx;
            backTop = frontTop + dy; backBottom = frontBottom + dy;
        }
        var slot = (frontRight - frontLeft) / Math.Max(1, count);
        double Y(double value) => frontBottom - (Math.Clamp(value, low, high) - low) / (high - low) * (frontBottom - frontTop);

        Box(backLeft, backTop, backRight - backLeft, backBottom - backTop, "none", "#000000", style is null ? .24 : .36);
        Polygon([frontLeft, frontTop, backLeft, backTop, backLeft, backBottom, frontLeft, frontBottom]);
        Polygon([frontLeft, frontBottom, backLeft, backBottom, backRight, backBottom, frontRight, frontBottom]);
        if (style?.CategoryGrid ?? (xAxis?.ShowGridLines != false))
            for (var category = 1; category < count; category++)
            {
                var x = frontLeft + category * slot;
                Line(x + dx, backTop, x + dx, backBottom);
                Line(x, frontBottom, x + dx, backBottom);
            }
        for (var tick = 0; tick < ticks; tick++)
        {
            var value = low + (high - low) * (tick / (ticks - 1d));
            var y = Y(value);
            if ((style?.ValueGrid ?? (yAxis?.ShowGridLines != false)) && tick > 0 && tick < ticks - 1 && Math.Abs(value) > (high - low) * 1e-12)
            { Line(frontLeft, y, backLeft, y + dy); Line(backLeft, y + dy, backRight, y + dy); }
            if (showAxisLabels) Text(Tick(value), frontLeft - font * (style is null ? 1.12 : .55), y + font * .343, font, "end");
        }
        if (low <= 0 && high >= 0)
        { Line(frontLeft, Y(0), backLeft, Y(0) + dy, .48); Line(backLeft, Y(0) + dy, backRight, Y(0) + dy, .48); }
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
                var left = grouped ? frontLeft + category * slot + (slot - groupWidth) / 2 + group * (barWidth + gap)
                    : frontLeft + slot * .095 + category * slot;
                var right = left + barWidth;
                var top = Math.Min(Y(baseline), Y(end)); var bottom = Math.Max(Y(baseline), Y(end));
                var color = Color(group, category);
                Polygon([left, bottom, left, top, right, top, right, bottom], color, .12);
                Polygon([right, bottom, right, top, right + dx, top + dy, right + dx, bottom + dy], Shade(color, .8), .12);
                Polygon([left, top, left + dx, top + dy, right + dx, top + dy, right, top], Shade(color, .6), .12);
            }
        }
        if (showAxisLabels)
        {
            double X(int index) => frontLeft + (index + .5) * slot + (style is null ? font * .66 : 0);
            var labelBaseline = frontBottom + font * (style is null ? 1.725 : 1.4);
            var angle = style is null || style.RotateCategoryLabels ? -45 : 0;
            var anchor = angle == 0 ? "middle" : "end";
            var visible = ChartAxisLabelLayout.Select(labels, X, font, angle, anchor, axis: xAxis,
                minimumX: 0, maximumX: width, maximumDepth: Math.Max(0, height - labelBaseline));
            foreach (var category in visible.Order()) Text(labels[category], X(category), labelBaseline, font, anchor, angle);
            var xTitle = style?.CategoryTitle ?? xAxis?.Title;
            var yTitle = style?.ValueTitle ?? yAxis?.Title;
            if (!string.IsNullOrEmpty(xTitle)) Text(xTitle, (frontLeft + frontRight) / 2, height - font * .2, font, "middle");
            if (!string.IsNullOrEmpty(yTitle)) Text(yTitle, style is null ? font * 1.5 : font * 1.35, (frontTop + frontBottom) / 2 + (style is null ? 0 : font), style is null ? font : font * 1.1, "middle", -90, bold: style is not null);
        }
        if (showLegend)
        {
            var top = Math.Max(font, Math.Min(height - legendHeight - font, height * (groups.Length > 1 ? .39643 : .40870) - legendHeight / 2));
            if (style?.Legend is { } legendFrame)
            {
                legendLeft = legendFrame.Left * width; legendWidth = (legendFrame.Right - legendFrame.Left) * width;
                top = legendFrame.Top * height; legendHeight = (legendFrame.Bottom - legendFrame.Top) * height;
                legendFont = Math.Min(font, legendHeight / (entries.Count * 1.2 + 1.1));
            }
            else Box(legendLeft + width * .009113, top + height * .01079, legendWidth, legendHeight, "#000000");
            if (style is not null && stacked && groups.Length > 1) entries.Reverse();
            Box(legendLeft, top, legendWidth, legendHeight, "#ffffff", "#000000", .12);
            for (var index = 0; index < entries.Count; index++)
            {
                var row = top + legendFont * ((style?.Legend is not null ? .8 : groups.Length > 1 ? 1.086 : 1.398) + index * 1.2);
                Box(legendLeft + legendFont * (style?.Legend is not null ? .85 : groups.Length > 1 ? 1.212 : 1.578), row, legendFont * .921, legendFont * .923, entries[index].Color, "#000000", .12);
                Text(entries[index].Label, legendLeft + legendFont * (style?.Legend is not null ? 2.26 : groups.Length > 1 ? 2.748 : 3.114), row + legendFont * .791, legendFont);
            }
        }
        image.Validate();
        return image;
    }

    private static string Shade(string color, double factor) => "#" + string.Concat(new[] { 1, 3, 5 }
        .Select(index => ((int)(Convert.ToInt32(color.Substring(index, 2), 16) * factor)).ToString("x2", CultureInfo.InvariantCulture)));

    internal static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return "#ffffff";
        if (value[0] == '#' && value.Length is 4 or 5 && value.Skip(1).All(Uri.IsHexDigit))
            value = "#" + string.Concat(value.Skip(1).Select(character => new string(character, 2)));
        if (value[0] == '#' && value.Length is 7 or 9 && value.Skip(1).All(Uri.IsHexDigit))
        {
            if (value.Length == 7) return value.ToLowerInvariant();
            var alpha = Convert.ToInt32(value.Substring(7, 2), 16) / 255d;
            return "#" + string.Concat(new[] { 1, 3, 5 }.Select(index => ((int)Math.Round(Convert.ToInt32(value.Substring(index, 2), 16) * alpha + 255 * (1 - alpha))).ToString("x2", CultureInfo.InvariantCulture)));
        }
        if (value.All(char.IsAsciiLetter) && Enum.TryParse<System.Drawing.KnownColor>(value, true, out var known)
            && known is >= System.Drawing.KnownColor.AliceBlue and <= System.Drawing.KnownColor.YellowGreen)
        {
            var color = System.Drawing.Color.FromKnownColor(known);
            return $"#{color.R:x2}{color.G:x2}{color.B:x2}";
        }
        return null;
    }
}
