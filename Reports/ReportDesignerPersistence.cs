using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public sealed record ReportDesignerSaveRequest(
    Guid DocumentId, string Xml, string SourceName, string? SavedPath, string? ExpectedHash, string? SourcePath = null);

public sealed record ReportDesignerSaveResult(bool Succeeded, string? Path = null, string? Error = null, string? Hash = null);

/// <summary>Host-selected storage with an atomic replacement and optimistic conflict check.</summary>
public static class ReportDesignerFileStore
{
    public static string Fingerprint(string xml) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml)));

    public static async Task<ReportDesignerSaveResult> SaveAsync(
        ReportDesignerSaveRequest request, string destination, string? backupDirectory = null,
        CancellationToken cancellationToken = default)
    {
        string? temporary = null;
        try
        {
            destination = System.IO.Path.GetFullPath(destination);
            if (!destination.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Report designs must be saved as XML.");
            var design = ReportDesignerXmlSerializer.FromXml(request.Xml, request.SourceName, request.SourcePath);
            var savedXml = request.Xml;
            if (design.Elements.Any(element => element.Kind == "Subreport"))
            {
                using var package = ReportDesignerPreview.Create(design);
                savedXml = package.ToSelfContainedXml();
            }
            var sameDestination = !string.IsNullOrWhiteSpace(request.SavedPath) &&
                string.Equals(destination, System.IO.Path.GetFullPath(request.SavedPath), StringComparison.Ordinal);
            async Task CheckConflictAsync()
            {
                if (File.Exists(destination))
                {
                    if (!sameDestination || request.ExpectedHash is null ||
                        Fingerprint(await File.ReadAllTextAsync(destination, cancellationToken)) != request.ExpectedHash)
                        throw new IOException("The destination already exists or has changed since it was opened. Reload it or choose a new filename.");
                }
                else if (sameDestination && request.ExpectedHash is not null)
                    throw new IOException("The saved report was removed. Choose a new filename to save this design.");
            }

            await CheckConflictAsync();
            var directory = System.IO.Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(directory);
            temporary = System.IO.Path.Combine(directory, $".fx-design-{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(temporary, savedXml, new UTF8Encoding(false), cancellationToken);
            await CheckConflictAsync();
            if (File.Exists(destination) && backupDirectory is not null)
            {
                Directory.CreateDirectory(backupDirectory);
                File.Copy(destination, System.IO.Path.Combine(backupDirectory,
                    $"{System.IO.Path.GetFileNameWithoutExtension(destination)}.{DateTime.UtcNow:yyyyMMdd_HHmmss_fffffff}.{Guid.NewGuid():N}.xml"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: sameDestination);
            return new(true, destination, Hash: Fingerprint(savedXml));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(false, Error: error.Message);
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}

/// <summary>A private snapshot package for the file-based viewer, never the save destination.</summary>
public sealed class ReportDesignerPreview : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fx-preview-{Guid.NewGuid():N}");
    private string? _sourceRoot;
    public string XmlPath { get; private set; } = "";

    public static ReportDesignerPreview Create(ReportDesignerDocument document)
    {
        var preview = new ReportDesignerPreview();
        try
        {
            Directory.CreateDirectory(preview._directory);
            preview._sourceRoot = string.IsNullOrWhiteSpace(document.SourcePath) ? null
                : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(document.SourcePath));
            var copied = new Dictionary<string, string>(StringComparer.Ordinal);
            preview.XmlPath = preview.CopyReport(ReportDesignerXmlSerializer.ToXml(document), document.SourcePath, copied, document.SourceName);
            return preview;
        }
        catch
        {
            preview.Dispose();
            throw;
        }
    }

    private string CopyReport(string xml, string sourcePath, Dictionary<string, string> copied, string? sourceName = null)
    {
        if (copied.Count >= 128)
            throw new InvalidDataException("The report snapshot exceeds 128 external subreports.");
        var fileName = System.IO.Path.GetFileName(string.IsNullOrWhiteSpace(sourcePath) ? sourceName : sourcePath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "Report.xml";
        // Keep the main report identity stable for structured data executors. Children remain in the bounded package root.
        fileName = System.IO.Path.ChangeExtension(fileName, ".xml");
        var output = System.IO.Path.Combine(_directory, fileName);
        for (var suffix = 1; copied.Values.Contains(output, StringComparer.OrdinalIgnoreCase); suffix++)
            output = System.IO.Path.Combine(_directory, $"{suffix}-{fileName}");
        copied[sourcePath] = output;
        var root = XDocument.Parse(xml);
        foreach (var obj in root.Descendants("SubreportObject"))
        {
            var name = (string?)obj.Attribute("SubreportName") ?? "";
            var report = obj.Ancestors("Report").FirstOrDefault();
            if (report?.Elements("SubReports").Elements("Report").Any(child =>
                string.Equals((string?)child.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase)) == true)
                continue;
            var hint = (string?)obj.Attribute("SubreportXmlPath") ?? "";
            if (_sourceRoot is null)
            {
                // An inserted placeholder (no inline definition, no file hint) stays a placeholder.
                if (string.IsNullOrWhiteSpace(hint)) continue;
                throw new InvalidDataException($"External subreport '{name}' needs a file-backed report package. Uploaded XML must include its subreports inline.");
            }
            var folder = System.IO.Path.GetDirectoryName(sourcePath) ?? ".";
            var candidates = new[]
            {
                string.IsNullOrWhiteSpace(hint) ? "" : System.IO.Path.GetFullPath(hint, System.IO.Path.GetFullPath(folder)),
                System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(sourcePath) + ".subreports", name + ".xml"),
                System.IO.Path.Combine(folder, name + ".xml")
            };
            var source = candidates.FirstOrDefault(File.Exists);
            if (source is null)
            {
                if (!string.IsNullOrWhiteSpace(hint))
                    throw new FileNotFoundException($"Preview requires the external subreport '{name}'.", hint);
                continue;
            }
            source = System.IO.Path.GetFullPath(source);
            if (!source.StartsWith(_sourceRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException($"External subreport '{name}' is outside the report package directory.");
            for (FileSystemInfo entry = new FileInfo(source); entry.FullName != _sourceRoot; entry = Directory.GetParent(entry.FullName)!)
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"External subreport '{name}' uses a symbolic link outside the supported package format.");
            if (!copied.TryGetValue(source, out var childPath))
            {
                var childXml = File.ReadAllText(source);
                ReportDesignerXmlSerializer.FromXml(childXml);
                childPath = CopyReport(childXml, source, copied);
            }
            obj.SetAttributeValue("SubreportXmlPath", childPath);
        }
        root.Save(output);
        return output;
    }

    internal string ToSelfContainedXml()
    {
        return Inline(XmlPath, new HashSet<string>(StringComparer.Ordinal)).ToString();

        XDocument Inline(string path, HashSet<string> ancestors)
        {
            if (!ancestors.Add(path))
                throw new InvalidDataException("A cyclic external subreport cannot be saved as a self-contained report.");
            var xml = XDocument.Load(path);
            foreach (var obj in xml.Descendants("SubreportObject").ToList())
            {
                var name = (string?)obj.Attribute("SubreportName") ?? "";
                var report = obj.Ancestors("Report").FirstOrDefault();
                if (report is null)
                    continue;
                var children = report.Element("SubReports");
                if (children?.Elements("Report").Any(child => string.Equals((string?)child.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase)) == true)
                {
                    obj.Attribute("SubreportXmlPath")?.Remove();
                    continue;
                }
                var childPath = (string?)obj.Attribute("SubreportXmlPath");
                if (string.IsNullOrEmpty(childPath))
                    continue;
                var child = Inline(childPath, ancestors).Root!;
                child.SetAttributeValue("Name", name);
                if (children is null)
                {
                    children = new XElement("SubReports");
                    report.Add(children);
                }
                children.Add(child);
                obj.Attribute("SubreportXmlPath")?.Remove();
            }
            ancestors.Remove(path);
            return xml;
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
