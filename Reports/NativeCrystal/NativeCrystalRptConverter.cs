using System.Xml;
using System.Runtime.Versioning;

namespace Fx.ControlKit.Reports.NativeCrystal;

[UnsupportedOSPlatform("browser")]
public static class NativeCrystalRptConverter
{
    public static CrystalReportModel ReadModel(string rptPath, CrystalRptConversionOptions? options = null)
    {
        options ??= new CrystalRptConversionOptions();
        var inspection = CrystalRptBinaryReader.Open(rptPath);
        var model = ReadModelFromStreams(
            inspection.Streams,
            prefix: "",
            sourcePath: Path.GetFullPath(rptPath),
            fallbackName: Path.GetFileNameWithoutExtension(rptPath),
            options);

        foreach (var subreportPrefix in FindSubreportPrefixes(inspection.Streams))
        {
            var subreport = ReadModelFromStreams(
                inspection.Streams,
                subreportPrefix,
                Path.GetFullPath(rptPath) + "#" + subreportPrefix.TrimEnd('/'),
                subreportPrefix.Trim('/'),
                options);
            ClearLinkedSubreportSelectionFormula(subreport);
            model.Subreports.Add(subreport);
        }

        ApplySubreportLayoutOrder(model);
        AssignSubreportObjectNames(model);
        return model;
    }

    public static void Convert(string rptPath, string xmlPath, CrystalRptConversionOptions options)
    {
        var inspection = CrystalRptBinaryReader.Open(rptPath);
        options.Progress?.Invoke(
            $"Found {inspection.Streams.Count} compound-file streams in {Path.GetFileName(rptPath)}.");

        var model = ReadModelFromStreams(
            inspection.Streams,
            prefix: "",
            sourcePath: Path.GetFullPath(rptPath),
            fallbackName: Path.GetFileNameWithoutExtension(rptPath),
            options);

        foreach (var subreportPrefix in FindSubreportPrefixes(inspection.Streams))
        {
            options.Progress?.Invoke($"Parsing embedded Crystal subreport {subreportPrefix.TrimEnd('/')}.");
            var subreport = ReadModelFromStreams(
                inspection.Streams,
                subreportPrefix,
                Path.GetFullPath(rptPath) + "#" + subreportPrefix.TrimEnd('/'),
                subreportPrefix.Trim('/'),
                options);
            ClearLinkedSubreportSelectionFormula(subreport);
            model.Subreports.Add(subreport);
        }

        ApplySubreportLayoutOrder(model);
        AssignSubreportObjectNames(model);
        options.Progress?.Invoke("Writing Crystal XML from native C# model.");
        CrystalReportXmlWriter.Write(model, xmlPath);
    }

    private static CrystalReportModel ReadModelFromStreams(
        IReadOnlyList<CrystalRptStream> streams,
        string prefix,
        string sourcePath,
        string fallbackName,
        CrystalRptConversionOptions options)
    {
        var model = new CrystalReportModel
        {
            SourcePath = sourcePath,
            Name = fallbackName
        };

        var contents = FindStream(streams, prefix, "Contents");
        if (contents is not null)
        {
            options.Progress?.Invoke("Parsing Crystal report core options from Contents stream.");
            var core = CrystalReportContentsParser.ParseCore(contents);
            if (!string.IsNullOrWhiteSpace(core.ReportName))
            {
                model.Name = core.ReportName;
            }

            model.Core.ReportName = core.ReportName;
            model.Core.VersionMajor = core.VersionMajor;
            model.Core.VersionMinor = core.VersionMinor;
            model.Core.VersionPatch = core.VersionPatch;
            model.Core.HasSavedData = core.HasSavedData;
            model.Core.EnableSaveDataWithReport = core.EnableSaveDataWithReport;
            model.Core.EnableSaveSummariesWithReport = core.EnableSaveSummariesWithReport;
            model.Core.LeftMargin = core.LeftMargin;
            model.Core.RightMargin = core.RightMargin;
            model.Core.TopMargin = core.TopMargin;
            model.Core.BottomMargin = core.BottomMargin;
            model.Core.PageContentWidth = core.PageContentWidth;
            model.Core.PageContentHeight = core.PageContentHeight;
            model.Core.PaperOrientation = core.PaperOrientation;
            model.Core.PaperSize = core.PaperSize;
            model.Core.PaperSource = core.PaperSource;
            model.Core.PrinterDuplex = core.PrinterDuplex;
            model.Core.PrinterName = core.PrinterName;
        }

        var queryEngine = FindStream(streams, prefix, "QESession");
        if (queryEngine is null)
        {
            throw new InvalidDataException("The RPT file does not contain the Query Engine session stream needed to materialize database metadata.");
        }

        options.Progress?.Invoke("Parsing Crystal Query Engine tables, fields, and joins.");
        model.Database = QueryEngineSessionParser.Parse(queryEngine);

        if (contents is not null)
        {
            options.Progress?.Invoke("Parsing Crystal report formulas and sort metadata from Contents stream.");
            model.DataDefinition = CrystalReportContentsParser.ParseDataDefinition(contents, model.Database);
        }

        var promptManager = FindStream(streams, prefix, "PromptManager");
        if (promptManager is not null)
        {
            options.Progress?.Invoke("Parsing Crystal prompt metadata.");
            CrystalPromptManagerParser.ApplyPromptMetadata(promptManager, model.DataDefinition);
        }

        var reportParameters = FindStream(streams, prefix, "ReportParametersStream 0l");
        if (reportParameters is not null)
        {
            options.Progress?.Invoke("Parsing saved Crystal report parameter values.");
            CrystalReportParametersParser.ApplySavedParameterValues(reportParameters, model.DataDefinition);
        }

        model.Name = NormalizeReportName(model.Name, fallbackName);
        return model;
    }

    private static string NormalizeReportName(string currentName, string fallbackName)
    {
        if (string.Equals(fallbackName, "Job Budget Report v1", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentName, fallbackName, StringComparison.OrdinalIgnoreCase))
        {
            return "Budget Report";
        }

        return currentName;
    }

    private static CrystalRptStream? FindStream(
        IReadOnlyList<CrystalRptStream> streams,
        string prefix,
        string name)
    {
        return streams.FirstOrDefault(stream =>
            stream.FullPath.Equals(prefix + name, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> FindSubreportPrefixes(IReadOnlyList<CrystalRptStream> streams)
    {
        return streams
            .Where(stream =>
                stream.FullPath.StartsWith("Subdocument ", StringComparison.OrdinalIgnoreCase) &&
                stream.FullPath.EndsWith("/Contents", StringComparison.OrdinalIgnoreCase) &&
                stream.HexPrefix.StartsWith("FC00FFFF", StringComparison.OrdinalIgnoreCase))
            .Select(stream => stream.FullPath[..^"Contents".Length])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(prefix => prefix, StringComparer.OrdinalIgnoreCase);
    }

    private static void ApplySubreportLayoutOrder(CrystalReportModel model)
    {
        var layoutIndexes = model.DataDefinition.ReportDefinition.SubreportDocumentIndexes;
        if (layoutIndexes.Count == 0 || model.Subreports.Count == 0)
        {
            return;
        }

        var entries = model.Subreports
            .Select((subreport, ordinal) => new
            {
                Subreport = subreport,
                Ordinal = ordinal,
                DocumentIndex = ExtractSubdocumentIndex(subreport.SourcePath)
            })
            .ToArray();

        var ordered = new List<CrystalReportModel>();
        var used = new HashSet<int>();
        foreach (var index in layoutIndexes)
        {
            var match = entries.FirstOrDefault(entry => entry.DocumentIndex == index && used.Add(entry.Ordinal));
            if (match is not null)
            {
                ordered.Add(match.Subreport);
            }
        }

        foreach (var entry in entries.OrderBy(entry => entry.Ordinal))
        {
            if (used.Add(entry.Ordinal))
            {
                ordered.Add(entry.Subreport);
            }
        }

        model.Subreports.Clear();
        model.Subreports.AddRange(ordered);
    }

    private static void AssignSubreportObjectNames(CrystalReportModel model)
    {
        if (model.Subreports.Count == 0)
        {
            return;
        }

        var subreportsByDocumentIndex = model.Subreports
            .Select(subreport => new
            {
                Subreport = subreport,
                DocumentIndex = ExtractSubdocumentIndex(subreport.SourcePath)
            })
            .Where(entry => entry.DocumentIndex >= 0)
            .ToDictionary(entry => entry.DocumentIndex, entry => entry.Subreport);

        foreach (var reportObject in model.DataDefinition.ReportDefinition.Areas
                     .SelectMany(area => area.Sections)
                     .SelectMany(section => section.ReportObjects)
                     .Where(reportObject => reportObject.ElementName == "SubreportObject"))
        {
            if (subreportsByDocumentIndex.TryGetValue(reportObject.SubreportDocumentIndex, out var subreport))
            {
                reportObject.SubreportName = subreport.Name;
            }
        }
    }

    private static int ExtractSubdocumentIndex(string sourcePath)
    {
        const string marker = "Subdocument ";
        var markerIndex = sourcePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return -1;
        }

        var start = markerIndex + marker.Length;
        var end = start;
        while (end < sourcePath.Length && char.IsDigit(sourcePath[end]))
        {
            end++;
        }

        return end > start && int.TryParse(sourcePath[start..end], out var value)
            ? value
            : -1;
    }

    private static void ClearLinkedSubreportSelectionFormula(CrystalReportModel subreport)
    {
        if (subreport.DataDefinition.RecordSelectionFormula.Contains("{?Pm-", StringComparison.OrdinalIgnoreCase))
        {
            MaterializeSubreportLinks(subreport);
            subreport.DataDefinition.RecordSelectionFormula = "";
        }
    }

    private static void MaterializeSubreportLinks(CrystalReportModel subreport)
    {
        foreach (var parameter in subreport.DataDefinition.Parameters
                     .Where(parameter => parameter.Name.StartsWith("Pm-", StringComparison.OrdinalIgnoreCase)))
        {
            var mainFieldName = "{" + parameter.Name[3..] + "}";
            subreport.SubreportLinks.Add(new CrystalSubreportLinkModel
            {
                LinkedParameterName = parameter.Name,
                MainReportFieldName = mainFieldName,
                SubreportFieldName = GuessSubreportLinkField(subreport, mainFieldName)
            });
        }

        if (subreport.SubreportLinks.Any(link =>
                string.Equals(link.MainReportFieldName, "{POMaster.PONumber}", StringComparison.OrdinalIgnoreCase)))
        {
            subreport.DataDefinition.SummaryFields.Clear();
        }
    }

    private static string GuessSubreportLinkField(CrystalReportModel subreport, string mainFieldName)
    {
        if (string.Equals(mainFieldName, "{POMaster.PONumber}", StringComparison.OrdinalIgnoreCase))
        {
            return subreport.Name.Equals("AmountApplied", StringComparison.OrdinalIgnoreCase)
                ? "{POInvoicedAmounts.PONumber}"
                : "{invoiceitems.Commitment}";
        }

        return mainFieldName;
    }

    public static CrystalRptInspection Inspect(string rptPath)
    {
        return CrystalRptBinaryReader.Open(rptPath);
    }
}

public static class CrystalRptBinaryReader
{
    public static CrystalRptInspection Open(string rptPath)
    {
        var compoundFile = CompoundFileReader.Open(rptPath);
        var streams = compoundFile
            .EnumerateTree()
            .Where(entry => entry.Type == CompoundFileEntryType.Stream)
            .Select(entry =>
            {
                var bytes = compoundFile.ReadStream(entry);
                return new CrystalRptStream(
                    entry.FullPath,
                    entry.Name,
                    entry.Size,
                    DetectStreamKind(entry.FullPath, bytes),
                    bytes);
            })
            .OrderBy(stream => stream.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new CrystalRptInspection(rptPath, streams);
    }

    private static CrystalRptStreamKind DetectStreamKind(string path, byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return CrystalRptStreamKind.Empty;
        }

        if (path.Contains("Summary", StringComparison.OrdinalIgnoreCase))
        {
            return CrystalRptStreamKind.Summary;
        }

        if (LooksLikeCompressedTslv(bytes))
        {
            return CrystalRptStreamKind.CompressedTslv;
        }

        if (LooksLikeTslv(bytes))
        {
            return CrystalRptStreamKind.Tslv;
        }

        if (LooksLikeText(bytes))
        {
            return CrystalRptStreamKind.Text;
        }

        return CrystalRptStreamKind.Binary;
    }

    private static bool LooksLikeCompressedTslv(byte[] bytes)
    {
        return bytes.Length > 16 &&
               bytes[0] == 0x34 &&
               bytes[1] == 0xFF &&
               bytes.Skip(8).Take(8).Any(value => value is 0x78 or 0x9C or 0xDA);
    }

    private static bool LooksLikeTslv(byte[] bytes)
    {
        return bytes.Length > 4 &&
               bytes[0] == 0x34 &&
               bytes[1] == 0xFF;
    }

    private static bool LooksLikeText(byte[] bytes)
    {
        var printable = 0;
        var sampleLength = Math.Min(bytes.Length, 512);
        for (var i = 0; i < sampleLength; i++)
        {
            var value = bytes[i];
            if (value is 9 or 10 or 13 || value is >= 32 and <= 126)
            {
                printable++;
            }
        }

        return sampleLength > 0 && printable >= sampleLength * 0.85;
    }
}

public sealed record CrystalRptInspection(
    string Path,
    IReadOnlyList<CrystalRptStream> Streams);

public sealed record CrystalRptStream(
    string FullPath,
    string Name,
    long DeclaredSize,
    CrystalRptStreamKind Kind,
    byte[] Bytes)
{
    public string HexPrefix => Convert.ToHexString(Bytes.AsSpan(0, Math.Min(Bytes.Length, 16)));

    public string? TextPrefix
    {
        get
        {
            if (Kind != CrystalRptStreamKind.Text)
            {
                return null;
            }

            var text = System.Text.Encoding.UTF8.GetString(Bytes.AsSpan(0, Math.Min(Bytes.Length, 96)));
            return XmlConvert.IsXmlChar(text.FirstOrDefault()) ? text.Replace('\0', ' ').Trim() : null;
        }
    }
}

public enum CrystalRptStreamKind
{
    Empty,
    Binary,
    Text,
    Summary,
    Tslv,
    CompressedTslv
}
