namespace Fx.ControlKit.Conversion;

/// <summary>
/// Layout of <see cref="ConverterShell"/>. SingleFile is one source editor and one
/// output editor. FileBrowser adds a source list and a converted-file list.
/// </summary>
public enum ConverterShellMode
{
    SingleFile,
    FileBrowser,

    /// <summary>
    /// Replaces the editors with a side-by-side live preview.
    /// The host turns it on with EnableRunPreview or by setting this mode.
    /// </summary>
    RunPreview
}

/// <summary>Which pane is filling the shell, if either.</summary>
public enum ConverterPane
{
    None,
    Source,
    Output
}

/// <summary>
/// One entry in a host-supplied kind list. FlexCore does not ship a language or
/// format catalog; the host binds whatever identifiers it understands.
/// </summary>
public sealed record ConverterKind
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
}

/// <summary>A named text file in the source or converted list.</summary>
public sealed record ConverterFile
{
    /// <summary>Identifier used for the synthetic file on a single-file <see cref="ConverterRequest"/>.</summary>
    public const string SingleFileId = "single";

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string Content { get; init; } = "";
    public string? MediaType { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? (string.IsNullOrWhiteSpace(RelativePath) ? "file" : Path.GetFileName(RelativePath.Replace('\\', '/')))
        : Name;
}

/// <summary>Snapshot passed to the host convert callback.</summary>
public sealed record ConverterRequest
{
    public ConverterShellMode Mode { get; init; }
    public string? SourceKindId { get; init; }
    public string? TargetKindId { get; init; }

    /// <summary>Text currently shown in the source editor.</summary>
    public string SourceText { get; init; } = "";

    /// <summary>File name shown for the source editor, when the user uploaded one.</summary>
    public string? SourceFileName { get; init; }

    public string Instructions { get; init; } = "";

    /// <summary>
    /// Files to convert. In <see cref="ConverterShellMode.FileBrowser"/> this is the
    /// source list. In <see cref="ConverterShellMode.SingleFile"/> it is either empty
    /// or one synthetic file whose <see cref="ConverterFile.Id"/> is
    /// <see cref="ConverterFile.SingleFileId"/>.
    /// </summary>
    public IReadOnlyList<ConverterFile> SourceFiles { get; init; } = [];

    public string? SelectedSourceFileId { get; init; }
}

/// <summary>Value returned by the host convert callback.</summary>
public sealed record ConverterResult
{
    public string OutputText { get; init; } = "";
    public string? OutputFileName { get; init; }
    public IReadOnlyList<ConverterFile> OutputFiles { get; init; } = [];
    public string? StatusMessage { get; init; }
    public bool Succeeded { get; init; } = true;

    public static ConverterResult FromText(string text, string? fileName = null, string? status = null) => new()
    {
        OutputText = text ?? "",
        OutputFileName = fileName,
        StatusMessage = status
    };

    public static ConverterResult FromFiles(IReadOnlyList<ConverterFile> files, string? status = null)
    {
        var list = files ?? [];
        var first = list.Count > 0 ? list[0] : null;
        return new ConverterResult
        {
            OutputFiles = list,
            OutputText = first?.Content ?? "",
            OutputFileName = first?.Name,
            StatusMessage = status
        };
    }

    public static ConverterResult Failed(string message) => new()
    {
        Succeeded = false,
        StatusMessage = message
    };
}

/// <summary>Text file the output pane can save.</summary>
public sealed record ConverterDownload
{
    public required string FileName { get; init; }
    public required string Content { get; init; }
    public string MediaType { get; init; } = "text/plain";

    public string Href
    {
        get
        {
            var type = string.IsNullOrWhiteSpace(MediaType) ? "text/plain" : MediaType.Trim();
            if (!type.Contains("charset", StringComparison.OrdinalIgnoreCase))
                type += ";charset=utf-8";
            return "data:" + type + "," + Uri.EscapeDataString(Content ?? "");
        }
    }
}
