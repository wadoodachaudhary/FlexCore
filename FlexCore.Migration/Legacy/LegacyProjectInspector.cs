using System.Xml;
using System.Xml.Linq;

namespace Fx.ControlKit.Migration;

/// <summary>Reads a folder produced by <see cref="LegacyProjectIntake.Extract"/>.</summary>
public static class LegacyProjectInspector
{
    private const string WebApplicationGuid = "{349c5851-65df-11da-9384-00065b846f21}";
    private const string WpfGuid = "{60dc8134-eba5-43b8-bcc9-bb4bc16c2548}";
    private const string MsbuildNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";

    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".webp",
        ".woff", ".woff2", ".ttf", ".eot", ".otf", ".html", ".htm", ".json"
    };

    private static readonly string[] SkipDirectories = ["bin", "obj", "packages", ".vs", ".git", "node_modules"];

    public static LegacyProjectInventory Inspect(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Directory is required.", nameof(directory));
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);

        var solutions = Directory.EnumerateFiles(root, "*.sln", SearchOption.AllDirectories)
            .Where(path => !IsSkipped(root, path))
            .Select(ReadSolution)
            .ToList();
        var projects = Directory.EnumerateFiles(root, "*.*proj", SearchOption.AllDirectories)
            .Where(path => !IsSkipped(root, path))
            .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
            .Select(path => ReadProject(root, path))
            .ToList();

        return new LegacyProjectInventory { Root = root, Solutions = solutions, Projects = projects };
    }

    private static LegacySolution ReadSolution(string path)
    {
        var projects = new List<string>();
        var directory = Path.GetDirectoryName(path)!;
        foreach (var line in File.ReadLines(path))
        {
            var marker = line.IndexOf(", \"", StringComparison.Ordinal);
            if (!line.StartsWith("Project(", StringComparison.Ordinal) || marker < 0) continue;
            var rest = line[(marker + 3)..];
            var end = rest.IndexOf('"', StringComparison.Ordinal);
            if (end <= 0) continue;
            var relative = rest[..end].Replace('\\', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(directory, relative));
            if (full.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
                projects.Add(full);
        }
        return new LegacySolution(path, Path.GetFileNameWithoutExtension(path), projects);
    }

    private static LegacyProject ReadProject(string root, string projectFile)
    {
        var directory = Path.GetDirectoryName(projectFile)!;
        var xml = LoadXml(projectFile);
        var language = projectFile.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ? "VB" : "C#";
        var guids = Values(xml, "ProjectTypeGuids")
            .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
        var references = ReferenceNames(xml);
        var target = First(xml, "TargetFramework") ?? First(xml, "TargetFrameworkVersion");
        var outputType = First(xml, "OutputType");
        var useWpf = Flag(xml, "UseWPF");
        var useWinForms = Flag(xml, "UseWindowsForms") || Flag(xml, "MyType") && string.Equals(First(xml, "MyType"), "WindowsForms", StringComparison.OrdinalIgnoreCase);
        var sdk = xml.Root?.Attribute("Sdk")?.Value ?? "";

        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !IsSkipped(directory, path))
            .ToList();
        var pages = files.Where(path => path.EndsWith(".aspx", StringComparison.OrdinalIgnoreCase)).Select(ReadDirectivePage).ToList();
        var masters = files.Where(path => path.EndsWith(".master", StringComparison.OrdinalIgnoreCase)).Select(ReadDirectivePage).ToList();
        var controls = files.Where(path => path.EndsWith(".ascx", StringComparison.OrdinalIgnoreCase)).Select(ReadDirectivePage).ToList();
        var codeBehind = files.Where(IsCodeBehind).ToList();
        var xaml = files.Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)).ToList();
        var forms = new List<LegacyPage>();
        foreach (var file in files.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)))
        {
            if (IsCodeBehind(file) || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                continue;
            string head;
            try
            {
                head = ReadHead(file, 4000);
            }
            catch (IOException)
            {
                continue;
            }
            if (head.Contains("System.Windows.Forms", StringComparison.Ordinal) && head.Contains(": Form", StringComparison.Ordinal))
                forms.Add(new LegacyPage { Path = file, Language = language });
            else if (head.Contains("Inherits System.Windows.Forms.Form", StringComparison.OrdinalIgnoreCase)
                || head.Contains("Inherits Form", StringComparison.OrdinalIgnoreCase))
                forms.Add(new LegacyPage { Path = file, Language = language });
        }

        var configs = files.Where(path =>
            path.EndsWith("Web.config", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("Web.Debug.config", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("Web.Release.config", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("App.config", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("appsettings.json", StringComparison.OrdinalIgnoreCase)).ToList();
        var connectionStrings = new List<LegacyConnectionString>();
        var settings = new List<LegacySetting>();
        foreach (var config in configs.Where(path => path.EndsWith(".config", StringComparison.OrdinalIgnoreCase)))
            ReadConfiguration(config, connectionStrings, settings);

        var models = files.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            .Where(path => InFolder(directory, path, "Models") || InFolder(directory, path, "ViewModel") || InFolder(directory, path, "ViewModels")
                || InFolder(directory, path, "Domain") || InFolder(directory, path, "Entities"))
            .Where(path => !IsCodeBehind(path))
            .ToList();
        var dataAccess = files.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".vb", StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                if (name.Contains("DbContext", StringComparison.OrdinalIgnoreCase) || name.Contains("Repository", StringComparison.OrdinalIgnoreCase))
                    return true;
                try
                {
                    var text = File.ReadAllText(path);
                    return text.Contains("DbContext", StringComparison.Ordinal) || text.Contains("SqlConnection", StringComparison.Ordinal);
                }
                catch (IOException)
                {
                    return false;
                }
            }).ToList();

        var seeds = new List<LegacySeedSource>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                seeds.Add(new LegacySeedSource(file, LegacySeedKind.Csv));
            else if (file.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                seeds.Add(new LegacySeedSource(file, LegacySeedKind.Sql));
            else if (name.Contains("Initializer", StringComparison.OrdinalIgnoreCase) && (name.EndsWith(".cs") || name.EndsWith(".vb")))
                seeds.Add(new LegacySeedSource(file, LegacySeedKind.Initializer));
            else if (name.Contains("Preconfigured", StringComparison.OrdinalIgnoreCase))
                seeds.Add(new LegacySeedSource(file, LegacySeedKind.PreconfiguredData));
            else if (name.Contains("Mock", StringComparison.OrdinalIgnoreCase) && (name.EndsWith(".cs") || name.EndsWith(".vb")))
                seeds.Add(new LegacySeedSource(file, LegacySeedKind.MockService));
        }

        var assets = files.Where(path => AssetExtensions.Contains(Path.GetExtension(path))).ToList();
        var hasViews = files.Any(path => path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase) && InFolder(directory, path, "Views"));
        var hasControllers = files.Any(path => Path.GetFileName(path).EndsWith("Controller.cs", StringComparison.OrdinalIgnoreCase));
        var hasApi = references.Any(item => item.Contains("System.Web.Http", StringComparison.OrdinalIgnoreCase))
            || files.Any(path =>
            {
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
                try { return File.ReadAllText(path).Contains("ApiController", StringComparison.Ordinal); }
                catch (IOException) { return false; }
            });
        var hasBlazor = files.Any(path => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            || sdk.Contains("Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase);
        var hasMvcReference = references.Any(item => item.Contains("System.Web.Mvc", StringComparison.OrdinalIgnoreCase)
            || item.Contains("Microsoft.AspNetCore.Mvc", StringComparison.OrdinalIgnoreCase));
        var hasWebFormsReference = references.Any(item => item.Equals("System.Web", StringComparison.OrdinalIgnoreCase)
            || item.StartsWith("System.Web", StringComparison.OrdinalIgnoreCase));
        var hasWinFormsReference = references.Any(item => item.Contains("System.Windows.Forms", StringComparison.OrdinalIgnoreCase));
        var hasWpfReference = references.Any(item => item.Contains("PresentationFramework", StringComparison.OrdinalIgnoreCase));
        var hasXamlWindow = xaml.Any(path =>
        {
            try { return ReadHead(path, 500).Contains("<Window", StringComparison.Ordinal); }
            catch (IOException) { return false; }
        });

        var kinds = new List<LegacyProjectKind>();
        var evidence = new List<string>();
        if (pages.Count > 0 || masters.Count > 0 || guids.Any(guid => guid.Equals(WebApplicationGuid, StringComparison.OrdinalIgnoreCase)) && hasWebFormsReference)
        {
            kinds.Add(LegacyProjectKind.WebForms);
            evidence.Add(pages.Count > 0
                ? $"{pages.Count} .aspx page(s), {masters.Count} master page(s), {controls.Count} user control(s)."
                : "Web Application project type with System.Web references.");
        }
        if (useWinForms || hasWinFormsReference || forms.Count > 0)
        {
            kinds.Add(LegacyProjectKind.WinForms);
            evidence.Add(forms.Count > 0 ? $"{forms.Count} Windows Forms form(s)." : "UseWindowsForms or a System.Windows.Forms reference.");
        }
        if (useWpf || hasWpfReference || guids.Any(guid => guid.Equals(WpfGuid, StringComparison.OrdinalIgnoreCase)) || hasXamlWindow)
        {
            kinds.Add(LegacyProjectKind.Wpf);
            evidence.Add(hasXamlWindow ? "XAML Window markup." : "UseWPF or a PresentationFramework reference.");
        }
        if (hasBlazor && pages.Count == 0)
        {
            kinds.Add(LegacyProjectKind.Blazor);
            evidence.Add("Razor components.");
        }
        if ((hasMvcReference || hasViews && hasControllers) && pages.Count == 0)
        {
            kinds.Add(LegacyProjectKind.Mvc);
            evidence.Add(hasViews ? "MVC views and controllers." : "An MVC framework reference.");
        }
        if (hasApi && pages.Count == 0)
        {
            kinds.Add(LegacyProjectKind.WebApi);
            evidence.Add("Web API controllers or a System.Web.Http reference.");
        }
        if (sdk.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) && files.Any(path => InFolder(directory, path, "Pages") && path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) && pages.Count == 0)
        {
            kinds.Add(LegacyProjectKind.RazorPages);
            evidence.Add("Razor Pages under Pages/.");
        }
        if (kinds.Count == 0 && string.Equals(outputType, "Library", StringComparison.OrdinalIgnoreCase))
        {
            kinds.Add(LegacyProjectKind.ClassLibrary);
            evidence.Add("OutputType is Library and no UI project type matched.");
        }
        if (kinds.Count == 0 && string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase))
        {
            kinds.Add(LegacyProjectKind.Console);
            evidence.Add("OutputType is Exe and no UI project type matched.");
        }
        if (kinds.Count == 0)
        {
            kinds.Add(LegacyProjectKind.Unknown);
            evidence.Add("No Web Forms, WinForms, WPF, MVC, Web API, Razor Pages, or Blazor markers matched.");
        }

        var primary = kinds[0];
        if (kinds.Contains(LegacyProjectKind.WebForms)) primary = LegacyProjectKind.WebForms;
        else if (kinds.Contains(LegacyProjectKind.WinForms)) primary = LegacyProjectKind.WinForms;
        else if (kinds.Contains(LegacyProjectKind.Wpf)) primary = LegacyProjectKind.Wpf;
        else if (kinds.Contains(LegacyProjectKind.Blazor)) primary = LegacyProjectKind.Blazor;

        return new LegacyProject
        {
            ProjectFile = projectFile,
            Directory = directory,
            Name = Path.GetFileNameWithoutExtension(projectFile),
            Kind = primary,
            Kinds = kinds,
            Language = language,
            TargetFramework = target,
            OutputType = outputType,
            ProjectTypeGuids = guids,
            References = references,
            Pages = pages,
            Masters = masters,
            UserControls = controls,
            Forms = forms,
            CodeBehind = codeBehind,
            Models = models,
            DataAccess = dataAccess,
            ConfigFiles = configs,
            ConnectionStrings = connectionStrings,
            AppSettings = settings,
            SeedData = seeds,
            StaticAssets = assets,
            Evidence = evidence
        };
    }

    private static LegacyPage ReadDirectivePage(string path)
    {
        var head = ReadHead(path, 2000);
        return new LegacyPage
        {
            Path = path,
            CodeBehind = Attribute(head, "CodeBehind") ?? Attribute(head, "CodeFile"),
            Inherits = Attribute(head, "Inherits"),
            MasterPage = Attribute(head, "MasterPageFile"),
            Title = Attribute(head, "Title"),
            Language = Attribute(head, "Language")
        };
    }

    private static string? Attribute(string head, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(head, name + @"\s*=\s*""([^""]*)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;
        match = System.Text.RegularExpressions.Regex.Match(head, name + @"\s*=\s*'([^']*)'", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static void ReadConfiguration(string path, List<LegacyConnectionString> connections, List<LegacySetting> settings)
    {
        var xml = LoadXml(path);
        if (xml.Root is null) return;
        foreach (var node in xml.Descendants().Where(item => item.Name.LocalName == "add"))
        {
            var parent = node.Parent?.Name.LocalName;
            if (parent == "connectionStrings")
            {
                var name = node.Attribute("name")?.Value;
                var value = node.Attribute("connectionString")?.Value;
                if (!string.IsNullOrEmpty(name) && value is not null)
                    connections.Add(new LegacyConnectionString(name, value, node.Attribute("providerName")?.Value, path));
            }
            else if (parent == "appSettings")
            {
                var key = node.Attribute("key")?.Value;
                var value = node.Attribute("value")?.Value;
                if (!string.IsNullOrEmpty(key) && value is not null)
                    settings.Add(new LegacySetting(key, value, path));
            }
        }
    }

    private static XDocument LoadXml(string path)
    {
        try
        {
            return XDocument.Load(path, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            return new XDocument(new XElement("Project"));
        }
    }

    private static IEnumerable<string> Values(XDocument xml, string localName) =>
        xml.Descendants().Where(item => item.Name.LocalName == localName).Select(item => item.Value.Trim()).Where(value => value.Length > 0);

    private static string? First(XDocument xml, string localName) => Values(xml, localName).FirstOrDefault();

    private static bool Flag(XDocument xml, string localName) =>
        Values(xml, localName).Any(value => value.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static List<string> ReferenceNames(XDocument xml)
    {
        var names = new List<string>();
        foreach (var node in xml.Descendants().Where(item => item.Name.LocalName is "Reference" or "PackageReference"))
        {
            var include = node.Attribute("Include")?.Value ?? node.Value;
            var comma = include.IndexOf(',');
            names.Add((comma < 0 ? include : include[..comma]).Trim());
        }
        return names;
    }

    private static bool IsCodeBehind(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".aspx.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".aspx.vb", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".master.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".master.vb", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".ascx.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".ascx.vb", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".designer.vb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool InFolder(string projectDirectory, string path, string folder)
    {
        var relative = Path.GetRelativePath(projectDirectory, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals(folder, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSkipped(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => SkipDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));
    }

    private static string ReadHead(string path, int max)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[Math.Min(max, stream.Length)];
        var read = stream.Read(buffer, 0, buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}
