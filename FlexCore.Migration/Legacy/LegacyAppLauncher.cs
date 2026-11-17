using Fx.ControlKit.Preview;

namespace Fx.ControlKit.Migration;

/// <summary>How <see cref="LegacyAppLauncher.Plan"/> chooses a host for the original web application.</summary>
public enum LegacyLaunchMode
{
    /// <summary>Use Mono when this machine can actually render the project. Otherwise use <see cref="LegacyLaunchOptions.RemoteUrl"/> when it is set.</summary>
    Auto,

    /// <summary>Start xsp4 even when the project is not expected to render. The plan says so.</summary>
    Mono,

    /// <summary>Show an already running site, such as IIS Express in a Windows VM.</summary>
    RemoteUrl
}

/// <summary>Options for <see cref="LegacyAppLauncher.Plan"/>.</summary>
public sealed class LegacyLaunchOptions
{
    public LegacyLaunchMode Mode { get; init; } = LegacyLaunchMode.Auto;
    public string? RemoteUrl { get; init; }
    public string? Xsp4Path { get; init; }
    public string? MonoPath { get; init; }
}

/// <summary>Which host a <see cref="LegacyLaunchPlan"/> selected.</summary>
public enum LegacyHostKind
{
    Mono,
    RemoteUrl,
    Unavailable
}

/// <summary>Whether <c>mono</c> and <c>xsp4</c> are on this machine.</summary>
public sealed record LegacyMonoCapability(bool HasMono, bool HasXsp4, string? MonoPath, string? Xsp4Path, string Message);

/// <summary>
/// A preview launch for the original application.
/// <see cref="Launch"/> is a <see cref="ProgramPreviewKind.Web"/> target so the preview proxies it,
/// whether the bytes come from xsp4 on this machine or from a remote IIS Express URL.
/// <see cref="RendersOnThisHost"/> is false when that proxy would not show the real Web Forms UI.
/// </summary>
public sealed class LegacyLaunchPlan
{
    public LegacyHostKind Kind { get; init; }
    public ProgramLaunch? Launch { get; init; }
    public bool RendersOnThisHost { get; init; }
    public string Report { get; init; } = "";
    public IReadOnlyList<string> Reasons { get; init; } = [];
    public LegacyMonoCapability? Mono { get; init; }
}

/// <summary>Builds a <see cref="ProgramLaunch"/> for the original Web Forms (or other) project.</summary>
public static class LegacyAppLauncher
{
    public static LegacyMonoCapability DetectMono(LegacyLaunchOptions? options = null)
    {
        var mono = options?.MonoPath ?? Find("mono");
        var xsp = options?.Xsp4Path ?? Find("xsp4");
        var message = xsp is null
            ? "xsp4 is not installed. Mono's ASP.NET host is not available on this machine."
            : "xsp4 is installed. It can serve trivial ASP.NET sites. It does not host ASP.NET 4.7.2 Web Forms that depend on EF6, LocalDB, or packages.config.";
        return new LegacyMonoCapability(mono is not null, xsp is not null, mono, xsp, message);
    }

    /// <summary>Chooses Mono or a remote URL and returns a web preview launch.</summary>
    public static LegacyLaunchPlan Plan(LegacyProject project, LegacyLaunchOptions? options = null)
    {
        if (project is null) throw new ArgumentNullException(nameof(project));
        options ??= new LegacyLaunchOptions();
        var mono = DetectMono(options);
        var reasons = Assess(project, mono);
        var renders = reasons.Count == 0 && mono.HasXsp4 && project.Kind == LegacyProjectKind.WebForms;
        var remote = NormalizeRemote(options.RemoteUrl);

        if (options.Mode == LegacyLaunchMode.RemoteUrl || options.Mode == LegacyLaunchMode.Auto && !renders && remote is not null)
        {
            if (remote is null)
            {
                return new LegacyLaunchPlan
                {
                    Kind = LegacyHostKind.Unavailable,
                    RendersOnThisHost = false,
                    Mono = mono,
                    Reasons = ["RemoteUrl is not an absolute http or https address."],
                    Report = "The legacy preview was set to a remote URL, but the URL is missing or not absolute http(s)."
                };
            }
            return new LegacyLaunchPlan
            {
                Kind = LegacyHostKind.RemoteUrl,
                RendersOnThisHost = false,
                Mono = mono,
                Reasons = reasons,
                Launch = new ProgramLaunch
                {
                    Kind = ProgramPreviewKind.Web,
                    Url = remote,
                    Label = project.Name + " (remote)"
                },
                Report = RemoteReport(project, remote, reasons, mono)
            };
        }

        if ((options.Mode == LegacyLaunchMode.Mono || options.Mode == LegacyLaunchMode.Auto) && mono.HasXsp4 && project.Kind == LegacyProjectKind.WebForms)
        {
            return new LegacyLaunchPlan
            {
                Kind = LegacyHostKind.Mono,
                RendersOnThisHost = renders,
                Mono = mono,
                Reasons = reasons,
                Launch = new ProgramLaunch
                {
                    FileName = mono.Xsp4Path,
                    Arguments = ["--address", "127.0.0.1", "--port", "{port}", "--root", project.Directory, "--nonstop"],
                    WorkingDirectory = project.Directory,
                    Kind = ProgramPreviewKind.Web,
                    Url = "http://127.0.0.1:{port}/",
                    Label = project.Name + " (Mono xsp4)"
                },
                Report = MonoReport(project, renders, reasons, mono)
            };
        }

        return new LegacyLaunchPlan
        {
            Kind = LegacyHostKind.Unavailable,
            RendersOnThisHost = false,
            Mono = mono,
            Reasons = reasons.Count > 0 ? reasons : [mono.Message],
            Report = UnavailableReport(project, reasons, mono)
        };
    }

    private static List<string> Assess(LegacyProject project, LegacyMonoCapability mono)
    {
        var reasons = new List<string>();
        if (project.Kind != LegacyProjectKind.WebForms)
            reasons.Add($"The project is {project.Kind}. xsp4 only serves ASP.NET web sites.");
        var framework = project.TargetFramework ?? "";
        if (framework.StartsWith("v4", StringComparison.OrdinalIgnoreCase) || framework.Contains("4.", StringComparison.Ordinal))
            reasons.Add($"Target framework {framework} is ASP.NET on .NET Framework. Mono's System.Web does not implement 4.7.2 Web Forms.");
        if (project.References.Any(item => item.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase) || item.Equals("System.Data.Entity", StringComparison.OrdinalIgnoreCase)))
            reasons.Add("The project references Entity Framework 6. Mono xsp4 cannot open that stack, and LocalDB is not on Linux.");
        if (File.Exists(Path.Combine(project.Directory, "packages.config")))
            reasons.Add("packages.config dependencies are not restored or built on Linux, so the site has no bin assemblies for xsp4 to load.");
        var bin = Path.Combine(project.Directory, "bin");
        if (!Directory.Exists(bin) || !Directory.EnumerateFiles(bin, "*.dll", SearchOption.AllDirectories).Any())
            reasons.Add("There is no prebuilt bin directory. xsp4 cannot compile this website in place.");
        if (project.UsesSqlServer)
            reasons.Add("A SQL Server or LocalDB connection string is present. That server is not available here. UseMockData may avoid it on Windows; xsp4 still fails before the page runs.");
        if (!mono.HasXsp4)
            reasons.Add(mono.Message);
        return reasons;
    }

    private static string? NormalizeRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        return uri.ToString();
    }

    private static string? Find(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string MonoReport(LegacyProject project, bool renders, IReadOnlyList<string> reasons, LegacyMonoCapability mono)
    {
        if (renders)
            return $"{project.Name} can be started with xsp4 at {mono.Xsp4Path}. The preview host proxies http://127.0.0.1:{{port}}/ as a web target.";
        return $"""
            {project.Name} is launched with xsp4 only because Mono mode was requested or xsp4 happens to be installed. It is not expected to render.
            {string.Join(Environment.NewLine, reasons.Select(reason => "- " + reason))}
            Set LegacyLaunchOptions.RemoteUrl to an IIS Express address (for example a Windows VM in UTM) to show the original site. The preview still proxies that URL as a web target inside the Windows-chrome pane.
            """;
    }

    private static string RemoteReport(LegacyProject project, string url, IReadOnlyList<string> reasons, LegacyMonoCapability mono)
    {
        return $"""
            {project.Name} is shown from {url}. No process is started. ProgramPreviewKind.Web proxies an absolute http(s) address that is already running, and iframes it directly when the address is public.
            Mono on this host: {mono.Message}
            {string.Join(Environment.NewLine, reasons.Select(reason => "- " + reason))}
            """;
    }

    private static string UnavailableReport(LegacyProject project, IReadOnlyList<string> reasons, LegacyMonoCapability mono)
    {
        return $"""
            {project.Name} cannot be shown from this machine.
            {mono.Message}
            {string.Join(Environment.NewLine, reasons.Select(reason => "- " + reason))}
            Install the site on IIS Express in a Windows VM and pass LegacyLaunchOptions.RemoteUrl. The preview proxies that URL as a web target.
            """;
    }
}

/// <summary>Options for <see cref="ConvertedAppLauncher.Create"/>.</summary>
public sealed class ConvertedLaunchOptions
{
    public string? DotnetPath { get; init; }

    /// <summary>When true and the built assembly exists, the launch runs that dll instead of <c>dotnet run</c>.</summary>
    public bool PreferBuiltAssembly { get; init; } = true;
}

/// <summary>Builds a <see cref="ProgramLaunch"/> for the converted Blazor app.</summary>
public static class ConvertedAppLauncher
{
    public static ProgramLaunch Create(WebFormsConversionResult conversion, ConvertedLaunchOptions? options = null)
    {
        if (conversion is null) throw new ArgumentNullException(nameof(conversion));
        if (!conversion.Succeeded || conversion.ProjectDirectory is null || conversion.ProjectFile is null)
            throw new InvalidOperationException(conversion.Report.Length == 0 ? "The conversion did not produce a project." : conversion.Report);
        options ??= new ConvertedLaunchOptions();
        var dotnet = options.DotnetPath ?? FindDotnet() ?? "dotnet";
        var tfm = ReadTarget(conversion.ProjectFile) ?? "net10.0";
        var assembly = conversion.AssemblyName ?? Path.GetFileNameWithoutExtension(conversion.ProjectFile);
        var built = Path.Combine(conversion.ProjectDirectory, "bin", "Debug", tfm, assembly + ".dll");
        if (options.PreferBuiltAssembly && File.Exists(built))
        {
            return new ProgramLaunch
            {
                FileName = dotnet,
                Arguments = [built, "--urls", "http://127.0.0.1:{port}"],
                WorkingDirectory = Path.GetDirectoryName(built)!,
                Kind = ProgramPreviewKind.Web,
                Url = "http://127.0.0.1:{port}/",
                Label = assembly,
                Environment = new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Development" }
            };
        }
        return new ProgramLaunch
        {
            FileName = dotnet,
            Arguments = ["run", "--project", conversion.ProjectFile, "--urls", "http://127.0.0.1:{port}"],
            WorkingDirectory = conversion.ProjectDirectory,
            Kind = ProgramPreviewKind.Web,
            Url = "http://127.0.0.1:{port}/",
            Label = assembly,
            Environment = new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = "Development" }
        };
    }

    private static string? ReadTarget(string projectFile)
    {
        var text = File.ReadAllText(projectFile);
        var match = System.Text.RegularExpressions.Regex.Match(text, @"<TargetFramework>([^<]+)</TargetFramework>");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? FindDotnet()
    {
        var home = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(home))
        {
            var rooted = Path.Combine(home, "dotnet");
            if (File.Exists(rooted)) return rooted;
        }
        var user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "dotnet");
        if (File.Exists(user)) return user;
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
