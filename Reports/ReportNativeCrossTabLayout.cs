using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

/// <summary>Authored cell dimensions for a native single-axis cross-tab. The outer object is a design-time template, not its expanded width.</summary>
public sealed record ReportNativeCrossTabLayout(int RowWidth, int ValueWidth, int CellHeight, int PaddingX, int PaddingY,
    string FontFamily, decimal FontSize, bool ValueBold)
{
    public ReportNativeCrossTabAppearance? Appearance { get; init; }
    public ReportNativeCrossTabCellStyle? RowStyle { get; init; }
    public ReportNativeCrossTabCellStyle? ColumnStyle { get; init; }
    public ReportNativeCrossTabCellStyle? ValueStyle { get; init; }
    public ReportNativeCrossTabCellStyle? RowTotalStyle { get; init; }
    public ReportNativeCrossTabCellStyle? ColumnTotalStyle { get; init; }
    public ReportNativeCrossTabCellStyle? GrandTotalStyle { get; init; }
    public ReportNativeCrossTabCellStyle? RowTotalLabelStyle { get; init; }
    public ReportNativeCrossTabCellStyle? ColumnTotalLabelStyle { get; init; }
    public int ParentHeaderHeight { get; init; }
    public int TotalWidth { get; init; }
    public ReportNativeCrossTabCellStyle? ParentHeaderStyle { get; init; }
    public string ColumnSumFormat { get; init; } = "";
    public string TotalSumFormat { get; init; } = "";
    public string StackFormats { get; init; } = "";
    public bool RowLabelsOnly { get; init; }
    public int HeaderHeight { get; init; }
    public bool SuppressColumnGrandTotals { get; init; }
    public bool SuppressRowGrandTotals { get; init; }
    public IReadOnlyList<ReportNativeCrossTabTemplate> Templates { get; init; } = [];
    public bool ColumnTotalsBefore { get; init; }
    public bool RowTotalsBefore { get; init; }
    public IReadOnlyList<int> SuppressedRowSubtotals { get; init; } = [];
    public IReadOnlyList<int> SuppressedColumnSubtotals { get; init; } = [];
    public int LeftInset => Appearance is { } a ? (int)Math.Ceiling(Math.Max(0, a.Stroke(4).Width) * 20) : 0;
    public ReportNativeCrossTabCellStyle? StyleFor(bool header, bool rowHeader, bool totalRow, bool totalColumn) => header
        ? totalColumn ? ColumnTotalLabelStyle ?? ColumnStyle : ColumnStyle
        : rowHeader ? totalRow ? RowTotalLabelStyle ?? RowStyle : RowStyle : totalRow && totalColumn ? GrandTotalStyle ?? ValueStyle
        : totalRow ? ColumnTotalStyle ?? ValueStyle : totalColumn ? RowTotalStyle ?? ValueStyle : ValueStyle;

    internal XElement ToXml() => new("NativeCrossTab", new XAttribute("RowWidth", RowWidth), new XAttribute("ValueWidth", ValueWidth),
        new XAttribute("CellHeight", CellHeight), new XAttribute("PaddingX", PaddingX), new XAttribute("PaddingY", PaddingY),
        new XAttribute("FontFamily", FontFamily), new XAttribute("FontSize", FontSize), new XAttribute("ValueBold", ValueBold),
        RowLabelsOnly ? new XAttribute("RowLabelsOnly", true) : null, new XAttribute("HeaderHeight", HeaderHeight), new XAttribute("ParentHeaderHeight", ParentHeaderHeight),
        ColumnSumFormat.Length == 0 ? null : new XAttribute("ColumnSumFormat", ColumnSumFormat),
        TotalSumFormat.Length == 0 ? null : new XAttribute("TotalSumFormat", TotalSumFormat),
        new XAttribute("TotalWidth", TotalWidth), new XAttribute("StackFormats", StackFormats),
        new XAttribute("SuppressColumnGrandTotals", SuppressColumnGrandTotals), new XAttribute("SuppressRowGrandTotals", SuppressRowGrandTotals),
        new XAttribute("ColumnTotalsBefore", ColumnTotalsBefore), new XAttribute("RowTotalsBefore", RowTotalsBefore),
        new XAttribute("SuppressedRowSubtotals", string.Join(",", SuppressedRowSubtotals)),
        new XAttribute("SuppressedColumnSubtotals", string.Join(",", SuppressedColumnSubtotals)),
        ParentHeaderStyle?.ToXml("ParentHeaderStyle"), Appearance?.ToXml(), RowStyle?.ToXml("RowStyle"), ColumnStyle?.ToXml("ColumnStyle"), ValueStyle?.ToXml("ValueStyle"),
        RowTotalStyle?.ToXml("RowTotalStyle"), ColumnTotalStyle?.ToXml("ColumnTotalStyle"), GrandTotalStyle?.ToXml("GrandTotalStyle"),
        RowTotalLabelStyle?.ToXml("RowTotalLabelStyle"), ColumnTotalLabelStyle?.ToXml("ColumnTotalLabelStyle"),
        Templates.Count == 0 ? null : new XElement("Templates", Templates.Select(template => template.ToXml())));

    internal static ReportNativeCrossTabLayout? Read(XElement? xml)
    {
        if (xml is null) return null;
        var result = new ReportNativeCrossTabLayout((int?)xml.Attribute("RowWidth") ?? 0, (int?)xml.Attribute("ValueWidth") ?? 0,
            (int?)xml.Attribute("CellHeight") ?? 0, (int?)xml.Attribute("PaddingX") ?? 0, (int?)xml.Attribute("PaddingY") ?? 0,
            (string?)xml.Attribute("FontFamily") ?? "", (decimal?)xml.Attribute("FontSize") ?? 0, (bool?)xml.Attribute("ValueBold") ?? false)
        {
            Appearance = ReportNativeCrossTabAppearance.Read(xml.Element("Appearance")),
            RowStyle = ReportNativeCrossTabCellStyle.Read(xml.Element("RowStyle")), ColumnStyle = ReportNativeCrossTabCellStyle.Read(xml.Element("ColumnStyle")),
            ValueStyle = ReportNativeCrossTabCellStyle.Read(xml.Element("ValueStyle")), RowTotalStyle = ReportNativeCrossTabCellStyle.Read(xml.Element("RowTotalStyle")),
            ColumnTotalStyle = ReportNativeCrossTabCellStyle.Read(xml.Element("ColumnTotalStyle")), GrandTotalStyle = ReportNativeCrossTabCellStyle.Read(xml.Element("GrandTotalStyle")),
            RowTotalLabelStyle = ReportNativeCrossTabCellStyle.Read(xml.Element("RowTotalLabelStyle")), ColumnTotalLabelStyle = ReportNativeCrossTabCellStyle.Read(xml.Element("ColumnTotalLabelStyle")),
            ParentHeaderHeight = (int?)xml.Attribute("ParentHeaderHeight") ?? 0, TotalWidth = (int?)xml.Attribute("TotalWidth") ?? 0,
            ColumnSumFormat = (string?)xml.Attribute("ColumnSumFormat") ?? "", TotalSumFormat = (string?)xml.Attribute("TotalSumFormat") ?? "",
            StackFormats = (string?)xml.Attribute("StackFormats") ?? "", ParentHeaderStyle = ReportNativeCrossTabCellStyle.Read(xml.Element("ParentHeaderStyle")),
            RowLabelsOnly = (bool?)xml.Attribute("RowLabelsOnly") ?? false, HeaderHeight = (int?)xml.Attribute("HeaderHeight") ?? 0,
            SuppressColumnGrandTotals = (bool?)xml.Attribute("SuppressColumnGrandTotals") ?? false,
            SuppressRowGrandTotals = (bool?)xml.Attribute("SuppressRowGrandTotals") ?? false,
            Templates = xml.Element("Templates")?.Elements("Cell").Select(ReportNativeCrossTabTemplate.Read).ToArray() ?? [],
            ColumnTotalsBefore = (bool?)xml.Attribute("ColumnTotalsBefore") ?? false,
            RowTotalsBefore = (bool?)xml.Attribute("RowTotalsBefore") ?? false,
            SuppressedRowSubtotals = Levels((string?)xml.Attribute("SuppressedRowSubtotals")),
            SuppressedColumnSubtotals = Levels((string?)xml.Attribute("SuppressedColumnSubtotals"))
        };
        if (result.RowWidth is <= 0 or > 100000 || result.ValueWidth is <= 0 or > 100000 || result.CellHeight is <= 0 or > 100000
            || result.ParentHeaderHeight is < 0 or > 100000 || result.TotalWidth is < 0 or > 100000
            || result.ParentHeaderHeight > 0 && result.TotalWidth == 0
            || result.PaddingX is < 0 or > 10000 || result.PaddingY is < 0 or > 10000 || result.HeaderHeight is < 0 or > 100000 || result.FontSize is <= 0 or > 200)
            throw new InvalidDataException("Invalid native cross-tab cell dimensions.");
        return result;
        static int[] Levels(string? text) => string.IsNullOrEmpty(text) ? [] : text.Split(',').Select(part => int.TryParse(part, out var level) && level is > 0 and <= 6
            ? level : throw new InvalidDataException("Invalid cross-tab subtotal level.")).ToArray();
    }
}

public sealed record ReportNativeCrossTabCellStyle(string FontFamily, decimal FontSize, bool Bold, bool Italic, bool Underline,
    string Color, string Background, string Alignment)
{
    public IReadOnlyList<ReportCrossTabCondition> Conditions { get; init; } = [];
    public bool Suppressed { get; init; }
    public int ClipWidthTwips { get; init; }
    internal XElement ToXml(string name) => new(name, new XAttribute("Font", FontFamily), new XAttribute("Size", FontSize),
        new XAttribute("Bold", Bold), new XAttribute("Italic", Italic), new XAttribute("Underline", Underline),
        new XAttribute("Color", Color), new XAttribute("Background", Background), new XAttribute("Alignment", Alignment),
        new XAttribute("Suppressed", Suppressed), new XAttribute("ClipWidthTwips", ClipWidthTwips), Conditions.Select(condition => condition.ToXml()));
    internal static ReportNativeCrossTabCellStyle? Read(XElement? xml) => xml is null ? null : new((string?)xml.Attribute("Font") ?? "Arial",
        (decimal?)xml.Attribute("Size") ?? 10, (bool?)xml.Attribute("Bold") ?? false, (bool?)xml.Attribute("Italic") ?? false,
        (bool?)xml.Attribute("Underline") ?? false, ReportValueFormatting.CssColor((string?)xml.Attribute("Color")),
        ReportValueFormatting.CssColor((string?)xml.Attribute("Background")), (string?)xml.Attribute("Alignment") is "center" ? "center" : (string?)xml.Attribute("Alignment") is "right" ? "right" : "left")
        { Suppressed = (bool?)xml.Attribute("Suppressed") ?? false, ClipWidthTwips = (int?)xml.Attribute("ClipWidthTwips") ?? 0,
            Conditions = xml.Elements("Condition").Select(ReportCrossTabCondition.Read).ToArray() };
    internal string Css => ReportObjectRenderer.FontStyle(FontFamily, FontSize, Bold, Italic, Underline, Color)
        + $"background:{ReportValueFormatting.CssColor(Background)};text-align:{Alignment};";
}

public sealed record ReportCrossTabCondition(string Property, ReportGridFormula Formula)
{
    internal XElement ToXml() => new("Condition", new XAttribute("Property", Property), Formula.ToXml("Formula"));
    internal static ReportCrossTabCondition Read(XElement xml) => new((string?)xml.Attribute("Property") ?? "", ReportGridFormula.Read(xml.Element("Formula")));
}

/// <summary>A saved header or summary template, including unequal total widths and each hierarchy level.</summary>
public sealed record ReportNativeCrossTabTemplate(int Axis, int ColumnLevel, int RowLevel, int Summary,
    int Left, int Top, int Width, int Height, int PaddingX, int PaddingY, string Alias,
    ReportNativeCrossTabCellStyle Style, ReportNativeNumericFormat? NumericFormat = null, ReportNativeDateTimeFormat? DateTimeFormat = null)
{
    public int CellWidth { get; init; }
    public int CellHeight { get; init; }
    internal XElement ToXml() => new("Cell", new XAttribute("Axis", Axis), new XAttribute("ColumnLevel", ColumnLevel),
        new XAttribute("RowLevel", RowLevel), new XAttribute("Summary", Summary), new XAttribute("Left", Left),
        new XAttribute("Top", Top), new XAttribute("Width", Width), new XAttribute("Height", Height),
        new XAttribute("PaddingX", PaddingX), new XAttribute("PaddingY", PaddingY), new XAttribute("Alias", Alias),
        new XAttribute("CellWidth", CellWidth), new XAttribute("CellHeight", CellHeight),
        Style.ToXml("Style"), NumericFormat?.ToXml(), DateTimeFormat?.ToXml());
    internal static ReportNativeCrossTabTemplate Read(XElement xml) => new((int?)xml.Attribute("Axis") ?? 0,
        (int?)xml.Attribute("ColumnLevel") ?? 0, (int?)xml.Attribute("RowLevel") ?? 0, (int?)xml.Attribute("Summary") ?? 0,
        (int?)xml.Attribute("Left") ?? 0, (int?)xml.Attribute("Top") ?? 0, (int?)xml.Attribute("Width") ?? 0,
        (int?)xml.Attribute("Height") ?? 0, (int?)xml.Attribute("PaddingX") ?? 0, (int?)xml.Attribute("PaddingY") ?? 0,
        (string?)xml.Attribute("Alias") ?? "", ReportNativeCrossTabCellStyle.Read(xml.Element("Style"))
            ?? throw new InvalidDataException("A cross-tab template has no style."), ReportNativeNumericFormat.Read(xml), ReportNativeDateTimeFormat.Read(xml))
        { CellWidth = (int?)xml.Attribute("CellWidth") ?? 0, CellHeight = (int?)xml.Attribute("CellHeight") ?? 0 };
}

/// <summary>Saved simple cross-tab labels, cell fills and indexed Crystal grid strokes.</summary>
public sealed record ReportNativeCrossTabAppearance(string RowTotalCaption, string ColumnTotalCaption,
    string RowBackground, string ColumnBackground, string ValueBackground, string GridLines, decimal RowTotalFontSize, decimal ColumnTotalFontSize)
{
    internal XElement ToXml() => new("Appearance", new XAttribute("RowTotalCaption", RowTotalCaption),
        new XAttribute("ColumnTotalCaption", ColumnTotalCaption), new XAttribute("RowBackground", RowBackground),
        new XAttribute("ColumnBackground", ColumnBackground), new XAttribute("ValueBackground", ValueBackground), new XAttribute("GridLines", GridLines), new XAttribute("RowTotalFontSize", RowTotalFontSize), new XAttribute("ColumnTotalFontSize", ColumnTotalFontSize));
    internal static ReportNativeCrossTabAppearance? Read(XElement? x) => x is null ? null : new(
        (string?)x.Attribute("RowTotalCaption") ?? "", (string?)x.Attribute("ColumnTotalCaption") ?? "",
        (string?)x.Attribute("RowBackground") ?? "transparent", (string?)x.Attribute("ColumnBackground") ?? "transparent",
        (string?)x.Attribute("ValueBackground") ?? "transparent", (string?)x.Attribute("GridLines") ?? "", (decimal?)x.Attribute("RowTotalFontSize") ?? 10, (decimal?)x.Attribute("ColumnTotalFontSize") ?? 10);
    public string Background(bool header, bool rowHeader) => header ? ColumnBackground : rowHeader ? RowBackground : ValueBackground;
    public (double Width, string Color) Stroke(int index)
    {
        var lines = GridLines.Split(';');
        if (index >= lines.Length) return (-1, "#000000");
        var parts = lines[index].Split(':');
        return parts.Length >= 2 && int.TryParse(parts[0], out var width) ? (width / 20d, parts[1]) : (-1, "#000000");
    }
    public string LineStyle(int index)
    {
        var lines = GridLines.Split(';');
        var parts = index >= 0 && index < lines.Length ? lines[index].Split(':') : [];
        return parts.Length > 2 && parts[2] is "Double" or "Dash" or "Dot" ? parts[2] : "Single";
    }
    // A simple grid has no generated corner object.
    // Horizontal boundaries are owned by the cell above; this avoids doubled PDF strokes.
    public (int Left, int Right, int Bottom, int Top) Edges(bool header, bool rowHeader, bool lastColumn, bool firstRow,
        bool totalRow = false, bool totalColumn = false, bool rowTotalsBefore = false, bool columnTotalsBefore = false,
        bool lastRow = false, bool firstLabel = true, bool lastLabel = true, bool firstValue = false)
    {
        (int Left, int Right, int Bottom, int Top) edges = header && rowHeader ? (-1, -1, -1, -1)
            : header ? (firstValue ? 10 : 6, lastColumn ? 11 : -1, 9, 8)
            : rowHeader ? (firstLabel ? 4 : 0, lastLabel ? 5 : -1, lastRow ? 3 : 1, firstRow ? 2 : -1)
            : (firstLabel && firstValue ? 12 : -1, lastColumn ? 15 : 12, lastRow ? 14 : 13, -1);
        if (totalRow && !header)
        {
            var rule = rowHeader ? 19 : 18;
            if (Stroke(rule).Width >= 0) edges = rowTotalsBefore ? edges with { Bottom = rule } : edges with { Top = rule };
        }
        if (totalColumn && !rowHeader)
        {
            var rule = header ? 17 : 16;
            if (Stroke(rule).Width >= 0) edges = columnTotalsBefore ? edges with { Right = rule } : edges with { Left = rule };
        }
        return edges;
    }
}
