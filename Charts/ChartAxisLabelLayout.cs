using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Charts;

// Select category captions without sampling or moving the chart's data points.
internal static class ChartAxisLabelLayout
{
    internal static double MeasureWidth(string caption, double fontSize, bool bold = false, bool italic = false)
    {
        try { return NativePdfPage.MeasureText(caption, fontSize, "Arial", bold, italic); }
        catch (NotSupportedException)
        {
            // Browser text can shape scripts outside the PDF font repertoire.
            // One em per UTF-16 unit gives those captions a conservative spacing bound.
            return caption.Length * fontSize;
        }
    }

    internal static HashSet<int> Select(IReadOnlyList<string> labels, Func<int, double> position,
        double fontSize, double angle = 0, string anchor = "middle", bool bold = false,
        bool italic = false, ChartAxis? axis = null, int maximumCount = int.MaxValue,
        double minimumX = double.NegativeInfinity, double maximumX = double.PositiveInfinity,
        double maximumDepth = double.PositiveInfinity)
    {
        if (labels.Count == 0) return [];
        if (axis?.AutoSkipLabels == false) return Enumerable.Range(0, labels.Count).ToHashSet();
        var limit = Math.Max(1, Math.Min(maximumCount, axis?.TickCount ?? int.MaxValue));
        var gap = axis?.MinimumLabelGap ?? 4;
        if (!double.IsFinite(gap) || gap < 0) gap = 4;
        var radians = angle * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var bounds = labels.Select((caption, index) =>
        {
            var width = MeasureWidth(caption, fontSize, bold, italic);
            var offset = anchor == "end" ? -width : anchor == "middle" ? -width / 2 : 0;
            var corners = new[] { (offset, -fontSize), (offset + width, -fontSize),
                (offset, fontSize * .3), (offset + width, fontSize * .3) };
            var horizontal = corners.Select(corner => corner.Item1 * cosine - corner.Item2 * sine).ToArray();
            var depth = corners.Max(corner => corner.Item1 * sine + corner.Item2 * cosine);
            return (Left: position(index) + horizontal.Min(), Right: position(index) + horizontal.Max(), Depth: depth);
        }).ToArray();
        var eligible = bounds.Select(bound => bound.Left >= minimumX && bound.Right <= maximumX
            && bound.Depth <= maximumDepth).ToArray();
        if (!eligible.Any(value => value)) return [];
        bool Fits(IEnumerable<int> indices)
        {
            var ordered = indices.Select(index => bounds[index]).OrderBy(bound => bound.Left).ToArray();
            return ordered.Zip(ordered.Skip(1)).All(pair => pair.First.Right + gap <= pair.Second.Left);
        }
        for (var stride = Math.Max(1, (int)Math.Ceiling((double)labels.Count / limit)); stride <= labels.Count; stride++)
        {
            var selected = Enumerable.Range(0, (labels.Count - 1) / stride + 1)
                .Select(index => index * stride).Where(index => eligible[index]).ToHashSet();
            if (selected.Count == 0 || !Fits(selected)) continue;
            var last = labels.Count - 1;
            if (eligible[last] && selected.Count < limit && !selected.Contains(last) && Fits(selected.Append(last))) selected.Add(last);
            return selected;
        }
        return [Array.FindIndex(eligible, value => value)];
    }
}
