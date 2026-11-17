namespace Fx.ControlKit.Artifacts;

/// <summary>
/// One file a translator wrote. <see cref="Format"/> selects the viewer.
/// </summary>
public sealed record TranslatedFile(
    string Id,
    string Title,
    string RelativePath,
    string Format,
    string? Status,
    string? Note,
    string? Fingerprint = null,
    string? DataPath = null);

/// <summary>The files one translation wrote, and the folders the viewers read.</summary>
public sealed class TranslationBundle
{
    public required string ApplicationKey { get; init; }
    public required string Title { get; init; }
    public required string SourceDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public required IReadOnlyList<TranslatedFile> Files { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = [];

    public string FullPath(TranslatedFile file)
    {
        var path = Path.GetFullPath(Path.Combine(OutputDirectory, file.RelativePath));
        var root = Path.GetFullPath(OutputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !path.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A translated file must stay inside its output folder.");
        return path;
    }
}

/// <summary>What a host asks an application to translate. Source files are only read.</summary>
public sealed class ArtifactTranslationRequest
{
    public required string SourceDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public string? ProjectName { get; init; }

    /// <summary>Source-relative paths the user picked. Null translates every file the application accepts.</summary>
    public IReadOnlyList<string>? SelectedFiles { get; init; }
}

/// <summary>
/// A translator for one source kind. The host registers an implementation per application
/// (Crystal Reports, Exstream, and so on). <see cref="SourceKind"/> and <see cref="TargetKind"/>
/// are stable catalog ids the host maps onto its own enums; FlexCore does not own those enums.
/// </summary>
public interface IArtifactApplication
{
    string Key { get; }
    string Title { get; }
    string Description { get; }

    /// <summary>Host catalog id for the source, such as <c>CrystalReports</c> or <c>StreamServe</c>.</summary>
    string SourceKind { get; }

    /// <summary>Host catalog id for the target, such as <c>BlazorServer</c> or <c>Exstream</c>.</summary>
    string TargetKind { get; }

    Task<TranslationBundle> TranslateAsync(ArtifactTranslationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The viewer for one <see cref="TranslatedFile.Format"/>. The component takes a <c>Model</c> parameter of type <see cref="TranslatedFileView"/>.</summary>
public interface ITranslatedFileViewer
{
    string Format { get; }
    string Title { get; }
    Type Component { get; }
}

/// <summary>The bundle and the file the browser handed to a viewer.</summary>
public sealed class TranslatedFileView
{
    public required TranslationBundle Bundle { get; init; }
    public required TranslatedFile File { get; init; }
}

public static class ArtifactPaths
{
    public static void EnsureOutputOutsideSource(string sourceDirectory, string outputDirectory)
    {
        var source = WithSeparator(Path.GetFullPath(sourceDirectory));
        var output = WithSeparator(Path.GetFullPath(outputDirectory));
        if (output.StartsWith(source, StringComparison.OrdinalIgnoreCase) || source.StartsWith(output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The translation output must be a different folder from the source. Source files are left unchanged.");
    }

    private static string WithSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
}
