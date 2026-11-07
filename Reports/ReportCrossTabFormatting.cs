using System.Globalization;

namespace Fx.ControlKit.Reports;

internal static class ReportCrossTabFormatting
{
    internal static bool IsSupported(string property) => property.ToLowerInvariant() is "color" or "fontcolor" or "backgroundcolor"
        or "bold" or "enablebold" or "italic" or "enableitalic" or "underline" or "enableunderline" or "size" or "fontsize"
        or "name" or "fontname" or "style" or "horizontalalignment" or "enablesuppress" or "displaystring" or "currencysymbol";

    internal static (ReportNativeCrossTabCellStyle Style, string? Display) Apply(ReportNativeCrossTabCellStyle original, string originalDisplay,
        Func<object?, CrystalFormulaContext> context, Func<ReportGridFormula, bool, bool?>? deferSuppression = null)
    {
        var style = original;
        string? display = null;
        foreach (var condition in original.Conditions)
        {
            var property = condition.Property.ToLowerInvariant();
            object? attribute = property switch
            {
                "color" or "fontcolor" => ColorValue(original.Color), "backgroundcolor" => ColorValue(original.Background),
                "bold" or "enablebold" => original.Bold, "italic" or "enableitalic" => original.Italic,
                "underline" or "enableunderline" => original.Underline, "size" or "fontsize" => original.FontSize,
                "name" or "fontname" => original.FontFamily, "style" => (original.Bold ? 1 : 0) + (original.Italic ? 2 : 0),
                "horizontalalignment" => original.Alignment switch { "center" => 2m, "right" => 3m, _ => 1m },
                "enablesuppress" => original.Suppressed, "displaystring" => originalDisplay,
                _ => throw new NotSupportedException("Unsupported cross-tab formatting condition: " + condition.Property)
            };
            // The terminal Boolean can determine visibility before a stateful cell runs in print order.
            var value = property == "enablesuppress" && deferSuppression?.Invoke(condition.Formula, original.Suppressed) is bool flag
                ? flag : condition.Formula.Compile(attribute).Evaluate(context(attribute));
            switch (property)
            {
                case "color": case "fontcolor": style = style with { Color = Color(value) }; break;
                case "backgroundcolor": style = style with { Background = Color(value) }; break;
                case "bold": case "enablebold": style = style with { Bold = CrystalFormula.Boolean(value) }; break;
                case "italic": case "enableitalic": style = style with { Italic = CrystalFormula.Boolean(value) }; break;
                case "underline": case "enableunderline": style = style with { Underline = CrystalFormula.Boolean(value) }; break;
                case "size": case "fontsize": style = style with { FontSize = Math.Clamp(CrystalFormula.Number(value), 1, 200) }; break;
                case "name": case "fontname": style = style with { FontFamily = Convert.ToString(value, CultureInfo.CurrentCulture) ?? original.FontFamily }; break;
                case "style":
                    var fontStyle = value is string text ? text : (int)CrystalFormula.Number(value) switch
                        { 0 => "Regular", 1 => "Bold", 2 => "Italic", 3 => "BoldItalic", _ => throw new InvalidDataException("Unknown cross-tab font style.") };
                    style = style with { Bold = fontStyle.Contains("bold", StringComparison.OrdinalIgnoreCase), Italic = fontStyle.Contains("italic", StringComparison.OrdinalIgnoreCase) };
                    break;
                case "horizontalalignment":
                    var alignment = value is string name ? name.ToLowerInvariant() : (int)CrystalFormula.Number(value) switch
                        { 0 or 1 => "left", 2 => "center", 3 => "right", _ => throw new NotSupportedException("Unsupported cross-tab alignment.") };
                    style = style with { Alignment = alignment switch { "horizontalcenter" => "center", "rightalign" => "right", "leftalign" => "left", "left" or "right" or "center" => alignment,
                        _ => throw new NotSupportedException("Unsupported cross-tab alignment: " + alignment) } };
                    break;
                case "enablesuppress": style = style with { Suppressed = CrystalFormula.Boolean(value) }; break;
                case "displaystring": display = Convert.ToString(value, CultureInfo.CurrentCulture) ?? ""; break;
            }
        }
        return (style with { Conditions = [] }, display);
    }

    private static object ColorValue(string color) => color == "transparent" ? -1m : color.StartsWith('#') && color.Length == 7
        && int.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
        ? (decimal)((rgb >> 16 & 255) | (rgb >> 8 & 255) << 8 | (rgb & 255) << 16) : color;
    private static string Color(object? value)
    {
        if (value is string text) return ReportValueFormatting.CssColor(text);
        var color = (int)CrystalFormula.Number(value);
        return color == -1 ? "transparent" : $"#{color & 255:x2}{color >> 8 & 255:x2}{color >> 16 & 255:x2}";
    }
}
