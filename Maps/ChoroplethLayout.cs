using System.Globalization;
using System.Text;

namespace Fx.ControlKit.Maps;

public sealed class ChoroplethScale
{
    readonly (double At, byte R, byte G, byte B)[] _stops;

    ChoroplethScale(IEnumerable<(double At, byte R, byte G, byte B)> stops, string noDataColor)
    {
        _stops = stops.OrderBy(stop => stop.At).ToArray();
        NoDataColor = noDataColor;
        if (_stops.Length < 2) throw new ArgumentException("A choropleth scale needs two stops.");
    }

    public string NoDataColor { get; }
    public IReadOnlyList<(double At, byte R, byte G, byte B)> Stops => _stops;

    public static ChoroplethScale YellowMaroon(string noDataColor = "#C5C6C4") => From(
    [
        new() { At = 0, Color = "#F6E27A" },
        new() { At = 0.28, Color = "#F0A23A" },
        new() { At = 0.52, Color = "#E15A3C" },
        new() { At = 0.75, Color = "#B2253A" },
        new() { At = 1, Color = "#5A1228" }
    ], noDataColor);

    public static ChoroplethScale From(IReadOnlyList<ChoroplethStop> stops, string noDataColor)
    {
        var parsed = new List<(double At, byte R, byte G, byte B)>();
        foreach (var stop in stops)
        {
            if (!TryRgb(stop.Color, out var rgb)) throw new ArgumentException($"Choropleth stop '{stop.Color}' is not a hex color.");
            parsed.Add((stop.At, rgb.R, rgb.G, rgb.B));
        }
        return new ChoroplethScale(parsed, noDataColor);
    }

    public string Color(double? value, double min, double max)
    {
        if (value is not double number || !double.IsFinite(number)) return NoDataColor;
        var t = max <= min ? 0.5 : Math.Clamp((number - min) / (max - min), 0, 1);
        var upper = 1;
        while (upper < _stops.Length - 1 && _stops[upper].At < t) upper++;
        var lower = upper - 1;
        var span = _stops[upper].At - _stops[lower].At;
        var mix = span <= 0 ? 0 : (t - _stops[lower].At) / span;
        return Hex(Lerp(_stops[lower].R, _stops[upper].R, mix), Lerp(_stops[lower].G, _stops[upper].G, mix), Lerp(_stops[lower].B, _stops[upper].B, mix));
    }

    public static bool TryRgb(string? text, out (byte R, byte G, byte B) color)
    {
        color = default;
        if (text is null) return false;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex.Select(item => $"{item}{item}"));
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits)) return false;
        color = ((byte)(bits >> 16), (byte)(bits >> 8), (byte)bits);
        return true;
    }

    static byte Lerp(byte a, byte b, double t) => (byte)Math.Clamp((int)Math.Round(a + (b - a) * t), 0, 255);
    public static string Hex(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}

public sealed class ChoroplethRegionGraphic
{
    public required MapShape Shape { get; init; }
    public required string Path { get; init; }
    public required string Fill { get; init; }
    public required string Stroke { get; init; }
    public required string AriaLabel { get; init; }
    public required string Tooltip { get; init; }
    public bool Interactive { get; init; }
    public string? ClipPath { get; init; }
}

public sealed class ChoroplethLabel
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required string Text { get; init; }
    public string? SecondLine { get; init; }
    public string Anchor { get; init; } = "middle";
    public double Size { get; init; } = 12;
    public bool Bold { get; init; } = true;
}

public sealed class ChoroplethCalloutGraphic
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required double AnchorX { get; init; }
    public required double AnchorY { get; init; }
    public required IReadOnlyList<string> Lines { get; init; }
}

public sealed class ChoroplethInsetGraphic
{
    public required string Id { get; init; }
    public required double Cx { get; init; }
    public required double Cy { get; init; }
    public required double Radius { get; init; }
    public required string Caption { get; init; }
    public string? Subcaption { get; init; }
}

public sealed class ChoroplethLayout
{
    public double Width { get; init; }
    public double Height { get; init; }
    public string Background { get; init; } = "#F7F5F2";
    public string Title { get; init; } = "";
    public string? SampleBanner { get; init; }
    public string? LegendCaption { get; init; }
    public string? Footnote { get; init; }
    public string? Message { get; init; }
    public string GradientId { get; init; } = "fx-choro-legend";
    public IReadOnlyList<(double Offset, string Color)> LegendStops { get; init; } = [];
    public string? LegendMin { get; init; }
    public string? LegendMax { get; init; }
    public double LegendX { get; init; }
    public double LegendY { get; init; }
    public double LegendWidth { get; init; }
    public IReadOnlyList<ChoroplethRegionGraphic> Regions { get; init; } = [];
    public IReadOnlyList<ChoroplethLabel> Labels { get; init; } = [];
    public IReadOnlyList<ChoroplethCalloutGraphic> Callouts { get; init; } = [];
    public IReadOnlyList<ChoroplethInsetGraphic> Insets { get; init; } = [];

    public string ToSvg()
    {
        var svg = new StringBuilder();
        svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ").Append(F(Width)).Append(' ').Append(F(Height)).Append("\" role=\"group\" aria-label=\"").Append(Esc(Title)).Append("\">");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"").Append(Esc(Background)).Append("\"/>");
        if (LegendStops.Count > 0 || Insets.Count > 0)
        {
            svg.Append("<defs>");
            if (LegendStops.Count > 0)
            {
                svg.Append("<linearGradient id=\"").Append(Esc(GradientId)).Append("\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"0\">");
                foreach (var stop in LegendStops) svg.Append("<stop offset=\"").Append((stop.Offset * 100).ToString("0.#", CultureInfo.InvariantCulture)).Append("%\" stop-color=\"").Append(Esc(stop.Color)).Append("\"/>");
                svg.Append("</linearGradient>");
            }
            foreach (var inset in Insets) svg.Append("<clipPath id=\"").Append(Esc(GradientId)).Append('-').Append(Esc(inset.Id)).Append("\"><circle cx=\"").Append(F(inset.Cx)).Append("\" cy=\"").Append(F(inset.Cy)).Append("\" r=\"").Append(F(inset.Radius)).Append("\"/></clipPath>");
            svg.Append("</defs>");
        }
        foreach (var region in Regions)
        {
            svg.Append("<path d=\"").Append(region.Path).Append("\" fill=\"").Append(Esc(region.Fill)).Append("\" stroke=\"").Append(Esc(region.Stroke)).Append("\" stroke-width=\"0.8\" fill-rule=\"evenodd\"");
            if (region.ClipPath is not null) svg.Append(" clip-path=\"url(#").Append(Esc(region.ClipPath)).Append(")\"");
            svg.Append("><title>").Append(Esc(region.Tooltip)).Append("</title></path>");
        }
        foreach (var inset in Insets)
        {
            svg.Append("<circle cx=\"").Append(F(inset.Cx)).Append("\" cy=\"").Append(F(inset.Cy)).Append("\" r=\"").Append(F(inset.Radius)).Append("\" fill=\"none\" stroke=\"#5c584f\" stroke-width=\"1\"/>");
            if (inset.Caption.Length > 0) svg.Append("<text x=\"").Append(F(inset.Cx)).Append("\" y=\"").Append(F(inset.Cy + inset.Radius + 13)).Append("\" text-anchor=\"middle\" font-size=\"11\" font-weight=\"700\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" fill=\"#1a1a1a\">").Append(Esc(inset.Caption)).Append("</text>");
            if (inset.Subcaption is { Length: > 0 }) svg.Append("<text x=\"").Append(F(inset.Cx)).Append("\" y=\"").Append(F(inset.Cy + inset.Radius + 25)).Append("\" text-anchor=\"middle\" font-size=\"9\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" fill=\"#3a342c\">").Append(Esc(inset.Subcaption)).Append("</text>");
        }
        foreach (var callout in Callouts)
        {
            var edgeX = callout.AnchorX < callout.X + callout.Width / 2 ? callout.X : callout.X + callout.Width;
            svg.Append("<line x1=\"").Append(F(callout.AnchorX)).Append("\" y1=\"").Append(F(callout.AnchorY)).Append("\" x2=\"").Append(F(edgeX)).Append("\" y2=\"").Append(F(callout.Y + callout.Height / 2)).Append("\" stroke=\"#3a342c\" stroke-width=\"1\"/>");
            svg.Append("<rect x=\"").Append(F(callout.X)).Append("\" y=\"").Append(F(callout.Y)).Append("\" width=\"").Append(F(callout.Width)).Append("\" height=\"").Append(F(callout.Height)).Append("\" rx=\"4\" fill=\"#fffdf8\" stroke=\"#3a342c\"/>");
            for (var i = 0; i < callout.Lines.Count; i++) svg.Append("<text x=\"").Append(F(callout.X + 8)).Append("\" y=\"").Append(F(callout.Y + 16 + i * 14)).Append("\" font-size=\"11\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" fill=\"#1a1a1a\">").Append(Esc(callout.Lines[i])).Append("</text>");
        }
        foreach (var label in Labels) AppendLabel(svg, label);
        svg.Append("<text x=\"24\" y=\"34\" font-size=\"26\" font-weight=\"700\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" fill=\"#161616\">").Append(Esc(Title)).Append("</text>");
        if (SampleBanner is not null) svg.Append("<text x=\"24\" y=\"52\" font-size=\"13\" font-weight=\"700\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" fill=\"#8a3b12\">").Append(Esc(SampleBanner)).Append("</text>");
        if (LegendCaption is not null) svg.Append("<text x=\"").Append(F(LegendX)).Append("\" y=\"").Append(F(LegendY - 6)).Append("\" font-size=\"12\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" fill=\"#333\">").Append(Esc(LegendCaption)).Append("</text>");
        if (LegendMin is not null)
        {
            svg.Append("<rect x=\"").Append(F(LegendX)).Append("\" y=\"").Append(F(LegendY)).Append("\" width=\"").Append(F(LegendWidth)).Append("\" height=\"12\" rx=\"2\" fill=\"url(#").Append(Esc(GradientId)).Append(")\" stroke=\"#888\" stroke-width=\"0.4\"/>");
            svg.Append("<text x=\"").Append(F(LegendX)).Append("\" y=\"").Append(F(LegendY + 26)).Append("\" font-size=\"11\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" fill=\"#333\">").Append(Esc(LegendMin)).Append("</text>");
            svg.Append("<text x=\"").Append(F(LegendX + LegendWidth)).Append("\" y=\"").Append(F(LegendY + 26)).Append("\" text-anchor=\"end\" font-size=\"11\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" fill=\"#333\">").Append(Esc(LegendMax)).Append("</text>");
        }
        if (Message is not null) svg.Append("<text x=\"").Append(F(Width / 2)).Append("\" y=\"").Append(F(Height / 2)).Append("\" text-anchor=\"middle\" font-size=\"22\" font-weight=\"700\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" fill=\"#3a342c\">").Append(Esc(Message)).Append("</text>");
        if (Footnote is not null)
        {
            var lines = Wrap(Footnote, 128);
            for (var i = 0; i < lines.Count; i++) svg.Append("<text x=\"24\" y=\"").Append(F(Height - 18 - (lines.Count - 1 - i) * 14)).Append("\" font-size=\"11\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" fill=\"#3f3a34\">").Append(Esc(lines[i])).Append("</text>");
        }
        svg.Append("</svg>");
        return svg.ToString();
    }

    static void AppendLabel(StringBuilder svg, ChoroplethLabel label)
    {
        svg.Append("<text x=\"").Append(F(label.X)).Append("\" y=\"").Append(F(label.Y)).Append("\" text-anchor=\"").Append(label.Anchor).Append("\" font-size=\"").Append(F(label.Size)).Append("\" font-weight=\"").Append(label.Bold ? "700" : "400").Append("\" font-family=\"Segoe UI, Helvetica, Arial, sans-serif\" fill=\"#1a1a1a\" stroke=\"#fff\" stroke-width=\"3\" paint-order=\"stroke\">").Append(Esc(label.Text));
        if (label.SecondLine is not null) svg.Append("<tspan x=\"").Append(F(label.X)).Append("\" dy=\"12\" font-size=\"10\" font-weight=\"500\">").Append(Esc(label.SecondLine)).Append("</tspan>");
        svg.Append("</text>");
    }

    public static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (next.Length > width && line.Length > 0) { lines.Add(line); line = word; }
            else line = next;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    static string Esc(string? text) => (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

public static class MapStoryComposer
{
    public static ChoroplethLayout Compose(MapStory story, MapStoryLevel level, MapGeographyCatalog geography, string? focusId, string? focusName, double width, double height)
    {
        var scale = story.Stops.Count >= 2 ? ChoroplethScale.From(story.Stops, story.NoDataColor) : ChoroplethScale.YellowMaroon(story.NoDataColor);
        var drawn = DrawList(level, geography, focusId);
        var insetIds = level.Insets.Select(inset => inset.Id).ToHashSet(StringComparer.Ordinal);
        var insets = drawn.Where(item => insetIds.Contains(item.Id)).ToList();
        var main = drawn.Where(item => !insetIds.Contains(item.Id)).ToList();
        if (level.Clip && level.Extent is not null) main = main.Select(item => item.Clip(level.Extent)).Where(item => item.Polygons.Count > 0).ToList();
        var westInsets = level.Insets.Where(inset => inset.Corner.Equals("west", StringComparison.OrdinalIgnoreCase)).Select(inset => inset.Id).ToHashSet(StringComparer.Ordinal);
        var southRow = insets.Any(item => !westInsets.Contains(item.Id));
        var padLeft = 28 + (insets.Any(item => westInsets.Contains(item.Id)) ? 96 : 0);
        var padBottom = 42 + (southRow ? 128 : 0);
        var padTop = level.UnavailableMessage is null ? 108 : 72;
        var title = level.UnavailableMessage is null ? level.Title : (string.IsNullOrWhiteSpace(focusName) ? level.Title : focusName);
        MapFrame? frame = null;
        var points = main.SelectMany(item => item.Points).ToList();
        if (points.Count > 0) frame = MapFrame.Fit(points, level.ProjectionKind, width, height, padLeft, padTop, 24, padBottom);

        var values = main.Concat(insets).Select(item => item.Value).Where(value => value is not null).Select(value => value!.Value).ToList();
        double min, max;
        if (level.LegendMin is double legendMin && level.LegendMax is double legendMax) { min = legendMin; max = legendMax; }
        else if (values.Count == 0) { min = 0; max = 1; }
        else { min = values.Min(); max = values.Max(); if (max <= min) max = min + 1; }

        var regions = new List<ChoroplethRegionGraphic>();
        var anchors = new Dictionary<string, (double X, double Y, double Left, double Top, double Right, double Bottom)>(StringComparer.Ordinal);
        if (frame is not null)
        {
            foreach (var item in main.OrderByDescending(feature => feature.Polygons.Count))
                regions.Add(Paint(story, level, item, frame, scale, min, max, null, anchors));
        }

        var placedInsets = new List<ChoroplethInsetGraphic>();
        var west = 0;
        var south = 0;
        foreach (var item in insets)
        {
            var onWest = westInsets.Contains(item.Id);
            var radius = onWest ? 42 : 32;
            var cx = onWest ? 70 : width - 72 - south * 86;
            var cy = onWest ? 168 + west * 112 : height - 150;
            if (onWest) west++; else south++;
            var localPoints = item.Points.ToList();
            if (localPoints.Count == 0) continue;
            var local = MapFrame.Fit(localPoints, level.ProjectionKind, radius * 1.55, radius * 1.55, 1, 1, 1, 1);
            var shifted = local.Translated(cx - local.Width / 2, cy - local.Height / 2);
            var gradientId = "fx-choro-" + new string(level.Id.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());
            regions.Add(Paint(story, level, item, shifted, scale, min, max, gradientId + "-" + item.Id, anchors));
            var caption = item.Value is double number ? story.Format(number) : "";
            var subcaption = item.Name.Length <= 18 ? item.Name : item.Name[..16].TrimEnd() + "…";
            placedInsets.Add(new ChoroplethInsetGraphic { Id = item.Id, Cx = cx, Cy = cy, Radius = radius, Caption = caption, Subcaption = subcaption });
        }

        var callouts = PlaceCallouts(level, anchors, width, height);
        var labels = new List<ChoroplethLabel>();
        if (level.UnavailableMessage is null)
        {
            var keepBottom = southRow ? height - 168 : height - 28;
            var reserved = callouts.Select(item => (item.X - 8, item.Y - 6, item.X + item.Width + 8, item.Y + item.Height + 6)).ToList();
            foreach (var inset in placedInsets)
                reserved.Add((inset.Cx - inset.Radius - 8, inset.Cy - inset.Radius - 8, inset.Cx + inset.Radius + 8, inset.Cy + inset.Radius + 30));
            labels.AddRange(PlaceLabels(story, main.Where(item => item.Value is not null), anchors, width, keepBottom, reserved));
        }
        var anySample = level.SampleData || level.Regions.Any(region => region.Sample);
        var footnote = level.Footnote;
        if (level.Regions.Any(region => region.Sample) && footnote is not null) footnote += " Values marked in the story file as sample data are not published figures.";
        return new ChoroplethLayout
        {
            Width = width,
            Height = height,
            Background = story.Background,
            Title = title,
            SampleBanner = anySample ? "Sample data" : null,
            LegendCaption = level.UnavailableMessage is null ? level.LegendCaption : null,
            Footnote = footnote,
            Message = level.UnavailableMessage,
            GradientId = "fx-choro-" + new string(level.Id.Select(item => char.IsLetterOrDigit(item) ? item : '-').ToArray()),
            LegendStops = level.UnavailableMessage is null && values.Count > 0 ? scale.Stops.Select(stop => (stop.At, ChoroplethScale.Hex(stop.R, stop.G, stop.B))).ToList() : [],
            LegendMin = level.UnavailableMessage is null && values.Count > 0 ? story.Format(min) : null,
            LegendMax = level.UnavailableMessage is null && values.Count > 0 ? story.Format(max) : null,
            LegendX = 24,
            LegendY = anySample ? 78 : 64,
            LegendWidth = 220,
            Regions = regions,
            Labels = labels,
            Callouts = callouts,
            Insets = placedInsets
        };
    }

    static List<DrawnFeature> DrawList(MapStoryLevel level, MapGeographyCatalog geography, string? focusId)
    {
        if (level.UnavailableMessage is not null)
        {
            var country = focusId is null ? null : geography.Country(focusId);
            return country is null ? [] : [DrawnFeature.From(country, null, null, false)];
        }
        if (level.Geography.Equals("continents", StringComparison.OrdinalIgnoreCase))
        {
            return level.Regions.Select(region =>
            {
                var members = region.Members.Count > 0
                    ? geography.Countries.Where(country => region.Members.Contains(country.Id))
                    : geography.Countries.Where(country => country.Continent == region.Id);
                return DrawnFeature.Group(region, members);
            }).Where(item => item.Polygons.Count > 0).ToList();
        }
        if (level.Geography.Equals("regions", StringComparison.OrdinalIgnoreCase))
        {
            return geography.RegionsFor(level.ParentGeographyId ?? "").Select(feature =>
            {
                var region = level.Regions.FirstOrDefault(item => item.Id == feature.Id);
                return DrawnFeature.From(feature, region?.Value, region, region?.Sample ?? false, region?.Name);
            }).ToList();
        }
        if (level.Geography.Equals("country", StringComparison.OrdinalIgnoreCase))
        {
            var country = focusId is null ? null : geography.Country(focusId);
            return country is null ? [] : [DrawnFeature.From(country, null, null, false)];
        }
        var listed = level.Regions.Select(region => region.Id).Concat(level.Insets.Select(inset => inset.Id)).ToHashSet(StringComparer.Ordinal);
        var pool = geography.Countries.Where(country => listed.Contains(country.Id) || level.Continent is null || country.Continent == level.Continent);
        return pool.Select(feature =>
        {
            var region = level.Regions.FirstOrDefault(item => item.Id == feature.Id);
            return DrawnFeature.From(feature, region?.Value, region, region?.Sample ?? false, region?.Name);
        }).ToList();
    }

    static ChoroplethRegionGraphic Paint(MapStory story, MapStoryLevel level, DrawnFeature item, MapFrame frame, ChoroplethScale scale, double min, double max, string? clip, Dictionary<string, (double X, double Y, double Left, double Top, double Right, double Bottom)> anchors)
    {
        var fill = item.Value is null ? scale.NoDataColor : scale.Color(item.Value, min, max);
        var stroke = level.Geography.Equals("continents", StringComparison.OrdinalIgnoreCase) ? fill : story.Stroke;
        var path = new StringBuilder();
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        List<MapPixel>? largest = null;
        foreach (var polygon in item.Polygons)
        {
            for (var ringIndex = 0; ringIndex < polygon.Count; ringIndex++)
            {
                var projected = polygon[ringIndex].Select(frame.Project).ToList();
                if (ringIndex == 0 && (largest is null || projected.Count > largest.Count)) largest = projected;
                for (var i = 0; i < projected.Count; i++)
                {
                    path.Append(i == 0 ? "M" : "L").Append(MapFrame.Format(projected[i].X)).Append(' ').Append(MapFrame.Format(projected[i].Y)).Append(' ');
                    left = Math.Min(left, projected[i].X); top = Math.Min(top, projected[i].Y);
                    right = Math.Max(right, projected[i].X); bottom = Math.Max(bottom, projected[i].Y);
                }
                path.Append("Z ");
            }
        }
        if (largest is { Count: > 0 })
        {
            var anchor = Anchor(largest);
            anchors[item.Id] = (anchor.X, anchor.Y, left, top, right, bottom);
        }
        var valueText = item.Value is double number ? story.Format(number) : "no data";
        var drill = item.Child || (item.Region is null && level.DefaultChildLevelId is not null);
        var aria = item.Name + ", " + valueText + (item.Sample ? ", sample data" : "") + (drill ? ". Opens the next map." : ".");
        return new ChoroplethRegionGraphic
        {
            Shape = new MapShape(item.Id, item.Name, [], Key: item.Id),
            Path = path.ToString(),
            Fill = fill,
            Stroke = stroke,
            AriaLabel = aria,
            Tooltip = aria,
            Interactive = true,
            ClipPath = clip
        };
    }

    static MapPixel Anchor(List<MapPixel> ring)
    {
        var cx = ring.Average(point => point.X);
        var cy = ring.Average(point => point.Y);
        if (Inside(ring, cx, cy)) return new(cx, cy);
        var left = ring.Min(point => point.X);
        var right = ring.Max(point => point.X);
        var top = ring.Min(point => point.Y);
        var bottom = ring.Max(point => point.Y);
        var mx = (left + right) / 2;
        var my = (top + bottom) / 2;
        return Inside(ring, mx, my) ? new(mx, my) : new(cx, cy);
    }

    static bool Inside(List<MapPixel> ring, double x, double y)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var crossing = (ring[i].Y > y) != (ring[j].Y > y) && x < (ring[j].X - ring[i].X) * (y - ring[i].Y) / (ring[j].Y - ring[i].Y + 1e-12) + ring[i].X;
            if (crossing) inside = !inside;
        }
        return inside;
    }

    static List<ChoroplethLabel> PlaceLabels(MapStory story, IEnumerable<DrawnFeature> features, Dictionary<string, (double X, double Y, double Left, double Top, double Right, double Bottom)> anchors, double width, double keepBottom, List<(double X, double Y, double R, double B)> reserved)
    {
        var labels = new List<ChoroplethLabel>();
        var occupied = new List<(double L, double T, double R, double B)> { (8, 24, 300, 108) };
        occupied.AddRange(reserved.Select(item => (item.X, item.Y, item.R, item.B)));
        foreach (var feature in features.OrderByDescending(item => anchors.TryGetValue(item.Id, out var box) ? (box.Right - box.Left) * (box.Bottom - box.Top) : 0))
        {
            if (!anchors.TryGetValue(feature.Id, out var box) || feature.Value is not double value) continue;
            var valueText = story.Format(value);
            var boxW = box.Right - box.Left;
            var boxH = box.Bottom - box.Top;
            var shortName = ShortLabel(feature.Name);
            var named = false;
            if (shortName is not null)
            {
                var wide = Math.Max(TextWidth(shortName, 10), TextWidth(valueText, 13));
                var fits = boxW >= wide * 0.72 && boxH >= 26;
                var roomy = (boxW >= 64 && boxH >= 34) || boxH >= 68 || boxW >= 96;
                if (fits || roomy)
                {
                    var nudges = new (double X, double Y)[]
                    {
                        (box.X, box.Y),
                        (box.X, box.Y + Math.Min(14, boxH * 0.22)),
                        (box.X, box.Y - Math.Min(14, boxH * 0.22)),
                        (box.X, box.Y + Math.Min(36, Math.Max(16, boxH * 0.3))),
                        (box.X + Math.Min(10, boxW * 0.12), box.Y),
                        (box.X - Math.Min(10, boxW * 0.12), box.Y)
                    };
                    foreach (var nudge in nudges)
                    {
                        if (!TryTake(occupied, nudge.X, nudge.Y, wide, 30, width, keepBottom, 2)) continue;
                        labels.Add(new ChoroplethLabel { X = nudge.X, Y = nudge.Y, Text = valueText, SecondLine = shortName, Anchor = "middle", Size = 13 });
                        named = true;
                        break;
                    }
                }
            }
            if (named) continue;
            var size = boxW < 16 ? 10 : 11;
            var estimateW = TextWidth(valueText, size);
            var estimateH = size + 6;
            var outside = estimateW > boxW * 0.85 || estimateH > boxH;
            var spots = new List<(double X, double Y)>();
            if (!outside) spots.Add((box.X, box.Y));
            spots.Add((box.X, box.Bottom + estimateH));
            spots.Add((box.X, box.Top - 8));
            spots.Add((box.Right + 8 + estimateW / 2, box.Y));
            spots.Add((box.Left - 8 - estimateW / 2, box.Y));
            spots.Add((box.X, box.Bottom + estimateH + 12));
            spots.Add((box.Right + 10 + estimateW / 2, box.Bottom + 10));
            spots.Add((box.Left - 10 - estimateW / 2, box.Bottom + 10));
            // Further sideways last, so a district pinched on the coast can sit in open water.
            spots.Add((box.Right + 36 + estimateW / 2, box.Y));
            spots.Add((box.Left - 36 - estimateW / 2, box.Y));
            if (outside) spots.Add((box.X, box.Y));
            foreach (var spot in spots)
            {
                var near = Math.Abs(spot.X - box.X) <= 36 && spot.Y >= box.Top - 8 && spot.Y <= box.Bottom + 18;
                if (shortName is not null && near)
                {
                    var wide = Math.Max(TextWidth(shortName, 10), TextWidth(valueText, 13));
                    if (TryTake(occupied, spot.X, spot.Y, wide, 30, width, keepBottom, 2))
                    {
                        labels.Add(new ChoroplethLabel { X = spot.X, Y = spot.Y, Text = valueText, SecondLine = shortName, Anchor = "middle", Size = 13 });
                        break;
                    }
                }
                if (!TryTake(occupied, spot.X, spot.Y, estimateW, estimateH, width, keepBottom, 8)) continue;
                labels.Add(new ChoroplethLabel { X = spot.X, Y = spot.Y, Text = valueText, Anchor = "middle", Size = size });
                break;
            }
        }
        return labels;
    }

    static bool TryTake(List<(double L, double T, double R, double B)> occupied, double x, double y, double w, double h, double width, double keepBottom, double padBottom)
    {
        var left = x - w / 2;
        var top = y - 13;
        var rect = (left - 2, top - 1, left + w + 2, top + h + padBottom);
        if (rect.Item1 < 4 || rect.Item3 > width - 4 || rect.Item2 < 100 || rect.Item4 > keepBottom) return false;
        if (occupied.Any(item => item.L < rect.Item3 && item.R > rect.Item1 && item.T < rect.Item4 && item.B > rect.Item2)) return false;
        occupied.Add(rect);
        return true;
    }

    static string? ShortLabel(string name)
    {
        if (name.Length <= 16) return name;
        var at = name.LastIndexOf(" de ", StringComparison.Ordinal);
        if (at >= 0)
        {
            var tail = name[(at + 4)..];
            if (tail.Length is >= 3 and <= 16) return tail;
        }
        return name.Length <= 20 ? name : null;
    }

    static double TextWidth(string text, double size) => Math.Max(8, text.Length * size * 0.58);

    static List<ChoroplethCalloutGraphic> PlaceCallouts(MapStoryLevel level, Dictionary<string, (double X, double Y, double Left, double Top, double Right, double Bottom)> anchors, double width, double height)
    {
        var callouts = new List<ChoroplethCalloutGraphic>();
        var occupied = new List<(double L, double T, double R, double B)>();
        foreach (var callout in level.Callouts)
        {
            if (!anchors.TryGetValue(callout.RegionId, out var anchor)) continue;
            var lines = ChoroplethLayout.Wrap(callout.Text, 34);
            var boxW = 196.0;
            var boxH = 12 + lines.Count * 14.0;
            var east = !callout.Placement.Equals("west", StringComparison.OrdinalIgnoreCase);
            var preferX = east ? anchor.Right + 16 : anchor.Left - 16 - boxW;
            var preferY = anchor.Y - boxH / 2;
            (double X, double Y)? best = null;
            var bestOverlap = double.PositiveInfinity;
            var bestDistance = double.PositiveInfinity;
            foreach (var candidate in CalloutCandidates(preferX, preferY, boxW, boxH, width, height, east))
            {
                var rect = (L: candidate.X, T: candidate.Y, R: candidate.X + boxW, B: candidate.Y + boxH);
                if (occupied.Any(item => item.L < rect.R && item.R > rect.L && item.T < rect.B && item.B > rect.T)) continue;
                var overlap = 0.0;
                foreach (var other in anchors)
                {
                    if (other.Key == callout.RegionId) continue;
                    var box = (L: other.Value.Left, T: other.Value.Top, R: other.Value.Right, B: other.Value.Bottom);
                    var area = Intersection(rect, box);
                    var boxArea = Math.Max(1, (box.R - box.L) * (box.B - box.T));
                    var coversAnchor = other.Value.X >= rect.L && other.Value.X <= rect.R && other.Value.Y >= rect.T && other.Value.Y <= rect.B;
                    overlap += (coversAnchor ? 8000 : 0) + area / boxArea * 4000 + area * 0.05;
                }
                var distance = Math.Abs(candidate.X - preferX) + Math.Abs(candidate.Y - preferY);
                overlap += distance * 6;
                if (overlap < bestOverlap - 1 || (Math.Abs(overlap - bestOverlap) <= 1 && distance < bestDistance))
                {
                    bestOverlap = overlap;
                    bestDistance = distance;
                    best = candidate;
                }
            }
            if (best is null) continue;
            var chosen = best.Value;
            occupied.Add((chosen.X, chosen.Y, chosen.X + boxW, chosen.Y + boxH));
            callouts.Add(new ChoroplethCalloutGraphic { X = chosen.X, Y = chosen.Y, Width = boxW, Height = boxH, AnchorX = anchor.X, AnchorY = anchor.Y, Lines = lines });
        }
        return callouts;
    }

    static IEnumerable<(double X, double Y)> CalloutCandidates(double preferX, double preferY, double boxW, double boxH, double width, double height, bool east)
    {
        var minX = 8.0;
        var maxX = Math.Max(minX, width - boxW - 8);
        var minY = 112.0;
        var maxY = Math.Max(minY, height - boxH - 36);
        double[] outward = [0, 40, 90, 150, 230, 340];
        double[] shiftY = [0, -40, 40, -80, 80, -130, 130, -190, 190];
        var seen = new HashSet<(int X, int Y)>();
        foreach (var extra in outward)
        foreach (var dy in shiftY)
        {
            var x = Math.Clamp(preferX + (east ? extra : -extra), minX, maxX);
            var y = Math.Clamp(preferY + dy, minY, maxY);
            if (seen.Add(((int)Math.Round(x), (int)Math.Round(y)))) yield return (x, y);
        }
    }

    static double Intersection((double L, double T, double R, double B) a, (double L, double T, double R, double B) b)
    {
        var w = Math.Min(a.R, b.R) - Math.Max(a.L, b.L);
        var h = Math.Min(a.B, b.B) - Math.Max(a.T, b.T);
        return w > 0 && h > 0 ? w * h : 0;
    }
}

sealed class DrawnFeature
{
    DrawnFeature(string id, string name, double? value, bool sample, bool child, MapStoryRegion? region, IReadOnlyList<IReadOnlyList<IReadOnlyList<GeoPoint>>> polygons)
    {
        Id = id; Name = name; Value = value; Sample = sample; Child = child; Region = region; Polygons = polygons;
    }

    public string Id { get; }
    public string Name { get; }
    public double? Value { get; }
    public bool Sample { get; }
    public bool Child { get; }
    public MapStoryRegion? Region { get; }
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<GeoPoint>>> Polygons { get; }
    public IEnumerable<GeoPoint> Points => Polygons.SelectMany(polygon => polygon.SelectMany(ring => ring));

    public static DrawnFeature From(MapFeature feature, double? value, MapStoryRegion? region, bool sample, string? name = null) =>
        new(feature.Id, string.IsNullOrWhiteSpace(name) ? feature.Name : name, value, sample, region?.ChildLevelId is { Length: > 0 }, region, feature.Polygons);

    public static DrawnFeature Group(MapStoryRegion region, IEnumerable<MapFeature> members)
    {
        var polygons = members.SelectMany(feature => feature.Polygons).ToList();
        return new(region.Id, region.Name, region.Value, region.Sample, region.ChildLevelId is { Length: > 0 }, region, polygons);
    }

    public DrawnFeature Clip(MapExtent extent)
    {
        var polygons = new List<IReadOnlyList<IReadOnlyList<GeoPoint>>>();
        foreach (var polygon in Polygons)
        {
            var rings = new List<IReadOnlyList<GeoPoint>>();
            foreach (var ring in polygon)
            {
                var clipped = MapFrame.ClipRing(ring, extent);
                if (clipped.Count >= 4) rings.Add(clipped);
            }
            if (rings.Count >= 1) polygons.Add(rings);
        }
        return new(Id, Name, Value, Sample, Child, Region, polygons);
    }
}
