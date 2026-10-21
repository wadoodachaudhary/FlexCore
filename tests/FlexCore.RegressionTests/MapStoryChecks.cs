using System.Globalization;
using System.Xml.Linq;
using Fx.ControlKit.Maps;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

internal static class MapStoryChecks
{
    public static async Task Run(HtmlRenderer renderer, Action<bool, string> check)
    {
        var low = ChoroplethScale.YellowMaroon().Color(2.2, 2.2, 10.3);
        var high = ChoroplethScale.YellowMaroon().Color(10.3, 2.2, 10.3);
        check(low == "#F6E27A" && high == "#5A1228", "yellow-to-maroon scale uses the reference endpoints");
        check(ChoroplethScale.YellowMaroon().Color(null, 0, 10) == "#C5C6C4", "choropleth paints missing values grey");

        var story = MapStory.LoadBundledUnemployment();
        var europe = story.Require("europe");
        var expected = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["FI"] = 10.3, ["ES"] = 10.0, ["SE"] = 8.7, ["FR"] = 8.2, ["EE"] = 8.0, ["GR"] = 7.4,
            ["LV"] = 7.3, ["LU"] = 7.3, ["AT"] = 6.7, ["RO"] = 6.4, ["IT"] = 6.2, ["LT"] = 6.1,
            ["BE"] = 5.9, ["DK"] = 5.9, ["IS"] = 5.8, ["PT"] = 5.7, ["SK"] = 5.7, ["CH"] = 5.1,
            ["IE"] = 5.0, ["GB"] = 4.9, ["HU"] = 4.7, ["NO"] = 4.5, ["HR"] = 4.3, ["CY"] = 4.1,
            ["DE"] = 4.0, ["NL"] = 4.0, ["BG"] = 3.5, ["PL"] = 3.4, ["SI"] = 3.4, ["MT"] = 3.4,
            ["CZ"] = 3.2, ["RU"] = 2.2
        };
        check(expected.All(pair => europe.Regions.Any(region => region.Id == pair.Key && region.Value == pair.Value)), "Europe level keeps the August 2026 reference rates");
        check(europe.LegendMin == 2.2 && europe.LegendMax == 10.3, "Europe legend is fixed at 2.2 to 10.3");
        check(europe.Callouts.Any(item => item.Text.Contains("Weak hiring")) && europe.Callouts.Any(item => item.Text.Contains("automakers")), "Europe keeps both reference callouts");
        check(europe.Footnote == "Switzerland and UK data from June 2026. Sources: Eurostat, ONS, Rosstat via Moscow Times.", "Europe footnote matches the reference");
        check(story.Levels.All(level => !level.SampleData) && story.Levels.SelectMany(level => level.Regions).All(region => !region.Sample), "bundled unemployment story does not label any figure as sample data");

        var again = MapStory.Load(story.ToJson());
        check(again.Require("europe").Regions.Single(region => region.Id == "FI").Value == 10.3 && again.Require("es").ParentLevelId == "europe", "map story JSON round-trips levels and values");
        Invalid(() => MapStory.Load("""{"rootLevelId":"a","levels":[{"id":"a","legendMin":5,"legendMax":5,"regions":[]}]}"""), "invalid legend range is rejected");

        var session = new MapStorySession(story);
        check(session.TryDrill("Europe") && session.TryDrill("ES") && string.Join(">", session.Path.Select(crumb => crumb.Label)) == "World>Europe>Spain", "breadcrumb drills World > Europe > Spain");
        session.Back();
        check(session.Current.Id == "europe", "back returns to the continent map");
        session.Jump(0);
        check(session.Current.Id == "world" && !session.CanBack, "breadcrumb jump returns to the world map");
        check(session.TryDrill("Europe") && session.TryDrill("UA", "Ukraine") && session.Current.UnavailableMessage == "Regional data not available" && session.Path[^1].Label == "Ukraine", "a country without provinces opens the unavailable state");

        var sample = MapStory.Load("""{"id":"sample","rootLevelId":"only","levels":[{"id":"only","title":"Sample map","breadcrumb":"Sample","geography":"countries","sampleData":true,"regions":[{"id":"ZZ","name":"Nowhere","value":1,"sample":true}]}]}""");
        var geography = MapGeographyCatalog.Bundled();
        var sampleLayout = MapStoryComposer.Compose(sample, sample.Require("only"), geography, null, null, 960, 640);
        check(sampleLayout.SampleBanner == "Sample data", "a story level can mark its figures as sample data");

        check(story.Require("es").Regions.Single(region => region.Id == "ES61").Value == 16.5, "Spain NUTS-2 keeps the published Andalucía rate");
        check(story.Require("de").Regions.Single(region => region.Id == "DEB2").Value is null && story.Require("fi").Regions.Single(region => region.Id == "FI20").Value is null, "unpublished NUTS-2 regions stay empty instead of receiving a stand-in rate");
        check(story.Require("us").Regions.Single(region => region.Id == "CA").Value == 5.1, "United States keeps the published California rate");
        check(story.Require("world").Footnote!.Contains("labour-force-weighted", StringComparison.OrdinalIgnoreCase), "continent rates are described as labour-force-weighted ILO estimates");

        var layout = MapStoryComposer.Compose(story, europe, geography, null, null, 960, 640);
        check(layout.LegendMin == "2.2%" && layout.LegendMax == "10.3%", "Europe graphic formats the legend range");
        var svg = layout.ToSvg();
        check(svg.Contains("UNEMPLOYMENT IN EUROPE") && svg.Contains("10.3%") && svg.Contains("Finland") && svg.Contains("Weak hiring") && svg.Contains("automakers") && svg.Contains("#C5C6C4") && svg.Contains("Cyprus") && svg.Contains("Luxembourg, 7.3%"), "Europe SVG carries the title, rates, callouts, grey and insets");
        var doc = XDocument.Parse(svg);
        XNamespace ns = doc.Root!.Name.Namespace;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var path in doc.Descendants(ns + "path"))
        {
            var numbers = System.Text.RegularExpressions.Regex.Matches(path.Attribute("d")!.Value, @"-?\d+\.?\d*").Select(match => double.Parse(match.Value, CultureInfo.InvariantCulture)).ToList();
            for (var i = 0; i + 1 < numbers.Count; i += 2)
            {
                minX = Math.Min(minX, numbers[i]); maxX = Math.Max(maxX, numbers[i]);
                minY = Math.Min(minY, numbers[i + 1]); maxY = Math.Max(maxY, numbers[i + 1]);
            }
        }
        check(minX >= -2 && minY >= -2 && maxX <= 962 && maxY <= 642, "Europe shapes stay inside the view box");

        var spain = story.RenderSvg(geography, "es");
        check(spain.Contains("Canarias") && spain.Contains("16.5%") && spain.Contains("Eurostat"), "Spain drill-down renders NUTS-2 rates and the Eurostat source");
        var world = story.RenderSvg(geography, "world");
        check(world.Contains("Unemployment by continent") && !world.Contains("Sample data"), "world map does not present the continent averages as sample data");

        var frame = MapFrame.Fit([new GeoPoint(36, -9), new GeoPoint(44, 4)], MapProjectionKind.Albers, 400, 300, 20, 20, 20, 20);
        var projected = frame.Project(new GeoPoint(40, -3));
        check(projected.X is > 20 and < 380 && projected.Y is > 20 and < 280, "fitted Albers projection keeps a point inside the frame");
        var robinson = MapFrame.Robinson(new GeoPoint(48, 12));
        check(double.IsFinite(robinson.X) && double.IsFinite(robinson.Y), "Robinson projection is finite");
        var clipped = MapFrame.ClipRing([new(0, -10), new(0, 10), new(10, 10), new(10, -10), new(0, -10)], new MapExtent { West = 0, South = 0, East = 5, North = 5 });
        check(clipped.Count >= 4 && clipped.All(point => point.Longitude is >= 0 and <= 5 && point.Latitude is >= 0 and <= 5), "extent clip keeps only the window");

        var shapes = MapProjection.ReadGeoJson("""{"type":"Feature","id":"area","properties":{"name":"Area"},"geometry":{"type":"Polygon","coordinates":[[[0,0],[10,0],[10,10],[0,0]]]}}""");
        check(shapes.Count == 1 && shapes[0].Key == "area", "GeoJSON shape keeps the feature id as its key");

        var html = (await renderer.RenderComponentAsync<MapStoryControl>(ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Story"] = story,
            ["Width"] = 960,
            ["Height"] = 640
        }))).ToHtmlString();
        check(html.Contains("Europe, 5.0%") && html.Contains("role=\"button\"") && html.Contains("aria-current=\"page\"") && html.Contains("World") && html.Contains("Unemployment by continent"), "map story control renders an accessible world choropleth and breadcrumb");
        check(html.Contains("Back"), "map story control renders the back control");

        void Invalid(Action action, string name)
        {
            try { action(); }
            catch (ArgumentException) { check(true, name); return; }
            throw new Exception(name);
        }
    }
}
