using System.Data;
using System.Xml.Linq;
using Fx.ControlKit.Charts;
using Fx.ControlKit.Reports;

internal static class AnalysisRenderingChecks
{
    public static void Run(Action<bool, string> check)
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        foreach (var rpt in Directory.GetFiles(fixtures, "*.rpt").OrderBy(path => path, StringComparer.Ordinal))
        {
            var xml = Path.Combine(Path.GetTempPath(), "fx-analysis-" + Path.GetFileNameWithoutExtension(rpt) + ".xml");
            CrystalRptToXml.Convert(rpt, xml, new(ExtractSubreports: false));
            var document = XDocument.Load(xml);
            var objects = document.Descendants().Where(element => element.Name.LocalName is "ChartObject" or "CrossTabObject").ToList();
            var name = Path.GetFileName(rpt);
            check(objects.Count > 0 && objects.All(element => element.Element("FlexKitAnalysis") is not null), name + " imports chart and cross-tab bindings");
            check(!document.Descendants("Diagnostic").Any(diagnostic => (string?)diagnostic.Attribute("Code") == "CRYSTAL_UNSUPPORTED_OBJECT"),
                name + " does not keep an unsupported-object diagnostic");
        }

        var layout = new ReportPositionedLayout { Document = ReportDesignerDocument.CreateBlank("Sales analysis") };
        var header = layout.Document.Sections.First(section => section.Kind == "ReportHeader");
        header.HeightTwips = 7200;
        header.Elements.Add(new ReportDesignerElement
        {
            SectionId = header.Id, Name = "MonthlyChart", Kind = "Chart",
            WidthTwips = 7200, HeightTwips = 3200, FontSize = 9,
            Analysis = new ReportAnalysisDefinition
            {
                Title = "Monthly amount", ChartType = ChartType.Line, CategoryField = "{Orders.Order Date}", ShowLegend = false,
                GroupKinds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["{Orders.Order Date}"] = 4 },
                Measures = [new() { Field = "{Orders.Amount}", Caption = "Amount", Aggregate = ReportAggregateType.Sum, Format = "N0" }]
            }
        });
        header.Elements.Add(new ReportDesignerElement
        {
            SectionId = header.Id, Name = "ShareChart", Kind = "Chart",
            TopTwips = 3300, WidthTwips = 3200, HeightTwips = 2800,
            Analysis = new ReportAnalysisDefinition
            {
                Title = "Share", ChartType = ChartType.Pie, SummariesAsCategories = true, ShowLegend = false,
                Measures =
                [
                    new() { Field = "{Orders.Amount}", Caption = "Amount", Aggregate = ReportAggregateType.Sum, Format = "N0" },
                    new() { Field = "{Orders.Units}", Caption = "Units", Aggregate = ReportAggregateType.Sum, Format = "N0" }
                ]
            }
        });
        header.Elements.Add(new ReportDesignerElement
        {
            SectionId = header.Id, Name = "RegionCrossTab", Kind = "CrossTab",
            TopTwips = 3300, LeftTwips = 3400, WidthTwips = 4800, HeightTwips = 2800, FontSize = 9,
            Analysis = new ReportAnalysisDefinition
            {
                RowFields = ["{Orders.Region}"], ColumnFields = ["{Orders.Year}"], ShowTotals = true,
                Measures = [new() { Field = "{Orders.Amount}", Caption = "Amount", Aggregate = ReportAggregateType.Sum, Format = "N0" }]
            }
        });
        layout.Bindings["{Orders.Order Date}"] = "OrderDate";
        layout.Bindings["{Orders.Amount}"] = "Amount";
        layout.Bindings["{Orders.Units}"] = "Units";
        layout.Bindings["{Orders.Region}"] = "Region";
        layout.Bindings["{Orders.Year}"] = "Year";
        var table = new DataTable();
        table.Columns.Add("OrderDate", typeof(DateTime));
        table.Columns.Add("Amount", typeof(decimal));
        table.Columns.Add("Units", typeof(int));
        table.Columns.Add("Region", typeof(string));
        table.Columns.Add("Year", typeof(string));
        table.Rows.Add(new DateTime(2026, 1, 5), 10m, 1, "East", "2024");
        table.Rows.Add(new DateTime(2026, 1, 20), 5m, 2, "East", "2025");
        table.Rows.Add(new DateTime(2026, 2, 2), 7m, 4, "West", "2024");

        var result = new ReportLayoutSession(layout, table).Paginate();
        var html = string.Join("\n", result.Pages);
        check(html.Contains("fx-chart", StringComparison.Ordinal) && html.Contains("<svg", StringComparison.Ordinal)
            && html.Contains("preserveAspectRatio=\"xMidYMid meet\"", StringComparison.Ordinal)
            && html.Contains("#2563eb", StringComparison.Ordinal) && !html.Contains("#2f6f9f", StringComparison.Ordinal),
            "positioned chart SVG is ChartControl output");
        check(html.Contains("1/1/2026", StringComparison.Ordinal) && html.Contains("2/1/2026", StringComparison.Ordinal)
            && !html.Contains("1/5/2026", StringComparison.Ordinal), "monthly chart buckets dates into months");
        check(html.Contains(">Share<") && html.Contains(">Amount<") && html.Contains(">Units<")
            && html.Contains("stroke=\"white\"", StringComparison.Ordinal), "summary pie draws one slice per measure");
        var points = new ChartDataPoint[] { new("A", 2), new("B", 5) };
        var bar = ChartSvg.Markup(ChartType.Bar, [new("Amount", ChartType.Bar, points)]);
        var area = ChartSvg.Markup(ChartType.Area, [new("Amount", ChartType.Area, points)]);
        var donut = ChartSvg.Markup(ChartType.Donut, [new("Share", ChartType.Donut, points)]);
        check(bar.Contains("fx-chart", StringComparison.Ordinal) && bar.Contains("rx=\"2\"", StringComparison.Ordinal) && bar.Contains("#2563eb", StringComparison.Ordinal),
            "bar markup is ChartControl SVG");
        check(area.Contains("fx-chart", StringComparison.Ordinal) && area.Contains("fill-opacity=\"0.15\"", StringComparison.Ordinal),
            "area markup is ChartControl SVG");
        check(donut.Contains("fx-chart", StringComparison.Ordinal) && donut.Contains("preserveAspectRatio=\"xMidYMid meet\"", StringComparison.Ordinal)
            && donut.Contains("stroke=\"white\"", StringComparison.Ordinal),
            "donut markup is ChartControl SVG");
        check(html.Contains("fx-report-table-fragment", StringComparison.Ordinal) && html.Contains(">East<") && html.Contains(">West<") && html.Contains(">2024<"),
            "cross-tab page shows row and column headers");
        check(!html.Contains("[Unsupported", StringComparison.Ordinal) && !html.Contains("not implemented", StringComparison.OrdinalIgnoreCase)
            && !html.Contains("Analytical report item", StringComparison.Ordinal), "chart and cross-tab placeholders are not written on the page");
        check(!result.Diagnostics.Any(diagnostic => diagnostic.Contains("not implemented", StringComparison.OrdinalIgnoreCase)
            || diagnostic.Contains("CRYSTAL_UNSUPPORTED_OBJECT", StringComparison.Ordinal)), "pagination does not report chart or cross-tab objects as unimplemented");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "fx-analysis-page.html"),
            "<!doctype html><meta charset=utf-8><style>body{margin:16px;background:#e8ecef} .fx-report-positioned-page{box-shadow:0 1px 4px #0003}</style>" + html);

        var grown = new ReportPositionedLayout { Document = ReportDesignerDocument.CreateBlank("Grown header") };
        var pageHeader = grown.Document.Sections.First(section => section.Kind == "PageHeader");
        pageHeader.Elements.Add(new ReportDesignerElement
        {
            SectionId = pageHeader.Id, Name = "GrownHeader", Kind = "Text", CanGrow = true,
            WidthTwips = 1440, HeightTwips = 200, FontSize = 10, Text = new string('W', 4000)
        });
        var grownRows = new DataTable();
        grownRows.Columns.Add("Id", typeof(int));
        grownRows.Rows.Add(1);
        var grownResult = new ReportLayoutSession(grown, grownRows).Paginate();
        check(grownResult.Pages.Count > 0 && grownResult.Diagnostics.Any(diagnostic => diagnostic.Contains("clipped to the designed section height", StringComparison.Ordinal)),
            "a page header that grows past its section is clipped instead of aborting the page");

        var filled = new ReportPositionedLayout { Document = ReportDesignerDocument.CreateBlank("Filled header") };
        filled.Document.Sections.First(section => section.Kind == "PageHeader").HeightTwips = filled.Document.Page.ContentHeightTwips;
        var filledThrew = false;
        try { _ = new ReportLayoutSession(filled, grownRows).Paginate(); }
        catch (InvalidDataException error) { filledThrew = error.Message.Contains("page area too large", StringComparison.Ordinal); }
        check(filledThrew, "a page header designed to fill the page still reports page area too large");

        var faulted = new ReportPositionedLayout { Document = ReportDesignerDocument.CreateBlank("Formula fault") };
        var detail = faulted.Document.Sections.First(section => section.Kind == "Detail");
        detail.Elements.Add(new ReportDesignerElement
        {
            SectionId = detail.Id, Name = "Fault", Kind = "Field", Binding = "{@Fault}", WidthTwips = 2000, HeightTwips = 300
        });
        faulted.Formulas["{@Fault}"] = CrystalFormula.Compile("WhilePrintingRecords; PageNumber / 0");
        var faultResult = new ReportLayoutSession(faulted, grownRows).Paginate();
        check(faultResult.Diagnostics.Any(diagnostic => diagnostic.Contains("Division by zero", StringComparison.Ordinal))
            && string.Join("\n", faultResult.Pages).Contains("Formula error", StringComparison.Ordinal),
            "a while-printing formula fault is a field diagnostic instead of a pagination abort");
    }
}
