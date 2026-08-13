namespace Fx.ControlKit;

public enum FileBrowserSelectionMode
{
    Files,
    Directories,
    FilesAndDirectories
}

public sealed class FileBrowserItem
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long? Size { get; init; }
    public DateTime? LastModified { get; init; }
    public string Extension { get; init; } = string.Empty;

    public string TypeText => IsDirectory
        ? "Folder"
        : string.IsNullOrWhiteSpace(Extension) ? "File" : $"{Extension.TrimStart('.').ToUpperInvariant()} File";
}
