using System.Text.Json;
using System.Xml.Linq;
using Fx.ControlKit.Artifacts;

namespace Fx.ControlKit.Artifacts.Exstream;

/// <summary>One node of a design-pack page outline (<c>label</c>, <c>storyId</c>, <c>children</c>, optional <c>type</c>).</summary>
public sealed class PackOutlineNode
{
    public string Label { get; init; } = "";
    public string Type { get; init; } = "Unknown";
    public string? StoryId { get; init; }
    public List<PackOutlineNode> Children { get; } = [];
}

/// <summary>A document inside an Exstream design pack (<c>content/documents/*.json</c>).</summary>
public sealed class PackDocument
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "process";
    public double PageWidthPt { get; init; }
    public double PageHeightPt { get; init; }
    public List<PackPage> Pages { get; } = [];
    public Dictionary<string, (bool Referencable, string Text)> Stories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Fonts { get; } = [];
    public List<string> Variables { get; } = [];
    public int Tables { get; init; }
}

public sealed record PackPage(int Number, string PageObjectId, List<PackOutlineNode> Outline);

/// <summary>
/// Reads an Exstream design pack: <c>manifest.json</c>, <c>content/documents</c>, converted scripts, and resource JSON.
/// The document shape is the one DotNetCCM's pack reader uses: pages, outline, stories, and the manifest.
/// The exporter that writes the pack stays in the host application.
/// </summary>
public static class ExstreamPackReader
{
    public const string NoDocumentsMessage =
        "The export has no .ssd documents on disk, so the designer has no pages to drill into. The project explorer still lists resources.xml.";

    public static bool IsPack(string? path) =>
        path is not null && File.Exists(Path.Combine(path, "manifest.json"));

    public static string? CommunicationName(string packDir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(packDir, "manifest.json")));
            return doc.RootElement.TryGetProperty("communication", out var name) ? name.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Translated files a viewer can open, read from the pack on disk.
    /// <paramref name="manifestNote"/> replaces the counted summary on the manifest row when the host
    /// already has exporter totals (resources, documents, scripts converted).
    /// </summary>
    public static IReadOnlyList<TranslatedFile> ListTranslatedFiles(string packDirectory, string? title = null, string? manifestNote = null)
    {
        var output = Path.GetFullPath(packDirectory);
        var name = string.IsNullOrWhiteSpace(title)
            ? CommunicationName(output) ?? Path.GetFileName(output.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : title;
        var documents = ListDocuments(output);
        var scripts = RelativeFiles(output, Path.Combine(output, "scripts", "converted"), "*.js");
        var resources = RelativeFiles(output, Path.Combine(output, "resources"), "*.json");
        var note = manifestNote ?? $"{ResourceCounts(output).Values.Sum()} resources, {documents.Count} documents, {scripts.Count} scripts";
        var files = new List<TranslatedFile>
        {
            new("manifest.json", name, "manifest.json", ExstreamPackViewerDescriptor.PackFormat, "written", note)
        };
        foreach (var (docName, kind, relative) in documents)
            files.Add(new TranslatedFile(relative, docName, relative, ExstreamPackViewerDescriptor.DocumentFormat, "written", kind));
        foreach (var relative in scripts)
            files.Add(new TranslatedFile(relative, Path.GetFileName(relative), relative, ExstreamPackViewerDescriptor.ScriptFormat, "written", "Converted script"));
        foreach (var relative in resources)
            files.Add(new TranslatedFile(relative, Path.GetFileNameWithoutExtension(relative), relative, ExstreamPackViewerDescriptor.ResourceFormat, "written", "Design-pack resource"));
        return files;
    }

    /// <summary>Pack-level messages that do not depend on the exporter. Script-review counts stay with the host.</summary>
    public static IReadOnlyList<string> GapMessages(string packDirectory) =>
        ListDocuments(packDirectory).Count == 0 ? [NoDocumentsMessage] : [];

    public static IReadOnlyList<(string Name, string Kind, string RelativePath)> ListDocuments(string packDir)
    {
        var dir = Path.Combine(packDir, "content", "documents");
        if (!Directory.Exists(dir)) return [];
        var docs = new List<(string, string, string)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? Path.GetFileNameWithoutExtension(file) : Path.GetFileNameWithoutExtension(file);
                var kind = doc.RootElement.TryGetProperty("kind", out var k) ? k.GetString() ?? "process" : "process";
                docs.Add((name, kind, Path.GetRelativePath(packDir, file).Replace('\\', '/')));
            }
            catch (JsonException) { }
        }
        return docs;
    }

    public static PackDocument? LoadDocument(string packDir, string relativeOrName)
    {
        var dir = Path.Combine(packDir, "content", "documents");
        if (!Directory.Exists(dir)) return null;
        var file = File.Exists(Path.Combine(packDir, relativeOrName))
            ? Path.Combine(packDir, relativeOrName)
            : Directory.EnumerateFiles(dir, "*.json").FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals(Sanitize(relativeOrName), StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(f).Equals(relativeOrName, StringComparison.OrdinalIgnoreCase));
        if (file is null || !File.Exists(file)) return null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var root = json.RootElement;
            var pageSize = root.TryGetProperty("pageSize", out var size) ? size : default;
            var doc = new PackDocument
            {
                Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? relativeOrName : relativeOrName,
                Kind = root.TryGetProperty("kind", out var kind) ? kind.GetString() ?? "process" : "process",
                PageWidthPt = pageSize.ValueKind == JsonValueKind.Object && pageSize.TryGetProperty("widthPt", out var w) ? w.GetDouble() : 0,
                PageHeightPt = pageSize.ValueKind == JsonValueKind.Object && pageSize.TryGetProperty("heightPt", out var h) ? h.GetDouble() : 0,
                Tables = root.TryGetProperty("tables", out var tables) && tables.TryGetInt32(out var count) ? count : 0
            };
            if (root.TryGetProperty("pages", out var pages))
                foreach (var page in pages.EnumerateArray())
                    doc.Pages.Add(new PackPage(
                        page.TryGetProperty("Number", out var number) ? number.GetInt32() : doc.Pages.Count + 1,
                        page.TryGetProperty("PageObjectId", out var id) ? id.GetString() ?? "" : "",
                        page.TryGetProperty("outline", out var outline) ? outline.EnumerateArray().Select(ReadOutline).ToList() : []));
            if (root.TryGetProperty("stories", out var stories))
                foreach (var story in stories.EnumerateArray())
                {
                    var storyId = story.TryGetProperty("Id", out var sid) ? sid.GetString() ?? "" : "";
                    if (storyId.Length == 0) continue;
                    doc.Stories[storyId] = (
                        story.TryGetProperty("Referencable", out var referencable) && referencable.ValueKind == JsonValueKind.True,
                        story.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "");
                }
            if (root.TryGetProperty("fonts", out var fonts))
                foreach (var font in fonts.EnumerateArray()) doc.Fonts.Add(font.GetString() ?? "");
            if (root.TryGetProperty("variables", out var variables))
                foreach (var variable in variables.EnumerateArray()) doc.Variables.Add(variable.GetString() ?? "");
            return doc;
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    public static IReadOnlyDictionary<string, int> ResourceCounts(string packDir)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in Resources(packDir))
            counts[resource.Type] = counts.GetValueOrDefault(resource.Type) + 1;
        return counts;
    }

    public static IReadOnlyList<(string Type, string Name)> Resources(string packDir)
    {
        var list = new List<(string, string)>();
        var manifest = Path.Combine(packDir, "manifest.json");
        if (!File.Exists(manifest)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("resources", out var resources)) return list;
            foreach (var resource in resources.EnumerateArray())
            {
                var type = resource.TryGetProperty("type", out var t) ? t.GetString() ?? "?" : "?";
                var name = resource.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                list.Add((type, name));
            }
        }
        catch (JsonException) { }
        return list;
    }

    private static PackOutlineNode ReadOutline(JsonElement element)
    {
        var node = new PackOutlineNode
        {
            Label = element.TryGetProperty("label", out var label) ? label.GetString() ?? "" : "",
            Type = element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() ?? "Unknown" : "Unknown",
            StoryId = element.TryGetProperty("storyId", out var story) && story.ValueKind == JsonValueKind.String ? story.GetString() : null
        };
        if (element.TryGetProperty("children", out var children))
            foreach (var child in children.EnumerateArray())
                node.Children.Add(ReadOutline(child));
        return node;
    }

    private static List<string> RelativeFiles(string packDirectory, string directory, string pattern)
    {
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, pattern)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => Path.GetRelativePath(packDirectory, f).Replace('\\', '/'))
            .ToList();
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}

/// <summary>One resources.xml entry. Missing files stay in the explorer; the export simply did not contain them.</summary>
public sealed record ExstreamResourceEntry(string Name, string Type, string RelativePath, bool OnDisk);

public static class ExstreamProjectIndex
{
    public static IReadOnlyList<ExstreamResourceEntry> Read(string exportRoot)
    {
        var xml = Path.Combine(exportRoot, "resources.xml");
        if (!File.Exists(xml)) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ExstreamResourceEntry>();
        var document = XDocument.Load(xml);
        foreach (var resource in document.Root?.Elements("Resource") ?? [])
        {
            var name = resource.Element("ResourceName")?.Value ?? "";
            var type = resource.Element("ResourceType")?.Value ?? "file";
            var path = (resource.Element("ResourcePath")?.Value ?? "").Replace('\\', '/');
            if (path.StartsWith("../", StringComparison.Ordinal)) path = path[3..];
            var key = type + "\n" + name;
            if (name.Length == 0 || !seen.Add(key)) continue;
            var full = Path.GetFullPath(Path.Combine(exportRoot, path));
            var root = Path.GetFullPath(exportRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var onDisk = full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
            entries.Add(new ExstreamResourceEntry(name, type, path, onDisk));
        }
        return entries;
    }
}

/// <summary>Viewer registration for each file a design pack lists.</summary>
public sealed class ExstreamPackViewerDescriptor : ITranslatedFileViewer
{
    public const string PackFormat = "exstream-pack";
    public const string DocumentFormat = "exstream-document";
    public const string ScriptFormat = "exstream-script";
    public const string ResourceFormat = "exstream-resource";

    private readonly string _format;
    private readonly string _title;
    public ExstreamPackViewerDescriptor(string format, string title) { _format = format; _title = title; }
    public string Format => _format;
    public string Title => _title;
    public Type Component => typeof(ExstreamPackViewer);

    public static ExstreamPackViewerDescriptor Pack { get; } = new(PackFormat, "Design pack");
    public static ExstreamPackViewerDescriptor Document { get; } = new(DocumentFormat, "Document");
    public static ExstreamPackViewerDescriptor Script { get; } = new(ScriptFormat, "Script");
    public static ExstreamPackViewerDescriptor Resource { get; } = new(ResourceFormat, "Resource");
}
