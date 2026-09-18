namespace Fx.ControlKit.Reports;

/// <summary>Owns viewer conversions, without reading or overwriting reference XML files.</summary>
public sealed class ReportConversionWorkspace : IDisposable
{
    private readonly List<string> _directories = [];
    private readonly object _gate = new();
    private bool _disposed;

    public async Task<string?> ResolveAsync(string path, CancellationToken cancellationToken = default)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return File.Exists(path) ? path : null;
        if (!path.EndsWith(".rpt", StringComparison.OrdinalIgnoreCase)) return null;
        if (OperatingSystem.IsBrowser())
            throw new NotSupportedException("Crystal .rpt conversion requires server-side .NET file access.");
        var directory = Path.Combine(Path.GetTempPath(), "flex-report-viewer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".xml");
            var result = await CrystalRptToXml.ConvertAsync(path, output,
                new CrystalRptConversionOptions(ExtractSubreports: true), cancellationToken);
            lock (_gate)
            {
                // An in-flight conversion owns its directory until it can hand it to the viewer.
                ObjectDisposedException.ThrowIf(_disposed, this);
                _directories.Add(directory);
            }
            return result.ReportXmlPath;
        }
        catch
        {
            Delete(directory);
            throw;
        }
    }

    public void Dispose()
    {
        string[] directories;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            directories = _directories.ToArray();
            _directories.Clear();
        }
        foreach (var directory in directories) Delete(directory);
    }

    private static void Delete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
