using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

/// <summary>Native DateTime composition: the system default date and time, or the authored date and time components
/// (the authored Gregorian date, the Windows short or long date, and the authored time components).
/// Weekday and era components, other calendars and conditional properties remain explicitly unsupported.</summary>
public sealed record ReportNativeDateTimeFormat(bool SystemDefaultFormatting, int WindowsDateDefault, int Order, string Separator,
    bool SuppressIfDuplicate = false, bool HasConditionalFormatting = false, string NativeArchive = "",
    ReportNativeDateComponents? DateComponents = null, ReportNativeTimeComponents? TimeComponents = null)
{
    internal string? UnsupportedReason => HasConditionalFormatting ? "conditional date/time field formatting is not implemented"
        : Order is < 0 or > 3 ? "the saved date/time order is unsupported"
        : SystemDefaultFormatting ? WindowsDateDefault is not (0 or 1) ? "the saved system date preference is unsupported" : null
        : (Order != 3 ? DateReason : null) ?? (Order != 2 ? TimeReason : null);

    // A custom format prints the Windows short or long date, or the authored date components, and the authored time components.
    private string? DateReason => WindowsDateDefault is 0 or 1 ? null
        : WindowsDateDefault != 2 ? "the saved date preference is unsupported"
        : DateComponents is null ? "custom date/time component formats were not extracted"
        : DateComponents.UnsupportedReason;

    private string? TimeReason => TimeComponents is null ? "custom time component formats were not extracted" : TimeComponents.UnsupportedReason;

    internal string Format(DateTime value, CultureInfo culture)
    {
        if (UnsupportedReason is { } reason) throw new NotSupportedException("Native date/time format: " + reason + ".");
        var date = Order == 3 ? "" : SystemDefaultFormatting || WindowsDateDefault != 2
            ? value.ToString(WindowsDateDefault == 0 ? "D" : "d", culture) : DateComponents!.Format(value, culture);
        var time = Order == 2 ? "" : SystemDefaultFormatting ? value.ToString("T", culture) : TimeComponents!.Format(value);
        return Order switch { 0 => date + Separator + time, 1 => time + Separator + date, 2 => date, 3 => time, _ => throw new InvalidOperationException() };
    }

    internal XElement ToXml() => new("FlexKitNativeDateTimeFormat",
        new XAttribute("SystemDefaultFormatting", SystemDefaultFormatting), new XAttribute("WindowsDateDefault", WindowsDateDefault),
        new XAttribute("Order", Order), new XAttribute("Separator", Separator), new XAttribute("SuppressIfDuplicate", SuppressIfDuplicate),
        new XAttribute("HasConditionalFormatting", HasConditionalFormatting), DateComponents?.ToXml(), TimeComponents?.ToXml(),
        new XElement("NativeArchive", new XAttribute("Encoding", "base64"), NativeArchive));

    internal static ReportNativeDateTimeFormat? Read(XElement parent) => parent.Element("FlexKitNativeDateTimeFormat") is { } xml
        ? new((bool?)xml.Attribute("SystemDefaultFormatting") ?? false, (int?)xml.Attribute("WindowsDateDefault") ?? -1,
            (int?)xml.Attribute("Order") ?? -1, (string?)xml.Attribute("Separator") ?? "", (bool?)xml.Attribute("SuppressIfDuplicate") ?? false,
            (bool?)xml.Attribute("HasConditionalFormatting") ?? false, xml.Element("NativeArchive")?.Value ?? "",
            ReportNativeDateComponents.Read(xml), ReportNativeTimeComponents.Read(xml)) : null;
}

/// <summary>The saved date-component properties: the order, the year (0 = two digits, 1 = four digits, 2 = omitted), month
/// (0 = number, 1 = two digits, 2 = abbreviated name, 3 = full name, 4 = omitted) and day (0 = number, 1 = two digits, 2 = omitted)
/// styles and the four separators. Only the Gregorian calendar without weekday or era components is rendered.</summary>
public sealed record ReportNativeDateComponents(int DateOrder, int YearType, int MonthType, int DayType,
    int DayOfWeekType, int EraType, int CalendarType, string ZeroSeparator, string FirstSeparator,
    string SecondSeparator, string ThirdSeparator, string DayOfWeekSeparator, int DayOfWeekPosition, int DayOfWeekEnclosure)
{
    internal string? UnsupportedReason => CalendarType != 1 ? "the custom date calendar is unsupported"
        : DateOrder is < 0 or > 2 ? "the custom date order is unsupported"
        : YearType is < 0 or > 2 || MonthType is < 0 or > 4 || DayType is < 0 or > 2 ? "the custom date component styles are unsupported"
        : DayOfWeekType != 2 || EraType != 2 ? "custom dates with weekday or era components are not implemented" : null;

    internal string Format(DateTime value, CultureInfo culture)
    {
        // The report explicitly selects the Gregorian calendar; runtime cultures such as th-TH must not change its year.
        // Authored separators are appended literally rather than interpreting '/' as a .NET culture-dependent date separator,
        // and each one prints after its component only when that component prints, as Crystal does ("Mar-99" and "March 1999"
        // with no day, "3/1" with no year, "1999" alone): an author who omits a component blanks the separator that follows it.
        var year = YearType == 2 ? null : (YearType == 0 ? value.Year % 100 : value.Year).ToString(YearType == 0 ? "D2" : "D4", CultureInfo.InvariantCulture);
        var month = MonthType switch
        {
            4 => null,
            2 => culture.DateTimeFormat.GetAbbreviatedMonthName(value.Month),
            3 => culture.DateTimeFormat.GetMonthName(value.Month),
            _ => value.Month.ToString(MonthType == 0 ? "D" : "D2", CultureInfo.InvariantCulture)
        };
        var day = DayType == 2 ? null : value.Day.ToString(DayType == 0 ? "D" : "D2", CultureInfo.InvariantCulture);
        var parts = DateOrder switch { 0 => new[] { year, month, day }, 1 => new[] { day, month, year }, _ => new[] { month, day, year } };
        var separators = new[] { FirstSeparator, SecondSeparator, ThirdSeparator };
        var text = new StringBuilder(ZeroSeparator);
        for (var i = 0; i < parts.Length; i++)
            if (parts[i] is { } part) text.Append(part).Append(separators[i]);
        return text.ToString();
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

/// <summary>The saved time-component properties: the 12- or 24-hour base, the AM/PM strings and whether they print before or
/// after the time, and the hour, minute and second styles (0 = two digits, 1 = no leading zero, 2 = omitted) with their separators.</summary>
public sealed record ReportNativeTimeComponents(int TimeBase, int AmPmFormat, int HourType, int MinuteType, int SecondType,
    string AmString, string PmString, string HourMinuteSeparator, string MinuteSecondSeparator)
{
    internal string? UnsupportedReason => TimeBase is not (0 or 1) ? "the custom time base is unsupported"
        : AmPmFormat is not (0 or 1) ? "the custom AM/PM position is unsupported"
        : HourType is < 0 or > 2 || MinuteType is < 0 or > 2 || SecondType is < 0 or > 2 ? "the custom time component styles are unsupported" : null;

    internal string Format(DateTime value)
    {
        // Separators print only between printed components; the authored strings are appended literally, never the culture's.
        var hour = TimeBase == 0 ? (value.Hour % 12 == 0 ? 12 : value.Hour % 12) : value.Hour;
        var text = new StringBuilder();
        if (HourType != 2) text.Append(hour.ToString(HourType == 0 ? "D2" : "D", CultureInfo.InvariantCulture));
        if (MinuteType != 2) text.Append(text.Length > 0 ? HourMinuteSeparator : "").Append(value.Minute.ToString(MinuteType == 0 ? "D2" : "D", CultureInfo.InvariantCulture));
        if (SecondType != 2) text.Append(text.Length > 0 ? MinuteSecondSeparator : "").Append(value.Second.ToString(SecondType == 0 ? "D2" : "D", CultureInfo.InvariantCulture));
        var amPm = TimeBase == 0 ? value.Hour < 12 ? AmString : PmString : "";
        return AmPmFormat == 0 ? amPm + text : text + amPm;
    }

    internal XElement ToXml() => new("TimeComponents", new XAttribute("TimeBase", TimeBase), new XAttribute("AmPmFormat", AmPmFormat),
        new XAttribute("HourType", HourType), new XAttribute("MinuteType", MinuteType), new XAttribute("SecondType", SecondType),
        new XAttribute("AmString", AmString), new XAttribute("PmString", PmString),
        new XAttribute("HourMinuteSeparator", HourMinuteSeparator), new XAttribute("MinuteSecondSeparator", MinuteSecondSeparator));

    internal static ReportNativeTimeComponents? Read(XElement parent) => parent.Element("TimeComponents") is { } xml
        ? new((int?)xml.Attribute("TimeBase") ?? -1, (int?)xml.Attribute("AmPmFormat") ?? -1, (int?)xml.Attribute("HourType") ?? -1,
            (int?)xml.Attribute("MinuteType") ?? -1, (int?)xml.Attribute("SecondType") ?? -1, (string?)xml.Attribute("AmString") ?? "",
            (string?)xml.Attribute("PmString") ?? "", (string?)xml.Attribute("HourMinuteSeparator") ?? "",
            (string?)xml.Attribute("MinuteSecondSeparator") ?? "") : null;
}
