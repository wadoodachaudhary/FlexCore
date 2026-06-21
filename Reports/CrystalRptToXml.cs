using System.Xml.Linq;
using System.Runtime.Versioning;
using Fx.ControlKit.Reports.NativeCrystal;

namespace Fx.ControlKit.Reports;

public sealed record CrystalRptConversionOptions(
    bool ExtractSubreports = true,
    Action<string>? Progress = null,
    TimeSpan? Timeout = null);

public sealed record CrystalRptConversionResult(
    string ReportXmlPath,
    IReadOnlyDictionary<string, string> SubreportXmlPaths);

[UnsupportedOSPlatform("browser")]
public static class CrystalRptToXml
{
    private const string SubreportDirSuffix = ".subreports";

    public static async Task<CrystalRptConversionResult> ConvertAsync(
        string rptPath,
        string xmlPath,
        CrystalRptConversionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Convert(rptPath, xmlPath, options);
        }, cancellationToken);
    }

    public static CrystalRptConversionResult Convert(
        string rptPath,
        string xmlPath,
        CrystalRptConversionOptions? options = null)
    {
        options ??= new CrystalRptConversionOptions();

        var reportPath = Path.GetFullPath(rptPath);
        var outputPath = Path.GetFullPath(xmlPath);
        if (!File.Exists(reportPath))
        {
            throw new FileNotFoundException("RPT file not found.", reportPath);
        }

        var parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        if (options.ExtractSubreports)
        {
            DeleteSubreportDirectory(outputPath);
        }

        options.Progress?.Invoke("Reading Crystal RPT binary.");
        NativeCrystalRptConverter.Convert(reportPath, outputPath, options);

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException($"Conversion completed without creating XML output: {outputPath}");
        }

        options.Progress?.Invoke("Collecting generated subreport XML.");
        var subreports = options.ExtractSubreports
            ? DiscoverSubreports(outputPath)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        return new CrystalRptConversionResult(outputPath, subreports);
    }

    public static int Main(string[] args)
    {
        try
        {
            var parsed = ParseOptions(args);
            Convert(parsed.RptPath, parsed.XmlPath, new CrystalRptConversionOptions(parsed.ExtractSubreports));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> DiscoverSubreports(string outputXmlPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputXmlPath) ?? ".";
        var outputStem = StripExtension(Path.GetFileName(outputXmlPath));
        var subreportDirectoryName = outputStem + SubreportDirSuffix;
        var subreportDirectory = Path.Combine(outputDirectory, subreportDirectoryName);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        TryReadSubreportReferences(outputXmlPath, entries);
        if (!Directory.Exists(subreportDirectory))
        {
            return entries;
        }

        foreach (var path in Directory.EnumerateFiles(subreportDirectory, "*.xml", SearchOption.TopDirectoryOnly))
        {
            var name = StripExtension(Path.GetFileName(path));
            var relativePath = Path.Combine(subreportDirectoryName, Path.GetFileName(path));
            entries.TryAdd(name, relativePath);
        }

        return entries;
    }

    private static void TryReadSubreportReferences(
        string outputXmlPath,
        Dictionary<string, string> entries)
    {
        try
        {
            var document = XDocument.Load(outputXmlPath, LoadOptions.PreserveWhitespace);
            foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "SubreportReference"))
            {
                var name = (string?)element.Attribute("Name") ?? "";
                var fileName = (string?)element.Attribute("FileName") ?? "";
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(fileName))
                {
                    entries[name] = fileName;
                }
            }
        }
        catch
        {
        }
    }

    private static void DeleteSubreportDirectory(string outputXmlPath)
    {
        var outputDirectory = Path.GetDirectoryName(outputXmlPath) ?? ".";
        var outputStem = StripExtension(Path.GetFileName(outputXmlPath));
        var subreportDirectory = Path.Combine(outputDirectory, outputStem + SubreportDirSuffix);
        if (Directory.Exists(subreportDirectory))
        {
            Directory.Delete(subreportDirectory, recursive: true);
        }
    }

    private static Options ParseOptions(string[] args)
    {
        var extractSubreports = true;
        var positional = new List<string>();
        foreach (var arg in args)
        {
            if (arg is "--help" or "-h")
            {
                throw new ArgumentException("Usage: CrystalRptToXml [--extract-subreports|--no-extract-subreports] input.rpt output.xml");
            }
            else if (arg == "--extract-subreports")
            {
                extractSubreports = true;
            }
            else if (arg == "--no-extract-subreports")
            {
                extractSubreports = false;
            }
            else
            {
                positional.Add(arg);
            }
        }

        if (positional.Count != 2)
        {
            throw new ArgumentException("Usage: CrystalRptToXml [--extract-subreports|--no-extract-subreports] input.rpt output.xml");
        }

        return new Options(positional[0], positional[1], extractSubreports);
    }

    private static string StripExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot <= 0 ? fileName : fileName[..dot];
    }

    private sealed record Options(string RptPath, string XmlPath, bool ExtractSubreports);
}
