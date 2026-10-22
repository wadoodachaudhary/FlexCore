using System.Text;

namespace Fx.ControlKit.Reports;

/// <summary>Offline, licensed compatibility fonts shared by measurement, preview and export.</summary>
public static class ReportFontCatalog
{
    private static readonly Lazy<string> Styles = new(BuildStyles);
    public static string Css => Styles.Value;
    public const string CalibriFamily = "Fx Report Calibri";
    public const string CalibriNotice = "Font compatibility: Calibri uses the bundled Carlito font when Calibri is not installed in the browser.";

    internal static string NativeLineHeight(string family, decimal points, bool native)
    {
        // Crystal's Arial paragraph leading is 11.4pt for a 10pt font.
        // Keep that authored-size leading separate from the rounded screen em;
        // multiplying the 13px screen font by 1.15 makes growing rows too short.
        if (native && family.Equals("Arial", StringComparison.OrdinalIgnoreCase))
            return ReportObjectRenderer.Number((double)Math.Clamp(points, 1, 300) * 96 / 72 * 1.14) + "px";
        // Calibri/Carlito: 1950 ascent + 550 descent, on a 2048-unit em.
        // The paragraph is laid out at the authored point size, before the
        // screen em is rounded. A generic 1.15 clips leading on wrapped lines.
        return native && (family.Equals("Calibri", StringComparison.OrdinalIgnoreCase)
                || family.Equals(CalibriFamily, StringComparison.OrdinalIgnoreCase))
            ? ReportObjectRenderer.Number((double)Math.Clamp(points, 1, 300) * 96 / 72 * 2500 / 2048) + "px"
            : "1.15";
    }

    private static string BuildStyles()
    {
        var css = new StringBuilder();
        foreach (var (face, local, weight, style) in new[]
        {
            ("Regular", "Calibri", 400, "normal"), ("Bold", "Calibri Bold", 700, "normal"),
            ("Italic", "Calibri Italic", 400, "italic"), ("BoldItalic", "Calibri Bold Italic", 700, "italic")
        })
        {
            using var source = typeof(ReportFontCatalog).Assembly.GetManifestResourceStream($"Fx.Reports.Fonts.Carlito-{face}.woff2")
                ?? throw new InvalidOperationException($"Missing report compatibility font: Carlito-{face}");
            using var bytes = new MemoryStream();
            source.CopyTo(bytes);
            css.Append($"@font-face{{font-family:'{CalibriFamily}';font-weight:{weight};font-style:{style};font-display:block;src:local('{local}'),url(data:font/woff2;base64,")
                .Append(Convert.ToBase64String(bytes.ToArray())).Append(") format('woff2');}");
        }
        return css.ToString();
    }
}
