using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Fx.ControlKit.Artifacts;
using Fx.ControlKit.Artifacts.Crystal;
using Fx.ControlKit.Artifacts.Exstream;
using Fx.ControlKit.Reports;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

internal static class ArtifactChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        void Throws<T>(Action action, string name) where T : Exception
        {
            try { action(); }
            catch (T) { check(true, name); return; }
            throw new Exception(name);
        }

        var crystal = Path.Combine(AppContext.BaseDirectory, "Fixtures", "artifacts", "crystal");
        var before = HashReports(crystal);
        check(before.Count == 5, "crystal sample folder has the five source reports");
        Throws<InvalidOperationException>(() => ArtifactPaths.EnsureOutputOutsideSource(crystal, crystal), "translation refuses to write into the source folder");
        Throws<InvalidOperationException>(() => ArtifactPaths.EnsureOutputOutsideSource(crystal, Path.Combine(crystal, "nested")), "translation refuses an output folder inside the source");

        using var output = new TempDir();
        var bundle = new TranslationBundle
        {
            ApplicationKey = "crystal-reports",
            Title = "guard",
            SourceDirectory = crystal,
            OutputDirectory = output.Path,
            Files = []
        };
        Throws<InvalidOperationException>(() => bundle.FullPath(new TranslatedFile("x", "x", "../outside.txt", "crystal-xml", "pass", null)), "a translated path cannot leave the output folder");

        var loader = new CrystalXmlReportLoader(NullLogger<CrystalXmlReportLoader>.Instance, new ReportOptions());
        var session = new CrystalSampleStore(Path.Combine(output.Path, CrystalSampleStore.DefaultFileName));
        string? chartXml = null;
        TranslatedFile? chartFile = null;
        foreach (var rpt in Directory.EnumerateFiles(crystal, "*.rpt", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(crystal, rpt).Replace('\\', '/');
            var stem = Path.GetFileNameWithoutExtension(rpt);
            var xml = Path.Combine(output.Path, "xml", stem + CrystalXmlViewerDescriptor.XmlExtension);
            Directory.CreateDirectory(Path.GetDirectoryName(xml)!);
            CrystalRptToXml.Convert(rpt, xml);
            var written = Directory.EnumerateFiles(Path.GetDirectoryName(xml)!, stem + ".*").Select(Path.GetFileName).Single();
            check(string.Equals(written, stem + ".xml", StringComparison.OrdinalIgnoreCase), stem + " keeps the .xml extension it was given");
            check(new FileInfo(xml).Length > 0, stem + " writes XML");
            var definition = loader.LoadPositioned(xml);
            check(definition.PositionedLayout is not null, stem + " loads through the positioned viewer loader");
            var children = definition.PositionedLayout!.Subreports.Values.Select(item => item.Definition);
            var binding = session.Bind(rpt, stem, relative, "pass", definition, children);
            var executor = new CrystalReportDataExecutor { Mode = CrystalReportDataMode.SyntheticSqlite, SampleReportHash = binding.Fingerprint };
            executor.UseSamples(binding.DataPath);
            var reloaded = loader.LoadPositioned(xml);
            var table = executor.Execute(reloaded, null);
            var result = new ReportLayoutSession(reloaded.PositionedLayout!, table).Paginate();
            check(result.Pages.Count >= 1, stem + " paginates on synthetic samples");
            check(CrystalReportPreview.CountPages(xml, table) == result.Pages.Count, stem + " preview helper matches the layout session");
            if (relative.Contains("chart_baseline", StringComparison.OrdinalIgnoreCase))
            {
                chartXml = xml;
                chartFile = new TranslatedFile(relative, stem, Path.GetRelativePath(output.Path, xml).Replace('\\', '/'), CrystalXmlViewerDescriptor.FormatKey, "pass", null, binding.Fingerprint, binding.DataPath);
                var text = File.ReadAllText(xml);
                check(text.Contains("ChartObject", StringComparison.Ordinal), "chart sample keeps its chart object");
                check(result.Pages.Any(page => page.Contains("fx-chart", StringComparison.Ordinal)
                    && page.Contains("<svg", StringComparison.Ordinal)
                    && page.Contains("<rect", StringComparison.Ordinal)
                    && page.Contains("Graph1", StringComparison.Ordinal)), "chart page is ChartControl output");
            }
            if (relative.Contains("crosstab_base", StringComparison.OrdinalIgnoreCase))
                check(result.Pages.Any(page => page.Contains("fx-pivot-table", StringComparison.Ordinal) || page.Contains("fx-report-table-fragment", StringComparison.Ordinal)),
                    "cross-tab page is PivotControl output");
        }
        check(HashReports(crystal).SequenceEqual(before), "converting sample reports does not change the source .rpt bytes");
        check(chartFile is not null, "chart sample was converted");

        var plain = new ReportDefinition
        {
            ReportId = "Sample",
            Sql = "SELECT Name FROM Customer",
            Columns = [new ReportColumn { Field = "Name" }]
        };
        using var samples = new TempDir();
        var store = new CrystalSampleStore(samples.Full("samples.db"));
        const string hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        store.Seed(new CrystalSampleReport(hash, "Sample", "Sample.rpt", "Test", "pass", 0, 0, []), plain, []);
        var roundTrip = new CrystalReportDataExecutor { Mode = CrystalReportDataMode.SyntheticSqlite, SampleReportHash = hash };
        roundTrip.UseSamples(store.DatabasePath);
        var rows = roundTrip.Execute(plain, null);
        check(rows.Rows.Count == 4 && rows.Columns[0].ColumnName == "Name" && rows.Rows[0][0]!.ToString()!.StartsWith("North", StringComparison.Ordinal),
            "synthetic samples round-trip through the executor");
        using var externalDir = new TempDir();
        var external = new CrystalSampleStore(externalDir.Full("corpus.db"));
        external.Seed(new CrystalSampleReport(CrystalSampleStore.Fingerprint(Directory.EnumerateFiles(crystal, "chart_baseline.rpt", SearchOption.AllDirectories).Single()), "chart", "chart_baseline.rpt", "Corpus", "pass", 0, 0, []), plain, []);
        var rebound = new CrystalSampleStore(externalDir.Full("session", CrystalSampleStore.DefaultFileName));
        var chartRpt = Directory.EnumerateFiles(crystal, "chart_baseline.rpt", SearchOption.AllDirectories).Single();
        var kept = rebound.Bind(chartRpt, "chart", "chart_baseline.rpt", "pass", plain, [], external);
        check(kept.DataPath == external.DatabasePath && !rebound.Available, "an existing corpus pack is reused instead of writing a second database");

        roundTrip.Mode = CrystalReportDataMode.LayoutOnly;
        var empty = roundTrip.Execute(plain, null);
        check(empty.Rows.Count == 0 && empty.Columns.Contains("Name"), "layout-only mode returns the columns and no rows");
        Throws<InvalidOperationException>(() => roundTrip.Execute("SELECT 1", null), "sample executor does not send SQL to a server");
        Throws<InvalidDataException>(() => new CrystalReportDataExecutor().LoadFixture("bad.json", "{}"), "an empty fixture is rejected");
        var query = CrystalReportDataExecutor.QueryHash(plain.Sql);
        var fixture = new CrystalReportDataExecutor { Mode = CrystalReportDataMode.Fixture };
        fixture.LoadFixture("pack.json", JsonSerializer.Serialize(new
        {
            Reports = new[]
            {
                new
                {
                    ReportId = "Sample",
                    SqlSha256 = query,
                    Parameters = new Dictionary<string, string> { ["Region"] = "North" },
                    Columns = new[] { new { Name = "Name", Type = "string" } },
                    Rows = new[] { new[] { "North" } }
                }
            }
        }));
        var matched = fixture.Execute(plain, new Dictionary<string, object> { ["@Region"] = "North" });
        check(matched.Rows.Count == 1 && (string)matched.Rows[0]["Name"] == "North", "a fixture matches the query fingerprint and parameters");
        Throws<InvalidDataException>(() => fixture.Execute(plain, new Dictionary<string, object> { ["Region"] = "South" }), "a fixture refuses a different parameter set");
        Throws<InvalidDataException>(() => CrystalSampleStore.TableName("nope", hash), "sample table names require a fingerprint");
        Throws<InvalidOperationException>(() => new UnavailableCrystalExporter().ExportToPdf(), "binary Crystal export stays unavailable");

        var exportRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "artifacts", "exstream", "Prj_Contract-pf_Contract");
        var index = ExstreamProjectIndex.Read(exportRoot);
        check(index.Count > 100 && index.Any(entry => entry.Name == "FooterWithAddress_USA.ssd" && entry.Type == "doc_out"),
            "resources.xml lists the contract sample catalog");
        using var escaped = new TempDir();
        File.WriteAllText(escaped.Full("resources.xml"), """
            <Resources><Resource><ResourceName>outside</ResourceName><ResourceType>file</ResourceType><ResourcePath>../../etc/passwd</ResourcePath></Resource></Resources>
            """);
        var outside = ExstreamProjectIndex.Read(escaped.Path).Single();
        check(outside.Name == "outside" && !outside.OnDisk, "a resources.xml path outside the export is not treated as on disk");

        using var pack = new TempDir();
        WritePack(pack.Path);
        check(ExstreamPackReader.IsPack(pack.Path) && ExstreamPackReader.CommunicationName(pack.Path) == "Letter", "a design pack is the manifest");
        var described = ExstreamPackReader.ListTranslatedFiles(pack.Path, "Letter");
        check(described.Any(file => file.Format == ExstreamPackViewerDescriptor.PackFormat)
            && described.Single(file => file.Format == ExstreamPackViewerDescriptor.DocumentFormat).Title == "Letter"
            && described.Any(file => file.Format == ExstreamPackViewerDescriptor.ScriptFormat && file.RelativePath.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            && described.Any(file => file.Format == ExstreamPackViewerDescriptor.ResourceFormat),
            "a pack lists the manifest, documents, scripts, and resources");
        check(ExstreamPackReader.GapMessages(pack.Path).Count == 0, "a pack with a document has nothing to warn about");
        var loaded = ExstreamPackReader.LoadDocument(pack.Path, described.Single(file => file.Format == ExstreamPackViewerDescriptor.DocumentFormat).RelativePath);
        check(loaded is not null && loaded.Pages.Count == 1 && loaded.Pages[0].Outline[0].Type == "Textbox"
            && loaded.Pages[0].Outline[0].Children[0].Type == "Flow"
            && loaded.Stories.Values.Any(story => story.Text.Contains("thank you", StringComparison.OrdinalIgnoreCase))
            && loaded.Variables.Contains("LegalName"),
            "the pack reader opens a document outline and its story");
        var counts = ExstreamPackReader.ResourceCounts(pack.Path);
        check(counts.Values.Sum() == 2 && counts["doc_out"] == 1, "asset counts come from the manifest");
        using var emptyPack = new TempDir();
        File.WriteAllText(emptyPack.Full("manifest.json"), """{"communication":"Empty","resources":[]}""");
        check(ExstreamPackReader.GapMessages(emptyPack.Path).Single() == ExstreamPackReader.NoDocumentsMessage, "a pack with no documents says so");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJSRuntime, NoJs>();
        services.AddFlexCoreArtifacts();
        using var provider = services.BuildServiceProvider();
        check(provider.GetServices<ITranslatedFileViewer>().Select(viewer => viewer.Format).SequenceEqual(
            [CrystalXmlViewerDescriptor.FormatKey, ExstreamPackViewerDescriptor.PackFormat, ExstreamPackViewerDescriptor.DocumentFormat, ExstreamPackViewerDescriptor.ScriptFormat, ExstreamPackViewerDescriptor.ResourceFormat]),
            "artifact hosting registers one viewer per translated format");
        using (var scope = provider.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<CrystalReportDataExecutor>();
            check(ReferenceEquals(executor, scope.ServiceProvider.GetRequiredService<IReportDataExecutor>())
                && ReferenceEquals(executor, scope.ServiceProvider.GetRequiredService<IReportDefinitionDataExecutor>()),
                "the sample executor is the report data executor");
            check(scope.ServiceProvider.GetRequiredService<IReportExporter>() is UnavailableCrystalExporter, "hosting does not register a SAP exporter");
            check(scope.ServiceProvider.GetRequiredService<CrystalXmlReportLoader>() is not null, "hosting can load Crystal XML");
        }

        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var viewers = provider.GetServices<ITranslatedFileViewer>().ToList();
            var letter = described.Single(file => file.Format == ExstreamPackViewerDescriptor.DocumentFormat);
            var packBundle = new TranslationBundle
            {
                ApplicationKey = "exstream",
                Title = "Letter",
                SourceDirectory = exportRoot,
                OutputDirectory = pack.Path,
                Files = described
            };
            var explorer = await renderer.RenderComponentAsync<ExstreamPackViewer>(Parameters(
                ("Model", new TranslatedFileView { Bundle = packBundle, File = described.Single(file => file.Format == ExstreamPackViewerDescriptor.PackFormat) })));
            var explorerHtml = explorer.ToHtmlString();
            check(explorerHtml.Contains("Project explorer") && explorerHtml.Contains("Asset library") && explorerHtml.Contains("FooterWithAddress_USA.ssd"),
                "the pack viewer opens the project explorer");
            var designer = await renderer.RenderComponentAsync<ExstreamPackViewer>(Parameters(
                ("Model", new TranslatedFileView { Bundle = packBundle, File = letter })));
            var designerHtml = designer.ToHtmlString();
            check(designerHtml.Contains("Designer") && designerHtml.Contains("thank you", StringComparison.OrdinalIgnoreCase) && designerHtml.Contains("Greeting"),
                "the pack viewer drills into a document story");
            var library = await renderer.RenderComponentAsync<ExstreamPackViewer>(Parameters(
                ("Model", new TranslatedFileView { Bundle = packBundle, File = described.Single(file => file.Format == ExstreamPackViewerDescriptor.ResourceFormat) })));
            check(library.ToHtmlString().Contains("Asset library") && library.ToHtmlString().Contains("doc_out"), "the pack viewer lists the asset library");

            var browserBundle = new TranslationBundle
            {
                ApplicationKey = "crystal-reports",
                Title = "Corpus handful",
                SourceDirectory = crystal,
                OutputDirectory = output.Path,
                Files =
                [
                    chartFile!,
                    new TranslatedFile("missing", "Missing", "notes.txt", "unknown-format", "pass", null)
                ],
                Messages = ["Sample message"]
            };
            var browser = await renderer.RenderComponentAsync<TranslatedFileBrowser>(Parameters(
                ("Bundle", browserBundle),
                ("Viewers", viewers),
                ("InitialFileId", "missing")));
            check(browser.ToHtmlString().Contains("No viewer is registered for unknown-format") && browser.ToHtmlString().Contains("Sample message"),
                "the browser asks for a viewer registered for the file format");

            var crystalView = await renderer.RenderComponentAsync<CrystalXmlViewer>(Parameters(
                ("Model", new TranslatedFileView { Bundle = browserBundle, File = chartFile! })));
            var crystalHtml = crystalView.ToHtmlString();
            // Static HTML rendering does not populate component refs or run OnAfterRenderAsync, so the
            // automatic ShowReportAsync pass is covered by the pagination checks above. This render checks
            // that the viewer mounts ReportWriterControl and selects the synthetic sample source.
            check(crystalHtml.Contains("fx-rpt-viewer-container", StringComparison.Ordinal)
                && crystalHtml.Contains("Synthetic samples are ready", StringComparison.Ordinal)
                && crystalHtml.Contains("ChartControl", StringComparison.Ordinal)
                && !crystalHtml.Contains("workflow-error"),
                "the Crystal viewer hosts ReportWriterControl on the synthetic sample source");
        });
    }

    private static void WritePack(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "content", "documents"));
        Directory.CreateDirectory(Path.Combine(root, "scripts", "converted"));
        Directory.CreateDirectory(Path.Combine(root, "resources"));
        File.WriteAllText(Path.Combine(root, "manifest.json"), """
            {"communication":"Letter","resources":[{"type":"doc_out","name":"Letter.ssd"},{"type":"image","name":"Logo"}]}
            """);
        File.WriteAllText(Path.Combine(root, "content", "documents", "Letter.json"), """
            {"name":"Letter","kind":"process","pages":[{"Number":1,"PageObjectId":"88000026","outline":[{"label":"Greeting","storyId":"s1","type":"Textbox","children":[{"label":"","type":"Flow","children":[]}]}]}],"pageSize":{"widthPt":612,"heightPt":792},"fonts":["Arial"],"variables":["LegalName"],"tables":0,"stories":[{"Id":"s1","Referencable":false,"text":"Thank you for your business."}]}
            """);
        File.WriteAllText(Path.Combine(root, "scripts", "converted", "greeting.js"), "function greeting() { return 1; }\n");
        File.WriteAllText(Path.Combine(root, "resources", "images.json"), "{}\n");
    }

    private static ParameterView Parameters(params (string Key, object? Value)[] pairs) =>
        ParameterView.FromDictionary(pairs.ToDictionary(pair => pair.Key, pair => pair.Value));

    private static List<(string Path, string Hash)> HashReports(string root) =>
        Directory.EnumerateFiles(root, "*.rpt", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => (path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
            .ToList();

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("fx-artifacts-").FullName;
        public string Full(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
