using System.Globalization;
using System.Xml.Linq;
using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

/// <summary>A chart whose saved drawing prints nothing but its category label, as Crystal prints a cheque's
/// "VOID" stamp on continuation pages: the category value, in the drawing's font, sized by the frame height and
/// rotated about the midpoint of its baseline at the frame centre. The viewer, HTML export and PDF export print
/// that value per record; no axis, bar or legend is drawn because Crystal draws none.</summary>
public sealed record ReportChartMarker
{
    /// <summary>The category field whose value the frame prints, as a report reference ({@Void Text}).</summary>
    public string Binding { get; init; } = "";
    public string Font { get; init; } = "Arial";
    /// <summary>The label's em as a fraction of the frame height (Crystal's drawing keeps sizes in 32768ths of the frame).</summary>
    public double SizeRatio { get; init; }
    /// <summary>Counter-clockwise rotation of the run in degrees.</summary>
    public int Angle { get; init; }
    public string Color { get; init; } = "#000000";

    /// <summary>Crystal lays the label out on the frame less a 16-twip inset: a 4320-twip frame prints at 10922/32768 x 4304/20 = 71.7 pt.</summary>
    internal const int FrameInsetTwips = 16;
    /// <summary>A label at least a fifth of the frame tall is a stamp; smaller labels belong to an ordinary category axis.</summary>
    internal const double MinimumSizeRatio = .2;
    /// <summary>The baseline of a one-em line box, in em, for Arial's 0.905 ascent and 0.212 descent (the preview's rotation pivot).</summary>
    private const double PreviewBaseline = .8465;

    public bool IsValid => Binding.Length > 0 && Font.Length > 0 && SizeRatio is >= MinimumSizeRatio and <= 1
        && Angle is > -360 and < 360 && System.Text.RegularExpressions.Regex.IsMatch(Color, "^#[0-9a-fA-F]{6}$");

    /// <summary>The label's em in points inside a frame of the given height.</summary>
    public double EmPoints(int heightTwips) => SizeRatio * Math.Max(0, heightTwips - FrameInsetTwips) / 20d;

    /// <summary>Where the run starts inside the frame, in points from its top-left corner (y down): the run's baseline
    /// midpoint sits at the frame centre and the run reads counter-clockwise by <see cref="Angle"/>.</summary>
    internal (double StartX, double StartY, double Em, double Advance, double Radians) Place(int widthTwips, int heightTwips, string text, string family)
    {
        var em = EmPoints(heightTwips);
        var advance = em > 0 && text.Length > 0 ? Measure(text, em, family) : 0;
        var radians = Angle * Math.PI / 180;
        var (sin, cos) = Math.SinCos(radians);
        return (widthTwips / 40d - advance / 2 * cos, heightTwips / 40d + advance / 2 * sin, em, advance, radians);
    }

    /// <summary>The run as positioned markup inside the chart object's box (the viewer and HTML export).</summary>
    internal string Html(ReportDesignerElement element, string text)
    {
        text = RunText(text);
        if (text.Length == 0) return "";
        var place = Place(element.WidthTwips, element.HeightTwips, text, Font);
        const double px = 4 / 3d;
        static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        return "<span data-fx-chart-marker=\"true\" style=\"position:absolute;left:" + N(place.StartX * px) + "px;top:" + N((place.StartY - PreviewBaseline * place.Em) * px)
            + "px;line-height:1;white-space:pre;transform-origin:0 " + N(PreviewBaseline) + "em;transform:rotate(" + N(-Angle) + "deg);font-family:'"
            + ReportObjectRenderer.Encode(CssFont) + "';font-size:" + N(place.Em * px) + "px;font-weight:normal;font-style:normal;color:" + Color + "\">"
            + ReportObjectRenderer.Encode(text) + "</span>";
    }

    /// <summary>The family the markup names: the renderer's own font-name rule (letters, digits, spaces and hyphens,
    /// at most 100 characters, else Arial), so a name read from a report cannot close the CSS string it sits in.</summary>
    internal string CssFont => Font.Length <= 100 && Font.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-') ? Font : "Arial";

    /// <summary>The value as the one run it prints, by the printed chart's category-label rule (PrintedCategoryBarChart):
    /// a line break, a tab or another control character prints as a space and the ends are trimmed; the spaces inside
    /// are kept, and the viewer paints them (white-space:pre) where the PDF does.</summary>
    internal static string RunText(string text) =>
        new string(text.ReplaceLineEndings(" ").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();

    // The run's advance with the embedded faces. A character neither the drawing's face nor Arial can measure (another
    // script, an emoji) is placed at 0.6 em, so the run still prints and stays centred; the browser draws its glyph.
    private static double Measure(string text, double em, string family)
    {
        try { return NativePdfPage.MeasureText(text, em, family); }
        catch (NotSupportedException) { }
        try { return NativePdfPage.MeasureText(text, em); }
        catch (NotSupportedException) { }
        var advance = 0d;
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = rune.ToString();
            try { advance += NativePdfPage.MeasureText(glyph, em, family); continue; }
            catch (NotSupportedException) { }
            try { advance += NativePdfPage.MeasureText(glyph, em); }
            catch (NotSupportedException) { advance += .6 * em; }
        }
        return advance;
    }

    internal XElement ToXml() => new("FlexKitChartMarker", new XAttribute("Binding", Binding), new XAttribute("Font", Font),
        new XAttribute("Size", SizeRatio.ToString("R", CultureInfo.InvariantCulture)), new XAttribute("Angle", Angle), new XAttribute("Color", Color));

    /// <summary>The marker saved under a chart object, or null when there is none or it is not usable.</summary>
    internal static ReportChartMarker? Read(XElement source)
    {
        if (source.Element("FlexKitChartMarker") is not { } x) return null;
        var marker = new ReportChartMarker
        {
            Binding = ((string?)x.Attribute("Binding") ?? "").Trim(), Font = ((string?)x.Attribute("Font") ?? "").Trim(),
            SizeRatio = double.TryParse((string?)x.Attribute("Size"), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) ? size : 0,
            Angle = int.TryParse((string?)x.Attribute("Angle"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var angle) ? angle : 0,
            Color = ((string?)x.Attribute("Color") ?? "").Trim()
        };
        return marker.IsValid ? marker : null;
    }
}
