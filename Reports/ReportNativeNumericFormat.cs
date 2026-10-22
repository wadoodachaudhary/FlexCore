using System.Globalization;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

/// <summary>Authored Crystal numeric properties, applied only to display; values used by formulas and summaries stay unchanged.</summary>
public sealed record ReportNativeNumericFormat(
    bool SuppressIfZero, int NegativeType, bool ThousandsSeparators, bool LeadingZero, int DecimalPlaces,
    int RoundingType, int CurrencySymbolType, bool OneCurrencySymbolPerPage, int CurrencyPosition,
    string ThousandSymbol, string DecimalSymbol, string CurrencySymbol, bool AllowFieldClipping,
    bool AccountingFormat, bool ReverseSign, string ZeroValue, bool SuppressIfNoHorizontalPages, string Prefix, string Suffix,
    bool HasConditionalFormatting = false, bool SystemDefaultFormatting = false, bool SuppressIfDuplicate = false,
    string NativeArchive = "", int ValueType = 6)
{
    internal string? UnsupportedReason => HasConditionalFormatting ? "conditional numeric formatting is not implemented"
        : SystemDefaultFormatting && (ValueType == 7 || CurrencySymbolType != 0) ? "system-default currency formatting is not implemented"
        : AccountingFormat ? "accounting numeric layout is not implemented"
        : OneCurrencySymbolPerPage && CurrencySymbolType != 0 ? "one-currency-symbol-per-page layout is not implemented"
        : SuppressIfNoHorizontalPages ? "horizontal-page numeric suppression is not implemented"
        : DecimalPlaces is < 0 or > 10 || RoundingType is < 0 or > 17 ? "the saved numeric precision is unsupported"
        : NegativeType is < 0 or > 3 ? "the saved negative-number style is unsupported"
        : CurrencySymbolType is not (0 or 2) ? "fixed-position currency symbols are not implemented"
        : CurrencyPosition is < 0 or > 3 ? "the saved currency position is unsupported"
        : string.IsNullOrEmpty(DecimalSymbol) ? "an empty decimal separator is unsupported"
        : ZeroValue != "<Default Format>" ? "custom zero-value layout is not implemented" : null;

    internal string Format(object value)
    {
        if (UnsupportedReason is { } reason) throw new NotSupportedException("Native numeric format: " + reason + ".");
        if (SystemDefaultFormatting)
        {
            // Crystal SystemFieldProperties takes numeric separators/grouping/sign from the active culture,
            // currency precision for ordinary fractional numbers, and zero decimals for integer fields.
            // It retains authored zero suppression, leading-zero, reverse-sign and prefix/suffix preferences.
            var system = CultureInfo.CurrentCulture.NumberFormat;
            var digits = ValueType is >= 0 and <= 5 ? 0 : system.CurrencyDecimalDigits;
            var authoredRoundingDigits = Math.Max(0, 11 - Math.Max(1, RoundingType));
            var effective = this with { SystemDefaultFormatting = false, DecimalPlaces = digits,
                RoundingType = authoredRoundingDigits == DecimalPlaces || ValueType is >= 0 and <= 5
                    ? 11 - digits : Math.Max(RoundingType, 11 - digits),
                ThousandsSeparators = system.NumberGroupSizes.FirstOrDefault() != 0,
                ThousandSymbol = system.NumberGroupSeparator, DecimalSymbol = system.NumberDecimalSeparator,
                NegativeType = EffectiveNegativeType,
                ZeroValue = "<Default Format>" };
            return effective.FormatCore(value, system.NumberGroupSizes);
        }
        return FormatCore(value, [3]);
    }

    private int EffectiveNegativeType => !SystemDefaultFormatting ? NegativeType
        : CultureInfo.CurrentCulture.NumberFormat.NumberNegativePattern switch { 0 => 3, 3 or 4 => 2, _ => 1 };

    // Crystal measures the reserved sign slots as well as the visible digits. Centered
    // fields reserve both slots; left/right alignment ignores the opposite outer slot.
    internal string OverflowProbe(string formatted, string alignment)
    {
        if (formatted.Length == 0 || ValueType is 1 or 3 or 5 || EffectiveNegativeType == 0) return formatted;
        var core = formatted.AsSpan();
        if (!core.StartsWith(Prefix, StringComparison.Ordinal) || !core.EndsWith(Suffix, StringComparison.Ordinal)
            || core.Length < Prefix.Length + Suffix.Length) return formatted;
        core = core.Slice(Prefix.Length, core.Length - Prefix.Length - Suffix.Length);
        var currency = CurrencySymbolType == 0 ? "" : CurrencySymbol;
        // Outside currency symbols surround the sign; inside symbols do not.
        if (CurrencyPosition == 0 && core.StartsWith(currency, StringComparison.Ordinal)) core = core[currency.Length..];
        if (CurrencyPosition == 2 && core.EndsWith(currency, StringComparison.Ordinal)) core = core[..(core.Length - currency.Length)];
        var (leading, trailing) = EffectiveNegativeType switch
        {
            1 => ("-", ""), 2 => ("", "-"), 3 => ("(", ")"), _ => ("", "")
        };
        if (leading.Length > 0 && core.StartsWith(leading, StringComparison.Ordinal)) leading = "";
        if (trailing.Length > 0 && core.EndsWith(trailing, StringComparison.Ordinal)) trailing = "";
        switch (alignment.ToLowerInvariant())
        {
            case "right": case "default": leading = ""; break;
            case "center": case "horizontalcenter": break;
            default: trailing = ""; break;
        }
        return leading + formatted + trailing;
    }

    private string FormatCore(object value, int[] groupSizes)
    {
        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        var originalNumber = number;
        if (SuppressIfZero && number == 0) return "";
        // Crystal's RoundingType maps 1..11 to 10..0 fractional digits and 12..17 to tens..millions.
        // Saved precision also limits the rounding precision. Round display only, never a bound value.
        var roundingDigits = Math.Min(DecimalPlaces, 11 - Math.Max(1, RoundingType));
        if (roundingDigits >= 0) number = decimal.Round(number, roundingDigits, MidpointRounding.AwayFromZero);
        else
        {
            var increment = (decimal)Math.Pow(10, -roundingDigits);
            number = decimal.Round(number / increment, 0, MidpointRounding.AwayFromZero) * increment;
        }
        // Crystal integer display preserves the original sign even if a coarse increment rounds it to zero.
        var signValue = ValueType is >= 0 and <= 5 ? originalNumber : number;
        var negative = ReverseSign ? signValue > 0 : signValue < 0;
        var symbols = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
        symbols.NumberDecimalSeparator = DecimalSymbol;
        symbols.NumberGroupSeparator = ThousandSymbol;
        symbols.NumberGroupSizes = groupSizes;
        var text = decimal.Abs(number).ToString((ThousandsSeparators ? "N" : "F") + DecimalPlaces, symbols);
        if (!LeadingZero && decimal.Abs(number) < 1 && DecimalPlaces > 0 && text.StartsWith("0", StringComparison.Ordinal)) text = text[1..];
        var currency = CurrencySymbolType == 0 ? "" : CurrencySymbol;
        string Signed(string digits) => !negative ? digits : NegativeType switch
        {
            1 => "-" + digits, 2 => digits + "-", 3 => "(" + digits + ")", _ => digits
        };
        text = CurrencyPosition switch
        {
            0 => currency + Signed(text), 1 => Signed(currency + text),
            2 => Signed(text) + currency, _ => Signed(text + currency)
        };
        return Prefix + text + Suffix;
    }

    internal XElement ToXml() => new("FlexKitNativeNumericFormat",
        new XAttribute("SuppressIfZero", SuppressIfZero), new XAttribute("NegativeType", NegativeType),
        new XAttribute("ThousandsSeparators", ThousandsSeparators), new XAttribute("LeadingZero", LeadingZero),
        new XAttribute("DecimalPlaces", DecimalPlaces), new XAttribute("RoundingType", RoundingType),
        new XAttribute("CurrencySymbolType", CurrencySymbolType), new XAttribute("OneCurrencySymbolPerPage", OneCurrencySymbolPerPage),
        new XAttribute("CurrencyPosition", CurrencyPosition), new XAttribute("ThousandSymbol", ThousandSymbol),
        new XAttribute("DecimalSymbol", DecimalSymbol), new XAttribute("CurrencySymbol", CurrencySymbol),
        new XAttribute("AllowFieldClipping", AllowFieldClipping), new XAttribute("AccountingFormat", AccountingFormat),
        new XAttribute("ReverseSign", ReverseSign), new XAttribute("ZeroValue", ZeroValue),
        new XAttribute("SuppressIfNoHorizontalPages", SuppressIfNoHorizontalPages), new XAttribute("Prefix", Prefix), new XAttribute("Suffix", Suffix),
        new XAttribute("HasConditionalFormatting", HasConditionalFormatting), new XAttribute("SystemDefaultFormatting", SystemDefaultFormatting),
        new XAttribute("SuppressIfDuplicate", SuppressIfDuplicate), new XAttribute("ValueType", ValueType), new XElement("NativeArchive", new XAttribute("Encoding", "base64"), NativeArchive));

    internal static ReportNativeNumericFormat? Read(XElement parent) => parent.Element("FlexKitNativeNumericFormat") is { } xml
        ? new((bool?)xml.Attribute("SuppressIfZero") ?? false, (int?)xml.Attribute("NegativeType") ?? -1,
            (bool?)xml.Attribute("ThousandsSeparators") ?? false, (bool?)xml.Attribute("LeadingZero") ?? false,
            (int?)xml.Attribute("DecimalPlaces") ?? -1, (int?)xml.Attribute("RoundingType") ?? -1,
            (int?)xml.Attribute("CurrencySymbolType") ?? -1, (bool?)xml.Attribute("OneCurrencySymbolPerPage") ?? false,
            (int?)xml.Attribute("CurrencyPosition") ?? -1, (string?)xml.Attribute("ThousandSymbol") ?? "",
            (string?)xml.Attribute("DecimalSymbol") ?? "", (string?)xml.Attribute("CurrencySymbol") ?? "",
            (bool?)xml.Attribute("AllowFieldClipping") ?? false, (bool?)xml.Attribute("AccountingFormat") ?? false,
            (bool?)xml.Attribute("ReverseSign") ?? false, (string?)xml.Attribute("ZeroValue") ?? "",
            (bool?)xml.Attribute("SuppressIfNoHorizontalPages") ?? false, (string?)xml.Attribute("Prefix") ?? "",
            (string?)xml.Attribute("Suffix") ?? "", (bool?)xml.Attribute("HasConditionalFormatting") ?? false,
            (bool?)xml.Attribute("SystemDefaultFormatting") ?? false, (bool?)xml.Attribute("SuppressIfDuplicate") ?? false,
            xml.Element("NativeArchive")?.Value ?? "", (int?)xml.Attribute("ValueType") ?? 6) : null;
}
