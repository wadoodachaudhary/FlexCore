using System.Globalization;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

/// <summary>The numeric presentation saved on a native report field, independent of its value.</summary>
public sealed record ReportNativeNumberFormat(bool SuppressIfZero, int NegativeType, bool ThousandsSeparators,
    bool LeadingZero, int DecimalPlaces, int RoundingType, int CurrencySymbolType, bool OneCurrencyPerPage,
    int CurrencyPosition, string ThousandSymbol, string DecimalSymbol, string CurrencySymbol,
    bool AccountingFormat, bool ReverseSign, string ZeroValue, bool SuppressIfNoHorizontalPages,
    string Prefix, string Suffix, bool SystemDefaultFormatting = false, bool SuppressIfDuplicate = false,
    bool HasConditionalFormatting = false, string NativeArchive = "")
{
    internal string? UnsupportedReason => SuppressIfDuplicate ? "suppress-if-duplicate numeric fields are not implemented"
        : HasConditionalFormatting ? "conditional numeric field formatting is not implemented"
        : AccountingFormat || OneCurrencyPerPage || SuppressIfNoHorizontalPages ? "page-dependent/accounting numeric formatting is not implemented"
        : DecimalPlaces is < 0 or > 10 || RoundingType is < 1 or > 17 ? "the saved precision is unsupported"
        : NegativeType is < 0 or > 3 ? "the saved negative-number style is unsupported"
        : CurrencySymbolType != 0 ? "positioned currency symbols are not implemented"
        : DecimalSymbol.Length == 0 ? "the decimal separator is empty" : null;

    internal string Format(object value, CultureInfo culture)
    {
        if (UnsupportedReason is { } reason) throw new NotSupportedException("Native numeric format: " + reason + ".");
        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        if (ReverseSign) number = -number;
        var digits = 11 - RoundingType;
        if (digits >= 0) number = decimal.Round(number, digits, MidpointRounding.AwayFromZero);
        else
        {
            var scale = (decimal)Math.Pow(10, -digits);
            number = decimal.Round(number / scale, 0, MidpointRounding.AwayFromZero) * scale;
        }
        number = decimal.Round(number, DecimalPlaces, MidpointRounding.AwayFromZero);
        if (number == 0)
        {
            if (SuppressIfZero) return "";
            if (ZeroValue != "<Default Format>") return ZeroValue;
        }
        var symbols = (NumberFormatInfo)culture.NumberFormat.Clone();
        if (!SystemDefaultFormatting)
        {
            symbols.NumberGroupSeparator = ThousandSymbol;
            symbols.NumberDecimalSeparator = DecimalSymbol;
            symbols.NumberGroupSizes = [3];
        }
        var text = Math.Abs(number).ToString((ThousandsSeparators ? "N" : "F") + DecimalPlaces, symbols);
        if (!LeadingZero && Math.Abs(number) < 1 && DecimalPlaces > 0) text = text[1..];
        if (number < 0)
            text = SystemDefaultFormatting ? symbols.NumberNegativePattern switch
            { 0 => "(" + text + ")", 1 => symbols.NegativeSign + text, 2 => symbols.NegativeSign + " " + text,
                3 => text + symbols.NegativeSign, _ => text + " " + symbols.NegativeSign }
            : NegativeType switch { 1 => "-" + text, 2 => text + "-", 3 => "(" + text + ")", _ => text };
        return Prefix + text + Suffix;
    }

    internal XElement ToXml() => new("FlexKitNativeNumberFormat",
        new XAttribute("SuppressIfZero", SuppressIfZero), new XAttribute("NegativeType", NegativeType),
        new XAttribute("ThousandsSeparators", ThousandsSeparators), new XAttribute("LeadingZero", LeadingZero),
        new XAttribute("DecimalPlaces", DecimalPlaces), new XAttribute("RoundingType", RoundingType),
        new XAttribute("CurrencySymbolType", CurrencySymbolType), new XAttribute("OneCurrencyPerPage", OneCurrencyPerPage),
        new XAttribute("CurrencyPosition", CurrencyPosition), new XAttribute("ThousandSymbol", ThousandSymbol),
        new XAttribute("DecimalSymbol", DecimalSymbol), new XAttribute("CurrencySymbol", CurrencySymbol),
        new XAttribute("AccountingFormat", AccountingFormat), new XAttribute("ReverseSign", ReverseSign),
        new XAttribute("ZeroValue", ZeroValue), new XAttribute("SuppressIfNoHorizontalPages", SuppressIfNoHorizontalPages),
        new XAttribute("Prefix", Prefix), new XAttribute("Suffix", Suffix),
        new XAttribute("SystemDefaultFormatting", SystemDefaultFormatting), new XAttribute("SuppressIfDuplicate", SuppressIfDuplicate),
        new XAttribute("HasConditionalFormatting", HasConditionalFormatting),
        new XElement("NativeArchive", new XAttribute("Encoding", "base64"), NativeArchive));

    internal static ReportNativeNumberFormat? Read(XElement parent) => parent.Element("FlexKitNativeNumberFormat") is { } xml
        ? new((bool?)xml.Attribute("SuppressIfZero") ?? false, (int?)xml.Attribute("NegativeType") ?? 1,
            (bool?)xml.Attribute("ThousandsSeparators") ?? true, (bool?)xml.Attribute("LeadingZero") ?? true,
            (int?)xml.Attribute("DecimalPlaces") ?? 2, (int?)xml.Attribute("RoundingType") ?? 9,
            (int?)xml.Attribute("CurrencySymbolType") ?? 0, (bool?)xml.Attribute("OneCurrencyPerPage") ?? false,
            (int?)xml.Attribute("CurrencyPosition") ?? 0, (string?)xml.Attribute("ThousandSymbol") ?? ",",
            (string?)xml.Attribute("DecimalSymbol") ?? ".", (string?)xml.Attribute("CurrencySymbol") ?? "$",
            (bool?)xml.Attribute("AccountingFormat") ?? false, (bool?)xml.Attribute("ReverseSign") ?? false,
            (string?)xml.Attribute("ZeroValue") ?? "<Default Format>", (bool?)xml.Attribute("SuppressIfNoHorizontalPages") ?? false,
            (string?)xml.Attribute("Prefix") ?? "", (string?)xml.Attribute("Suffix") ?? "",
            (bool?)xml.Attribute("SystemDefaultFormatting") ?? false, (bool?)xml.Attribute("SuppressIfDuplicate") ?? false,
            (bool?)xml.Attribute("HasConditionalFormatting") ?? false, xml.Element("NativeArchive")?.Value ?? "") : null;
}
