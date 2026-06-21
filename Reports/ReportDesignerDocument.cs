using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public sealed class ReportDesignerDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled Report";
    public string SourceName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public ReportDesignerPage Page { get; set; } = new();
    public List<ReportDesignerDataTable> DataSources { get; set; } = new();
    public List<ReportDesignerSection> Sections { get; set; } = new();
    public List<ReportDesignerField> Fields { get; set; } = new();
    public List<ReportDesignerParameter> Parameters { get; set; } = new();
    public List<ReportDesignerGroup> Groups { get; set; } = new();
    public List<ReportDesignerSort> Sorts { get; set; } = new();
    public List<ReportDesignerSummary> Summaries { get; set; } = new();
    public List<ReportDesignerFilter> Filters { get; set; } = new();
    public List<ReportDesignerSubreport> Subreports { get; set; } = new();
    public List<ReportDesignerDataLink> Links { get; set; } = new();
    public string CustomSql { get; set; } = "";
    public bool IsDirty { get; set; }
    public string StatusMessage { get; set; } = "";

    internal XDocument? SourceDocument { get; set; }
    internal Dictionary<string, XElement> SourceSections { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, XElement> SourceObjects { get; } = new(StringComparer.Ordinal);

    public IEnumerable<ReportDesignerElement> Elements => Sections.SelectMany(section => section.Elements);

    public static ReportDesignerDocument CreateBlank(string title = "Untitled Report")
    {
        var document = new ReportDesignerDocument
        {
            Title = title,
            SourceName = title
        };

        document.Sections.AddRange(new[]
        {
            new ReportDesignerSection { Id = "report-header", Name = "ReportHeaderSection1", Kind = "ReportHeader", AreaName = "ReportHeaderArea1", HeightTwips = 540 },
            new ReportDesignerSection { Id = "page-header", Name = "PageHeaderSection1", Kind = "PageHeader", AreaName = "PageHeaderArea1", HeightTwips = 480 },
            new ReportDesignerSection { Id = "detail", Name = "DetailSection1", Kind = "Detail", AreaName = "DetailArea1", HeightTwips = 360 },
            new ReportDesignerSection { Id = "report-footer", Name = "ReportFooterSection1", Kind = "ReportFooter", AreaName = "ReportFooterArea1", HeightTwips = 480 },
            new ReportDesignerSection { Id = "page-footer", Name = "PageFooterSection1", Kind = "PageFooter", AreaName = "PageFooterArea1", HeightTwips = 420 }
        });

        return document;
    }

    public static ReportDesignerDocument CreateFromWizard(ReportCreationWizardResult wizard)
    {
        ArgumentNullException.ThrowIfNull(wizard);

        var title = string.IsNullOrWhiteSpace(wizard.Title) ? "Untitled Report" : wizard.Title.Trim();
        var document = CreateBlank(title);
        document.Id = Guid.NewGuid().ToString("N");
        document.SourceName = $"{title}.xml";
        document.SourcePath = "";
        document.StatusMessage = "Created by report wizard.";
        document.DataSources = wizard.SelectedTables.Select(CloneTable).ToList();
        document.Fields = BuildWizardFields(wizard).ToList();
        document.Groups = wizard.GroupFields
            .Select((field, index) => new ReportDesignerGroup
            {
                Name = $"Group #{index + 1}: {field.DisplayName}",
                Condition = $"{{{field.DisplayName}}}",
                SortDirection = "Ascending"
            })
            .ToList();
        document.Summaries = wizard.Summaries.Select(CloneSummary).ToList();
        document.Filters = wizard.Filters.Select(CloneFilter).ToList();
        document.Links = wizard.Links.Select(CloneLink).ToList();
        document.CustomSql = wizard.CustomSql ?? "";

        var displayFields = wizard.DisplayFields.Count > 0
            ? wizard.DisplayFields.ToList()
            : document.Fields.Where(field => !field.IsFormula).Take(5).ToList();

        document.Sections = BuildWizardSections(document, displayFields, wizard);
        ApplyWizardTemplate(document, wizard.TemplateName);
        document.IsDirty = true;
        return document;
    }

    private static IEnumerable<ReportDesignerField> BuildWizardFields(ReportCreationWizardResult wizard)
    {
        var fields = new Dictionary<string, ReportDesignerField>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in wizard.SelectedTables.SelectMany(table => table.Fields)
                     .Concat(wizard.DisplayFields)
                     .Concat(wizard.GroupFields)
                     .Concat(wizard.Summaries.Select(summary => summary.Field))
                     .Concat(wizard.Filters.Select(filter => filter.Field)))
        {
            fields[field.DisplayName] = CloneField(field);
        }

        return fields.Values;
    }

    private static List<ReportDesignerSection> BuildWizardSections(
        ReportDesignerDocument document,
        List<ReportDesignerField> displayFields,
        ReportCreationWizardResult wizard)
    {
        var sections = new List<ReportDesignerSection>();
        var reportHeader = NewSection("report-header", "ReportHeaderSection1", "ReportHeader", 420);
        var pageHeader = NewSection("page-header-a", "PageHeaderSection1", "PageHeader", 780);
        var columnHeader = NewSection("page-header-b", "PageHeaderSection2", "PageHeader", 360);
        var detail = NewSection("detail", "DetailSection1", "Detail", 360);
        var reportFooter = NewSection("report-footer", "ReportFooterSection1", "ReportFooter", 480);
        var pageFooter = NewSection("page-footer", "PageFooterSection1", "PageFooter", 360);

        sections.Add(reportHeader);
        sections.Add(pageHeader);
        sections.Add(columnHeader);

        AddText(pageHeader, "ReportTitle", document.Title, 240, 180, 4320, 420, 22m, true, "#5b789c");
        AddText(pageHeader, "ReportDescriptionLabel", "Report Description:", 240, 600, 1800, 300, 11m, false, "#5b789c");
        AddText(pageHeader, "ReportComments", "Report Comments", 240, 900, 2400, 300, 10m, false, "#202020");

        var groupHeaderSections = new List<ReportDesignerSection>();
        var groupFooterSections = new List<ReportDesignerSection>();
        for (var index = 0; index < wizard.GroupFields.Count; index++)
        {
            var field = wizard.GroupFields[index];
            var groupHeader = NewSection($"group-header-{index + 1}", $"GroupHeaderSection{index + 1}", "GroupHeader", 560);
            var groupFooter = NewSection($"group-footer-{index + 1}", $"GroupFooterSection{index + 1}", "GroupFooter", 420);
            AddText(groupHeader, $"GroupHeaderLabel{index + 1}", $"Group #{index + 1}: {field.Name}", 240, 150, 4200, 360, 20m, true, "#5b789c");
            groupHeaderSections.Add(groupHeader);
            groupFooterSections.Insert(0, groupFooter);
        }

        foreach (var groupHeader in groupHeaderSections)
            sections.Add(groupHeader);

        AddColumnObjects(columnHeader, detail, displayFields, document.Page.ContentWidthTwips);
        sections.Add(detail);

        foreach (var groupFooter in groupFooterSections)
        {
            AddSummaryObjects(groupFooter, wizard.Summaries, document.Page.ContentWidthTwips);
            sections.Add(groupFooter);
        }

        AddSummaryObjects(reportFooter, wizard.Summaries, document.Page.ContentWidthTwips);
        sections.Add(reportFooter);
        sections.Add(pageFooter);

        AddText(pageFooter, "PageNumber", "Page 1", document.Page.ContentWidthTwips - 1440, 120, 1200, 240, 9m, false, "#707070");
        return sections;
    }

    private static ReportDesignerSection NewSection(string id, string name, string kind, int heightTwips)
    {
        return new ReportDesignerSection
        {
            Id = id,
            Name = name,
            Kind = kind,
            AreaName = $"{kind}Area",
            HeightTwips = heightTwips,
            KeepTogether = true
        };
    }

    private static void AddColumnObjects(
        ReportDesignerSection columnHeader,
        ReportDesignerSection detail,
        List<ReportDesignerField> fields,
        int contentWidthTwips)
    {
        if (fields.Count == 0)
            return;

        var usableWidth = Math.Max(3600, contentWidthTwips - 480);
        var columnWidth = Math.Clamp(usableWidth / fields.Count, 1200, 2700);
        var left = 240;

        foreach (var field in fields)
        {
            AddText(columnHeader, $"{field.Name}Heading", field.Name, left, 120, columnWidth - 90, 260, 10m, true, "#ffffff", "#5b789c", "FieldHeading");
            AddField(detail, $"{field.Name}Value", field, left, 90, columnWidth - 90, 260);
            left += columnWidth;
        }
    }

    private static void AddSummaryObjects(ReportDesignerSection section, List<ReportDesignerSummary> summaries, int contentWidthTwips)
    {
        if (summaries.Count == 0)
            return;

        var top = 90;
        foreach (var summary in summaries)
        {
            AddText(section, $"Summary{summary.Field.Name}", $"{summary.Operation} of {summary.Field.DisplayName}", 240, top, contentWidthTwips - 720, 260, 10m, true, "#6b6b6b");
            top += 280;
        }
    }

    private static void AddText(
        ReportDesignerSection section,
        string name,
        string text,
        int left,
        int top,
        int width,
        int height,
        decimal fontSize,
        bool bold,
        string color,
        string background = "transparent",
        string kind = "Text")
    {
        section.Elements.Add(new ReportDesignerElement
        {
            Id = Guid.NewGuid().ToString("N"),
            SectionId = section.Id,
            Name = name,
            Kind = kind,
            Text = text,
            LeftTwips = left,
            TopTwips = top,
            WidthTwips = width,
            HeightTwips = height,
            FontFamily = "Arial",
            FontSize = fontSize,
            Bold = bold,
            TextColor = color,
            BackgroundColor = background
        });
    }

    private static void AddField(
        ReportDesignerSection section,
        string name,
        ReportDesignerField field,
        int left,
        int top,
        int width,
        int height)
    {
        section.Elements.Add(new ReportDesignerElement
        {
            Id = Guid.NewGuid().ToString("N"),
            SectionId = section.Id,
            Name = name,
            Kind = "Field",
            Binding = $"{{{field.DisplayName}}}",
            LeftTwips = left,
            TopTwips = top,
            WidthTwips = width,
            HeightTwips = height,
            FontFamily = "Arial",
            FontSize = 10m,
            TextColor = "#000000"
        });
    }

    private static void ApplyWizardTemplate(ReportDesignerDocument document, string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName) || string.Equals(templateName, "No Template", StringComparison.OrdinalIgnoreCase))
            return;

        var useBlue = templateName.Contains("Blue", StringComparison.OrdinalIgnoreCase)
            || templateName.Contains("Block", StringComparison.OrdinalIgnoreCase);
        if (!useBlue)
            return;

        foreach (var section in document.Sections)
        {
            foreach (var element in section.Elements.Where(element => element.Kind == "FieldHeading"))
            {
                element.BackgroundColor = "#5b789c";
                element.TextColor = "#ffffff";
                element.Bold = true;
            }
        }
    }

    private static ReportDesignerDataTable CloneTable(ReportDesignerDataTable table)
    {
        return new ReportDesignerDataTable
        {
            Name = table.Name,
            Schema = table.Schema,
            Fields = table.Fields.Select(CloneField).ToList()
        };
    }

    private static ReportDesignerField CloneField(ReportDesignerField field)
    {
        return new ReportDesignerField
        {
            Name = field.Name,
            Table = field.Table,
            LongName = field.LongName,
            Formula = field.Formula,
            Type = field.Type,
            IsFormula = field.IsFormula
        };
    }

    private static ReportDesignerFilter CloneFilter(ReportDesignerFilter filter)
    {
        return new ReportDesignerFilter
        {
            Field = CloneField(filter.Field),
            Operator = filter.Operator,
            Value = filter.Value
        };
    }

    private static ReportDesignerSummary CloneSummary(ReportDesignerSummary summary)
    {
        return new ReportDesignerSummary
        {
            Field = CloneField(summary.Field),
            Operation = summary.Operation,
            GroupName = summary.GroupName
        };
    }

    private static ReportDesignerDataLink CloneLink(ReportDesignerDataLink link)
    {
        return new ReportDesignerDataLink
        {
            LeftTable = link.LeftTable,
            LeftField = link.LeftField,
            RightTable = link.RightTable,
            RightField = link.RightField,
            JoinType = link.JoinType
        };
    }
}

public sealed class ReportDesignerPage
{
    public string Orientation { get; set; } = "Portrait";
    public string PaperSize { get; set; } = "PaperLetter";
    public int ContentWidthTwips { get; set; } = 11520;
    public int ContentHeightTwips { get; set; } = 15120;
    public int MarginLeftTwips { get; set; } = 720;
    public int MarginTopTwips { get; set; } = 720;
    public int MarginRightTwips { get; set; } = 720;
    public int MarginBottomTwips { get; set; } = 720;
}

public sealed class ReportDesignerSection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceKey { get; set; } = "";
    public string Name { get; set; } = "Section";
    public string Kind { get; set; } = "Detail";
    public string AreaName { get; set; } = "";
    public int HeightTwips { get; set; } = 360;
    public bool HideForDrillDown { get; set; }
    public bool IsSuppressed { get; set; }
    public bool PrintAtBottomOfPage { get; set; }
    public bool SuppressIfBlank { get; set; }
    public bool UnderlayFollowingSections { get; set; }
    public bool ReadOnly { get; set; }
    public bool RelativePositions { get; set; }
    public bool NewPageBefore { get; set; }
    public bool NewPageAfter { get; set; }
    public bool ResetPageNumberAfter { get; set; }
    public bool KeepTogether { get; set; } = true;
    public string BackgroundColor { get; set; } = "#ffffff";
    public List<ReportDesignerElement> Elements { get; set; } = new();
}

public sealed class ReportDesignerElement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceKey { get; set; } = "";
    public string SectionId { get; set; } = "";
    public string Name { get; set; } = "Object";
    public string Kind { get; set; } = "Text";
    public string Text { get; set; } = "";
    public string Binding { get; set; } = "";
    public string SubreportName { get; set; } = "";
    public string SubreportXmlPath { get; set; } = "";
    public int LeftTwips { get; set; }
    public int TopTwips { get; set; }
    public int WidthTwips { get; set; } = 1440;
    public int HeightTwips { get; set; } = 240;
    public string FontFamily { get; set; } = "Arial";
    public decimal FontSize { get; set; } = 10m;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public string TextColor { get; set; } = "#000000";
    public string BackgroundColor { get; set; } = "transparent";
    public string HorizontalAlignment { get; set; } = "Default";
    public bool IsSuppressed { get; set; }
    public bool CanGrow { get; set; }
    public string FormatString { get; set; } = "";
    public bool LockFormat { get; set; }
    public bool LockSizePosition { get; set; }
    public int IndentTwips { get; set; }
    public ReportDesignerHighlightRule HighlightRule { get; set; } = new();

    public string DisplayText
    {
        get
        {
            if (string.Equals(Kind, "Subreport", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(SubreportName) ? "Subreport" : SubreportName;

            if (!string.IsNullOrWhiteSpace(Text))
                return Text;

            return string.IsNullOrWhiteSpace(Binding) ? Name : Binding;
        }
    }

    public ReportDesignerElement CloneFor(string sectionId, string name)
    {
        return new ReportDesignerElement
        {
            Id = Guid.NewGuid().ToString("N"),
            SectionId = sectionId,
            Name = name,
            Kind = Kind,
            Text = Text,
            Binding = Binding,
            SubreportName = SubreportName,
            SubreportXmlPath = SubreportXmlPath,
            LeftTwips = LeftTwips + ReportDesignerMetrics.PixelsToTwips(12),
            TopTwips = TopTwips + ReportDesignerMetrics.PixelsToTwips(12),
            WidthTwips = WidthTwips,
            HeightTwips = HeightTwips,
            FontFamily = FontFamily,
            FontSize = FontSize,
            Bold = Bold,
            Italic = Italic,
            Underline = Underline,
            TextColor = TextColor,
            BackgroundColor = BackgroundColor,
            HorizontalAlignment = HorizontalAlignment,
            IsSuppressed = IsSuppressed,
            CanGrow = CanGrow,
            FormatString = FormatString,
            LockFormat = LockFormat,
            LockSizePosition = LockSizePosition,
            IndentTwips = IndentTwips,
            HighlightRule = HighlightRule.Clone()
        };
    }
}

public sealed class ReportDesignerHighlightRule
{
    public bool Enabled { get; set; }
    public string FieldName { get; set; } = "";
    public string Operator { get; set; } = "is equal to";
    public string Value { get; set; } = "";
    public string FontStyle { get; set; } = "Default";
    public string TextColor { get; set; } = "#000000";
    public string BackgroundColor { get; set; } = "transparent";
    public string BorderStyle { get; set; } = "Default border style";

    public ReportDesignerHighlightRule Clone() => new()
    {
        Enabled = Enabled,
        FieldName = FieldName,
        Operator = Operator,
        Value = Value,
        FontStyle = FontStyle,
        TextColor = TextColor,
        BackgroundColor = BackgroundColor,
        BorderStyle = BorderStyle
    };
}

public sealed class ReportDesignerField
{
    public string Name { get; set; } = "";
    public string Table { get; set; } = "";
    public string LongName { get; set; } = "";
    public string Formula { get; set; } = "";
    public string Type { get; set; } = "";
    public bool IsFormula { get; set; }
    public string DisplayName => string.IsNullOrWhiteSpace(Table) ? Name : $"{Table}.{Name}";
}

public sealed class ReportDesignerDataTable
{
    public string Name { get; set; } = "";
    public string Schema { get; set; } = "";
    public List<ReportDesignerField> Fields { get; set; } = new();
    public string DisplayName => string.IsNullOrWhiteSpace(Schema) ? Name : $"{Schema}.{Name}";
}

public sealed class ReportDesignerSummary
{
    public ReportDesignerField Field { get; set; } = new();
    public string Operation { get; set; } = "Sum";
    public string GroupName { get; set; } = "";
}

public sealed class ReportDesignerSort
{
    public ReportDesignerField Field { get; set; } = new();
    public string Direction { get; set; } = "Ascending";
}

public sealed class ReportDesignerFilter
{
    public ReportDesignerField Field { get; set; } = new();
    public string Operator { get; set; } = "is equal to";
    public string Value { get; set; } = "";
}

public sealed class ReportCreationWizardResult
{
    public string Title { get; set; } = "Untitled Report";
    public List<ReportDesignerDataTable> SelectedTables { get; set; } = new();
    public List<ReportDesignerField> DisplayFields { get; set; } = new();
    public List<ReportDesignerField> GroupFields { get; set; } = new();
    public List<ReportDesignerSummary> Summaries { get; set; } = new();
    public List<ReportDesignerFilter> Filters { get; set; } = new();
    public List<ReportDesignerDataLink> Links { get; set; } = new();
    public string CustomSql { get; set; } = "";
    public string TemplateName { get; set; } = "No Template";
}

public sealed class ReportDesignerParameter
{
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Required { get; set; } = true;
    public bool AllowMultiple { get; set; }
}

public sealed class ReportDesignerGroup
{
    public string Name { get; set; } = "";
    public string Condition { get; set; } = "";
    public string SortDirection { get; set; } = "Ascending";
}

public sealed class ReportDesignerSubreport
{
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
}

public sealed class ReportDesignerDataLink
{
    public string LeftTable { get; set; } = "";
    public string LeftField { get; set; } = "";
    public string RightTable { get; set; } = "";
    public string RightField { get; set; } = "";
    public string JoinType { get; set; } = "Inner";

    public string DisplayName => string.IsNullOrWhiteSpace(LeftTable) && string.IsNullOrWhiteSpace(RightTable)
        ? "New link"
        : $"{LeftTable}.{LeftField} = {RightTable}.{RightField}";
}

public sealed class ReportDesignerElementSelection
{
    public ReportDesignerSection Section { get; set; } = default!;
    public ReportDesignerElement Element { get; set; } = default!;
}

public static class ReportDesignerMetrics
{
    public const int TwipsPerPixel = 15;

    public static int TwipsToPixels(int twips)
    {
        return twips <= 0 ? 0 : Math.Max(1, (int)Math.Round(twips / (double)TwipsPerPixel));
    }

    public static int PixelsToTwips(int pixels)
    {
        return Math.Max(0, pixels * TwipsPerPixel);
    }
}

public static class ReportDesignerXmlSerializer
{
    private const string MetadataElementName = "FlexKitReportDesigner";

    public static ReportDesignerDocument FromFile(string xmlFilePath)
    {
        if (!File.Exists(xmlFilePath))
            throw new FileNotFoundException($"Report XML not found: {xmlFilePath}", xmlFilePath);

        return FromXml(File.ReadAllText(xmlFilePath), Path.GetFileName(xmlFilePath), xmlFilePath);
    }

    public static ReportDesignerDocument FromXml(string xml, string? sourceName = null, string? sourcePath = null)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return ReportDesignerDocument.CreateBlank();

        var sourceDocument = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var root = sourceDocument.Root ?? throw new InvalidDataException("Invalid report XML: missing root element.");
        var reportDefinition = Child(root, "ReportDefinition");

        var document = new ReportDesignerDocument
        {
            SourceDocument = sourceDocument,
            SourceName = sourceName ?? Attribute(root, "Name") ?? "Report.xml",
            SourcePath = sourcePath ?? "",
            Title = GetReportTitle(root, sourceName),
            Page = ParsePage(root)
        };

        ParseFields(root, document);
        ParseLinks(root, document);
        ParseCustomSql(root, document);
        ParseParameters(root, document);
        ParseGroups(root, document);
        ParseSorts(root, document);
        ParseSummaries(root, document);
        ParseFilters(root, document);
        ParseSubreports(root, document);

        if (reportDefinition != null)
            ParseSections(reportDefinition, document);

        if (document.Sections.Count == 0)
            document.Sections.AddRange(ReportDesignerDocument.CreateBlank(document.Title).Sections);

        document.StatusMessage = $"Loaded {document.Sections.Count} sections and {document.Elements.Count()} objects.";
        return document;
    }

    public static string ToXml(ReportDesignerDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sourceDocument = document.SourceDocument ?? BuildSourceDocument(document);
        ApplyDesignerToSource(document, sourceDocument);
        document.SourceDocument = sourceDocument;
        return sourceDocument.ToString(SaveOptions.None);
    }

    private static XDocument BuildSourceDocument(ReportDesignerDocument document)
    {
        var root = new XElement("Report",
            new XAttribute("Name", string.IsNullOrWhiteSpace(document.Title) ? "Untitled Report" : document.Title),
            new XAttribute("HasSavedData", "False"));

        root.Add(new XElement("Summaryinfo", new XAttribute("ReportTitle", document.Title ?? "")));
        root.Add(new XElement("PrintOptions",
            new XAttribute("PageContentHeight", document.Page.ContentHeightTwips),
            new XAttribute("PageContentWidth", document.Page.ContentWidthTwips),
            new XAttribute("PaperOrientation", document.Page.Orientation),
            new XAttribute("PaperSize", document.Page.PaperSize),
            new XElement("PageMargins",
                new XAttribute("bottomMargin", document.Page.MarginBottomTwips),
                new XAttribute("leftMargin", document.Page.MarginLeftTwips),
                new XAttribute("rightMargin", document.Page.MarginRightTwips),
                new XAttribute("topMargin", document.Page.MarginTopTwips))));

        root.Add(BuildDatabaseElement(document));
        root.Add(BuildDataDefinitionElement(document));

        var areas = new XElement("Areas");
        root.Add(new XElement("ReportDefinition", areas));

        var built = new XDocument(root);
        document.SourceSections.Clear();
        document.SourceObjects.Clear();

        foreach (var section in document.Sections)
        {
            var area = CreateAreaElement(section);
            var sectionElement = CreateSectionElement(section);
            area.Element("Sections")!.Add(sectionElement);
            areas.Add(area);
            section.SourceKey = section.Id;
            document.SourceSections[section.SourceKey] = sectionElement;

            var objects = sectionElement.Element("ReportObjects")!;
            foreach (var element in section.Elements)
            {
                var objectElement = CreateObjectElement(element);
                element.SourceKey = element.Id;
                objects.Add(objectElement);
                document.SourceObjects[element.SourceKey] = objectElement;
            }
        }

        return built;
    }

    private static XElement BuildDatabaseElement(ReportDesignerDocument document)
    {
        var tables = document.DataSources.Count > 0
            ? document.DataSources
            : document.Fields
                .Where(field => !string.IsNullOrWhiteSpace(field.Table))
                .GroupBy(field => field.Table)
                .Select(group => new ReportDesignerDataTable
                {
                    Name = group.Key,
                    Fields = group.Select(CloneField).ToList()
                })
                .ToList();

        return new XElement("Database",
            BuildTableLinksElement(document),
            new XElement("Tables",
                tables.Select(table =>
                    new XElement("Table",
                        new XAttribute("Name", table.Name),
                        new XAttribute("Alias", table.Name),
                        new XAttribute("QualifiedName", string.IsNullOrWhiteSpace(table.Schema) ? table.Name : $"{table.Schema}.{table.Name}"),
                        new XElement("Fields",
                            table.Fields.Select(field =>
                                new XElement("Field",
                                    new XAttribute("Name", field.Name),
                                    new XAttribute("ShortName", field.Name),
                                    new XAttribute("LongName", string.IsNullOrWhiteSpace(field.LongName) ? field.DisplayName : field.LongName),
                                    new XAttribute("Type", string.IsNullOrWhiteSpace(field.Type) ? "String" : field.Type))))))));
    }

    private static XElement BuildTableLinksElement(ReportDesignerDocument document)
    {
        return new XElement("TableLinks",
            document.Links
                .Where(link =>
                    !string.IsNullOrWhiteSpace(link.LeftTable) &&
                    !string.IsNullOrWhiteSpace(link.LeftField) &&
                    !string.IsNullOrWhiteSpace(link.RightTable) &&
                    !string.IsNullOrWhiteSpace(link.RightField))
                .Select(link =>
                    new XElement("TableLink",
                        new XAttribute("JoinType", ToCrystalJoinType(link.JoinType)),
                        new XElement("SourceFields", BuildLinkField(link.LeftTable, link.LeftField)),
                        new XElement("DestinationFields", BuildLinkField(link.RightTable, link.RightField)))));
    }

    private static XElement BuildLinkField(string table, string field)
    {
        return new XElement("Field",
            new XAttribute("FormulaName", $"{{{table}.{field}}}"),
            new XAttribute("Kind", "DatabaseField"),
            new XAttribute("Name", field),
            new XAttribute("NumberOfBytes", 0),
            new XAttribute("ValueType", "Xsd:stringField"));
    }

    private static XElement BuildDataDefinitionElement(ReportDesignerDocument document)
    {
        return new XElement("DataDefinition",
            new XElement("Groups",
                document.Groups.Select(group =>
                    new XElement("Group",
                        new XAttribute("Name", group.Name),
                        new XAttribute("ConditionField", group.Condition),
                        new XAttribute("SortDirection", group.SortDirection)))),
            new XElement("RecordSelection",
                document.Filters.Select(filter =>
                    new XElement("Filter",
                        new XAttribute("Field", filter.Field.DisplayName),
                        new XAttribute("Operator", filter.Operator),
                        new XAttribute("Value", filter.Value)))),
            new XElement("SortFields",
                document.Sorts.Select(sort =>
                    new XElement("SortField",
                        new XAttribute("Field", sort.Field.DisplayName),
                        new XAttribute("Direction", sort.Direction)))),
            new XElement("SummaryFieldDefinitions",
                document.Summaries.Select(summary =>
                    new XElement("SummaryFieldDefinition",
                        new XAttribute("Field", summary.Field.DisplayName),
                        new XAttribute("Operation", summary.Operation),
                        new XAttribute("GroupName", summary.GroupName)))),
            new XElement("FormulaFieldDefinitions",
                document.Fields.Where(field => field.IsFormula).Select(field =>
                    new XElement("FormulaFieldDefinition",
                        new XAttribute("Name", field.Name),
                        new XAttribute("FormulaName", string.IsNullOrWhiteSpace(field.Formula) ? $"@{field.Name}" : field.Formula),
                        new XAttribute("ValueType", string.IsNullOrWhiteSpace(field.Type) ? "Formula" : field.Type)))),
            new XElement("ParameterFieldDefinitions",
                document.Parameters.Select(parameter =>
                    new XElement("ParameterFieldDefinition",
                        new XAttribute("Name", parameter.Name),
                        new XAttribute("PromptText", parameter.Prompt),
                        new XAttribute("ValueType", parameter.Type),
                        new XAttribute("OptionalPrompt", Lower(!parameter.Required)),
                        new XAttribute("EnableAllowMultipleValue", Lower(parameter.AllowMultiple))))));
    }

    private static ReportDesignerField CloneField(ReportDesignerField field)
    {
        return new ReportDesignerField
        {
            Name = field.Name,
            Table = field.Table,
            LongName = field.LongName,
            Formula = field.Formula,
            Type = field.Type,
            IsFormula = field.IsFormula
        };
    }

    private static void ApplyDesignerToSource(ReportDesignerDocument document, XDocument sourceDocument)
    {
        var root = sourceDocument.Root ?? throw new InvalidDataException("Invalid report XML: missing root element.");

        root.SetAttributeValue("Name", string.IsNullOrWhiteSpace(document.Title) ? document.SourceName : document.Title);
        ApplyPrintOptions(root, document.Page);
        ApplyDatabaseDefinition(root, document);
        ApplyDataDefinition(root, document);

        foreach (var section in document.Sections)
        {
            var sectionElement = ResolveSectionElement(document, root, section);
            ApplySection(section, sectionElement);

            var objectsElement = EnsureChild(sectionElement, "ReportObjects");
            var retainedObjects = new HashSet<XElement>();
            foreach (var element in section.Elements)
            {
                element.SectionId = section.Id;
                var objectElement = ResolveObjectElement(document, objectsElement, element);
                ApplyElement(element, objectElement);
                retainedObjects.Add(objectElement);
            }

            foreach (var objectElement in objectsElement.Elements().Where(IsReportObjectElement).ToList())
            {
                if (!retainedObjects.Contains(objectElement))
                    objectElement.Remove();
            }
        }

        UpsertDesignerMetadata(root, document);
    }

    private static XElement ResolveSectionElement(ReportDesignerDocument document, XElement root, ReportDesignerSection section)
    {
        if (!string.IsNullOrWhiteSpace(section.SourceKey) &&
            document.SourceSections.TryGetValue(section.SourceKey, out var existing) && existing.Document != null)
            return existing;

        var byName = root.Descendants("Section").FirstOrDefault(item =>
            string.Equals(Attribute(item, "Name"), section.Name, StringComparison.OrdinalIgnoreCase));
        if (byName != null)
        {
            section.SourceKey = section.Id;
            document.SourceSections[section.SourceKey] = byName;
            return byName;
        }

        var reportDefinition = Child(root, "ReportDefinition") ?? EnsureChild(root, "ReportDefinition");
        var areas = Child(reportDefinition, "Areas") ?? EnsureChild(reportDefinition, "Areas");
        var area = CreateAreaElement(section);
        var sectionElement = CreateSectionElement(section);
        area.Element("Sections")!.Add(sectionElement);
        areas.Add(area);
        section.SourceKey = section.Id;
        document.SourceSections[section.SourceKey] = sectionElement;
        return sectionElement;
    }

    private static XElement ResolveObjectElement(ReportDesignerDocument document, XElement objectsElement, ReportDesignerElement element)
    {
        if (!string.IsNullOrWhiteSpace(element.SourceKey) &&
            document.SourceObjects.TryGetValue(element.SourceKey, out var existing) && existing.Document != null)
            return existing;

        var byName = objectsElement.Elements().FirstOrDefault(item =>
            string.Equals(Attribute(item, "Name"), element.Name, StringComparison.OrdinalIgnoreCase));
        if (byName != null)
        {
            element.SourceKey = element.Id;
            document.SourceObjects[element.SourceKey] = byName;
            return byName;
        }

        var objectElement = CreateObjectElement(element);
        objectsElement.Add(objectElement);
        element.SourceKey = element.Id;
        document.SourceObjects[element.SourceKey] = objectElement;
        return objectElement;
    }

    private static void ApplyPrintOptions(XElement root, ReportDesignerPage page)
    {
        var printOptions = Child(root, "PrintOptions") ?? EnsureChild(root, "PrintOptions");
        printOptions.SetAttributeValue("PageContentHeight", page.ContentHeightTwips);
        printOptions.SetAttributeValue("PageContentWidth", page.ContentWidthTwips);
        printOptions.SetAttributeValue("PaperOrientation", page.Orientation);
        printOptions.SetAttributeValue("PaperSize", page.PaperSize);

        var margins = Child(printOptions, "PageMargins") ?? EnsureChild(printOptions, "PageMargins");
        margins.SetAttributeValue("bottomMargin", page.MarginBottomTwips);
        margins.SetAttributeValue("leftMargin", page.MarginLeftTwips);
        margins.SetAttributeValue("rightMargin", page.MarginRightTwips);
        margins.SetAttributeValue("topMargin", page.MarginTopTwips);
    }

    private static void ApplyDatabaseDefinition(XElement root, ReportDesignerDocument document)
    {
        ReplaceRootChild(root, "Database", BuildDatabaseElement(document));
    }

    private static void ApplyDataDefinition(XElement root, ReportDesignerDocument document)
    {
        ReplaceRootChild(root, "DataDefinition", BuildDataDefinitionElement(document));
    }

    private static void ReplaceRootChild(XElement root, string name, XElement replacement)
    {
        var existing = Child(root, name);
        if (existing != null)
        {
            existing.ReplaceWith(replacement);
            return;
        }

        root.Add(replacement);
    }

    private static void ApplySection(ReportDesignerSection section, XElement sectionElement)
    {
        sectionElement.SetAttributeValue("Name", section.Name);
        sectionElement.SetAttributeValue("Kind", section.Kind);
        sectionElement.SetAttributeValue("Height", section.HeightTwips);

        var format = Child(sectionElement, "SectionFormat") ?? EnsureChild(sectionElement, "SectionFormat");
        format.SetAttributeValue("EnableHideForDrillDown", Lower(section.HideForDrillDown));
        format.SetAttributeValue("EnableNewPageAfter", Lower(section.NewPageAfter));
        format.SetAttributeValue("EnableNewPageBefore", Lower(section.NewPageBefore));
        format.SetAttributeValue("EnablePrintAtBottomOfPage", Lower(section.PrintAtBottomOfPage));
        format.SetAttributeValue("EnableResetPageNumberAfter", Lower(section.ResetPageNumberAfter));
        format.SetAttributeValue("EnableSuppress", Lower(section.IsSuppressed));
        format.SetAttributeValue("EnableSuppressIfBlank", Lower(section.SuppressIfBlank));
        format.SetAttributeValue("EnableUnderlaySection", Lower(section.UnderlayFollowingSections));
        format.SetAttributeValue("EnableKeepTogether", Lower(section.KeepTogether));
        format.SetAttributeValue("ReadOnly", Lower(section.ReadOnly));
        format.SetAttributeValue("RelativePositions", Lower(section.RelativePositions));

        var background = Child(format, "BackgroundColor") ?? EnsureChild(format, "BackgroundColor");
        SetColorElement(background, section.BackgroundColor, alphaWhenTransparent: 0);
    }

    private static void ApplyElement(ReportDesignerElement element, XElement objectElement)
    {
        objectElement.SetAttributeValue("Name", string.IsNullOrWhiteSpace(element.Name) ? element.Kind : element.Name);
        objectElement.SetAttributeValue("Kind", string.IsNullOrWhiteSpace(element.Kind) ? InferKindFromObjectElement(objectElement) : element.Kind);
        objectElement.SetAttributeValue("Top", element.TopTwips);
        objectElement.SetAttributeValue("Left", element.LeftTwips);
        objectElement.SetAttributeValue("Width", element.WidthTwips);
        objectElement.SetAttributeValue("Height", element.HeightTwips);

        var kind = element.Kind.Trim();
        if (string.Equals(kind, "Text", StringComparison.OrdinalIgnoreCase))
        {
            (Child(objectElement, "Text") ?? EnsureChild(objectElement, "Text")).Value = element.Text ?? "";
        }
        else if (string.Equals(kind, "FieldHeading", StringComparison.OrdinalIgnoreCase))
        {
            objectElement.SetAttributeValue("FieldObjectName", element.Binding ?? "");
            (Child(objectElement, "Text") ?? EnsureChild(objectElement, "Text")).Value = element.Text ?? "";
        }
        else if (string.Equals(kind, "Subreport", StringComparison.OrdinalIgnoreCase))
        {
            objectElement.SetAttributeValue("SubreportName", element.SubreportName ?? "");
            if (!string.IsNullOrWhiteSpace(element.SubreportXmlPath))
                objectElement.SetAttributeValue("SubreportXmlPath", element.SubreportXmlPath);
        }
        else
        {
            objectElement.SetAttributeValue("DataSource", element.Binding ?? "");
        }

        var color = Child(objectElement, "Color") ?? EnsureChild(objectElement, "Color");
        SetColorElement(color, element.TextColor, alphaWhenTransparent: 255);

        var font = Child(objectElement, "Font") ?? EnsureChild(objectElement, "Font");
        font.SetAttributeValue("Bold", Lower(element.Bold));
        font.SetAttributeValue("FontFamily", string.IsNullOrWhiteSpace(element.FontFamily) ? "Arial" : element.FontFamily);
        font.SetAttributeValue("Italic", Lower(element.Italic));
        font.SetAttributeValue("Name", string.IsNullOrWhiteSpace(element.FontFamily) ? "Arial" : element.FontFamily);
        font.SetAttributeValue("OriginalFontName", string.IsNullOrWhiteSpace(element.FontFamily) ? "Arial" : element.FontFamily);
        font.SetAttributeValue("Size", element.FontSize.ToString("0.##", CultureInfo.InvariantCulture));
        font.SetAttributeValue("SizeinPoints", element.FontSize.ToString("0.##", CultureInfo.InvariantCulture));
        font.SetAttributeValue("Style", element.Bold ? "Bold" : element.Underline ? "Underline" : "Regular");
        font.SetAttributeValue("Underline", Lower(element.Underline));
        font.SetAttributeValue("Unit", "Point");

        var objectFormat = Child(objectElement, "ObjectFormat") ?? EnsureChild(objectElement, "ObjectFormat");
        objectFormat.SetAttributeValue("EnableCanGrow", Lower(element.CanGrow));
        objectFormat.SetAttributeValue("EnableSuppress", Lower(element.IsSuppressed));
        objectFormat.SetAttributeValue("HorizontalAlignment", string.IsNullOrWhiteSpace(element.HorizontalAlignment) ? "Default" : element.HorizontalAlignment);
        objectFormat.SetAttributeValue("FlexKitFormatString", element.FormatString ?? "");
        objectFormat.SetAttributeValue("FlexKitLockFormat", Lower(element.LockFormat));
        objectFormat.SetAttributeValue("FlexKitLockSizePosition", Lower(element.LockSizePosition));
        objectFormat.SetAttributeValue("FlexKitIndentTwips", element.IndentTwips);

        var border = Child(objectElement, "Border") ?? EnsureChild(objectElement, "Border");
        var background = Child(border, "BackgroundColor") ?? EnsureChild(border, "BackgroundColor");
        SetColorElement(background, element.BackgroundColor, alphaWhenTransparent: 0);

        objectElement.Elements("FlexKitHighlightRule").Remove();
        if (element.HighlightRule.Enabled)
        {
            objectElement.Add(new XElement("FlexKitHighlightRule",
                new XAttribute("Enabled", Lower(element.HighlightRule.Enabled)),
                new XAttribute("FieldName", element.HighlightRule.FieldName ?? ""),
                new XAttribute("Operator", element.HighlightRule.Operator ?? ""),
                new XAttribute("Value", element.HighlightRule.Value ?? ""),
                new XAttribute("FontStyle", element.HighlightRule.FontStyle ?? ""),
                new XAttribute("TextColor", element.HighlightRule.TextColor ?? ""),
                new XAttribute("BackgroundColor", element.HighlightRule.BackgroundColor ?? ""),
                new XAttribute("BorderStyle", element.HighlightRule.BorderStyle ?? "")));
        }
    }

    private static void UpsertDesignerMetadata(XElement root, ReportDesignerDocument document)
    {
        root.Elements(MetadataElementName).Remove();

        var sections = new XElement("Sections",
            document.Sections.Select(section =>
                new XElement("Section",
                    new XAttribute("Id", section.Id),
                    new XAttribute("Name", section.Name),
                    new XAttribute("Kind", section.Kind),
                    new XAttribute("HeightTwips", section.HeightTwips),
                    new XAttribute("Suppressed", Lower(section.IsSuppressed)),
                    section.Elements.Select(element =>
                        new XElement("Object",
                            new XAttribute("Id", element.Id),
                            new XAttribute("Name", element.Name),
                            new XAttribute("Kind", element.Kind),
                            new XAttribute("LeftTwips", element.LeftTwips),
                            new XAttribute("TopTwips", element.TopTwips),
                            new XAttribute("WidthTwips", element.WidthTwips),
                            new XAttribute("HeightTwips", element.HeightTwips),
                            new XAttribute("FormatString", element.FormatString ?? ""),
                            new XAttribute("LockFormat", Lower(element.LockFormat)),
                            new XAttribute("LockSizePosition", Lower(element.LockSizePosition)),
                            new XAttribute("IndentTwips", element.IndentTwips))))));

        var links = new XElement("Links",
            document.Links.Select(link =>
                new XElement("Link",
                    new XAttribute("LeftTable", link.LeftTable ?? ""),
                    new XAttribute("LeftField", link.LeftField ?? ""),
                    new XAttribute("RightTable", link.RightTable ?? ""),
                    new XAttribute("RightField", link.RightField ?? ""),
                    new XAttribute("JoinType", link.JoinType ?? ""))));
        var sql = new XElement("Sql",
            new XAttribute("Query", document.CustomSql ?? ""));

        root.Add(new XElement(MetadataElementName,
            new XAttribute("Version", "1"),
            new XAttribute("SavedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            sections,
            links,
            sql));
    }

    private static XElement CreateAreaElement(ReportDesignerSection section)
    {
        return new XElement("Area",
            new XAttribute("Kind", section.Kind),
            new XAttribute("Name", string.IsNullOrWhiteSpace(section.AreaName) ? $"{section.Kind}Area" : section.AreaName),
            new XElement("AreaFormat",
                new XAttribute("EnableKeepTogether", "false"),
                new XAttribute("EnableNewPageAfter", "false"),
                new XAttribute("EnableNewPageBefore", "false"),
                new XAttribute("EnablePrintAtBottomOfPage", "false"),
                new XAttribute("EnableResetPageNumberAfter", "false"),
                new XAttribute("EnableSuppress", "false"),
                new XAttribute("EnableHideForDrillDown", "false")),
            new XElement("Sections"));
    }

    private static XElement CreateSectionElement(ReportDesignerSection section)
    {
        return new XElement("Section",
            new XAttribute("Height", section.HeightTwips),
            new XAttribute("Kind", section.Kind),
            new XAttribute("Name", section.Name),
            new XElement("SectionFormat",
                new XAttribute("CssClass", ""),
                new XAttribute("EnableKeepTogether", Lower(section.KeepTogether)),
                new XAttribute("EnableHideForDrillDown", Lower(section.HideForDrillDown)),
                new XAttribute("EnableNewPageAfter", Lower(section.NewPageAfter)),
                new XAttribute("EnableNewPageBefore", Lower(section.NewPageBefore)),
                new XAttribute("EnablePrintAtBottomOfPage", Lower(section.PrintAtBottomOfPage)),
                new XAttribute("EnableResetPageNumberAfter", Lower(section.ResetPageNumberAfter)),
                new XAttribute("EnableSuppress", Lower(section.IsSuppressed)),
                new XAttribute("EnableSuppressIfBlank", Lower(section.SuppressIfBlank)),
                new XAttribute("EnableUnderlaySection", Lower(section.UnderlayFollowingSections)),
                new XAttribute("ReadOnly", Lower(section.ReadOnly)),
                new XAttribute("RelativePositions", Lower(section.RelativePositions)),
                new XElement("SectionAreaConditionFormulas"),
                CreateColorElement("BackgroundColor", section.BackgroundColor, 0)),
            new XElement("ReportObjects"));
    }

    private static XElement CreateObjectElement(ReportDesignerElement element)
    {
        var tag = element.Kind switch
        {
            var kind when string.Equals(kind, "Text", StringComparison.OrdinalIgnoreCase) => "TextObject",
            var kind when string.Equals(kind, "FieldHeading", StringComparison.OrdinalIgnoreCase) => "FieldHeadingObject",
            var kind when string.Equals(kind, "Subreport", StringComparison.OrdinalIgnoreCase) => "SubreportObject",
            var kind when string.Equals(kind, "Box", StringComparison.OrdinalIgnoreCase) => "BoxObject",
            var kind when string.Equals(kind, "Line", StringComparison.OrdinalIgnoreCase) => "LineObject",
            _ => "FieldObject"
        };

        var objectElement = new XElement(tag,
            new XAttribute("Name", string.IsNullOrWhiteSpace(element.Name) ? element.Kind : element.Name),
            new XAttribute("Kind", string.IsNullOrWhiteSpace(element.Kind) ? "Field" : element.Kind),
            new XAttribute("Top", element.TopTwips),
            new XAttribute("Left", element.LeftTwips),
            new XAttribute("Width", element.WidthTwips),
            new XAttribute("Height", element.HeightTwips));

        if (tag == "TextObject" || tag == "FieldHeadingObject")
            objectElement.Add(new XElement("Text", element.Text ?? ""));
        else if (tag == "SubreportObject")
            objectElement.SetAttributeValue("SubreportName", element.SubreportName ?? "");
        else
            objectElement.SetAttributeValue("DataSource", element.Binding ?? "");

        objectElement.Add(CreateColorElement("Color", element.TextColor, 255));
        objectElement.Add(new XElement("Font",
            new XAttribute("Bold", Lower(element.Bold)),
            new XAttribute("FontFamily", string.IsNullOrWhiteSpace(element.FontFamily) ? "Arial" : element.FontFamily),
            new XAttribute("Italic", Lower(element.Italic)),
            new XAttribute("Name", string.IsNullOrWhiteSpace(element.FontFamily) ? "Arial" : element.FontFamily),
            new XAttribute("OriginalFontName", string.IsNullOrWhiteSpace(element.FontFamily) ? "Arial" : element.FontFamily),
            new XAttribute("Size", element.FontSize.ToString("0.##", CultureInfo.InvariantCulture)),
            new XAttribute("SizeinPoints", element.FontSize.ToString("0.##", CultureInfo.InvariantCulture)),
            new XAttribute("Style", element.Bold ? "Bold" : element.Underline ? "Underline" : "Regular"),
            new XAttribute("Underline", Lower(element.Underline)),
            new XAttribute("Unit", "Point")));
        objectElement.Add(new XElement("ObjectFormat",
            new XAttribute("EnableCanGrow", Lower(element.CanGrow)),
            new XAttribute("EnableCloseAtPageBreak", "true"),
            new XAttribute("EnableKeepTogether", "true"),
            new XAttribute("EnableSuppress", Lower(element.IsSuppressed)),
            new XAttribute("HorizontalAlignment", string.IsNullOrWhiteSpace(element.HorizontalAlignment) ? "Default" : element.HorizontalAlignment)));
        objectElement.Add(new XElement("ObjectFormatConditionFormulas"));
        return objectElement;
    }

    private static void ParseSections(XElement reportDefinition, ReportDesignerDocument document)
    {
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var area in Child(reportDefinition, "Areas")?.Elements("Area") ?? Enumerable.Empty<XElement>())
        {
            var areaName = Attribute(area, "Name") ?? "";
            foreach (var sectionElement in Child(area, "Sections")?.Elements("Section") ?? Enumerable.Empty<XElement>())
            {
                var sectionName = Attribute(sectionElement, "Name") ?? "Section";
                var sectionId = MakeUniqueId(sectionName, usedIds);
                var format = Child(sectionElement, "SectionFormat");
                var section = new ReportDesignerSection
                {
                    Id = sectionId,
                    SourceKey = sectionId,
                    Name = sectionName,
                    Kind = Attribute(sectionElement, "Kind") ?? Attribute(area, "Kind") ?? "Detail",
                    AreaName = areaName,
                    HeightTwips = ParseInt(Attribute(sectionElement, "Height"), 360),
                    HideForDrillDown = ParseBool(Attribute(format, "EnableHideForDrillDown")),
                    IsSuppressed = ParseBool(Attribute(format, "EnableSuppress")),
                    PrintAtBottomOfPage = ParseBool(Attribute(format, "EnablePrintAtBottomOfPage")),
                    SuppressIfBlank = ParseBool(Attribute(format, "EnableSuppressIfBlank")),
                    UnderlayFollowingSections = ParseBool(Attribute(format, "EnableUnderlaySection")),
                    ReadOnly = ParseBool(Attribute(format, "ReadOnly")),
                    RelativePositions = ParseBool(Attribute(format, "RelativePositions")),
                    NewPageBefore = ParseBool(Attribute(format, "EnableNewPageBefore")),
                    NewPageAfter = ParseBool(Attribute(format, "EnableNewPageAfter")),
                    ResetPageNumberAfter = ParseBool(Attribute(format, "EnableResetPageNumberAfter")),
                    KeepTogether = ParseBool(Attribute(format, "EnableKeepTogether"), true),
                    BackgroundColor = ColorFromElement(Child(format, "BackgroundColor"), "#ffffff")
                };

                document.SourceSections[section.SourceKey] = sectionElement;
                ParseObjects(sectionElement, section, document);
                document.Sections.Add(section);
            }
        }
    }

    private static void ParseObjects(XElement sectionElement, ReportDesignerSection section, ReportDesignerDocument document)
    {
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var objectElement in Child(sectionElement, "ReportObjects")?.Elements() ?? Enumerable.Empty<XElement>())
        {
            if (!objectElement.Name.LocalName.EndsWith("Object", StringComparison.OrdinalIgnoreCase))
                continue;

            var objectName = Attribute(objectElement, "Name") ?? objectElement.Name.LocalName;
            var id = MakeUniqueId($"{section.Id}-{objectName}", usedIds);
            var kind = InferKindFromObjectElement(objectElement);
            var objectFormat = Child(objectElement, "ObjectFormat");
            var font = Child(objectElement, "Font");
            var element = new ReportDesignerElement
            {
                Id = id,
                SourceKey = id,
                SectionId = section.Id,
                Name = objectName,
                Kind = kind,
                Text = Child(objectElement, "Text")?.Value ?? "",
                Binding = Attribute(objectElement, "DataSource") ?? Attribute(objectElement, "FieldObjectName") ?? "",
                SubreportName = Attribute(objectElement, "SubreportName") ?? "",
                SubreportXmlPath = Attribute(objectElement, "SubreportXmlPath") ?? "",
                LeftTwips = ParseInt(Attribute(objectElement, "Left")),
                TopTwips = ParseInt(Attribute(objectElement, "Top")),
                WidthTwips = ParseInt(Attribute(objectElement, "Width"), 1440),
                HeightTwips = ParseInt(Attribute(objectElement, "Height"), 240),
                FontFamily = Attribute(font, "FontFamily") ?? Attribute(font, "Name") ?? "Arial",
                FontSize = ParseDecimal(Attribute(font, "SizeinPoints") ?? Attribute(font, "Size"), 10m),
                Bold = ParseBool(Attribute(font, "Bold")),
                Italic = ParseBool(Attribute(font, "Italic")),
                Underline = ParseBool(Attribute(font, "Underline")),
                TextColor = ColorFromElement(Child(objectElement, "Color"), "#000000"),
                BackgroundColor = ColorFromElement(Child(Child(objectElement, "Border"), "BackgroundColor"), "transparent"),
                HorizontalAlignment = Attribute(objectFormat, "HorizontalAlignment") ?? "Default",
                IsSuppressed = ParseBool(Attribute(objectFormat, "EnableSuppress")),
                CanGrow = ParseBool(Attribute(objectFormat, "EnableCanGrow")),
                FormatString = Attribute(objectFormat, "FlexKitFormatString") ?? "",
                LockFormat = ParseBool(Attribute(objectFormat, "FlexKitLockFormat")),
                LockSizePosition = ParseBool(Attribute(objectFormat, "FlexKitLockSizePosition")),
                IndentTwips = ParseInt(Attribute(objectFormat, "FlexKitIndentTwips")),
                HighlightRule = ParseHighlightRule(Child(objectElement, "FlexKitHighlightRule"))
            };

            if (string.Equals(kind, "Subreport", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(element.Text))
                element.Text = string.IsNullOrWhiteSpace(element.SubreportName) ? "Subreport" : element.SubreportName;

            document.SourceObjects[element.SourceKey] = objectElement;
            section.Elements.Add(element);
        }
    }

    private static void ParseFields(XElement root, ReportDesignerDocument document)
    {
        foreach (var table in Child(Child(root, "Database"), "Tables")?.Elements("Table") ?? Enumerable.Empty<XElement>())
        {
            var tableName = Attribute(table, "Alias") ?? Attribute(table, "Name") ?? "";
            var dataTable = new ReportDesignerDataTable
            {
                Name = tableName,
                Schema = ParseSchemaName(Attribute(table, "QualifiedName"))
            };

            foreach (var field in Child(table, "Fields")?.Elements("Field") ?? Enumerable.Empty<XElement>())
            {
                var designerField = new ReportDesignerField
                {
                    Name = Attribute(field, "Name") ?? Attribute(field, "ShortName") ?? "",
                    Table = tableName,
                    LongName = Attribute(field, "LongName") ?? "",
                    Formula = Attribute(field, "FormulaForm") ?? Attribute(field, "FormulaName") ?? "",
                    Type = Attribute(field, "Type") ?? Attribute(field, "ValueType") ?? ""
                };
                document.Fields.Add(designerField);
                dataTable.Fields.Add(CloneField(designerField));
            }

            if (!string.IsNullOrWhiteSpace(dataTable.Name))
                document.DataSources.Add(dataTable);
        }

        foreach (var formula in Child(Child(root, "DataDefinition"), "FormulaFieldDefinitions")?.Elements("FormulaFieldDefinition") ?? Enumerable.Empty<XElement>())
        {
            var name = Attribute(formula, "Name") ?? Attribute(formula, "FormulaName") ?? "Formula";
            document.Fields.Add(new ReportDesignerField
            {
                Name = name.TrimStart('@'),
                Formula = Attribute(formula, "FormulaName") ?? $"{{@{name.TrimStart('@')}}}",
                Type = Attribute(formula, "ValueType") ?? "Formula",
                IsFormula = true
            });
        }
    }

    private static void ParseLinks(XElement root, ReportDesignerDocument document)
    {
        foreach (var tableLink in Child(Child(root, "Database"), "TableLinks")?.Elements("TableLink") ?? Enumerable.Empty<XElement>())
        {
            var sourceFields = ParseLinkFields(Child(tableLink, "SourceFields"));
            var destinationFields = ParseLinkFields(Child(tableLink, "DestinationFields"));
            var count = Math.Min(sourceFields.Count, destinationFields.Count);
            for (var index = 0; index < count; index++)
            {
                var source = sourceFields[index];
                var destination = destinationFields[index];
                document.Links.Add(new ReportDesignerDataLink
                {
                    LeftTable = source.Table,
                    LeftField = source.Field,
                    RightTable = destination.Table,
                    RightField = destination.Field,
                    JoinType = FromCrystalJoinType(Attribute(tableLink, "JoinType"))
                });
            }
        }

        foreach (var link in Child(root, MetadataElementName)?.Element("Links")?.Elements("Link") ?? Enumerable.Empty<XElement>())
        {
            var designerLink = new ReportDesignerDataLink
            {
                LeftTable = Attribute(link, "LeftTable") ?? "",
                LeftField = Attribute(link, "LeftField") ?? "",
                RightTable = Attribute(link, "RightTable") ?? "",
                RightField = Attribute(link, "RightField") ?? "",
                JoinType = Attribute(link, "JoinType") ?? "Inner"
            };

            if (!document.Links.Any(existing => SameLink(existing, designerLink)))
                document.Links.Add(designerLink);
        }
    }

    private static List<(string Table, string Field)> ParseLinkFields(XElement? container)
    {
        var fields = new List<(string Table, string Field)>();
        foreach (var fieldElement in container?.Elements("Field") ?? Enumerable.Empty<XElement>())
        {
            var formulaName = Attribute(fieldElement, "FormulaName") ?? "";
            var parsed = ParseCrystalFieldReference(formulaName);
            if (parsed.HasValue)
                fields.Add(parsed.Value);
        }

        return fields;
    }

    private static void ParseCustomSql(XElement root, ReportDesignerDocument document)
    {
        var metadata = Child(root, MetadataElementName);
        document.CustomSql = Attribute(metadata?.Element("Sql"), "Query") ?? "";
    }

    private static (string Table, string Field)? ParseCrystalFieldReference(string formulaName)
    {
        if (string.IsNullOrWhiteSpace(formulaName))
            return null;

        var match = Regex.Match(formulaName.Trim(), @"^\{([^.}]+)\.([^}]+)\}$");
        if (!match.Success)
            return null;

        return (match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
    }

    private static bool SameLink(ReportDesignerDataLink left, ReportDesignerDataLink right)
    {
        return string.Equals(left.LeftTable, right.LeftTable, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.LeftField, right.LeftField, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.RightTable, right.RightTable, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.RightField, right.RightField, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.JoinType, right.JoinType, StringComparison.OrdinalIgnoreCase);
    }

    private static string ParseSchemaName(string? qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
            return "";

        var parts = qualifiedName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 1 ? parts[^2] : "";
    }

    private static void ParseParameters(XElement root, ReportDesignerDocument document)
    {
        foreach (var parameter in Child(Child(root, "DataDefinition"), "ParameterFieldDefinitions")?.Elements("ParameterFieldDefinition") ?? Enumerable.Empty<XElement>())
        {
            document.Parameters.Add(new ReportDesignerParameter
            {
                Name = Attribute(parameter, "Name") ?? "",
                Prompt = Attribute(parameter, "PromptText") ?? Attribute(parameter, "Prompt") ?? "",
                Type = Attribute(parameter, "ParameterValueKind") ?? Attribute(parameter, "ValueType") ?? "",
                Required = !ParseBool(Attribute(parameter, "OptionalPrompt")),
                AllowMultiple = ParseBool(Attribute(parameter, "EnableAllowMultipleValue")) || ParseBool(Attribute(parameter, "AllowMultipleValue"))
            });
        }
    }

    private static void ParseGroups(XElement root, ReportDesignerDocument document)
    {
        foreach (var group in Child(Child(root, "DataDefinition"), "Groups")?.Elements("Group") ?? Enumerable.Empty<XElement>())
        {
            var condition = Attribute(group, "ConditionField") ?? Attribute(group, "GroupCondition") ?? Attribute(group, "FormulaName") ?? "";
            if (string.IsNullOrWhiteSpace(condition))
                condition = group.Descendants("Field").Select(field => Attribute(field, "FormulaName")).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

            document.Groups.Add(new ReportDesignerGroup
            {
                Name = Attribute(group, "Name") ?? condition,
                Condition = condition,
                SortDirection = Attribute(group, "SortDirection") ?? Attribute(group, "ConditionSortDirection") ?? "Ascending"
            });
        }
    }

    private static void ParseSorts(XElement root, ReportDesignerDocument document)
    {
        foreach (var sort in Child(Child(root, "DataDefinition"), "SortFields")?.Elements("SortField") ?? Enumerable.Empty<XElement>())
        {
            var fieldName = Attribute(sort, "Field") ?? Attribute(sort, "DataSource") ?? Attribute(sort, "FormulaName") ?? "";
            var field = ResolveDesignerField(document, fieldName);
            if (string.IsNullOrWhiteSpace(field.DisplayName))
                continue;

            document.Sorts.Add(new ReportDesignerSort
            {
                Field = CloneField(field),
                Direction = Attribute(sort, "Direction") ?? Attribute(sort, "SortDirection") ?? "Ascending"
            });
        }
    }

    private static void ParseSummaries(XElement root, ReportDesignerDocument document)
    {
        foreach (var summary in Child(Child(root, "DataDefinition"), "SummaryFieldDefinitions")?.Elements("SummaryFieldDefinition") ?? Enumerable.Empty<XElement>())
        {
            var fieldName = Attribute(summary, "Field") ?? Attribute(summary, "FieldName") ?? Attribute(summary, "DataSource") ?? "";
            var field = ResolveDesignerField(document, fieldName);
            if (string.IsNullOrWhiteSpace(field.DisplayName))
                continue;

            document.Summaries.Add(new ReportDesignerSummary
            {
                Field = CloneField(field),
                Operation = Attribute(summary, "Operation") ?? Attribute(summary, "SummaryOperation") ?? "Sum",
                GroupName = Attribute(summary, "GroupName") ?? Attribute(summary, "Group") ?? ""
            });
        }
    }

    private static void ParseFilters(XElement root, ReportDesignerDocument document)
    {
        foreach (var filter in Child(Child(root, "DataDefinition"), "RecordSelection")?.Elements("Filter") ?? Enumerable.Empty<XElement>())
        {
            var fieldName = Attribute(filter, "Field") ?? "";
            if (string.IsNullOrWhiteSpace(fieldName))
                continue;

            var field = ResolveDesignerField(document, fieldName);

            document.Filters.Add(new ReportDesignerFilter
            {
                Field = CloneField(field),
                Operator = Attribute(filter, "Operator") ?? "is equal to",
                Value = Attribute(filter, "Value") ?? ""
            });
        }
    }

    private static ReportDesignerField ResolveDesignerField(ReportDesignerDocument document, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
            return new ReportDesignerField();

        var cleaned = fieldName.Trim().Trim('{', '}');
        if (cleaned.StartsWith("@", StringComparison.Ordinal))
            cleaned = cleaned[1..];

        return document.Fields.FirstOrDefault(candidate =>
                   string.Equals(candidate.DisplayName, cleaned, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(candidate.Name, cleaned, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(candidate.Formula.Trim('{', '}').TrimStart('@'), cleaned, StringComparison.OrdinalIgnoreCase))
               ?? new ReportDesignerField { Name = cleaned };
    }

    private static void ParseSubreports(XElement root, ReportDesignerDocument document)
    {
        foreach (var reference in Child(root, "SubreportReferences")?.Elements("SubreportReference") ?? Enumerable.Empty<XElement>())
        {
            document.Subreports.Add(new ReportDesignerSubreport
            {
                Name = Attribute(reference, "Name") ?? "",
                FileName = Attribute(reference, "FileName") ?? ""
            });
        }
    }

    private static ReportDesignerPage ParsePage(XElement root)
    {
        var printOptions = Child(root, "PrintOptions");
        var margins = Child(printOptions, "PageMargins");
        return new ReportDesignerPage
        {
            Orientation = Attribute(printOptions, "PaperOrientation") ?? "Portrait",
            PaperSize = Attribute(printOptions, "PaperSize") ?? "PaperLetter",
            ContentWidthTwips = ParseInt(Attribute(printOptions, "PageContentWidth"), 11520),
            ContentHeightTwips = ParseInt(Attribute(printOptions, "PageContentHeight"), 15120),
            MarginLeftTwips = ParseInt(Attribute(margins, "leftMargin"), 720),
            MarginTopTwips = ParseInt(Attribute(margins, "topMargin"), 720),
            MarginRightTwips = ParseInt(Attribute(margins, "rightMargin"), 720),
            MarginBottomTwips = ParseInt(Attribute(margins, "bottomMargin"), 720)
        };
    }

    private static string GetReportTitle(XElement root, string? sourceName)
    {
        var summaryTitle = Attribute(Child(root, "Summaryinfo"), "ReportTitle");
        if (!string.IsNullOrWhiteSpace(summaryTitle))
            return summaryTitle.Trim();

        var firstTitleText = Child(root, "ReportDefinition")?
            .Descendants("TextObject")
            .Select(text => Child(text, "Text")?.Value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(firstTitleText))
            return firstTitleText!;

        return Path.GetFileNameWithoutExtension(sourceName) ?? Attribute(root, "Name") ?? "Untitled Report";
    }

    private static string InferKindFromObjectElement(XElement objectElement)
    {
        var kind = Attribute(objectElement, "Kind");
        if (!string.IsNullOrWhiteSpace(kind))
            return kind!;

        return objectElement.Name.LocalName switch
        {
            "TextObject" => "Text",
            "FieldHeadingObject" => "FieldHeading",
            "SubreportObject" => "Subreport",
            "BoxObject" => "Box",
            "LineObject" => "Line",
            _ => "Field"
        };
    }

    private static bool IsReportObjectElement(XElement element)
    {
        return element.Name.LocalName.EndsWith("Object", StringComparison.OrdinalIgnoreCase);
    }

    private static XElement? Child(XContainer? parent, string name)
    {
        return parent?.Elements().FirstOrDefault(element => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static XElement EnsureChild(XElement parent, string name)
    {
        var child = Child(parent, name);
        if (child != null)
            return child;

        child = new XElement(name);
        parent.Add(child);
        return child;
    }

    private static string? Attribute(XElement? element, string name)
    {
        return element?.Attributes().FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    private static int ParseInt(string? value, int fallback = 0)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static decimal ParseDecimal(string? value, decimal fallback = 0m)
    {
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static bool ParseBool(string? value, bool fallback = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static ReportDesignerHighlightRule ParseHighlightRule(XElement? element)
    {
        if (element is null)
            return new ReportDesignerHighlightRule();

        return new ReportDesignerHighlightRule
        {
            Enabled = ParseBool(Attribute(element, "Enabled")),
            FieldName = Attribute(element, "FieldName") ?? "",
            Operator = Attribute(element, "Operator") ?? "is equal to",
            Value = Attribute(element, "Value") ?? "",
            FontStyle = Attribute(element, "FontStyle") ?? "Default",
            TextColor = Attribute(element, "TextColor") ?? "#000000",
            BackgroundColor = Attribute(element, "BackgroundColor") ?? "transparent",
            BorderStyle = Attribute(element, "BorderStyle") ?? "Default border style"
        };
    }

    private static string ToCrystalJoinType(string? joinType)
    {
        return joinType?.Trim().ToLowerInvariant() switch
        {
            "left outer" or "leftouter" => "LeftOuter",
            "right outer" or "rightouter" => "RightOuter",
            "full outer" or "fullouter" => "FullOuter",
            _ => "Inner"
        };
    }

    private static string FromCrystalJoinType(string? joinType)
    {
        return joinType?.Trim().ToLowerInvariant() switch
        {
            "leftouter" or "left outer" => "Left Outer",
            "rightouter" or "right outer" => "Right Outer",
            "fullouter" or "full outer" => "Full Outer",
            _ => "Inner"
        };
    }

    private static string MakeUniqueId(string stem, ISet<string> used)
    {
        var clean = Regex.Replace(stem, "[^A-Za-z0-9_-]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(clean))
            clean = "item";

        var id = clean;
        var suffix = 2;
        while (!used.Add(id))
            id = $"{clean}-{suffix++}";

        return id;
    }

    private static string ColorFromElement(XElement? color, string fallback)
    {
        if (color == null)
            return fallback;

        var alpha = ParseInt(Attribute(color, "A"), 255);
        if (alpha == 0 && string.Equals(color.Name.LocalName, "BackgroundColor", StringComparison.OrdinalIgnoreCase))
            return "transparent";

        var r = ParseInt(Attribute(color, "R"), -1);
        var g = ParseInt(Attribute(color, "G"), -1);
        var b = ParseInt(Attribute(color, "B"), -1);
        if (r >= 0 && g >= 0 && b >= 0)
            return $"#{Math.Clamp(r, 0, 255):X2}{Math.Clamp(g, 0, 255):X2}{Math.Clamp(b, 0, 255):X2}".ToLowerInvariant();

        var name = Attribute(color, "Name");
        return string.Equals(name, "Black", StringComparison.OrdinalIgnoreCase) ? "#000000" : fallback;
    }

    private static XElement CreateColorElement(string elementName, string color, int alphaWhenTransparent)
    {
        var element = new XElement(elementName);
        SetColorElement(element, color, alphaWhenTransparent);
        return element;
    }

    private static void SetColorElement(XElement element, string color, int alphaWhenTransparent)
    {
        var (r, g, b, alpha) = ParseCssColor(color, alphaWhenTransparent);
        element.SetAttributeValue("Name", alpha == 0 ? "ffffffff" : $"ff{r:X2}{g:X2}{b:X2}".ToLowerInvariant());
        element.SetAttributeValue("A", alpha);
        element.SetAttributeValue("R", r);
        element.SetAttributeValue("G", g);
        element.SetAttributeValue("B", b);
    }

    private static (int R, int G, int B, int A) ParseCssColor(string? color, int alphaWhenTransparent)
    {
        if (string.IsNullOrWhiteSpace(color) || string.Equals(color, "transparent", StringComparison.OrdinalIgnoreCase))
            return (255, 255, 255, alphaWhenTransparent == 0 ? 0 : alphaWhenTransparent);

        var hex = color.Trim().TrimStart('#');
        if (hex.Length == 3)
            hex = string.Concat(hex.Select(ch => new string(ch, 2)));

        if (hex.Length == 6 && int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r) &&
            int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g) &&
            int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return (r, g, b, 255);

        return (0, 0, 0, 255);
    }

    private static string Lower(bool value) => value ? "true" : "false";
}
