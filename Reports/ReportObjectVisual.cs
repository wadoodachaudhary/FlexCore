using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public sealed class ReportTextRun
{
    public string Text { get; set; } = "";
    public string Binding { get; set; } = "";
    public string FontFamily { get; set; } = "Arial";
    public decimal FontSize { get; set; } = 10;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public string Color { get; set; } = "#000000";
    public ReportTextRun Clone() => (ReportTextRun)MemberwiseClone();
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
    /// <summary>Native stroke width in twips; zero is a hairline, null preserves legacy renderer defaults.</summary>
    public int? LineWidthTwips { get; set; }
    public bool CloseAtPageBreak { get; set; } = true;
    public ReportObjectVisual Clone() => new()
    {
        ImageDataUrl = ImageDataUrl, VectorImage = VectorImage?.Clone(), ImageFit = ImageFit, Runs = Runs.Select(run => run.Clone()).ToList(),
        BorderColor = BorderColor, TopLine = TopLine, BottomLine = BottomLine, LeftLine = LeftLine, RightLine = RightLine,
        CloseAtPageBreak = CloseAtPageBreak, LineWidthTwips = LineWidthTwips
    };

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
            LineWidthTwips = lineWidth,
            CloseAtPageBreak = (bool?)element.Element("ObjectFormat")?.Attribute("EnableCloseAtPageBreak") ?? true,
            BorderColor = color is null ? "#000000" : $"#{Channel("R"):x2}{Channel("G"):x2}{Channel("B"):x2}"
        };
        foreach (var run in visual?.Elements("Run") ?? []) result.Runs.Add(new ReportTextRun
        {
            Text = run.Value, Binding = (string?)run.Attribute("Binding") ?? "",
            FontFamily = (string?)run.Attribute("Font") ?? "Arial", FontSize = (decimal?)run.Attribute("Size") ?? 10,
            Bold = (bool?)run.Attribute("Bold") ?? false, Italic = (bool?)run.Attribute("Italic") ?? false,
            Underline = (bool?)run.Attribute("Underline") ?? false, Color = (string?)run.Attribute("Color") ?? "#000000"
        });
        // Crystal's SDK export keeps a text object's embedded fields as "{Table.Field}" / "{@Formula}" tokens inside <Text>, in the
        // object's one font; the native converter writes them as runs. Without runs the tokens would print as text.
        if (visual is null && element.Name.LocalName == "TextObject" && element.Element("Text")?.Value is { } text && EmbeddedField.IsMatch(text))
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
                    new XAttribute("Underline", run.Underline), new XAttribute("Color", run.Color), run.Text))));
        var border = element.Element("Border");
        if (border is null) { border = new XElement("Border"); element.Add(border); }
        border.SetAttributeValue("TopLineStyle", visual.TopLine);
        border.SetAttributeValue("BottomLineStyle", visual.BottomLine);
        border.SetAttributeValue("LeftLineStyle", visual.LeftLine);
        border.SetAttributeValue("RightLineStyle", visual.RightLine);
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
        return item.CanGrow && item.Kind == "Field" ? item.MaxNumberOfLines : 0;
    }

    public static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");
    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    public static string Color(string? color, string fallback = "transparent") => color is { Length: 7 or 9 } && color[0] == '#' && color.AsSpan(1).ToString().All(Uri.IsHexDigit) ? color : fallback;
    private static string Font(string value) => value.Length <= 100 && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-') ? value : "Arial";
    public static string FontStyle(string family, decimal size, bool bold, bool italic, bool underline, string color) =>
        $"font-family:'{Font(family)}',sans-serif;font-size:{Number((double)Math.Clamp(size, 1, 300))}pt;font-weight:{(bold ? 700 : 400)};font-style:{(italic ? "italic" : "normal")};text-decoration:{(underline ? "underline" : "none")};color:{Color(color, "#000000")};";

    public static string Style(ReportDesignerElement item, bool position = true)
    {
        var lineLimit = TextLineLimit(item);
        var outsideBorder = NativeNumericOutsideBorder(item);
        var bounds = position ? $"position:absolute;left:{Number(item.LeftTwips / 15d)}px;top:{Number(item.TopTwips / 15d)}px;" : outsideBorder ? "position:relative;" : "";
        var align = item.HorizontalAlignment.ToLowerInvariant() switch { "right" => "right", "center" or "horizontalcenter" => "center", "justified" => "justify", "default" when item.NativeNumericFormat is not null || item.NativeNumberFormat is not null => "right", _ => "left" };
        var geometry = item.Kind == "Line" || outsideBorder ? "overflow:visible;" : "overflow:hidden;";
        var whiteSpace = item.Kind == "Field" && (item.NativeNumericFormat is not null || item.NativeNumberFormat is not null || !item.CanGrow && item.NativeDateTimeFormat is not null) ? "pre" : "pre-wrap";
        // Layout retains the authored minimum box. Its text still observes the
        // line cap when that box is taller than the permitted growing content.
        var clip = lineLimit > 0 ? $"max-height:calc({lineLimit} * 1lh + {Number(BorderPixels(item.Visual.TopLine, item.Visual.LineWidthTwips) + BorderPixels(item.Visual.BottomLine, item.Visual.LineWidthTwips))}px);" : "";
        return bounds + geometry + clip + $"width:{Number(Math.Max(0, item.WidthTwips) / 15d)}px;height:{Number(Math.Max(0, item.HeightTwips) / 15d)}px;box-sizing:border-box;white-space:{whiteSpace};overflow-wrap:{(whiteSpace == "pre" ? "normal" : "anywhere")};line-height:1.15;letter-spacing:0;text-align:{align};padding-left:{Number(item.IndentTwips / 15d)}px;background:{Color(item.BackgroundColor)};" +
            FontStyle(item.FontFamily, item.FontSize, item.Bold, item.Italic, item.Underline, item.TextColor) +
            (item.Kind == "Line" || outsideBorder ? "" : BorderStyle(item.Visual));
    }

    // Paint native numeric borders outside the authored text box without
    // shrinking its text area or changing its clipping and alignment.
    private static bool NativeNumericOutsideBorder(ReportDesignerElement item) => item.Kind == "Field" && !item.CanGrow
        && (item.NativeNumericFormat is not null || item.NativeNumberFormat is not null)
        && new[] { item.Visual.TopLine, item.Visual.BottomLine, item.Visual.LeftLine, item.Visual.RightLine }
            .Any(style => BorderPixels(style, item.Visual.LineWidthTwips) > 0);

    private static string BorderStyle(ReportObjectVisual visual) =>
        $"border-top:{Border(visual.TopLine, visual.BorderColor, visual.LineWidthTwips)};border-bottom:{Border(visual.BottomLine, visual.BorderColor, visual.LineWidthTwips)};border-left:{Border(visual.LeftLine, visual.BorderColor, visual.LineWidthTwips)};border-right:{Border(visual.RightLine, visual.BorderColor, visual.LineWidthTwips)};";

    private static string DecorateNativeNumeric(ReportDesignerElement item, string content)
    {
        if (!NativeNumericOutsideBorder(item)) return content;
        return "<span data-fx-native-field-text=\"true\" style=\"display:block;width:100%;height:100%;overflow:hidden;\">" + content + "</span>"
            + $"<span data-fx-native-field-border=\"true\" aria-hidden=\"true\" style=\"position:absolute;pointer-events:none;left:-{Number(BorderPixels(item.Visual.LeftLine, item.Visual.LineWidthTwips))}px;top:-{Number(BorderPixels(item.Visual.TopLine, item.Visual.LineWidthTwips))}px;width:100%;height:100%;box-sizing:content-box;{BorderStyle(item.Visual)}\"></span>";
    }

    private static double BorderPixels(string style, int? widthTwips) => style switch
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

    public static string Content(ReportDesignerElement item, Func<string, string>? resolve = null, bool design = false)
    {
        resolve ??= reference => reference;
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
                var style = lineStyle switch { "Single" or "SingleLine" => "solid", "Double" or "DoubleLine" => "double",
                    "Dash" or "DashLine" => "dashed", "Dot" or "DotLine" => "dotted", _ => "none" };
                border = $"{Number(pixels)}px {style} {Color(item.Visual.BorderColor, "#000000")}";
                center = $" translateY(-{Number(pixels / 2)}px)";
            }
            return $"<span style=\"display:block;position:absolute;transform-origin:0 0;width:{Number(Math.Sqrt((double)item.WidthTwips * item.WidthTwips + (double)item.HeightTwips * item.HeightTwips) / 15)}px;transform:rotate({Number(Math.Atan2(item.HeightTwips, item.WidthTwips))}rad){center};border-top:{border}\"></span>";
        }
        if (item.Kind == "Box") return "";
        if (item.Kind == "Subreport") return Encode("[Subreport: " + item.SubreportName + "]");
        if (item.Kind is not ("Text" or "FieldHeading" or "Field")) return Encode("[Unsupported " + item.Kind + ": " + item.Name + "]");
        if (item.Visual.Runs.Count > 0)
        {
            var html = new StringBuilder();
            foreach (var run in item.Visual.Runs)
                html.Append("<span style=\"").Append(FontStyle(run.FontFamily, run.FontSize, run.Bold, run.Italic, run.Underline, run.Color)).Append("\">")
                    .Append(Encode(string.IsNullOrWhiteSpace(run.Binding) ? run.Text : resolve(run.Binding))).Append("</span>");
            return DecorateNativeNumeric(item, html.ToString());
        }
        return DecorateNativeNumeric(item, Encode(item.Kind == "Field" ? resolve(item.Binding) : item.Text));
    }
}
