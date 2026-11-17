using System.IO.Compression;

namespace Fx.ControlKit.Migration;

/// <summary>Limits for <see cref="LegacyProjectIntake.Extract"/>. Exceeding one rejects the archive.</summary>
public sealed class LegacyArchiveLimits
{
    public long MaxArchiveBytes { get; init; } = 100L * 1024 * 1024;
    public long MaxEntryBytes { get; init; } = 32L * 1024 * 1024;
    public int MaxEntries { get; init; } = 5_000;
    public long MaxExtractedBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when a limit is not positive.</summary>
    public void Validate()
    {
        if (MaxArchiveBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxArchiveBytes));
        if (MaxEntryBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxEntryBytes));
        if (MaxEntries < 1) throw new ArgumentOutOfRangeException(nameof(MaxEntries));
        if (MaxExtractedBytes < 1) throw new ArgumentOutOfRangeException(nameof(MaxExtractedBytes));
    }
}

/// <summary>
/// Opens an uploaded project archive and describes what is inside.
/// Mutarjim calls <see cref="Extract(Stream, string, LegacyArchiveLimits?)"/> and then <see cref="Inspect"/>.
/// </summary>
public static class LegacyProjectIntake
{
    /// <summary>Extracts <paramref name="archive"/> into <paramref name="destination"/>. Rejects zip-slip paths and oversized archives.</summary>
    public static LegacyExtraction Extract(Stream archive, string destination, LegacyArchiveLimits? limits = null)
    {
        if (archive is null) throw new ArgumentNullException(nameof(archive));
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Destination is required.", nameof(destination));
        var rules = limits ?? new LegacyArchiveLimits();
        rules.Validate();
        if (archive.CanSeek && archive.Length > rules.MaxArchiveBytes)
            throw new InvalidDataException($"The archive is {archive.Length} bytes, above the limit of {rules.MaxArchiveBytes}.");

        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        var warnings = new List<string>();
        long extracted = 0;
        var seen = 0;

        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            seen++;
            if (seen > rules.MaxEntries)
                throw new InvalidDataException($"The archive has more than {rules.MaxEntries} entries.");
            if (string.IsNullOrEmpty(entry.Name) && (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')))
            {
                var directory = CombineSafe(root, rootPrefix, entry.FullName);
                Directory.CreateDirectory(directory);
                continue;
            }
            if (entry.Length > rules.MaxEntryBytes)
                throw new InvalidDataException($"'{entry.FullName}' is {entry.Length} bytes, above the entry limit of {rules.MaxEntryBytes}.");
            if (IsSymlink(entry))
            {
                warnings.Add($"Skipped symbolic link '{entry.FullName}'.");
                continue;
            }

            var target = CombineSafe(root, rootPrefix, entry.FullName);
            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            extracted += entry.Length;
            if (extracted > rules.MaxExtractedBytes)
                throw new InvalidDataException($"Extracted size would exceed {rules.MaxExtractedBytes} bytes.");
            entry.ExtractToFile(target, overwrite: true);
            files.Add(target);
        }

        return new LegacyExtraction
        {
            Destination = root,
            Files = files,
            ExtractedBytes = extracted,
            Warnings = warnings
        };
    }

    /// <summary>Extracts the archive at <paramref name="archivePath"/>.</summary>
    public static LegacyExtraction ExtractFile(string archivePath, string destination, LegacyArchiveLimits? limits = null)
    {
        if (string.IsNullOrWhiteSpace(archivePath)) throw new ArgumentException("Archive path is required.", nameof(archivePath));
        var info = new FileInfo(archivePath);
        if (!info.Exists) throw new FileNotFoundException("Archive was not found.", archivePath);
        var rules = limits ?? new LegacyArchiveLimits();
        rules.Validate();
        if (info.Length > rules.MaxArchiveBytes)
            throw new InvalidDataException($"The archive is {info.Length} bytes, above the limit of {rules.MaxArchiveBytes}.");
        using var stream = File.OpenRead(archivePath);
        return Extract(stream, destination, rules);
    }

    /// <summary>Classifies solutions and projects under <paramref name="directory"/>.</summary>
    public static LegacyProjectInventory Inspect(string directory) => LegacyProjectInspector.Inspect(directory);

    private static string CombineSafe(string root, string rootPrefix, string entryName)
    {
        var relative = entryName.Replace('\\', '/').Trim();
        if (relative.Contains('\0'))
            throw new InvalidDataException("An archive entry name contains NUL.");
        if (relative.Length == 0)
            throw new InvalidDataException("An archive entry has an empty name.");
        if (Path.IsPathRooted(entryName) || relative.StartsWith('/') || relative.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException($"Archive entry '{entryName}' is an absolute path.");

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            throw new InvalidDataException($"Archive entry '{entryName}' has no path.");
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                throw new InvalidDataException($"Archive entry '{entryName}' escapes the destination.");
            if (segment.Contains(':') || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException($"Archive entry '{entryName}' has an invalid name.");
        }

        var combined = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!combined.StartsWith(rootPrefix, StringComparison.Ordinal) && !string.Equals(combined, root, StringComparison.Ordinal))
            throw new InvalidDataException($"Archive entry '{entryName}' escapes the destination.");
        return combined;
    }

    private static bool IsSymlink(ZipArchiveEntry entry)
    {
        var mode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        return (mode & 0xF000) == 0xA000;
    }
}
