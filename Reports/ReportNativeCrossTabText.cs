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

    internal static string ClipRowLabel(string text, ReportNativeCrossTabCellStyle? style, int cellWidth, int padding)
    {
        var width = style?.ClipWidthTwips is > 0 ? Math.Min(cellWidth, style.ClipWidthTwips) : cellWidth;
        return Clip(text, style, width - 2 * padding);
    }

    internal static string Wrap(string text, ReportNativeCrossTabCellStyle? style, int widthTwips)
    {
        if (style is null) return text;
        var lines = new List<string>();
        foreach (var paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Clip(candidate, style, widthTwips) != candidate) { lines.Add(line); line = word; }
                else line = candidate;
            }
            lines.Add(line);
        }
        return string.Join("\n", lines);
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
