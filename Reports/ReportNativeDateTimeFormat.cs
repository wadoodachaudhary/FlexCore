using System.Globalization;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

/// <summary>Native DateTime composition, including the authored numeric Gregorian date-only format.
/// Custom time components and conditional properties remain explicitly unsupported.</summary>
public sealed record ReportNativeDateTimeFormat(bool SystemDefaultFormatting, int WindowsDateDefault, int Order, string Separator,
    bool SuppressIfDuplicate = false, bool HasConditionalFormatting = false, string NativeArchive = "",
    ReportNativeDateComponents? DateComponents = null)
{
    internal string? UnsupportedReason => SuppressIfDuplicate ? "suppress-if-duplicate date/time fields are not implemented"
        : HasConditionalFormatting ? "conditional date/time field formatting is not implemented"
        : Order is < 0 or > 3 ? "the saved date/time order is unsupported"
        : SystemDefaultFormatting ? WindowsDateDefault is not (0 or 1) ? "the saved system date preference is unsupported" : null
        : Order != 2 ? "custom time component formats are not implemented"
        : WindowsDateDefault != 2 ? "custom date/time component formats with system date preferences are not implemented"
        : DateComponents is null ? "custom date/time component formats were not extracted"
        : DateComponents.UnsupportedReason;

    internal string Format(DateTime value, CultureInfo culture)
    {
        if (UnsupportedReason is { } reason) throw new NotSupportedException("Native date/time format: " + reason + ".");
        if (!SystemDefaultFormatting) return DateComponents!.Format(value);
        var date = value.ToString(WindowsDateDefault == 0 ? "D" : "d", culture);
        var time = value.ToString("T", culture);
        return Order switch { 0 => date + Separator + time, 1 => time + Separator + date, 2 => date, 3 => time, _ => throw new InvalidOperationException() };
    }

    internal XElement ToXml() => new("FlexKitNativeDateTimeFormat",
        new XAttribute("SystemDefaultFormatting", SystemDefaultFormatting), new XAttribute("WindowsDateDefault", WindowsDateDefault),
        new XAttribute("Order", Order), new XAttribute("Separator", Separator), new XAttribute("SuppressIfDuplicate", SuppressIfDuplicate),
        new XAttribute("HasConditionalFormatting", HasConditionalFormatting), DateComponents?.ToXml(),
        new XElement("NativeArchive", new XAttribute("Encoding", "base64"), NativeArchive));

    internal static ReportNativeDateTimeFormat? Read(XElement parent) => parent.Element("FlexKitNativeDateTimeFormat") is { } xml
        ? new((bool?)xml.Attribute("SystemDefaultFormatting") ?? false, (int?)xml.Attribute("WindowsDateDefault") ?? -1,
            (int?)xml.Attribute("Order") ?? -1, (string?)xml.Attribute("Separator") ?? "", (bool?)xml.Attribute("SuppressIfDuplicate") ?? false,
            (bool?)xml.Attribute("HasConditionalFormatting") ?? false, xml.Element("NativeArchive")?.Value ?? "",
            ReportNativeDateComponents.Read(xml)) : null;
}

/// <summary>The saved date-component properties. Only numeric Gregorian dates without weekday or era are rendered.</summary>
public sealed record ReportNativeDateComponents(int DateOrder, int YearType, int MonthType, int DayType,
    int DayOfWeekType, int EraType, int CalendarType, string ZeroSeparator, string FirstSeparator,
    string SecondSeparator, string ThirdSeparator, string DayOfWeekSeparator, int DayOfWeekPosition, int DayOfWeekEnclosure)
{
    internal string? UnsupportedReason => CalendarType != 1 ? "the custom date calendar is unsupported"
        : DateOrder is < 0 or > 2 ? "the custom date order is unsupported"
        : YearType is not (0 or 1) || MonthType is not (0 or 1) || DayType is not (0 or 1)
            ? "custom dates with omitted or named components are not implemented"
        : DayOfWeekType != 2 || EraType != 2 ? "custom dates with weekday or era components are not implemented" : null;

    internal string Format(DateTime value)
    {
        // The report explicitly selects the Gregorian calendar; runtime cultures such as th-TH must not change its year.
        // Append authored separators literally rather than interpreting '/' as a .NET culture-dependent date separator.
        var year = (YearType == 0 ? value.Year % 100 : value.Year).ToString(YearType == 0 ? "D2" : "D4", CultureInfo.InvariantCulture);
        var month = value.Month.ToString(MonthType == 0 ? "D" : "D2", CultureInfo.InvariantCulture);
        var day = value.Day.ToString(DayType == 0 ? "D" : "D2", CultureInfo.InvariantCulture);
        var parts = DateOrder switch { 0 => new[] { year, month, day }, 1 => new[] { day, month, year }, _ => new[] { month, day, year } };
        return ZeroSeparator + parts[0] + FirstSeparator + parts[1] + SecondSeparator + parts[2] + ThirdSeparator;
    }

    internal XElement ToXml() => new("DateComponents", new XAttribute("DateOrder", DateOrder),
        new XAttribute("YearType", YearType), new XAttribute("MonthType", MonthType), new XAttribute("DayType", DayType),
        new XAttribute("DayOfWeekType", DayOfWeekType), new XAttribute("EraType", EraType), new XAttribute("CalendarType", CalendarType),
        new XAttribute("ZeroSeparator", ZeroSeparator), new XAttribute("FirstSeparator", FirstSeparator),
        new XAttribute("SecondSeparator", SecondSeparator), new XAttribute("ThirdSeparator", ThirdSeparator),
        new XAttribute("DayOfWeekSeparator", DayOfWeekSeparator), new XAttribute("DayOfWeekPosition", DayOfWeekPosition),
        new XAttribute("DayOfWeekEnclosure", DayOfWeekEnclosure));

    internal static ReportNativeDateComponents? Read(XElement parent) => parent.Element("DateComponents") is { } xml
        ? new((int?)xml.Attribute("DateOrder") ?? -1, (int?)xml.Attribute("YearType") ?? -1,
            (int?)xml.Attribute("MonthType") ?? -1, (int?)xml.Attribute("DayType") ?? -1,
            (int?)xml.Attribute("DayOfWeekType") ?? -1, (int?)xml.Attribute("EraType") ?? -1,
            (int?)xml.Attribute("CalendarType") ?? -1, (string?)xml.Attribute("ZeroSeparator") ?? "",
            (string?)xml.Attribute("FirstSeparator") ?? "", (string?)xml.Attribute("SecondSeparator") ?? "",
            (string?)xml.Attribute("ThirdSeparator") ?? "", (string?)xml.Attribute("DayOfWeekSeparator") ?? "",
            (int?)xml.Attribute("DayOfWeekPosition") ?? -1, (int?)xml.Attribute("DayOfWeekEnclosure") ?? -1) : null;
}
