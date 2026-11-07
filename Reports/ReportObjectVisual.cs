using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public sealed class ReportTextRun
{
    public string Text { get; set; } = "";
    public string Binding { get; set; } = "";
    /// <summary>A positive Crystal character spacing sets the advance between glyph origins in twips.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int CharacterSpacingTwips { get; set; }
    public string FontFamily { get; set; } = "Arial";
    public decimal FontSize { get; set; } = 10;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public string Color { get; set; } = "#000000";
    public Dictionary<string, string> FontConditionFormulas { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The authored date/time format of an embedded date field; the object's own format applies when absent.</summary>
    public ReportNativeDateTimeFormat? NativeDateTimeFormat { get; set; }
    public ReportTextRun Clone()
    {
        var clone = (ReportTextRun)MemberwiseClone();
        clone.FontConditionFormulas = new(FontConditionFormulas, StringComparer.OrdinalIgnoreCase);
        return clone;
    }
}

/// <summary>Portable, inert content; no HTML, remote URLs, or executable SVG is accepted.</summary>
public sealed class ReportObjectVisual
{
    public string ImageDataUrl { get; set; } = "";
    public ReportVectorImage? VectorImage { get; set; }
    public string ImageFit { get; set; } = "contain";
    public List<ReportTextRun> Runs { get; set; } = [];
    public string BorderColor { get; set; } = "#000000";
    public string TopLine { get; set; } = "NoLine";
    public string BottomLine { get; set; } = "NoLine";
    public string LeftLine { get; set; } = "NoLine";
    public string RightLine { get; set; } = "NoLine";
    /// <summary>Crystal's hard, three-point shadow outside the bottom and right edges.</summary>
    // Omitted from snapshots when unset, so page-band baselines stay stable (as NoPrinterLayout).
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasDropShadow { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool TightHorizontal { get; set; }
    internal const double DropShadowPoints = 3;
    /// <summary>Native stroke width in twips; zero is a hairline, null preserves legacy renderer defaults.</summary>
    public int? LineWidthTwips { get; set; }
    public bool CloseAtPageBreak { get; set; } = true;
    public ReportObjectVisual Clone()
    {
        var runs = new List<ReportTextRun>(Runs.Count);
        foreach (var run in Runs) runs.Add(run.Clone());
        return new()
        {
            ImageDataUrl = ImageDataUrl, VectorImage = VectorImage?.Clone(), ImageFit = ImageFit, Runs = runs,
            BorderColor = BorderColor, TopLine = TopLine, BottomLine = BottomLine, LeftLine = LeftLine, RightLine = RightLine,
            CloseAtPageBreak = CloseAtPageBreak, LineWidthTwips = LineWidthTwips, HasDropShadow = HasDropShadow, TightHorizontal = TightHorizontal
        };
    }

    public static string EmbedImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 5 * 1024 * 1024) throw new InvalidDataException("Images must be 5 MB or smaller.");
        var mime = bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
            : bytes.StartsWith(new byte[] { 255, 216, 255 }) ? "image/jpeg"
            : bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) ? "image/gif"
            : bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8) ? "image/webp"
            : ValidBitmap(bytes) ? "image/bmp"
            : throw new InvalidDataException("Use a PNG, JPEG, GIF, WebP, or BMP image.");
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    public static bool IsEmbeddedImage(string url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > 7 * 1024 * 1024) return false;
        var comma = url.IndexOf(',');
        if (comma < 0 || !new[] { "data:image/png;base64", "data:image/jpeg;base64", "data:image/gif;base64", "data:image/webp;base64", "data:image/bmp;base64" }.Contains(url[..comma])) return false;
        try { return EmbedImage(Convert.FromBase64String(url[(comma + 1)..])) == url; }
        catch (Exception ex) when (ex is FormatException or InvalidDataException) { return false; }
    }

    private static bool ValidBitmap(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 54 || !bytes[..2].SequenceEqual("BM"u8)) return false;
        var offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
        var header = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]);
        var width = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[18..]);
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[22..]);
        return header >= 40 && header <= bytes.Length - 14 && offset >= 14 + header && offset < bytes.Length &&
            width > 0 && height != 0 && (long)width * Math.Abs((long)height) <= 40_000_000;
    }

    internal static ReportObjectVisual Read(XElement element)
    {
        var visual = element.Element("FlexKitVisual");
        var border = element.Element("Border");
        var color = border?.Element("BorderColor");
        var lineWidth = (int?)element.Attribute("LineThickness") ?? (int?)border?.Attribute("LineWidth");
        if (lineWidth < 0) throw new InvalidDataException("Native line width cannot be negative.");
        var result = new ReportObjectVisual
        {
            ImageDataUrl = visual?.Element("Image")?.Value ?? "",
            VectorImage = ReportVectorImage.Read(visual?.Element("Vector")),
            ImageFit = (string?)visual?.Attribute("ImageFit") ?? "contain",
            TopLine = (string?)border?.Attribute("TopLineStyle") ?? "NoLine",
            BottomLine = (string?)border?.Attribute("BottomLineStyle") ?? "NoLine",
            LeftLine = (string?)border?.Attribute("LeftLineStyle") ?? "NoLine",
            RightLine = (string?)border?.Attribute("RightLineStyle") ?? "NoLine",
            TightHorizontal = (bool?)border?.Attribute("EnableTightHorizontal") ?? false,
            HasDropShadow = (bool?)border?.Attribute("HasDropShadow") ?? false,
            LineWidthTwips = lineWidth,
            CloseAtPageBreak = (bool?)element.Element("ObjectFormat")?.Attribute("EnableCloseAtPageBreak") ?? true,
            BorderColor = color is null ? "#000000" : $"#{Channel("R"):x2}{Channel("G"):x2}{Channel("B"):x2}"
        };
        foreach (var run in visual?.Elements("Run") ?? []) result.Runs.Add(new ReportTextRun
        {
            Text = string.Concat(run.Nodes().OfType<XText>().Select(text => text.Value)), Binding = (string?)run.Attribute("Binding") ?? "",
            FontFamily = (string?)run.Attribute("Font") ?? "Arial", FontSize = (decimal?)run.Attribute("Size") ?? 10,
            Bold = (bool?)run.Attribute("Bold") ?? false, Italic = (bool?)run.Attribute("Italic") ?? false,
            Underline = (bool?)run.Attribute("Underline") ?? false, Color = (string?)run.Attribute("Color") ?? "#000000",
            CharacterSpacingTwips = (int?)run.Attribute("CharacterSpacingTwips") ?? 0,
            NativeDateTimeFormat = ReportNativeDateTimeFormat.Read(run),
            FontConditionFormulas = run.Element("FontColorConditionFormulas")?.Attributes()
                .ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase)
        });
        // Crystal's SDK export keeps a text object's embedded fields as "{Table.Field}" / "{@Formula}" tokens inside <Text>, in the
        // object's one font; the native converter writes them as runs. Without runs the tokens would print as text.
        if (visual is null && element.Name.LocalName == "TextObject"
            && element.Ancestors("Report").FirstOrDefault()?.Attribute(NativeCrystal.CrystalReportXmlVersion.Attribute) is null
            && element.Element("Text")?.Value is { } text && EmbeddedField.IsMatch(text))
        {
            var font = element.Element("Font");
            var ink = element.Element("Color");
            var family = (string?)font?.Attribute("FontFamily") ?? (string?)font?.Attribute("Name") ?? "Arial";
            var size = decimal.TryParse((string?)font?.Attribute("SizeinPoints") ?? (string?)font?.Attribute("Size"), NumberStyles.Number, CultureInfo.InvariantCulture, out var points) ? points : 10m;
            var textColor = ink is null ? "#000000" : $"#{Math.Clamp((int?)ink.Attribute("R") ?? 0, 0, 255):x2}{Math.Clamp((int?)ink.Attribute("G") ?? 0, 0, 255):x2}{Math.Clamp((int?)ink.Attribute("B") ?? 0, 0, 255):x2}";
            ReportTextRun Run(string literal, string binding) => new()
            {
                Text = literal, Binding = binding, FontFamily = family, FontSize = size, Color = textColor,
                Bold = (bool?)font?.Attribute("Bold") ?? false, Italic = (bool?)font?.Attribute("Italic") ?? false, Underline = (bool?)font?.Attribute("Underline") ?? false
            };
            var at = 0;
            foreach (System.Text.RegularExpressions.Match token in EmbeddedField.Matches(text))
            {
                if (token.Index > at) result.Runs.Add(Run(text[at..token.Index], ""));
                result.Runs.Add(Run("", token.Value));
                at = token.Index + token.Length;
            }
            if (at < text.Length) result.Runs.Add(Run(text[at..], ""));
        }
        return result;
        int Channel(string name) => Math.Clamp((int?)color?.Attribute(name) ?? 0, 0, 255);
    }

    private static readonly System.Text.RegularExpressions.Regex EmbeddedField = new(@"\{[@?#%]?[A-Za-z_][^{}]*\}", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static void Write(ReportObjectVisual visual, XElement element)
    {
        element.Element("FlexKitVisual")?.Remove();
        if (visual.Runs.Count > 0 || visual.ImageDataUrl.Length > 0 || visual.VectorImage is not null)
            element.Add(new XElement("FlexKitVisual", new XAttribute("ImageFit", visual.ImageFit),
                visual.ImageDataUrl.Length > 0 ? new XElement("Image", visual.ImageDataUrl) : null,
                visual.VectorImage?.ToXml(),
                visual.Runs.Select(run => new XElement("Run", new XAttribute("Binding", run.Binding),
                    new XAttribute("Font", run.FontFamily), new XAttribute("Size", run.FontSize),
                    new XAttribute("Bold", run.Bold), new XAttribute("Italic", run.Italic),
                    new XAttribute("Underline", run.Underline), new XAttribute("Color", run.Color),
                    run.CharacterSpacingTwips > 0 ? new XAttribute("CharacterSpacingTwips", run.CharacterSpacingTwips) : null, run.Text,
                    run.NativeDateTimeFormat?.ToXml(),
                    run.FontConditionFormulas.Count == 0 ? null : new XElement("FontColorConditionFormulas",
                        run.FontConditionFormulas.Select(c => new XAttribute(c.Key, c.Value)))))));
        var border = element.Element("Border");
        if (border is null) { border = new XElement("Border"); element.Add(border); }
        border.SetAttributeValue("TopLineStyle", visual.TopLine);
        border.SetAttributeValue("BottomLineStyle", visual.BottomLine);
        border.SetAttributeValue("LeftLineStyle", visual.LeftLine);
        border.SetAttributeValue("RightLineStyle", visual.RightLine);
        border.SetAttributeValue("HasDropShadow", visual.HasDropShadow);
        border.SetAttributeValue("EnableTightHorizontal", visual.TightHorizontal ? true : (bool?)null);
        border.SetAttributeValue("LineWidth", visual.LineWidthTwips);
        if (element.Name.LocalName == "LineObject") element.SetAttributeValue("LineThickness", visual.LineWidthTwips);
        var color = ReportObjectRenderer.Color(visual.BorderColor, "#000000");
        border.Element("BorderColor")?.Remove();
        border.Add(new XElement("BorderColor", new XAttribute("A", 255),
            new XAttribute("R", Convert.ToInt32(color.Substring(1, 2), 16)),
            new XAttribute("G", Convert.ToInt32(color.Substring(3, 2), 16)),
            new XAttribute("B", Convert.ToInt32(color.Substring(5, 2), 16))));
    }
}

public readonly record struct ReportObjectBounds(int Left, int Top, int Width, int Height)
{
    public static ReportObjectBounds Of(ReportDesignerElement item) => new(item.LeftTwips, item.TopTwips, item.WidthTwips, item.HeightTwips);
    public void Apply(ReportDesignerElement item)
    {
        item.LeftTwips = Left; item.TopTwips = Top; item.WidthTwips = Width; item.HeightTwips = Height;
    }

    public ReportObjectBounds Transform(double dx, double dy, string handle, int contentWidth, int grid = 60)
    {
        int Snap(double value) => grid > 0 ? (int)Math.Round(value / grid, MidpointRounding.AwayFromZero) * grid : (int)Math.Round(value);
        if (handle == "move") return this with { Left = Math.Clamp(Snap(Left + dx), 0, Math.Max(0, contentWidth - Width)), Top = Math.Max(0, Snap(Top + dy)) };
        var right = Left + Width;
        var bottom = Top + Height;
        var left = handle.Contains('w') ? Math.Clamp(Snap(Left + dx), 0, Math.Max(0, right - 15)) : Left;
        var top = handle.Contains('n') ? Math.Clamp(Snap(Top + dy), 0, Math.Max(0, bottom - 15)) : Top;
        if (handle.Contains('e')) right = Math.Clamp(Snap(right + dx), left + 15, Math.Max(left + 15, contentWidth));
        if (handle.Contains('s')) bottom = Math.Max(top + 15, Snap(bottom + dy));
        return new(left, top, right - left, bottom - top);
    }
}

/// <summary>One HTML vocabulary for the design surface and printed pages.</summary>
public static class ReportObjectRenderer
{
    internal static int TextLineLimit(ReportDesignerElement item)
    {
        if (item.MaxNumberOfLines < 0) throw new InvalidDataException("Maximum field line count cannot be negative.");
        if (ClipNativeLongWords(item)) return FixedLineCount(item);
        return item.CanGrow && item.Kind == "Field" ? item.MaxNumberOfLines : 0;
    }

    /// <summary>
    /// The lines a fixed-height object shows: one per whole font size of its inner height, one at least. A 446-twip
    /// column heading in 10pt bold shows two lines ("PO Issued" / "Date") although its box is shorter than two
    /// 237.6-twip printer lines, and a 240-twip summary label in the same font shows one; a 289-twip 11pt field
    /// and 221-twip 9pt fields show one, a 348-twip 7pt memo shows two.
    /// </summary>
    internal static int FixedLineCount(ReportDesignerElement item)
    {
        var inner = item.HeightTwips - 15 * (BorderPixels(item.Visual.TopLine, item.Visual.LineWidthTwips)
            + BorderPixels(item.Visual.BottomLine, item.Visual.LineWidthTwips));
        return Math.Max(1, (int)Math.Floor(inner / ((double)Math.Clamp(item.FontSize, 1, 300) * 20)));
    }

    // Shared by the viewer and PDF writer: a fixed one-line string clips at
    // the right edge rather than wrapping a trailing word into an invisible line.
    // Crystal treats a fixed text object without room for a printer line the same way, embedded
    // fields included ("Item Notes: {Notes}" prints its first line to the glyph that
    // reaches the edge, not the words that fit before it).
    internal static bool WrapText(ReportDesignerElement item) => !(item.Kind == "Field"
        && (item.NativeNumericFormat is not null || item.NativeNumberFormat is not null
            || !item.CanGrow && (item.NativeDateTimeFormat is not null || FixedLineCount(item) == 1))
        || item.Kind is "Text" or "FieldHeading" && !item.CanGrow && item.UseCrystalScreenMetrics && !NativeFixedTextWraps(item));

    /// <summary>
    /// Whether a fixed text object lays its text out as printer lines. Crystal wraps "Comments: {Notes}" in a
    /// 224-twip 8pt box (one 184.8-twip line and 39 twips over) and paints a 240-twip bold 10pt summary label
    /// as one line clipped at its right edge although its 237.6-twip line fits by a printer pixel; a box needs
    /// a screen pixel beyond the line. Fonts without printer metrics keep the whole-font-size count.
    /// </summary>
    /// <summary>
    /// The device whose font metrics lay the item's text out: Crystal's 600-dpi printer device, which also draws a
    /// fixed single-line field whatever the report's device, or the 96-dpi screen for a text object or growing field
    /// of a report authored with no printer.
    /// </summary>
    internal static int TextDpi(ReportDesignerElement item) => item.NoPrinterLayout && !(item.Kind == "Field"
        && (item.NativeNumericFormat is not null || item.NativeNumberFormat is not null
            || !item.CanGrow && (item.NativeDateTimeFormat is not null || FixedLineCount(item) == 1))) ? 96 : CrystalDeviceText.DeviceDpi;

    internal static bool NativeFixedTextWraps(ReportDesignerElement item)
    {
        if (ReportFontCatalog.NativeLineHeightPixels(item.FontFamily, item.FontSize, true, item.Bold, item.Italic, TextDpi(item)) is not { } pixels)
            return FixedLineCount(item) != 1;
        var inner = item.HeightTwips - 15 * (BorderPixels(item.Visual.TopLine, item.Visual.LineWidthTwips)
            + BorderPixels(item.Visual.BottomLine, item.Visual.LineWidthTwips));
        return inner >= pixels * 15 + 15;
    }

    internal static bool ClipNativeLongWords(ReportDesignerElement item) => item.UseCrystalScreenMetrics
        && item.Kind == "Field" && !item.CanGrow && WrapText(item);

    public static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    public static string Color(string? color, string fallback = "transparent") => color is { Length: 7 or 9 } && color[0] == '#' && color.AsSpan(1).ToString().All(Uri.IsHexDigit) ? color : fallback;
    private static string Font(string value) => value.Length <= 100 && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-') ? value : "Arial";
    public static string FontStyle(string family, decimal size, bool bold, bool italic, bool underline, string color, bool crystalScreenMetrics = false, int dpi = CrystalDeviceText.DeviceDpi)
    {
        if (family.Equals("Calibri", StringComparison.OrdinalIgnoreCase)) family = ReportFontCatalog.CalibriFamily;
        var points = (double)Math.Clamp(size, 1, 300);
        // Crystal lays a printed or exported report out on the printer device: the em is the LOGFONT height
        // MulDiv(points, 600, 72) in 600-dpi pixels (83 for 10pt), with unkerned advances. Measuring at that em
        // (13.28 CSS px for 10pt) reproduces Crystal's breaks on both Master Assembly references: the 13px screen
        // LOGFONT of Crystal's preview let 1-2% too-wide lines ("Add Capping to Stubwall - Per") fit, and the exact
        // 10pt em wrapped "Towel Bar - Moen YB2424CH" (131.72pt in a 131.25pt field), which Crystal prints on one line.
        // Keep the point size in the design; measurement and painting must use the same realized size.
        var cssSize = crystalScreenMetrics ? Number(Math.Round(points * dpi / 72, MidpointRounding.AwayFromZero) * 96 / dpi) + "px" : Number(points) + "pt";
        return (crystalScreenMetrics ? $"--fx-report-font-points:{Number(points)};font-kerning:none;" : "") + $"font-family:'{Font(family)}',sans-serif;font-size:{cssSize};font-weight:{(bold ? 700 : 400)};font-style:{(italic ? "italic" : "normal")};text-decoration:{(underline ? "underline" : "none")};color:{Color(color, "#000000")};";
    }

    public static string Style(ReportDesignerElement item, bool position = true, int originLeftTwips = 0, int originTopTwips = 0)
    {
        var textFrame = item.TextLayoutFrame();
        var lineLimit = TextLineLimit(textFrame);
        var outsideBorder = NativeNumericOutsideBorder(item);
        var screenText = item.UseCrystalScreenMetrics && item.Kind is "Text" or "Field" or "FieldHeading";
        double Coordinate(int value, int origin) => screenText
            ? Math.Floor(((double)origin + value) / 15) - origin / 15d : value / 15d;
        // Snap the absolute paper origin, not each nested section independently.
        // Keep authored twips and measured widths intact for wrapping and overflow.
        var bounds = position ? $"position:absolute;left:{Number(Coordinate(item.LeftTwips, originLeftTwips))}px;top:{Number(Coordinate(item.TopTwips, originTopTwips))}px;" : outsideBorder ? "position:relative;" : "";
        var align = item.HorizontalAlignment.ToLowerInvariant() switch { "right" => "right", "center" or "horizontalcenter" => "center", "justified" => "justify", "default" when item.NativeNumericFormat is not null || item.NativeNumberFormat is not null => "right", _ => "left" };
        var geometry = item.Kind == "Line" || outsideBorder ? "overflow:visible;" : "overflow:hidden;";
        // A fixed field with room for only one line clips at its right edge;
        // word wrapping would hide an entire trailing word instead of its excess.
        var writingMode = item.HasRotatedText ? $"writing-mode:{(item.TextRotation == 90 ? "sideways-lr" : "sideways-rl")};" : "";
        var whiteSpace = WrapText(textFrame) ? "pre-wrap" : "pre";
        // Layout retains the authored minimum box. Its text still observes the
        // line cap when that box is taller than the permitted growing content.
        var clip = !item.HasRotatedText && lineLimit > 0 && !ClipNativeLongWords(item) ? $"max-height:calc({lineLimit} * 1lh + {Number(BorderPixels(item.Visual.TopLine, item.Visual.LineWidthTwips) + BorderPixels(item.Visual.BottomLine, item.Visual.LineWidthTwips))}px);" : "";
        var lineHeight = ReportFontCatalog.NativeLineHeight(item.FontFamily, item.FontSize, item.UseCrystalScreenMetrics, item.Bold, item.Italic, TextDpi(item));
        return bounds + geometry + clip + writingMode + $"width:{Number(Math.Max(0, item.WidthTwips) / 15d)}px;height:{Number(Math.Max(0, item.HeightTwips) / 15d)}px;box-sizing:border-box;white-space:{whiteSpace};overflow-wrap:{(whiteSpace == "pre" || ClipNativeLongWords(textFrame) ? "normal" : "anywhere")};line-height:{lineHeight};letter-spacing:0;text-align:{align};{(item.HasRotatedText ? "padding-inline-start" : "padding-left")}:{Number(item.IndentTwips / 15d)}px;background:{Color(item.BackgroundColor)};" +
            FontStyle(item.FontFamily, item.FontSize, item.Bold, item.Italic, item.Underline, item.TextColor, item.UseCrystalScreenMetrics, TextDpi(item)) +
            (item.Kind == "Line" || outsideBorder ? "" : BorderStyle(item.Visual)) +
            (item.Kind != "Line" && item.Visual.HasDropShadow ? $"box-shadow:{Number(ReportObjectVisual.DropShadowPoints * 4 / 3)}px {Number(ReportObjectVisual.DropShadowPoints * 4 / 3)}px 0 #000000;" : "");
    }

    internal static bool IsDuplicateNativeTextPaint(ReportPageObject? previous, ReportPageObject current, int originLeft, int originTop)
    {
        static bool PlainNativeText(ReportPageObject item) => item.Analysis is null && item.Table is null
            && item.Element.UseCrystalScreenMetrics && !item.Element.CanGrow
            && item.Element.Kind is "Text" or "Field" or "FieldHeading"
            && Color(item.Element.BackgroundColor) == "transparent"
            && new[] { item.Element.Visual.TopLine, item.Element.Visual.BottomLine, item.Element.Visual.LeftLine, item.Element.Visual.RightLine }
                .All(line => BorderPixels(line, item.Element.Visual.LineWidthTwips) == 0);
        if (previous is null || current.FormattedContent.Length == 0 || !PlainNativeText(previous) || !PlainNativeText(current)
            || previous.FormattedContent != current.FormattedContent
            || previous.UnmaskedNumericContent != current.UnmaskedNumericContent) return false;
        // Native device coordinates round to a screen pixel. Repainting identical
        // glyphs there adds no ink in GDI, but HTML antialiasing makes them heavier.
        // Only adjacent, undecorated, identical paint is coalesced; snapshots retain both fields.
        static double Pixel(int value, int origin) => Math.Round(((double)value + origin) / 15, MidpointRounding.AwayFromZero);
        return Pixel(previous.Element.LeftTwips, originLeft) == Pixel(current.Element.LeftTwips, originLeft)
            && Pixel(previous.Element.TopTwips, originTop) == Pixel(current.Element.TopTwips, originTop)
            && Style(previous.Element, false) == Style(current.Element, false);
    }

    // Paint native numeric borders outside the authored text box without
    // shrinking its text area or changing its clipping and alignment.
    private static bool NativeNumericOutsideBorder(ReportDesignerElement item) => item.Kind == "Field" && !item.CanGrow
        && (item.NativeNumericFormat is not null || item.NativeNumberFormat is not null)
        && new[] { item.Visual.TopLine, item.Visual.BottomLine, item.Visual.LeftLine, item.Visual.RightLine }
            .Any(style => BorderPixels(style, item.Visual.LineWidthTwips) > 0);

    private static string BorderStyle(ReportObjectVisual visual) =>
        $"border-top:{Border(visual.TopLine, visual.BorderColor, visual.LineWidthTwips)};border-bottom:{Border(visual.BottomLine, visual.BorderColor, visual.LineWidthTwips)};border-left:{Border(visual.LeftLine, visual.BorderColor, visual.LineWidthTwips)};border-right:{Border(visual.RightLine, visual.BorderColor, visual.LineWidthTwips)};";

    private static string DecorateNativeField(ReportDesignerElement item, string content)
    {
        // Keep the authored border/box intact while clipping to complete memo lines.
        if (ClipNativeLongWords(item))
            return $"<span data-fx-native-memo=\"true\" style=\"display:block;max-height:calc({TextLineLimit(item)} * 1lh);overflow:hidden\">{content}</span>";
        if (item.UseCrystalScreenMetrics && item.FormatString.Length == 0 && item.NativeNumericFormat is { } numeric)
        {
            var text = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(content, "<[^>]+>", ""));
            var (leadingSlot, trailingSlot) = numeric.PaintedSignSlots(text, item.HorizontalAlignment);
            if (leadingSlot.Length > 0 || trailingSlot.Length > 0)
            {
                double leading = 0, trailing = 0;
                try
                {
                    if (leadingSlot.Length > 0) leading = Fx.ControlKit.Grid.NativePdfPage.MeasureText(leadingSlot, 1, item.FontFamily, item.Bold, item.Italic);
                    if (trailingSlot.Length > 0) trailing = Fx.ControlKit.Grid.NativePdfPage.MeasureText(trailingSlot, 1, item.FontFamily, item.Bold, item.Italic);
                }
                catch (NotSupportedException) { /* The browser retains its normal layout for fonts without portable metrics. */ }
                if (leading > 0 || trailing > 0)
                    content = $"<span data-fx-native-sign-inset=\"true\" style=\"display:block;box-sizing:border-box;width:100%;height:100%;padding-left:{Number(leading)}em;padding-right:{Number(trailing)}em\">{content}</span>";
            }
        }
        if (!NativeNumericOutsideBorder(item)) return content;
        return "<span data-fx-native-field-text=\"true\" style=\"display:block;width:100%;height:100%;overflow:hidden;\">" + content + "</span>"
            + $"<span data-fx-native-field-border=\"true\" aria-hidden=\"true\" style=\"position:absolute;pointer-events:none;left:-{Number(BorderPixels(item.Visual.LeftLine, item.Visual.LineWidthTwips))}px;top:-{Number(BorderPixels(item.Visual.TopLine, item.Visual.LineWidthTwips))}px;width:100%;height:100%;box-sizing:content-box;{BorderStyle(item.Visual)}\"></span>";
    }

    internal static double BorderPixels(string style, int? widthTwips) => style switch
    {
        "Double" => Math.Max(3, (widthTwips ?? 15) / 5d),
        "Single" or "Dash" or "Dot" => Math.Max(1, (widthTwips ?? 15) / 15d),
        _ => 0
    };

    private static string Border(string style, string color, int? widthTwips = 15) => style switch
    {
        "Single" => $"{Number(Math.Max(1, (widthTwips ?? 15) / 15d))}px solid {Color(color, "#000000")}", "Double" => $"{Number(Math.Max(3, (widthTwips ?? 15) / 5d))}px double {Color(color, "#000000")}",
        "Dash" => $"{Number(Math.Max(1, (widthTwips ?? 15) / 15d))}px dashed {Color(color, "#000000")}", "Dot" => $"{Number(Math.Max(1, (widthTwips ?? 15) / 15d))}px dotted {Color(color, "#000000")}", _ => "0"
    };

    public static string Content(ReportDesignerElement item, Func<string, string>? resolve = null, bool design = false,
        Func<ReportTextRun, string?>? resolveRun = null)
    {
        item = item.TextLayoutFrame();
        resolve ??= reference => reference;
        resolveRun ??= run => resolve(run.Binding);
        if (item.Kind == "Picture")
        {
            var image = ReportObjectVisual.IsEmbeddedImage(item.Visual.ImageDataUrl) ? item.Visual.ImageDataUrl : item.Visual.VectorImage?.ToDataUrl();
            return image is not null ? $"<img alt=\"{Encode(item.Name)}\" draggable=\"false\" src=\"{image}\" style=\"display:block;width:100%;height:100%;object-fit:{(item.Visual.ImageFit is "cover" or "fill" ? item.Visual.ImageFit : "contain")}\">"
                : $"<span data-fx-missing-image=\"true\">{Encode("[Missing image: " + item.Name + "]")}</span>";
        }
        if (item.Kind == "Line")
        {
            var lineStyle = item.Visual.TopLine == "NoLine" ? "Single" : item.Visual.TopLine;
            var border = Border(lineStyle, item.Visual.BorderColor);
            var center = "";
            if (item.Visual.LineWidthTwips is { } twips)
            {
                if (twips < 0) throw new InvalidDataException("Native line width cannot be negative.");
                // A native zero-width pen is a device hairline, not an invisible CSS border. Use one CSS pixel;
                // exact printer/device hairlines remain device-dependent. Native coordinates locate the stroke center.
                var pixels = twips == 0 ? 1d : twips / 15d;
                if (item.UseCrystalScreenMetrics) pixels = Math.Ceiling(pixels);
                var style = lineStyle switch { "Single" or "SingleLine" => "solid", "Double" or "DoubleLine" => "double",
                    "Dash" or "DashLine" => "dashed", "Dot" or "DotLine" => "dotted", _ => "none" };
                border = $"{Number(pixels)}px {style} {Color(item.Visual.BorderColor, "#000000")}";
                // Section clipping must not discard half a horizontal rule placed on its top edge.
                var inset = item.UseCrystalScreenMetrics && item.HeightTwips == 0
                    ? Math.Min(pixels / 2, Math.Max(0, item.TopTwips / 15d)) : pixels / 2;
                center = $" translateY(-{Number(inset)}px)";
            }
            return $"<span style=\"display:block;position:absolute;transform-origin:0 0;width:{Number(Math.Sqrt((double)item.WidthTwips * item.WidthTwips + (double)item.HeightTwips * item.HeightTwips) / 15)}px;transform:rotate({Number(Math.Atan2(item.HeightTwips, item.WidthTwips))}rad){center};border-top:{border}\"></span>";
        }
        if (item.Kind == "Map") return design ? Encode(item.Name) : "";
        if (item.Kind == "Box") return "";
        if (item.Kind == "Subreport") return Encode("[Subreport: " + item.SubreportName + "]");
        if (item.Kind is not ("Text" or "FieldHeading" or "Field")) return Encode("[Unsupported " + item.Kind + ": " + item.Name + "]");
        if (item.Visual.Runs.Count > 0)
        {
            var html = new StringBuilder();
            // Embedded data keeps its line endings. A following literal (even a space) belongs on
            // the next line when the field ends in a newline, and contributes to CanGrow height.
            var resolved = item.Visual.Runs.Select(run => (Run: run, Value: string.IsNullOrWhiteSpace(run.Binding) ? run.Text : resolveRun(run) ?? "")).ToArray();
            if (item.SuppressEmbeddedFieldBlankLines)
            {
                var lines = new List<List<(ReportTextRun Run, string Value)>> { new() };
                foreach (var value in resolved)
                {
                    var parts = value.Value.ReplaceLineEndings("\n").Split('\n');
                    for (var index = 0; index < parts.Length; index++)
                    {
                        if (index > 0) lines.Add([]);
                        lines[^1].Add((value.Run, parts[index]));
                    }
                }
                var visible = lines.Where((line, index) => index == lines.Count - 1 || !line.Any(part => part.Run.Binding.Length > 0)
                    || line.Any(part => part.Run.Binding.Length > 0 && !string.IsNullOrWhiteSpace(part.Value))).ToArray();
                for (var index = 0; index < visible.Length; index++)
                {
                    if (index > 0) html.Append("\n");
                    foreach (var part in visible[index]) Append(part.Run, part.Value);
                }
            }
            else foreach (var value in resolved) Append(value.Run, value.Value);
            return DecorateNativeField(item, html.ToString());

            void Append(ReportTextRun run, string value)
            {
                var dpi = TextDpi(item);
                html.Append("<span style=\"").Append(FontStyle(run.FontFamily, run.FontSize, run.Bold, run.Italic, run.Underline, run.Color, item.UseCrystalScreenMetrics, dpi));
                if (run.CharacterSpacingTwips > 0)
                {
                    // Crystal sets a fixed advance on the printer grid. CSS letter-spacing would add to each glyph's natural width.
                    const int printDpi = CrystalDeviceText.DeviceDpi;
                    var advance = Math.Max(1, Math.Round(run.CharacterSpacingTwips * (double)printDpi / 1440, MidpointRounding.AwayFromZero)) * 72 / printDpi;
                    html.Append("--fx-character-pitch:").Append(Number(advance)).Append("pt\">");
                    var characters = StringInfo.GetTextElementEnumerator(value.ReplaceLineEndings("\n"));
                    while (characters.MoveNext())
                    {
                        var glyph = characters.GetTextElement();
                        if (glyph == "\n") html.Append('\n');
                        else html.Append("<span style=\"display:inline-block;width:var(--fx-character-pitch);white-space:pre\">")
                            .Append(Encode(glyph)).Append("</span>");
                    }
                }
                else html.Append("\">").Append(EncodeText(item, value));
                html.Append("</span>");
            }
        }
        var text = item.Kind == "Field" ? resolve(item.Binding) : item.Text;
        if (item.Kind == "Field" && item.InterpretRtf && ReportRtfField.TryFormat(text, out var richText))
            return DecorateNativeField(item, richText);
        return DecorateNativeField(item, item.Kind == "Field" && item.InterpretHtml ? HtmlField(text) : EncodeText(item, text));
    }

    // Interpret display markup into trusted spans; never insert authored HTML into the viewer.
    private static string HtmlField(string? source)
    {
        var html = new StringBuilder();
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var document = parser.ParseDocument("<body></body>");
        void Visit(AngleSharp.Dom.INode node)
        {
            if (node is AngleSharp.Dom.IText literal) { html.Append(Encode(literal.Data)); return; }
            if (node is not AngleSharp.Dom.IElement tag) return;
            var name = tag.LocalName;
            if (name is "script" or "style" or "template" or "iframe" or "object") return;
            if (name == "br") { html.Append("<br>"); return; }
            var style = name switch { "b" or "strong" => "font-weight:bold", "i" or "em" => "font-style:italic",
                "u" => "text-decoration:underline", _ => "" };
            if (name == "font" && tag.GetAttribute("color") is { } color)
            {
                var named = System.Drawing.Color.FromName(color);
                var safe = Color(color, "");
                if (safe.Length == 0 && named.IsKnownColor) safe = $"#{named.R:x2}{named.G:x2}{named.B:x2}";
                if (safe.Length > 0) style = "color:" + safe;
            }
            if (style.Length > 0) html.Append("<span style=\"").Append(style).Append("\">");
            foreach (var child in tag.ChildNodes) Visit(child);
            if (style.Length > 0) html.Append("</span>");
        }
        foreach (var node in parser.ParseFragment(source ?? "", document.Body!)) Visit(node);
        return html.ToString();
    }

    private static string EncodeText(ReportDesignerElement item, string? value)
    {
        if (string.IsNullOrEmpty(value) || !ClipNativeLongWords(item)) return Encode(value);
        // Fixed Crystal memo fields wrap at whitespace and clip oversized tokens.
        // CSS alone also breaks XML/URLs at punctuation, unlike the native viewer.
        var html = new StringBuilder();
        foreach (System.Text.RegularExpressions.Match token in System.Text.RegularExpressions.Regex.Matches(value, @"\s+|\S+"))
        {
            if (char.IsWhiteSpace(token.Value[0])) html.Append(Encode(token.Value));
            else html.Append("<span style=\"white-space:nowrap\">").Append(Encode(token.Value)).Append("</span>");
        }
        return html.ToString();
    }
}
