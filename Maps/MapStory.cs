using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fx.ControlKit.Maps;

/// <summary>
/// A drill-down choropleth defined in C# or JSON. Each <see cref="MapStoryLevel"/>
/// draws regions from a <see cref="MapGeographyCatalog"/>. A region with
/// <see cref="MapStoryRegion.ChildLevelId"/> opens that level; a level with
/// <see cref="MapStoryLevel.UnavailableMessage"/> is the explicit "no regional data" state.
/// Set <see cref="MapStoryLevel.SampleData"/> when a figure is not from a published source.
/// </summary>
public sealed class MapStory
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string ValueFormat { get; set; } = "0.0";
    public string ValueSuffix { get; set; } = "";
    public string NoDataColor { get; set; } = "#C5C6C4";
    public string Stroke { get; set; } = "#ffffff";
    public string Background { get; set; } = "#F7F5F2";
    public string RootLevelId { get; set; } = "";
    public List<ChoroplethStop> Stops { get; set; } = [];
    public List<MapStorySource> Sources { get; set; } = [];
    public List<MapStoryLevel> Levels { get; set; } = [];

    public MapStoryLevel? Find(string id) => Levels.FirstOrDefault(level => level.Id == id);
    public MapStoryLevel Require(string id) => Find(id) ?? throw new ArgumentException($"Map story has no level '{id}'.");

    public string Format(double value) => value.ToString(string.IsNullOrWhiteSpace(ValueFormat) ? "0.0" : ValueFormat, CultureInfo.InvariantCulture) + ValueSuffix;

    public static MapStory Load(string json)
    {
        var story = JsonSerializer.Deserialize<MapStory>(json, JsonOptions) ?? throw new ArgumentException("Map story JSON is empty.");
        story.Normalize();
        story.Validate();
        return story;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>The bundled unemployment story. Figures and sources live in wwwroot/maps/stories/unemployment.json.</summary>
    public static MapStory LoadBundledUnemployment() => Load(MapAssets.Text("Fx.ControlKit.maps.unemployment.json"));

    /// <summary>Static SVG of one level, for a report or an export. Same layout as <see cref="MapStoryControl"/>.</summary>
    public string RenderSvg(MapGeographyCatalog geography, string levelId, double width = 960, double height = 640, string? focusId = null, string? focusName = null) =>
        MapStoryComposer.Compose(this, Require(levelId), geography, focusId, focusName, width, height).ToSvg();

    void Normalize()
    {
        Levels ??= [];
        Stops ??= [];
        Sources ??= [];
        foreach (var level in Levels)
        {
            level.Regions ??= [];
            level.Callouts ??= [];
            level.Insets ??= [];
            foreach (var region in level.Regions) region.Members ??= [];
            if (string.IsNullOrWhiteSpace(level.Breadcrumb)) level.Breadcrumb = level.Title;
            if (string.IsNullOrWhiteSpace(level.Id)) throw new ArgumentException($"A {Title} level is missing an id.");
        }
        if (string.IsNullOrWhiteSpace(RootLevelId) && Levels.Count > 0) RootLevelId = Levels[0].Id;
    }

    void Validate()
    {
        if (Levels.Count == 0) throw new ArgumentException("A map story needs at least one level.");
        if (Levels.Select(level => level.Id).Distinct(StringComparer.Ordinal).Count() != Levels.Count) throw new ArgumentException("Map story level ids must be unique.");
        if (Find(RootLevelId) is null) throw new ArgumentException($"Map story root '{RootLevelId}' does not exist.");
        foreach (var level in Levels)
        {
            if (level.ParentLevelId is { Length: > 0 } parent && Find(parent) is null) throw new ArgumentException($"Level '{level.Id}' parent '{parent}' does not exist.");
            if (level.DefaultChildLevelId is { Length: > 0 } fallback && Find(fallback) is null) throw new ArgumentException($"Level '{level.Id}' default child '{fallback}' does not exist.");
            if (level.LegendMin is double min && level.LegendMax is double max && (!(max > min) || !double.IsFinite(min) || !double.IsFinite(max))) throw new ArgumentException($"Level '{level.Id}' legend range is invalid.");
            if (level.Regions.Select(region => region.Id).Distinct(StringComparer.Ordinal).Count() != level.Regions.Count) throw new ArgumentException($"Level '{level.Id}' has duplicate region ids.");
            foreach (var region in level.Regions)
            {
                if (region.Value is double value && !double.IsFinite(value)) throw new ArgumentException($"Level '{level.Id}' region '{region.Id}' has a non-finite value.");
                if (region.ChildLevelId is { Length: > 0 } child && Find(child) is null) throw new ArgumentException($"Region '{region.Id}' child '{child}' does not exist.");
            }
        }
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };
}

public sealed class MapStoryLevel
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Breadcrumb { get; set; } = "";
    public string? ParentLevelId { get; set; }
    /// <summary>continents, countries, regions, or country (one outline).</summary>
    public string Geography { get; set; } = "countries";
    public string? Continent { get; set; }
    public string? ParentGeographyId { get; set; }
    public string Projection { get; set; } = "Albers";
    public MapExtent? Extent { get; set; }
    public bool Clip { get; set; }
    public double? LegendMin { get; set; }
    public double? LegendMax { get; set; }
    public string? LegendCaption { get; set; }
    public string? Footnote { get; set; }
    /// <summary>When true the graphic says "Sample data". Use this instead of presenting stand-in numbers as published.</summary>
    public bool SampleData { get; set; }
    public string? UnavailableMessage { get; set; }
    public string? DefaultChildLevelId { get; set; }
    public List<MapStoryRegion> Regions { get; set; } = [];
    public List<MapStoryCallout> Callouts { get; set; } = [];
    public List<MapStoryInset> Insets { get; set; } = [];

    public MapProjectionKind ProjectionKind => Projection?.ToLowerInvariant() switch
    {
        "robinson" => MapProjectionKind.Robinson,
        "mercator" => MapProjectionKind.Mercator,
        _ => MapProjectionKind.Albers
    };
}

public sealed class MapStoryRegion
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double? Value { get; set; }
    public string? ChildLevelId { get; set; }
    public bool Sample { get; set; }
    /// <summary>Country ids that form a continent (or other group). Empty means "match the continent name".</summary>
    public List<string> Members { get; set; } = [];
}

public sealed class MapStoryCallout
{
    public string Id { get; set; } = "";
    public string RegionId { get; set; } = "";
    public string Placement { get; set; } = "east";
    public string Text { get; set; } = "";
}

public sealed class MapStoryInset
{
    public string Id { get; set; } = "";
    /// <summary>west or south. West draws a column of circles; south draws a row along the bottom.</summary>
    public string Corner { get; set; } = "south";
}

public sealed class ChoroplethStop
{
    public double At { get; set; }
    public string Color { get; set; } = "#F6E27A";
}

public sealed class MapStorySource
{
    public string? LevelId { get; set; }
    public string? Publisher { get; set; }
    public string? Dataset { get; set; }
    public string? Period { get; set; }
    public string? Retrieved { get; set; }
    public string? Note { get; set; }
}

public sealed record MapStoryCrumb(string LevelId, string Label, string? FocusId, string? FocusName);

/// <summary>Breadcrumb stack for a story. World, then Europe, then Spain.</summary>
public sealed class MapStorySession
{
    readonly MapStory _story;
    readonly List<MapStoryCrumb> _path = [];

    public MapStorySession(MapStory story)
    {
        _story = story ?? throw new ArgumentNullException(nameof(story));
        var root = story.Require(story.RootLevelId);
        _path.Add(new(root.Id, root.Breadcrumb, null, null));
    }

    public MapStoryLevel Current => _story.Require(_path[^1].LevelId);
    public string? FocusId => _path[^1].FocusId;
    public string? FocusName => _path[^1].FocusName;
    public IReadOnlyList<MapStoryCrumb> Path => _path;
    public bool CanBack => _path.Count > 1;

    public bool TryDrill(string regionId, string? regionName = null)
    {
        var level = Current;
        var region = level.Regions.FirstOrDefault(item => item.Id == regionId);
        var childId = region?.ChildLevelId;
        if (string.IsNullOrEmpty(childId) && region is null) childId = level.DefaultChildLevelId;
        if (string.IsNullOrEmpty(childId)) return false;
        var child = _story.Find(childId);
        if (child is null) return false;
        var name = region?.Name ?? regionName ?? regionId;
        var label = child.UnavailableMessage is not null ? name : child.Breadcrumb;
        _path.Add(new(child.Id, label, regionId, name));
        return true;
    }

    public void Back()
    {
        if (CanBack) _path.RemoveAt(_path.Count - 1);
    }

    public void Jump(int index)
    {
        if (index < 0 || index >= _path.Count) throw new ArgumentOutOfRangeException(nameof(index));
        _path.RemoveRange(index + 1, _path.Count - index - 1);
    }
}
