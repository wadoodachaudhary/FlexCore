using System.Text.Json;

namespace Fx.ControlKit.Maps;

/// <summary>One geographic feature. Polygons are rings of <see cref="GeoPoint"/> (latitude, longitude); the first ring is the exterior.</summary>
public sealed class MapFeature
{
    public MapFeature(string id, string name, string? continent, string? parentId, IReadOnlyList<IReadOnlyList<IReadOnlyList<GeoPoint>>> polygons)
    {
        Id = id;
        Name = name;
        Continent = continent;
        ParentId = parentId;
        Polygons = polygons;
    }

    public string Id { get; }
    public string Name { get; }
    public string? Continent { get; }
    public string? ParentId { get; }
    public IReadOnlyList<IReadOnlyList<IReadOnlyList<GeoPoint>>> Polygons { get; }

    public IEnumerable<GeoPoint> Points => Polygons.SelectMany(polygon => polygon.SelectMany(ring => ring));
}

/// <summary>Country and province boundaries used to draw a <see cref="MapStory"/>.</summary>
public sealed class MapGeographyCatalog
{
    MapGeographyCatalog(IReadOnlyList<MapFeature> countries, IReadOnlyList<MapFeature> regions)
    {
        Countries = countries;
        Regions = regions;
    }

    public IReadOnlyList<MapFeature> Countries { get; }
    public IReadOnlyList<MapFeature> Regions { get; }

    public static MapGeographyCatalog Load(string countriesGeoJson, string regionsGeoJson) =>
        new(MapGeoJson.Read(countriesGeoJson), MapGeoJson.Read(regionsGeoJson));

    /// <summary>Natural Earth countries and the bundled NUTS-2 / US state shapes.</summary>
    public static MapGeographyCatalog Bundled() =>
        Load(MapAssets.Text("Fx.ControlKit.maps.countries.geojson"), MapAssets.Text("Fx.ControlKit.maps.regions.geojson"));

    public MapFeature? Country(string id) => Countries.FirstOrDefault(feature => feature.Id == id);
    public IReadOnlyList<MapFeature> RegionsFor(string parentId) => Regions.Where(feature => feature.ParentId == parentId).ToList();
}

public static class MapGeoJson
{
    public static IReadOnlyList<MapFeature> Read(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var features = new List<MapFeature>();
        var points = 0;
        GeoPoint Point(JsonElement node)
        {
            if (++points > 200000) throw new ArgumentException("GeoJSON exceeds 200,000 coordinates.");
            var pair = node.EnumerateArray().Select(item => item.GetDouble()).ToArray();
            if (pair.Length < 2 || !double.IsFinite(pair[0]) || !double.IsFinite(pair[1])) throw new ArgumentException("Invalid GeoJSON coordinate.");
            return new GeoPoint(pair[1], pair[0]);
        }
        List<IReadOnlyList<IReadOnlyList<GeoPoint>>> Geometry(JsonElement geometry)
        {
            var type = geometry.GetProperty("type").GetString();
            if (type is "Polygon" or "MultiPolygon")
            {
                var coordinates = geometry.GetProperty("coordinates");
                var result = new List<IReadOnlyList<IReadOnlyList<GeoPoint>>>();
                var polygons = type == "Polygon" ? new[] { coordinates } : coordinates.EnumerateArray().ToArray();
                foreach (var polygon in polygons)
                {
                    var rings = polygon.EnumerateArray().Select(ring => (IReadOnlyList<GeoPoint>)ring.EnumerateArray().Select(Point).ToList()).Where(ring => ring.Count >= 4).ToList();
                    if (rings.Count > 0) result.Add(rings);
                }
                return result;
            }
            return [];
        }
        foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
        {
            var id = feature.TryGetProperty("id", out var key) ? key.ToString() : "";
            if (id.Length == 0) continue;
            var properties = feature.GetProperty("properties");
            var name = properties.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? id : id;
            var continent = properties.TryGetProperty("continent", out var continentNode) ? continentNode.GetString() : null;
            var parent = properties.TryGetProperty("parent", out var parentNode) ? parentNode.GetString() : null;
            var polygons = Geometry(feature.GetProperty("geometry"));
            if (polygons.Count > 0) features.Add(new MapFeature(id, name, continent, parent, polygons));
        }
        return features;
    }
}

static class MapAssets
{
    public static string Text(string name)
    {
        using var stream = typeof(MapAssets).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Map asset '{name}' is not in the assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
