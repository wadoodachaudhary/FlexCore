using System.Text;
using Fx.ControlKit.Grid;

namespace Fx.ControlKit.Reports;

internal static class ReportNativeCrossTabText
{
    internal static double LineHeight(ReportNativeCrossTabCellStyle style)
    {
        var family = NativePdfFont.AuthoredFamily(style.FontFamily, style.Bold, style.Italic);
        var font = NativePdfFont.Resolve(family, style.Bold, style.Italic);
        return (font.Ascent - font.Descent) / 1000 * (double)style.FontSize;
    }

    internal static string Clip(string text, ReportNativeCrossTabCellStyle? style, int availableTwips)
    {
        if (style is null || string.IsNullOrEmpty(text)) return text;
        var font = CrystalDeviceFont.Resolve(style.FontFamily, style.Bold, style.Italic);
        if (font is null) return text;
        var ppem = (int)Math.Round((double)style.FontSize * CrystalDeviceText.DeviceDpi / 72, MidpointRounding.AwayFromZero);
        var limit = Math.Max(0, (long)Math.Floor(availableTwips * (double)CrystalDeviceText.DeviceDpi / 1440) + 1);
        var used = 0L; var prefix = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (!rune.IsBmp) return text;
            var advance = font.DeviceAdvance((char)rune.Value, ppem);
            if (advance < 0) return text;
            if (used + advance > limit) break;
            used += advance; prefix.Append(rune);
        }
        return prefix.ToString();
    }
}
