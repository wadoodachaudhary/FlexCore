using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

/// <summary>
/// In-memory design model for a Crystal Reports XML layout. The model keeps the
/// Crystal band/object structure but is intentionally UI-friendly for Blazor.
/// </summary>
public sealed class ReportDesignerDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Untitled Report";
    public string SourceName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public ReportDesignerPage Page { get; set; } = new();
    public List<ReportDesignerSection> Sections { get; set; } = new();
    public List<ReportDesignerField> Fields { get; set; } = new();
    public List<ReportDesignerParameter> Parameters { get; set; } = new();
    public List<ReportDesignerGroup> Groups { get; set; } = new();
    public List<ReportDesignerSubreport> Subreports { get; set; } = new();
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
    public bool IsSuppressed { get; set; }
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
            CanGrow = CanGrow
        };
    }
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

/// <summary>
/// Parser and writer for the Crystal XML subset used by the FlexKit report designer.
/// </summary>
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
        ParseParameters(root, document);
        ParseGroups(root, document);
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

    private static void ApplyDesignerToSource(ReportDesignerDocument document, XDocument sourceDocument)
    {
        var root = sourceDocument.Root ?? throw new InvalidDataException("Invalid report XML: missing root element.");

        root.SetAttributeValue("Name", string.IsNullOrWhiteSpace(document.Title) ? document.SourceName : document.Title);
        ApplyPrintOptions(root, document.Page);

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

    private static void ApplySection(ReportDesignerSection section, XElement sectionElement)
    {
        sectionElement.SetAttributeValue("Name", section.Name);
        sectionElement.SetAttributeValue("Kind", section.Kind);
        sectionElement.SetAttributeValue("Height", section.HeightTwips);

        var format = Child(sectionElement, "SectionFormat") ?? EnsureChild(sectionElement, "SectionFormat");
        format.SetAttributeValue("EnableSuppress", Lower(section.IsSuppressed));
        format.SetAttributeValue("EnableKeepTogether", Lower(section.KeepTogether));

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
    }

    private static void UpsertDesignerMetadata(XElement root, ReportDesignerDocument document)
    {
        root.Elements(MetadataElementName).Remove();
        root.Add(new XElement(MetadataElementName,
            new XAttribute("Version", "1"),
            new XAttribute("SavedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            new XElement("Sections",
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
                                new XAttribute("HeightTwips", element.HeightTwips))))))));
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
                new XAttribute("EnableNewPageAfter", "false"),
                new XAttribute("EnableNewPageBefore", "false"),
                new XAttribute("EnablePrintAtBottomOfPage", "false"),
                new XAttribute("EnableResetPageNumberAfter", "false"),
                new XAttribute("EnableSuppress", Lower(section.IsSuppressed)),
                new XAttribute("EnableSuppressIfBlank", "false"),
                new XAttribute("EnableUnderlaySection", "false"),
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
                    IsSuppressed = ParseBool(Attribute(format, "EnableSuppress")),
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
                CanGrow = ParseBool(Attribute(objectFormat, "EnableCanGrow"))
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
            foreach (var field in Child(table, "Fields")?.Elements("Field") ?? Enumerable.Empty<XElement>())
            {
                document.Fields.Add(new ReportDesignerField
                {
                    Name = Attribute(field, "Name") ?? Attribute(field, "ShortName") ?? "",
                    Table = tableName,
                    LongName = Attribute(field, "LongName") ?? "",
                    Formula = Attribute(field, "FormulaForm") ?? Attribute(field, "FormulaName") ?? "",
                    Type = Attribute(field, "Type") ?? Attribute(field, "ValueType") ?? ""
                });
            }
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
