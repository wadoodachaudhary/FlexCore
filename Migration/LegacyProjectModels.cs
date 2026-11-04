namespace Fx.ControlKit.Migration;

/// <summary>Kind of project found in an uploaded legacy archive.</summary>
public enum LegacyProjectKind
{
    Unknown,
    WebForms,
    WinForms,
    Wpf,
    Mvc,
    WebApi,
    RazorPages,
    Blazor,
    ClassLibrary,
    Console
}

/// <summary>Where a seed file came from.</summary>
public enum LegacySeedKind
{
    Csv,
    Sql,
    Initializer,
    MockService,
    PreconfiguredData
}

/// <summary>One solution named by a <c>.sln</c> file.</summary>
public sealed record LegacySolution(string Path, string Name, IReadOnlyList<string> ProjectPaths);

/// <summary>A page, master, user control, or form file.</summary>
public sealed record LegacyPage
{
    public required string Path { get; init; }
    public string? CodeBehind { get; init; }
    public string? Inherits { get; init; }
    public string? MasterPage { get; init; }
    public string? Title { get; init; }
    public string? Language { get; init; }
}

/// <summary>A connection string read from <c>Web.config</c> or <c>App.config</c>.</summary>
public sealed record LegacyConnectionString(string Name, string Value, string? ProviderName, string SourcePath);

/// <summary>An <c>appSettings</c> entry.</summary>
public sealed record LegacySetting(string Key, string Value, string SourcePath);

/// <summary>A file the converter can use to fill a database the legacy server cannot reach.</summary>
public sealed record LegacySeedSource(string Path, LegacySeedKind Kind);

/// <summary>One project, classified from its project file and the files beside it.</summary>
public sealed class LegacyProject
{
    public required string ProjectFile { get; init; }
    public required string Directory { get; init; }
    public required string Name { get; init; }
    public required LegacyProjectKind Kind { get; init; }
    public IReadOnlyList<LegacyProjectKind> Kinds { get; init; } = [];
    public string Language { get; init; } = "C#";
    public string? TargetFramework { get; init; }
    public string? OutputType { get; init; }
    public IReadOnlyList<string> ProjectTypeGuids { get; init; } = [];
    public IReadOnlyList<string> References { get; init; } = [];
    public IReadOnlyList<LegacyPage> Pages { get; init; } = [];
    public IReadOnlyList<LegacyPage> Masters { get; init; } = [];
    public IReadOnlyList<LegacyPage> UserControls { get; init; } = [];
    public IReadOnlyList<LegacyPage> Forms { get; init; } = [];
    public IReadOnlyList<string> CodeBehind { get; init; } = [];
    public IReadOnlyList<string> Models { get; init; } = [];
    public IReadOnlyList<string> DataAccess { get; init; } = [];
    public IReadOnlyList<string> ConfigFiles { get; init; } = [];
    public IReadOnlyList<LegacyConnectionString> ConnectionStrings { get; init; } = [];
    public IReadOnlyList<LegacySetting> AppSettings { get; init; } = [];
    public IReadOnlyList<LegacySeedSource> SeedData { get; init; } = [];
    public IReadOnlyList<string> StaticAssets { get; init; } = [];
    public IReadOnlyList<string> Evidence { get; init; } = [];

    /// <summary>True when <see cref="ConnectionStrings"/> names LocalDB or SQL Server.</summary>
    public bool UsesSqlServer => ConnectionStrings.Any(item =>
        item.Value.Contains("localdb", StringComparison.OrdinalIgnoreCase)
        || item.ProviderName?.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) == true
        || item.Value.Contains("Data Source", StringComparison.OrdinalIgnoreCase)
            && (item.Value.Contains("SQLEXPRESS", StringComparison.OrdinalIgnoreCase)
                || item.Value.Contains("Initial Catalog", StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Everything <see cref="LegacyProjectIntake.Inspect"/> found under a folder.</summary>
public sealed class LegacyProjectInventory
{
    public required string Root { get; init; }
    public IReadOnlyList<LegacySolution> Solutions { get; init; } = [];
    public IReadOnlyList<LegacyProject> Projects { get; init; } = [];

    public LegacyProject? PrimaryWebFormsProject =>
        Projects.FirstOrDefault(project => project.Kind == LegacyProjectKind.WebForms);
}

/// <summary>Files written by <see cref="LegacyProjectIntake.Extract"/>.</summary>
public sealed class LegacyExtraction
{
    public required string Destination { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public long ExtractedBytes { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>A construct the converter left for a person, written into the output and the report.</summary>
public sealed record MigrationTodo(string Path, string Message);

/// <summary>Options for <see cref="WebFormsBlazorConverter"/>.</summary>
public sealed class WebFormsConversionOptions
{
    /// <summary>Target framework of the generated Blazor project. Defaults to this repository's current target.</summary>
    public string TargetFramework { get; init; } = "net10.0";

    /// <summary>SQLite file name, relative to the generated project.</summary>
    public string SqliteFileName { get; init; } = "catalog.db";

    /// <summary>
    /// When true, CSV files win over preconfigured data even if <c>UseCustomizationData</c> is false.
    /// When null, the converter follows <c>UseCustomizationData</c> and falls back to CSV, then to mock data.
    /// </summary>
    public bool? PreferCsvSeed { get; init; }
}

/// <summary>A generated Blazor project and the gaps that were not converted silently.</summary>
public sealed class WebFormsConversionResult
{
    public bool Succeeded { get; init; }
    public string? ProjectDirectory { get; init; }
    public string? ProjectFile { get; init; }
    public string? AssemblyName { get; init; }
    public string? SqlitePath { get; init; }
    public string? ReportPath { get; init; }
    public IReadOnlyList<string> OutputFiles { get; init; } = [];
    public IReadOnlyList<MigrationTodo> Todos { get; init; } = [];
    public string Report { get; init; } = "";
    public LegacyProject? Source { get; init; }
}
