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
            model.Subreports.Add(subreport);
        }

        ApplySubreportLayoutOrder(model);
        AssignSubreportObjectNames(model);
        ApplySubreportLinks(model, options);
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
            model.Subreports.Add(subreport);
        }

        ApplySubreportLayoutOrder(model);
        AssignSubreportObjectNames(model);
        ApplySubreportLinks(model, options);
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

        // Report summary info is metadata only: an unreadable stream leaves it blank and is reported.
        if (FindStream(streams, prefix, "\u0005SummaryInformation") is { } summary)
        {
            try { model.SummaryInformation = CrystalSummaryInformationParser.Parse(summary.Bytes); }
            catch (InvalidDataException error)
            {
                var diagnostic = new CrystalConversionDiagnostic("CRYSTAL_SUMMARY_INFORMATION", model.Name, "", "", "",
                    $"The report summary information (title, author, comments) could not be read ({error.Message}); it is left blank.");
                model.ConversionDiagnostics.Add(diagnostic);
                options.Progress?.Invoke($"[{diagnostic.Code}] {model.Name}: {diagnostic.Message}");
            }
        }

        var queryEngine = FindStream(streams, prefix, "QESession");
        var legacyDatabase = FindStream(streams, prefix, "Database (TLV)");
        if (queryEngine is null && legacyDatabase is null)
        {
            throw new InvalidDataException("The RPT file does not contain the Query Engine session stream needed to materialize database metadata.");
        }

        options.Progress?.Invoke("Parsing Crystal Query Engine tables, fields, and joins.");
        model.Database = queryEngine is not null
            ? QueryEngineSessionParser.Parse(queryEngine)
            : LegacyCrystalDatabaseParser.Parse(legacyDatabase!);

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
        foreach (var warning in model.Database.ParseWarnings.Concat(model.DataDefinition.ParseWarnings))
        {
            model.ConversionDiagnostics.Add(new("CRYSTAL_PARTIAL_EXTRACTION", model.Name, "", "", "", warning));
            options.Progress?.Invoke($"[CRYSTAL_PARTIAL_EXTRACTION] {model.Name}: {warning}");
        }
        // The formula engine that runs the custom functions reads their declarations; a function it cannot compile keeps its text and is reported.
        var functions = CrystalCustomFunction.CompileAll(model.DataDefinition.CustomFunctions.Where(f => f.Name.Trim().Length > 0 && !string.IsNullOrWhiteSpace(f.FormulaText))
            .Select(f => new CrystalCustomFunctionSource(f.Name, f.FormulaText, f.Syntax == 1 ? "Basic" : "Crystal")));
        foreach (var function in model.DataDefinition.CustomFunctions)
        {
            string? problem;
            if (functions.TryGetValue(function.Name.Trim(), out var compiled))
            {
                function.Parameters = compiled.Parameters;
                problem = function.ArgumentDescriptions.Count > 0 && compiled.Parameters.Count != function.ArgumentDescriptions.Count
                    ? $"Custom function '{function.Name}' declares {compiled.Parameters.Count} argument(s) but the report stores metadata for {function.ArgumentDescriptions.Count}; its arguments are emitted by position."
                    : null;
            }
            else problem = $"Custom function '{function.Name}' cannot be run by the formula engine ({functions.Errors.GetValueOrDefault(function.Name.Trim()) ?? "it has no text"}); its text is kept and formulas calling it fail.";
            if (problem is null) continue;
            var diagnostic = new CrystalConversionDiagnostic("CRYSTAL_CUSTOM_FUNCTION", model.Name, "", function.Name, "CustomFunction", problem);
            model.ConversionDiagnostics.Add(diagnostic);
            options.Progress?.Invoke($"[{diagnostic.Code}] {model.Name}/{function.Name}: {diagnostic.Message}");
        }
        foreach (var area in model.DataDefinition.ReportDefinition.Areas)
        foreach (var section in area.Sections)
        foreach (var obj in section.ReportObjects.Where(obj => obj.UnsupportedSource is not null))
        {
            var source = obj.UnsupportedSource!;
            source.Stream = contents?.FullPath ?? prefix + "Contents";
            if (obj.Analysis is { } analysis && area.Kind is "GroupHeader" or "GroupFooter")
            {
                var group = area.GroupPairOrder > 0 ? area.GroupPairOrder : area.GroupIndex;
                if (group > 0 && group <= model.DataDefinition.Groups.Count)
                    analysis.GroupScope = model.DataDefinition.Groups[group - 1].ConditionField;
                else
                {
                    obj.Analysis = null;
                    obj.AnalysisDiagnostic = "The containing analytical group scope could not be resolved.";
                }
            }
            if (obj.Analysis is not null)
            {
                var defaults = new CrystalConversionDiagnostic("CRYSTAL_ANALYSIS_STYLE_DEFAULTS", model.Name, section.Name, obj.Name, obj.Kind,
                    "Native analytical field bindings were imported. Legacy drawing styles, cell formatting and total visibility use editable FlexKit defaults; original TSLV bytes are retained.");
                model.ConversionDiagnostics.Add(defaults);
                options.Progress?.Invoke($"[{defaults.Code}] {model.Name}/{section.Name}/{obj.Name}: {defaults.Message}");
                continue;
            }
            var message = $"{obj.Kind} conversion preserves available identity, bounds, common formatting and opaque TSLV bytes; chart data/series and cross-tab grouping/cells are not interpreted or rendered."
                + (source.MetadataDiagnostics.Count == 0 ? "" : " " + string.Join(" ", source.MetadataDiagnostics))
                + (obj.AnalysisDiagnostic.Length == 0 ? "" : " " + obj.AnalysisDiagnostic);
            var diagnostic = new CrystalConversionDiagnostic("CRYSTAL_UNSUPPORTED_OBJECT", model.Name, section.Name, obj.Name, obj.Kind, message);
            model.ConversionDiagnostics.Add(diagnostic);
            options.Progress?.Invoke($"[{diagnostic.Code}] {model.Name}/{section.Name}/{obj.Name}: {message}");
        }
        CrystalPictureStorage.Apply(model, streams, prefix, options.Progress);
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
                stream.FullPath.Split('/')[..^1].All(part => part.StartsWith("Subdocument ", StringComparison.OrdinalIgnoreCase)))
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

    // Crystal passes main-report values into subreport parameters through the links stored with the subreport
    // object; the subreport's record selection formula does the filtering (linking only adds or removes a
    // "{field} = {?Pm-x}" operand of its top-level And). The stored subreport field is metadata that the runtime only
    // uses for dependency tracking and can be stale, so a link becomes a row filter (SubreportFieldName) only when the
    // formula ANDs exactly one such database-field test for its discrete, single-valued parameter, and a stored field
    // that disagrees with the formula is reported. A formula made of nothing but those tests is fully expressed by the
    // links and is dropped; any other formula is kept verbatim.
    private static void ApplySubreportLinks(CrystalReportModel model, CrystalRptConversionOptions options)
    {
        var subreportObjects = model.DataDefinition.ReportDefinition.Areas
            .SelectMany(area => area.Sections)
            .SelectMany(section => section.ReportObjects)
            .Where(reportObject => reportObject.ElementName == "SubreportObject")
            .ToList();
        foreach (var subreport in model.Subreports)
        {
            var documentIndex = ExtractSubdocumentIndex(subreport.SourcePath);
            var data = subreport.DataDefinition;
            // The formula engine compiles at most 32 KB of formula text; a longer formula is kept whole and its links only supply parameters.
            var conjuncts = data.RecordSelectionFormulaSyntax == 0 && data.RecordSelectionFormula.Length <= 32768 ? SelectionConjuncts(data.RecordSelectionFormula) : null;
            if (data.RecordSelectionFormula.Length > 32768)
                AddLinkDiagnostic(subreport, options, $"The record selection formula of subreport '{subreport.Name}' exceeds 32 KB, so its links are not matched to it; they only supply parameters.");
            var filterTests = 0;
            foreach (var stored in subreportObjects
                         .Where(reportObject => documentIndex >= 0 && reportObject.SubreportDocumentIndex == documentIndex)
                         .SelectMany(reportObject => reportObject.StoredSubreportLinks))
            {
                var parameter = data.Parameters.FirstOrDefault(candidate => candidate.Id == stored.ParameterId);
                if (parameter is null || stored.MainReportFieldName.Length == 0)
                {
                    AddLinkDiagnostic(subreport, options, $"A stored link of subreport '{subreport.Name}' names " +
                        (parameter is null ? $"parameter #{stored.ParameterId}, which the subreport does not define" : $"no main-report field for parameter '{parameter.Name}'") +
                        "; the link is not emitted.");
                    continue;
                }

                if (subreport.SubreportLinks.Any(link => string.Equals(link.LinkedParameterName, parameter.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var field = "";
                if (conjuncts is not null && !parameter.AllowMultiple && !parameter.AllowRange)
                {
                    var tests = conjuncts.Select(conjunct => LinkTestField(conjunct, parameter.Name)).Where(test => test is not null).ToList();
                    if (tests.Count == 1)
                    {
                        field = tests[0]!;
                        filterTests++;
                    }
                }

                var storedField = stored.SubreportFieldType < 0
                    ? ""
                    : CrystalReportContentsParser.ResolveFieldManagerReference(data, stored.SubreportFieldType, stored.SubreportFieldIndex);
                if (storedField.Length > 0 && (field.Length > 0
                        ? !storedField.Equals(field, StringComparison.OrdinalIgnoreCase)
                        : !data.RecordSelectionFormula.Contains(storedField, StringComparison.OrdinalIgnoreCase)))
                {
                    AddLinkDiagnostic(subreport, options, $"Subreport '{subreport.Name}' stores {storedField} as the linked field for {parameter.FormulaName}, " +
                        (field.Length > 0
                            ? $"but its record selection formula tests {field}; the link filters on {field}, as Crystal filters by the formula."
                            : $"but its record selection formula does not use {storedField}; the link only supplies the parameter, as Crystal filters by the formula."));
                }

                subreport.SubreportLinks.Add(new CrystalSubreportLinkModel
                {
                    LinkedParameterName = parameter.Name,
                    MainReportFieldName = stored.MainReportFieldName,
                    SubreportFieldName = field
                });
            }

            foreach (var parameter in data.Parameters.Where(parameter =>
                         parameter.Name.StartsWith("Pm-", StringComparison.OrdinalIgnoreCase) &&
                         !subreport.SubreportLinks.Any(link => string.Equals(link.LinkedParameterName, parameter.Name, StringComparison.OrdinalIgnoreCase)) &&
                         data.RecordSelectionFormula.Contains(parameter.FormulaName, StringComparison.OrdinalIgnoreCase)))
            {
                AddLinkDiagnostic(subreport, options, $"The record selection formula of subreport '{subreport.Name}' uses {parameter.FormulaName}, " +
                    "but no stored subreport link feeds that parameter; Crystal would prompt for it.");
            }

            if (conjuncts is not null && conjuncts.Count > 0 && filterTests == conjuncts.Count)
                data.RecordSelectionFormula = "";
        }
    }

    private static void AddLinkDiagnostic(CrystalReportModel subreport, CrystalRptConversionOptions options, string message)
    {
        var diagnostic = new CrystalConversionDiagnostic("CRYSTAL_SUBREPORT_LINK", subreport.Name, "", subreport.Name, "Subreport", message);
        subreport.ConversionDiagnostics.Add(diagnostic);
        options.Progress?.Invoke($"[{diagnostic.Code}] {subreport.Name}: {message}");
    }

    private static string? LinkTestField(string conjunct, string parameterName)
    {
        var reference = System.Text.RegularExpressions.Regex.Escape("{?" + parameterName + "}");
        var match = System.Text.RegularExpressions.Regex.Match(conjunct,
            @"^(?:\{(?<field>[^{}?@#%][^{}]*)\}\s*=\s*" + reference + "|" + reference + @"\s*=\s*\{(?<field>[^{}?@#%][^{}]*)\})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return match.Success ? "{" + match.Groups["field"].Value + "}" : null;
    }

    private static readonly HashSet<string> OpaqueSelectionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "or", "xor", "eqv", "imp", "if", "then", "else", "select", "case", "default", "while", "for", "do",
        "local", "global", "shared", "dim", "redim", "formula", "function", "exit"
    };

    // The top-level And operands of a Crystal-syntax selection formula, splitting fully parenthesised operands too. A formula holding a statement
    // separator or an assignment, and an operand whose top level holds anything but And (Or, If, ...), stay whole; unbalanced text gives null.
    // One pass matches the brackets and literals, so the split is linear and iterative however deeply the formula nests.
    private static List<string>? SelectionConjuncts(string formula)
    {
        var text = StripFormulaComments(formula);
        if (text is null || text.Trim().Length == 0) return null;
        var closes = new int[text.Length];
        var open = new Stack<int>();
        var statements = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'' or '{')
            {
                var end = SkipLiteral(text, i);
                if (end < 0) return null;
                closes[i] = end;
                i = end;
            }
            else if (c is '(' or '[') open.Push(i);
            else if (c is ')' or ']')
            {
                if (!open.TryPop(out var start) || text[start] != (c == ')' ? '(' : '[')) return null;
                closes[start] = i;
            }
            else if (c == ';' || c == ':' && i + 1 < text.Length && text[i + 1] == '=') statements = true;
        }

        if (open.Count > 0) return null;
        var conjuncts = new List<string>();
        var pending = new Stack<(int Start, int End)>();
        pending.Push((0, text.Length));
        while (pending.TryPop(out var span))
        {
            var (start, end) = TrimSpan(text, span.Start, span.End);
            while (end - start > 1 && text[start] == '(' && closes[start] == end - 1) (start, end) = TrimSpan(text, start + 1, end - 1);
            var parts = new List<(int Start, int End)>();
            var opaque = statements;
            var from = start;
            for (var i = start; i < end && !opaque; i++)
            {
                var c = text[i];
                if (c is '"' or '\'' or '{' or '(' or '[') { i = closes[i]; continue; }
                if (!char.IsLetter(c) || i > start && IsFormulaWordChar(text[i - 1])) continue;
                var wordEnd = i;
                while (wordEnd < end && IsFormulaWordChar(text[wordEnd])) wordEnd++;
                var word = text[i..wordEnd];
                if (word.Equals("and", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add((from, i));
                    from = wordEnd;
                }
                else if (OpaqueSelectionWords.Contains(word)) opaque = true;
                i = wordEnd - 1;
            }

            parts.Add((from, end));
            if (opaque || parts.Count == 1)
            {
                conjuncts.Add(text[start..end]);
                continue;
            }

            for (var part = parts.Count - 1; part >= 0; part--) pending.Push(parts[part]);
        }

        return conjuncts;
    }

    private static (int Start, int End) TrimSpan(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return (start, end);
    }

    private static bool IsFormulaWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Index of the character that closes the string literal or field reference starting at 'start', or -1.
    private static int SkipLiteral(string text, int start)
    {
        var close = text[start] == '{' ? '}' : text[start];
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] != close) continue;
            if (close != '}' && i + 1 < text.Length && text[i + 1] == close)
            {
                i++;
                continue;
            }

            return i;
        }

        return -1;
    }

    private static string? StripFormulaComments(string formula)
    {
        var builder = new System.Text.StringBuilder(formula.Length);
        for (var i = 0; i < formula.Length; i++)
        {
            var c = formula[i];
            if (c is '"' or '\'' or '{')
            {
                var end = SkipLiteral(formula, i);
                if (end < 0) return null;
                builder.Append(formula, i, end - i + 1);
                i = end;
            }
            else if (c == '/' && i + 1 < formula.Length && formula[i + 1] == '/')
            {
                while (i < formula.Length && formula[i] != '\n') i++;
                builder.Append('\n');
            }
            else builder.Append(c);
        }

        return builder.ToString();
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
        // Crystal TSLV streams begin with an uncompressed stream header record,
        // then deflate-compressed record data. Header byte 0x34 is the common
        // flag pattern for the v9+ reports in this project.
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
