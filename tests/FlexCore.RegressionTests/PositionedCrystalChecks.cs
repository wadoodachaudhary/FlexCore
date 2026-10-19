using System.Data;
using System.Xml.Linq;
using Fx.ControlKit.Reports;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

internal static class PositionedCrystalChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fx-positioned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var factory = LoggerFactory.Create(builder => { });
            var loader = new CrystalXmlReportLoader(factory.CreateLogger<CrystalXmlReportLoader>(), new ReportOptions { SchemaPrefix = "" });

            // Record selection reaches SQL only through the AST subsets in
            // CrystalXmlReportLoader.Selection.cs (an AND tree of typed field = {?Param}
            // equalities) and CrystalXmlReportLoader.BoundedSelection.cs (those equalities
            // as guarded, bound list membership, beside field/literal comparisons that are
            // not translated). SQL is only ever a superset: every other shape stays
            // unfiltered and the runtime evaluates the full formula on the returned rows.
            var plainPath = Write(directory, "plain.xml", PlainReport());
            var plain = loader.LoadPositioned(plainPath);
            var plainNarrowed = plain.Sql.Contains(" WHERE ", StringComparison.Ordinal)
                && plain.Sql.Contains("(@__fxPositionedSelectionGuard0 = 1 OR CONVERT(nvarchar(max), [POMaster].[PONumber]) COLLATE Latin1_General_100_BIN2 LIKE N'%[^ -~]%' OR CONVERT(nvarchar(max), [POMaster].[PONumber]) COLLATE Latin1_General_100_CI_AS = @PONumber)", StringComparison.Ordinal)
                && plain.Sql.Contains("[POMaster].[DivisionID] AS", StringComparison.Ordinal)
                && plain.PositionedLayout!.RecordSelection is not null
                && !plain.Sql.Contains("'Open'", StringComparison.Ordinal)
                && !plain.Sql.Contains("Secret", StringComparison.Ordinal)
                && !plain.Sql.Contains("//", StringComparison.Ordinal);
            check(plainNarrowed, plainNarrowed
                ? "an AND selection narrows SQL by its {?Name} equality and leaves the literal comparison to the positioned runtime"
                : "an AND selection narrows SQL by its {?Name} equality and leaves the literal comparison to the positioned runtime sql=[" + plain.Sql + "]");
            check(plain.PositionedLayout!.RecordSelection is not null
                && !plain.PositionedLayout.Bindings.ContainsKey("{?Pm-POMaster.PONumber}")
                && !plain.Sql.Contains("?Pm-POMaster", StringComparison.Ordinal),
                "a dotted prompt is read from values and is not a query column");

            var either = loader.LoadPositioned(Write(directory, "either.xml", SelectionReport(
                "({POMaster.PONumber} = {?PONumber}) OR ({POMaster.DivisionID} = 7)")));
            check(!either.Sql.Contains(" WHERE ", StringComparison.Ordinal) && either.PositionedLayout!.RecordSelection is not null,
                "a parenthesized OR stays with the positioned runtime");

            var shaped = loader.LoadPositioned(Write(directory, "shaped.xml", SelectionReport(
                "Year({POMaster.OrderDate}) = 2026 OR {POMaster.PONumber} = {?PONumber}")));
            check(!shaped.Sql.Contains(" WHERE ", StringComparison.Ordinal) && shaped.PositionedLayout!.RecordSelection is not null,
                "a selection that calls a function stays with the positioned runtime");

            var scalar = loader.LoadPositioned(Write(directory, "scalar.xml", SelectionReport(
                "{POMaster.PONumber} = {?PONumber}")));
            var scalarPushed = scalar.Sql.Contains(" WHERE ", StringComparison.Ordinal)
                && scalar.Sql.Contains("CONVERT(nvarchar(max), [POMaster].[PONumber]) COLLATE Latin1_General_100_CI_AS = CONVERT(nvarchar(max), @PONumber) COLLATE Latin1_General_100_CI_AS", StringComparison.Ordinal)
                && scalar.Sql.Contains("Latin1_General_100_BIN2 LIKE N'%[^ -~]%'", StringComparison.Ordinal)
                && !scalar.Sql.Contains("__fx_selection", StringComparison.Ordinal)
                && scalar.PositionedLayout!.RecordSelection is not null
                && Accepts(scalar, new Dictionary<string, object> { ["ponumber"] = "PO-100" })
                && !Accepts(scalar, new Dictionary<string, object> { ["PONumber"] = 100 })
                && !Accepts(scalar, new Dictionary<string, object>());
            check(scalarPushed, scalarPushed
                ? "a typed field = {?Name} string selection narrows SQL with the guarded case-insensitive predicate"
                : "a typed field = {?Name} string selection narrows SQL with the guarded case-insensitive predicate sql=[" + scalar.Sql + "]");

            var mismatched = loader.LoadPositioned(Write(directory, "mismatched.xml", SelectionReport(
                "{POMaster.PONumber} = {?PONumber}", valueKind: "Number")));
            check(!mismatched.Sql.Contains(" WHERE ", StringComparison.Ordinal) && mismatched.PositionedLayout!.RecordSelection is not null,
                "a prompt whose type differs from the field stays with the positioned runtime");

            var multiPath = Write(directory, "multi.xml", SelectionReport("{POMaster.PONumber} = {?PONumber}", multiple: true));
            var multi = loader.LoadPositioned(multiPath);
            var guardedSql = multi.Sql.Contains("(@__fxPositionedSelectionGuard0 = 1 OR CONVERT(nvarchar(max), [POMaster].[PONumber]) COLLATE Latin1_General_100_BIN2 LIKE N'%[^ -~]%' OR CONVERT(nvarchar(max), [POMaster].[PONumber]) COLLATE Latin1_General_100_CI_AS = @PONumber)", StringComparison.Ordinal)
                && !multi.Sql.Contains("__fx_selection", StringComparison.Ordinal);
            var multiBound = MultiValueParameterRewriter.Apply(multi, new Dictionary<string, IReadOnlyList<string>> { ["PONumber"] = ["PO-100", "PO-200"] });
            var nonAscii = new Dictionary<string, object>(multiBound) { ["PONumber_1"] = "\u00c9tage" };
            var expanded = guardedSql
                && multi.Sql.Contains("COLLATE Latin1_General_100_CI_AS IN (@PONumber_0, @PONumber_1)", StringComparison.Ordinal)
                && !multi.Sql.Contains("PO-100", StringComparison.Ordinal)
                && Equals(multiBound["PONumber_1"], "PO-200")
                && Accepts(multi, multiBound)
                && Equals(multiBound["__fxPositionedSelectionGuard0"], false)
                && Accepts(multi, nonAscii)
                && Equals(nonAscii["__fxPositionedSelectionGuard0"], true)
                && !Accepts(multi, new Dictionary<string, object>(multiBound) { ["PONumber_1"] = 200 });
            check(expanded, expanded
                ? "a multi-value field = {?Name} selection narrows SQL through guarded, bound IN-list membership"
                : "a multi-value field = {?Name} selection narrows SQL through guarded, bound IN-list membership sql=[" + multi.Sql + "]");

            var reversed = loader.LoadPositioned(Write(directory, "reversed.xml", SelectionReport(
                "{?PONumber} = {POMaster.PONumber}", multiple: true)));
            check(reversed.Sql == loader.LoadPositioned(multiPath).Sql
                && reversed.PositionedLayout!.RecordSelection is not null,
                "a reversed {?Name} = field equality narrows SQL the same way");

            var range = loader.LoadPositioned(Write(directory, "range.xml", SelectionReport(
                "{POMaster.OrderDate} = {?When}", range: true, name: "When")));
            check(!range.Sql.Contains(" WHERE ", StringComparison.Ordinal) && range.PositionedLayout!.RecordSelection is not null,
                "a range prompt keeps the whole positioned query for the runtime");

            var linked = loader.LoadPositioned(plainPath);
            var subreport = linked.PositionedLayout!.Subreports.Values.Single();
            check(subreport.Links.Single().Parameter == "Pm-POMaster.PONumber",
                "a {?Pm-X} subreport link is stored by its bare name");
            // The {?Pm-X} link selection is not a SQL identifier, so the child query is
            // not narrowed; the session binds the link value (BindSubreport below) and
            // the runtime applies the child's selection to its rows.
            var childSql = subreport.Definition.Sql;
            var childProjectsOwnTables = childSql.Contains("[POLines].[Item]", StringComparison.Ordinal)
                && childSql.Contains("[POLines].[PONumber]", StringComparison.Ordinal)
                && !childSql.Contains(" WHERE ", StringComparison.Ordinal)
                && subreport.Definition.PositionedLayout!.RecordSelection is not null
                && !childSql.Contains("[POMaster]", StringComparison.Ordinal)
                && !childSql.Contains("[Vendor]", StringComparison.Ordinal)
                && !subreport.Definition.PositionedLayout.Bindings.ContainsKey("{POMaster.DivisionID}")
                && !subreport.Definition.PositionedLayout.Bindings.ContainsKey("{?Pm-POMaster.PONumber}");
            check(childProjectsOwnTables, childProjectsOwnTables
                ? "a subreport query projects only its own tables and leaves its link selection to the runtime"
                : "a subreport query projects only its own tables and leaves its link selection to the runtime sql=[" + childSql
                    + "] bindings=[" + string.Join(", ", subreport.Definition.PositionedLayout!.Bindings.Keys) + "]");
            var mainProjectsOwnTables = linked.Sql.Contains("[POMaster].[PONumber]", StringComparison.Ordinal)
                && !linked.Sql.Contains("POLines", StringComparison.Ordinal)
                && !linked.Sql.Contains("[Vendor]", StringComparison.Ordinal)
                && !linked.PositionedLayout.Bindings.ContainsKey("{Vendor.Name}");
            check(mainProjectsOwnTables, mainProjectsOwnTables
                ? "the main positioned query drops fields from tables it does not use"
                : "the main positioned query drops fields from tables it does not use sql=[" + linked.Sql + "]");

            var designed = ReportDesignerXmlSerializer.FromXml(KindReport());
            string Kind(string name) => designed.Elements.Single(element => element.Name == name).Kind;
            check(Kind("Amount") == "Field" && Kind("Title") == "Text" && Kind("Lines") == "Subreport"
                && Kind("Logo") == "Picture" && Kind("Frame") == "Box" && Kind("Rule") == "Line"
                && Kind("Caption") == "FieldHeading" && Kind("SavedField") == "Field" && Kind("SavedChart") == "Chart"
                && Kind("SavedPivot") == "Pivot",
                "SDK object classes map to the designer vocabulary and explicit kinds stay");
            check(Kind("Monthly") == "Chart" && Kind("Region") == "CrossTab",
                "chart and cross-tab objects keep the analysis vocabulary");

            BindSubreport(check, new[] { "North", "South" }, sql => sql.Contains("IN (@PmOrdersNumber_0, @PmOrdersNumber_1)", StringComparison.Ordinal),
                bound => Equals(bound["PmOrdersNumber_0"], "North") && Equals(bound["PmOrdersNumber_1"], "South") && bound["PmOrdersNumber"] is string,
                "North,South", "several subreport link values expand to IN and the formula keeps the array");
            BindSubreport(check, "ONLY-A", sql => sql.Contains("= @PmOrdersNumber", StringComparison.Ordinal) && !sql.Contains(" IN ", StringComparison.Ordinal),
                bound => Equals(bound["PmOrdersNumber"], "ONLY-A"),
                "ONLY-A", "one subreport link value binds as itself");
            BindSubreport(check, Array.Empty<string>(), sql => sql.Contains("= @PmOrdersNumber", StringComparison.Ordinal) && !sql.Contains(" IN ", StringComparison.Ordinal),
                bound => bound["PmOrdersNumber"] is DBNull,
                null, "a subreport link with no values binds NULL");

            await ShowPositioned(check, plainPath, literalResidual: true);
            await ShowPositioned(check, multiPath, literalResidual: false);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void BindSubreport(Action<bool, string> check, object? code, Func<string, bool> sql, Func<IReadOnlyDictionary<string, object>, bool> bound, string? shown, string name)
    {
        var childDocument = ReportDesignerDocument.CreateBlank("Lines");
        var header = childDocument.Sections.First(section => section.Kind == "ReportHeader");
        header.Elements.Add(new ReportDesignerElement
        {
            SectionId = header.Id, Name = "Joined", Kind = "Field", Binding = "{@Joined}", WidthTwips = 2400, HeightTwips = 240
        });
        var childLayout = new ReportPositionedLayout { Document = childDocument };
        childLayout.Formulas["{@Joined}"] = CrystalFormula.Compile("Join({?Pm-Orders.Number}, \",\")", "Crystal", "");
        const string original = "SELECT [POLines].[Item] AS [Item] FROM POLines WHERE [POLines].[Code] = @PmOrdersNumber";
        var child = new ReportDefinition { Sql = original, Title = "Lines", ReportId = "Lines", PositionedLayout = childLayout };
        var parent = new ReportPositionedLayout { Document = ReportDesignerDocument.CreateBlank("Orders") };
        parent.Bindings["{Orders.Code}"] = "Code";
        var detail = parent.Document.Sections.First(section => section.Kind == "Detail");
        var element = new ReportDesignerElement
        {
            SectionId = detail.Id, Name = "Lines", Kind = "Subreport", SubreportName = "Lines", WidthTwips = 4000, HeightTwips = 600
        };
        detail.Elements.Add(element);
        parent.Subreports[element.Id] = new(child, [new ReportLayoutParameterLink("Pm-Orders.Number", "{Orders.Code}", "")]);
        var table = new DataTable();
        table.Columns.Add("Code", typeof(object));
        table.Rows.Add(new object[] { code! });
        string? seenSql = null;
        IReadOnlyDictionary<string, object>? seen = null;
        var result = new ReportLayoutSession(parent, table, executeSubreport: (query, parameters) =>
        {
            seenSql = query.Sql;
            seen = parameters;
            return new DataTable();
        }).Paginate();
        var html = string.Join('\n', result.Pages);
        var displayed = shown is null
            ? !html.Contains("Formula error", StringComparison.Ordinal)
            : html.Contains(shown, StringComparison.Ordinal);
        if (seenSql is null || !sql(seenSql) || seen is null || !bound(seen) || child.Sql != original || !displayed)
            check(false, name + " sql=[" + seenSql + "] html=[" + html + "] diag=[" + string.Join(" | ", result.Diagnostics) + "]");
        else
            check(true, name);
    }

    private static async Task ShowPositioned(Action<bool, string> check, string path, bool literalResidual)
    {
        var captured = new CapturedExecutor();
        var activation = new Components();
        var services = new ServiceCollection().AddLogging()
            .AddSingleton<IComponentActivator>(activation)
            .AddSingleton<CrystalXmlReportLoader>()
            .AddSingleton<ReportOptions>(new ReportOptions { SchemaPrefix = "" })
            .AddSingleton<IReportDataExecutor>(captured)
            .AddSingleton<IReportSessionContext, EmptySession>()
            .AddSingleton<IReportViewerSettings, InMemoryReportViewerSettings>()
            .AddSingleton<IReportExporter, UnusedExporter>()
            .AddSingleton<IJSRuntime, NoJs>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            await renderer.RenderComponentAsync<ReportWriterControl>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["ShowChrome"] = false,
                ["UsePositionedLayout"] = true
            }));
            var writer = activation.All.OfType<ReportWriterControl>().Single();
            await writer.ShowReportAsync(path, new Dictionary<string, string> { ["Multi:PONumber"] = "PO-100\u0002PO-200" });
            var html = string.Join('\n', writer.Tabs.SelectMany(tab => tab.Pages));
            var call = captured.Calls.FirstOrDefault();
            var diagnostics = string.Join(" | ", writer.Tabs.SelectMany(tab => tab.RuntimeDiagnostics));
            // The executor returns PO-999 either way: SQL is only ever a superset, and the
            // runtime evaluates the full selection formula on every returned row. A literal
            // comparison beside the {?Name} equality never reaches SQL.
            var sqlShape = call.Sql is not null
                && call.Sql.Contains("COLLATE Latin1_General_100_CI_AS IN (@PONumber_0, @PONumber_1)", StringComparison.Ordinal)
                && call.Sql.Contains("@__fxPositionedSelectionGuard0 = 1", StringComparison.Ordinal)
                && !call.Sql.Contains("'Open'", StringComparison.Ordinal);
            var passed = string.IsNullOrEmpty(writer.ErrorMessage)
                && sqlShape
                && call.Parameters is not null
                && Equals(call.Parameters["PONumber_0"], "PO-100")
                && Equals(call.Parameters["PONumber_1"], "PO-200")
                && Equals(call.Parameters["__fxPositionedSelectionGuard0"], false)
                && call.Parameters["PONumber"] is string
                && html.Contains("PO-100,PO-200", StringComparison.Ordinal)
                && !html.Contains("PO-999", StringComparison.Ordinal);
            var name = literalResidual
                ? "positioned formulas keep the multi-value array and the runtime still filters a narrowed query whose literal comparison stays out of SQL"
                : "a narrowed multi-value query binds the expanded list and the runtime still filters its rows";
            check(passed, passed
                ? name
                : name + " error=[" + writer.ErrorMessage
                    + "] sql=[" + call.Sql + "] html=[" + html + "] diag=[" + diagnostics + "]");
        });
    }

    // A narrowed string selector takes exactly one string value per SQL parameter.
    private static bool Accepts(ReportDefinition definition, IDictionary<string, object> parameters)
    {
        try { definition.ValidateSqlParameters(parameters); return true; }
        catch (InvalidDataException) { return false; }
    }

    private static string Write(string directory, string name, XElement report)
    {
        var path = Path.Combine(directory, name);
        new XDocument(report).Save(path);
        return path;
    }

    private static XElement PlainReport()
    {
        var report = SelectionReport("""
            // {POMaster.Secret} = "hidden"
            {POMaster.PONumber} = {?PONumber} AND {POMaster.DivisionID} = "Open"
            """, multiple: true);
        report.Add(new XElement("SubReports", ChildReport()));
        var detail = report.Element("ReportDefinition")!.Descendants("ReportObjects").First();
        detail.Add(new XElement("SubreportObject",
            new XAttribute("Name", "LinesObject"), new XAttribute("SubreportName", "Lines"),
            new XAttribute("Width", "4000"), new XAttribute("Height", "800")));
        detail.Add(new XElement("FieldObject",
            new XAttribute("Name", "VendorName"), new XAttribute("Kind", "FieldObject"), new XAttribute("DataSource", "{Vendor.Name}")));
        return report;
    }

    private static XElement ChildReport() => new("Report",
        new XAttribute("Name", "Lines"),
        new XElement("Database", new XElement("Tables",
            Table("POLines", "PONumber", "Item"))),
        new XElement("DataDefinition",
            new XElement("RecordSelectionFormula", "{POLines.PONumber} = {?Pm-POMaster.PONumber}")),
        new XElement("SubReportLinks", new XElement("SubReportLink",
            new XAttribute("LinkedParameterName", "{?Pm-POMaster.PONumber}"),
            new XAttribute("MainReportFieldName", "{POMaster.PONumber}"),
            new XAttribute("SubreportFieldName", "{POLines.PONumber}"))),
        Areas(new XElement("FieldObject",
            new XAttribute("Name", "Item"), new XAttribute("Kind", "FieldObject"), new XAttribute("DataSource", "{POLines.Item}")),
            new XElement("FieldObject",
                new XAttribute("Name", "MainDivision"), new XAttribute("Kind", "FieldObject"), new XAttribute("DataSource", "{POMaster.DivisionID}")),
            new XElement("FieldObject",
                new XAttribute("Name", "LinkValue"), new XAttribute("Kind", "FieldObject"), new XAttribute("DataSource", "{?Pm-POMaster.PONumber}"))));

    private static XElement SelectionReport(string selection, bool multiple = false, bool range = false, string name = "PONumber", string valueKind = "String")
    {
        var parameters = new XElement("ParameterFieldDefinitions", Parameter(name, multiple, range, valueKind));
        if (name != "PONumber") parameters.Add(Parameter("PONumber", false, false));
        return new XElement("Report",
            new XAttribute("Name", "PO"),
            new XElement("Summaryinfo", new XAttribute("ReportTitle", "PO")),
            new XElement("Database", new XElement("Tables", Table("POMaster", "PONumber", "DivisionID", "OrderDate"))),
            new XElement("DataDefinition",
                new XElement("RecordSelectionFormula", selection),
                parameters,
                new XElement("FormulaFieldDefinitions", new XElement("FormulaFieldDefinition",
                    new XAttribute("Name", "Joined"), new XAttribute("FormulaName", "{@Joined}"), new XAttribute("ValueType", "StringField"),
                    "Join({?PONumber}, \",\")"))),
            Areas(
                new XElement("FieldObject", new XAttribute("Name", "Joined"), new XAttribute("Kind", "FieldObject"), new XAttribute("DataSource", "{@Joined}")),
                new XElement("FieldObject", new XAttribute("Name", "Number"), new XAttribute("Kind", "FieldObject"), new XAttribute("DataSource", "{POMaster.PONumber}"))));
    }

    // ValueType as the SDK export writes it ("Xsd:stringField"); the SQL selector
    // requires the declared, prompt and field types to agree.
    private static XElement Parameter(string name, bool multiple, bool range, string valueKind = "String") => new("ParameterFieldDefinition",
        new XAttribute("Name", name),
        new XAttribute("ParameterType", "ReportParameter"),
        new XAttribute("ParameterValueKind", valueKind + "Parameter"),
        new XAttribute("ValueType", valueKind == "String" ? "Xsd:stringField" : "Xsd:decimalField"),
        new XAttribute("ParameterFieldUsage", "InUse, DataFetching"),
        new XAttribute("PromptText", name),
        new XAttribute("EnableAllowMultipleValue", multiple ? "True" : "False"),
        new XAttribute("DiscreteOrRangeKind", range ? "RangeValue" : "DiscreteValue"));

    private static XElement Table(string alias, params string[] fields) => new("Table",
        new XAttribute("Alias", alias), new XAttribute("Name", alias),
        new XElement("Fields", fields.Select(field => new XElement("Field", new XAttribute("Name", field), new XAttribute("Type", "StringField")))));

    private static XElement Areas(XElement headerObject, params XElement[] detailObjects) => new("ReportDefinition",
        new XElement("Areas",
            new XElement("Area", new XAttribute("Kind", "ReportHeader"), new XAttribute("Name", "ReportHeaderArea1"),
                new XElement("Sections", new XElement("Section", new XAttribute("Kind", "ReportHeader"), new XAttribute("Name", "ReportHeaderSection1"), new XAttribute("Height", "360"),
                    new XElement("ReportObjects", headerObject)))),
            new XElement("Area", new XAttribute("Kind", "Detail"), new XAttribute("Name", "DetailArea1"),
                new XElement("Sections", new XElement("Section", new XAttribute("Kind", "Detail"), new XAttribute("Name", "DetailSection1"), new XAttribute("Height", "360"),
                    new XElement("ReportObjects", detailObjects))))));

    private static string KindReport() =>
        """
        <Report Name="Kinds">
          <ReportDefinition>
            <Areas>
              <Area Kind="Detail" Name="DetailArea1">
                <Sections>
                  <Section Kind="Detail" Name="DetailSection1" Height="360">
                    <ReportObjects>
                      <FieldObject Name="Amount" Kind="FieldObject"/>
                      <TextObject Name="Title"><Text>Hello</Text></TextObject>
                      <SubreportObject Name="Lines" Kind="SubreportObject" SubreportName="Lines"/>
                      <PictureObject Name="Logo"/>
                      <BoxObject Name="Frame"/>
                      <LineObject Name="Rule"/>
                      <FieldHeadingObject Name="Caption"/>
                      <FieldObject Name="SavedField" Kind="Field"/>
                      <ChartObject Name="SavedChart" Kind="Chart"/>
                      <FieldObject Name="SavedPivot" Kind="Pivot"/>
                      <ChartObject Name="Monthly"/>
                      <CrossTabObject Name="Region" Kind="CrossTabObject"/>
                    </ReportObjects>
                  </Section>
                </Sections>
              </Area>
            </Areas>
          </ReportDefinition>
        </Report>
        """;

    private sealed class CapturedExecutor : IReportDataExecutor
    {
        public List<(string Sql, IDictionary<string, object>? Parameters)> Calls { get; } = [];
        public DataTable Execute(string sql, IDictionary<string, object>? parameters)
        {
            Calls.Add((sql, parameters is null ? null : new Dictionary<string, object>(parameters, StringComparer.OrdinalIgnoreCase)));
            if (sql.Contains("POLines", StringComparison.Ordinal)) return new DataTable();
            var table = new DataTable();
            table.Columns.Add("POMaster_PONumber", typeof(string));
            table.Columns.Add("POMaster_DivisionID", typeof(string));
            table.Rows.Add("PO-100", "Open");
            table.Rows.Add("PO-999", "Open");
            return table;
        }
    }

    private sealed class EmptySession : IReportSessionContext
    {
        public object? Get(string parameterName) => null;
    }

    private sealed class UnusedExporter : IReportExporter
    {
        public bool IsLoaded => false;
        public void LoadReport(string rptFilePath) { }
        public void SetParameters(Dictionary<string, string> parameters) { }
        public byte[] ExportToPdf() => [];
        public byte[] ExportToExcel() => [];
        public byte[] ExportToWord() => [];
        public byte[] ExportToRtf() => [];
        public byte[] ExportToCsv() => [];
    }

    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => ValueTask.FromResult(default(T)!);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => ValueTask.FromResult(default(T)!);
    }
}
