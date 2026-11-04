using System.Text;

namespace Fx.ControlKit.Reports;

/// <summary>Offline, licensed compatibility fonts shared by measurement, preview and export.</summary>
public static class ReportFontCatalog
{
    private static readonly Lazy<string> Styles = new(BuildStyles);
    public static string Css => Styles.Value;
    public const string CalibriFamily = "Fx Report Calibri";
    public const string CalibriNotice = "Font compatibility: Calibri uses the bundled Carlito font when Calibri is not installed in the browser.";

    internal static string NativeLineHeight(string family, decimal points, bool native, bool bold = false, bool italic = false, int dpi = CrystalDeviceText.DeviceDpi)
        => NativeLineHeightPixels(family, points, native, bold, italic, dpi) is { } pixels ? ReportObjectRenderer.Number(pixels) + "px" : "1.15";

    internal static double? NativeLineHeightPixels(string family, decimal points, bool native, bool bold = false, bool italic = false, int dpi = CrystalDeviceText.DeviceDpi)
    {
        if (native && family.Equals("Arial", StringComparison.OrdinalIgnoreCase))
        {
            // Crystal's paragraph leading is the printer font's GDI line pitch: the face's hinted
            // VDMX extent at the 600-dpi ppem plus the rounded line gap (95 device pixels for 10pt,
            // 99 for bold 10pt, 78 for italic 8pt), carried back to CSS pixels. Keep that
            // authored-size leading separate from the rounded screen em; multiplying the 13px
            // screen font by 1.15 makes growing rows too short.
            var size = Math.Clamp(points, 1, 300);
            var ppem = (int)Math.Round(size * dpi / 72, MidpointRounding.AwayFromZero);
            if (CrystalDeviceFont.Resolve(family, bold, italic)?.LinePitch(ppem) is { } pitch)
                return pitch * 96d / dpi;
            // Without the installed font: 11.4pt for a 10pt font, truncated to whole device units.
            return (double)(Math.Floor(size * 1.14m * dpi / 72) * 96 / dpi);
        }
        // Calibri/Carlito: 1950 ascent + 550 descent, on a 2048-unit em.
        // The paragraph is laid out at the authored point size, before the
        // screen em is rounded. A generic 1.15 clips leading on wrapped lines.
        return native && (family.Equals("Calibri", StringComparison.OrdinalIgnoreCase)
                || family.Equals(CalibriFamily, StringComparison.OrdinalIgnoreCase))
            ? (double)Math.Clamp(points, 1, 300) * 96 / 72 * 2500 / 2048
            : null;
    }

    /// <summary>
    /// The printer font's GDI ascent (the hinted VDMX yMax at the 600-dpi ppem) in CSS pixels: where Crystal puts
    /// a line's baseline below the line's top. Null for faces other than the installed Arial.
    /// </summary>
    internal static double? NativeAscentPixels(string family, decimal points, bool bold = false, bool italic = false, int dpi = CrystalDeviceText.DeviceDpi)
    {
        if (!family.Equals("Arial", StringComparison.OrdinalIgnoreCase)) return null;
        var size = Math.Clamp(points, 1, 300);
        var ppem = (int)Math.Round(size * dpi / 72, MidpointRounding.AwayFromZero);
        return CrystalDeviceFont.Resolve(family, bold, italic)?.Ascent(ppem) * 96d / dpi;
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
