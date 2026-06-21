using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Reports;

[UnsupportedOSPlatform("browser")]
public sealed class RptToXmlConverter
{
    private const string SubreportDirSuffix = ".subreports";
    private static readonly SemaphoreSlim ConvertGate = new(initialCount: 1, maxCount: 1);

    private readonly ILogger<RptToXmlConverter> _logger;

    public RptToXmlConverter(ILogger<RptToXmlConverter> logger)
    {
        _logger = logger;
    }

    public async Task ConvertAsync(string rptPath, string xmlPath, CancellationToken cancellationToken = default)
    {
        ValidateJob(rptPath, xmlPath);

        await ConvertGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunConverterAsync(rptPath, xmlPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ConvertGate.Release();
        }
    }

    public async Task ConvertBatchAsync(
        IReadOnlyList<(string RptPath, string XmlPath)> jobs,
        CancellationToken cancellationToken = default)
    {
        if (jobs is null || jobs.Count == 0) return;

        foreach (var (rpt, xml) in jobs)
        {
            ValidateJob(rpt, xml);
        }

        await ConvertGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("Converting {Count} RPT file(s) to XML", jobs.Count);
            foreach (var (rpt, xml) in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RunConverterAsync(rpt, xml, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ConvertGate.Release();
        }
    }

    private async Task RunConverterAsync(string rptPath, string xmlPath, CancellationToken cancellationToken)
    {
        var reportPath = Path.GetFullPath(rptPath);
        var outputPath = Path.GetFullPath(xmlPath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
        DeleteSubreportDirectory(outputPath);

        _logger.LogInformation(
            "Converting Crystal RPT with native C# parser: {Rpt} -> {Xml}",
            reportPath,
            outputPath);

        var result = await CrystalRptToXml.ConvertAsync(
            reportPath,
            outputPath,
            new CrystalRptConversionOptions(
                ExtractSubreports: true,
                Progress: message => _logger.LogDebug("RptToXml: {Message}", message)),
            cancellationToken).ConfigureAwait(false);

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException($"Native RptToXml conversion completed without creating XML output: {outputPath}");
        }

        _logger.LogInformation(
            "Report XML converted: {Xml}; subreports: {SubreportCount}",
            result.ReportXmlPath,
            result.SubreportXmlPaths.Count);
    }

    private static void DeleteSubreportDirectory(string outputXmlPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputXmlPath) ?? ".";
        var outputStem = Path.GetFileNameWithoutExtension(outputXmlPath);
        var subreportDirectory = Path.Combine(outputDirectory, outputStem + SubreportDirSuffix);
        if (Directory.Exists(subreportDirectory))
        {
            Directory.Delete(subreportDirectory, recursive: true);
        }
    }

    private static void ValidateJob(string rptPath, string xmlPath)
    {
        if (string.IsNullOrWhiteSpace(rptPath)) throw new ArgumentException("rptPath must be provided.", nameof(rptPath));
        if (string.IsNullOrWhiteSpace(xmlPath)) throw new ArgumentException("xmlPath must be provided.", nameof(xmlPath));
        if (!File.Exists(rptPath)) throw new FileNotFoundException("Crystal report file not found.", rptPath);
    }
}
