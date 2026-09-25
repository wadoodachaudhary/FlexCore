using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Reports;

public sealed partial class CrystalFormula
{
    private static readonly HashSet<string> AggregateNames = new("sum average avg count distinctcount minimum maximum".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> StringFunctions = new(("totext cstr left right mid trim ltrim rtrim trimleft trimright uppercase lowercase ucase lcase replace chr chrw space replicate replicatestring " +
        "propercase strreverse join monthname weekdayname barcodec39 barcodec39ascii groupname picture towords").Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> BooleanFunctions = new(("isnull hasvalue isnumber isnumeric isdate isdatetime istime onfirstrecord onlastrecord cbool previousisnull nextisnull " +
        "haslowerbound hasupperbound includeslowerbound includesupperbound").Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> NumberFunctions = new(("len length instr instrrev asc ascw val tonumber cdbl ccur cint round roundup truncate int fix abs sgn ceiling floor remainder mod " +
        "year month day hour minute second dayofweek weekday datediff datepart count distinctcount ubound sqr exp log pi rnd atn sin cos tan rgb color groupinglevel counthierarchicalchildren").Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Functions = new(("sum average avg count distinctcount minimum maximum iif switch choose isnull hasvalue previous next onfirstrecord onlastrecord " +
        "totext cstr tonumber cdbl ccur cint cbool val isnumber isnumeric todate cdate date datetime cdatetime datetimevalue datevalue time timevalue ctime dateserial timeserial " +
        "dateadd datediff datepart year month day hour minute second dayofweek weekday monthname weekdayname isdate isdatetime istime " +
        "haslowerbound hasupperbound includeslowerbound includesupperbound getlowerbound getupperbound " +
        "left right mid len length trim ltrim rtrim trimleft trimright uppercase lowercase ucase lcase propercase replace instr instrrev strreverse chr chrw asc ascw space replicate replicatestring picture towords " +
        "split join filter ubound makearray array round roundup truncate int fix abs sgn ceiling floor remainder mod sqr exp log pi rnd atn sin cos tan " +
        "groupname rgb color barcodec39 barcodec39ascii groupinglevel counthierarchicalchildren defaultvaluesfornulls exceptionsfornulls evaluateafter " +
        "previousvalue nextvalue previousisnull nextisnull").Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ZeroArgumentFunctions = new("pi rnd".Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Constants = new(("true false yes no null currentdate currentdatetime currenttime today now timer printdate printtime datadate datatime " +
        "pagenumber totalpagecount pagenofm recordnumber onfirstrecord onlastrecord drilldowngrouplevel inrepeatedgroupheader currentfieldvalue groupnumber " +
        "filename fileauthor filecreationdate modificationdate modificationtime reporttitle reportcomments recordselection groupselection " +
        "crnoline crsingleline crdoubleline crdashedline crdottedline crdefaulthoraligned crleftaligned crcenteredhorizontally crrightaligned crjustified " +
        "whileprintingrecords whilereadingrecords beforereadingrecords defaultvaluesfornulls exceptionsfornulls " +
        "crred red crgreen green crblue blue crblack black crwhite white crmaroon maroon crolive olive crnavy navy crpurple purple crteal teal crgray gray " +
        "crsilver silver crlime lime cryellow yellow crfuchsia fuchsia craqua aqua crnocolor nocolor crregular crbold critalic crbolditalic " +
        "crsunday crmonday crtuesday crwednesday crthursday crfriday crsaturday crusesystem crfirstjan1 crfirstfourdays crfirstfullweek " +
        "weektodatefromsun monthtodate yeartodate last7days last4weekstosun lastfullweek lastfullmonth alldatestotoday alldatestoyesterday alldatesfromtoday " +
        "alldatesfromtomorrow aged0to30days aged31to60days aged61to90days over90days next30days next31to60days next61to90days next91to365days " +
        "calendar1stqtr calendar2ndqtr calendar3rdqtr calendar4thqtr calendar1sthalf calendar2ndhalf lastyearmtd lastyearytd").Split(' '), StringComparer.OrdinalIgnoreCase);
    private static bool KnownSymbol(string name) => Constants.Contains(name) || ZeroArgumentFunctions.Contains(name);
    private static bool IsAggregate(Call call) => AggregateNames.Contains(call.Name) && call.Args.Length >= 1 && call.Args[0] is Reference reference && !reference.Name.StartsWith("{?", StringComparison.Ordinal);
    private static object? FunctionDefault(string name) => StringFunctions.Contains(name) ? "" : BooleanFunctions.Contains(name) ? false : NumberFunctions.Contains(name) ? 0m : null;

    private static object? SymbolValue(Frame f, string name) => name.ToLowerInvariant() switch
    {
        "true" or "yes" => true, "false" or "no" => false, "null" => null,
        "currentdate" or "today" or "printdate" or "datadate" => f.Context.Now.Date, "currentdatetime" or "now" => f.Context.Now,
        "currenttime" or "printtime" or "datatime" => f.Context.Now.TimeOfDay, "timer" => (decimal)f.Context.Now.TimeOfDay.TotalSeconds,
        "pagenumber" => f.Context.PageNumber, "totalpagecount" => f.Context.TotalPageCount,
        "pagenofm" => $"Page {Text(f.Context.PageNumber)} of {Text(f.Context.TotalPageCount)}",
        "filename" or "fileauthor" or "filecreationdate" or "modificationdate" or "modificationtime" or "reporttitle" or "reportcomments" or "recordselection" or "groupselection"
            => f.Context.DocumentProperty(name.ToLowerInvariant()),
        "crnoline" or "crdefaulthoraligned" => 0m, "crsingleline" or "crleftaligned" => 1m, "crdoubleline" or "crcenteredhorizontally" => 2m,
        "crdashedline" or "crrightaligned" => 3m, "crdottedline" or "crjustified" => 4m,
        "recordnumber" => f.Context.RecordIndex + 1, "onfirstrecord" => f.Context.RecordIndex == 0,
        "onlastrecord" => f.Context.RecordIndex == f.Context.RecordCount - 1,
        "drilldowngrouplevel" => f.Context.DrillDownGroupLevel, "inrepeatedgroupheader" => f.Context.InRepeatedGroupHeader,
        "currentfieldvalue" => f.Context.CurrentFieldValue(), "groupnumber" => f.Context.GroupNumber(),
        "whileprintingrecords" or "whilereadingrecords" or "beforereadingrecords" => null,
        "defaultvaluesfornulls" => f.DefaultNulls = true, "exceptionsfornulls" => f.DefaultNulls = false,
        "crred" or "red" => 255m, "crgreen" or "green" => 32768m, "crblue" or "blue" => 16711680m, "crblack" or "black" => 0m, "crwhite" or "white" => 16777215m,
        "crmaroon" or "maroon" => 128m, "crolive" or "olive" => 32896m, "crnavy" or "navy" => 8388608m, "crpurple" or "purple" => 8388736m,
        "crteal" or "teal" => 8421376m, "crgray" or "gray" => 8421504m, "crsilver" or "silver" => 12632256m, "crlime" or "lime" => 65280m,
        "cryellow" or "yellow" => 65535m, "crfuchsia" or "fuchsia" => 16711935m, "craqua" or "aqua" => 16776960m, "crnocolor" or "nocolor" => -1m,
        "crregular" => 0m, "crbold" => 1m, "critalic" => 2m, "crbolditalic" => 3m,
        "crusesystem" => 0m, "crsunday" or "crfirstjan1" => 1m, "crmonday" or "crfirstfourdays" => 2m, "crtuesday" or "crfirstfullweek" => 3m,
        "crwednesday" => 4m, "crthursday" => 5m, "crfriday" => 6m, "crsaturday" => 7m,
        "pi" => (decimal)Math.PI, "rnd" => (decimal)Random(f.Context).Next(),
        var range when DateRange(range, f.Context.Now.Date) is { } dates => dates,
        _ => throw new InvalidDataException($"Unknown Crystal symbol '{name}'.")
    };

    private static CrystalRange? DateRange(string name, DateTime today)
    {
        var weekStart = today.AddDays(-(int)today.DayOfWeek);
        var year = today.Year;
        var lastYear = new DateTime(year - 1, today.Month, today.Month == 2 && today.Day == 29 ? 28 : today.Day);
        CrystalRange Span(DateTime start, DateTime end) => new(start, end) { StartIsDate = true, EndIsDate = true };
        return name switch
        {
            "weektodatefromsun" => Span(weekStart, today), "monthtodate" => Span(new(year, today.Month, 1), today), "yeartodate" => Span(new(year, 1, 1), today),
            "last7days" => Span(today.AddDays(-6), today), "last4weekstosun" => Span(weekStart.AddDays(-27), weekStart),
            "lastfullweek" => Span(weekStart.AddDays(-7), weekStart.AddDays(-1)),
            "lastfullmonth" => Span(new DateTime(year, today.Month, 1).AddMonths(-1), today.AddDays(-today.Day)),
            "alldatestotoday" => Span(DateTime.MinValue, today), "alldatestoyesterday" => Span(DateTime.MinValue, today.AddDays(-1)),
            "alldatesfromtoday" => Span(today, new(9999, 12, 31)), "alldatesfromtomorrow" => Span(today.AddDays(1), new(9999, 12, 31)),
            "aged0to30days" => Span(today.AddDays(-30), today), "aged31to60days" => Span(today.AddDays(-60), today.AddDays(-31)),
            "aged61to90days" => Span(today.AddDays(-90), today.AddDays(-61)), "over90days" => Span(DateTime.MinValue, today.AddDays(-91)),
            "next30days" => Span(today, today.AddDays(30)), "next31to60days" => Span(today.AddDays(31), today.AddDays(60)),
            "next61to90days" => Span(today.AddDays(61), today.AddDays(90)), "next91to365days" => Span(today.AddDays(91), today.AddDays(365)),
            "calendar1stqtr" => Span(new(year, 1, 1), new(year, 3, 31)), "calendar2ndqtr" => Span(new(year, 4, 1), new(year, 6, 30)),
            "calendar3rdqtr" => Span(new(year, 7, 1), new(year, 9, 30)), "calendar4thqtr" => Span(new(year, 10, 1), new(year, 12, 31)),
            "calendar1sthalf" => Span(new(year, 1, 1), new(year, 6, 30)), "calendar2ndhalf" => Span(new(year, 7, 1), new(year, 12, 31)),
            "lastyearmtd" => Span(new(year - 1, today.Month, 1), lastYear), "lastyearytd" => Span(new(year - 1, 1, 1), lastYear),
            _ => null
        };
    }

    private static object? Invoke(Frame f, Call call)
    {
        var args = call.Args; var name = call.Name.ToLowerInvariant();
        var values = new object?[args.Length]; var done = new bool[args.Length];
        object? Raw(int i)
        {
            if (i >= args.Length) throw new InvalidDataException($"{call.Name}: missing argument {i + 1}.");
            if (!done[i]) { values[i] = args[i].Get(f); done[i] = true; }
            return values[i];
        }
        object? Arg(int i) => name is "isnull" or "hasvalue" ? Raw(i) : f.NonNull(Raw(i));
        int Int(int i) => checked((int)Number(Arg(i)));
        string Str(int i) => Text(Arg(i));
        bool Has(int i) => i < args.Length;
        if (name == "iif") { var test = Boolean(Arg(0)); var yes = Arg(1); var no = Arg(2); return test ? yes : no; }
        if (name == "switch")
        {
            if (args.Length % 2 != 0) throw new InvalidDataException("Switch requires condition/value pairs.");
            for (var i = 0; i < args.Length; i++) _ = Arg(i);
            for (var i = 0; i < args.Length; i += 2) if (Boolean(Arg(i))) return Arg(i + 1);
            return f.Default;
        }
        if (name == "choose") { for (var index = 0; index < args.Length; index++) _ = Arg(index); var i = Int(0); return i >= 1 && i < args.Length ? Arg(i) : f.Default; }
        if (AggregateNames.Contains(name))
        {
            if (IsAggregate(call))
            {
                // Sum ({field}, {group field}, "monthly"): the third argument names the group's date, time or Boolean condition.
                if (args.Length == 3 && args[1] is Reference conditionGroup && args[2] is Literal { Value: string condition })
                    return f.Context.ConditionalAggregate(name, ((Reference)args[0]).Name, conditionGroup.Name, condition);
                if (args.Length > 2 || args.Length == 2 && args[1] is not Reference) throw new NotSupportedException($"{call.Name} requires a field and optional group reference.");
                return f.Context.Aggregate(name, ((Reference)args[0]).Name, args.Length == 2 ? ((Reference)args[1]).Name : "");
            }
            if (args.Length != 1) throw new NotSupportedException($"{call.Name} requires a field and optional group reference.");
            var value = Arg(0);
            return value is CrystalRange range ? RangeSummary(call.Name, range)
                : ArraySummary(f, call.Name, AsArray(value) ?? (args[0] is Reference ? [value] : throw new InvalidDataException($"{call.Name} requires a field, an array or a range.")));
        }
        if (name is "previous" or "next" or "previousvalue" or "nextvalue" or "previousisnull" or "nextisnull")
        {
            if (args.Length != 1 || args[0] is not Reference reference) throw new NotSupportedException($"{call.Name} requires a field reference.");
            var relative = f.Context.Relative(reference.Name, name.StartsWith("previous", StringComparison.Ordinal) ? -1 : 1);
            return name.EndsWith("isnull", StringComparison.Ordinal) ? relative is null or DBNull : relative;
        }
        if (name == "groupinglevel")
        {
            if (args.Length != 1 || args[0] is not Reference field) throw new NotSupportedException("GroupingLevel requires a field reference.");
            return (decimal)f.Context.GroupingLevel(field.Name);
        }
        // GroupName ({field}) or GroupName ({field}, "weekly"): the condition picks the group among those on the same field.
        if (name == "groupname")
        {
            if (args.Length is < 1 or > 2) throw new InvalidDataException("GroupName requires a field and an optional group condition.");
            var condition = args.Length == 2 ? Str(1) : null;
            if (f.Context.GroupName is { } groupName)
                return args[0] is Reference group ? Text(HostValue(groupName(group.Name, condition))) : throw new NotSupportedException("GroupName requires a field reference.");
            return condition is null ? Text(Arg(0))
                : throw new NotSupportedException($"GroupName with the group condition \"{condition}\" requires the report's groups, which this context does not provide.");
        }
        try
        {
            return name switch
            {
                "isnull" => Arg(0) is null or DBNull, "hasvalue" => Arg(0) is not (null or DBNull),
                "onfirstrecord" => f.Context.RecordIndex == 0, "onlastrecord" => f.Context.RecordIndex == f.Context.RecordCount - 1,
                "totext" or "cstr" => ToText(Arg(0), args.Skip(1).Select(n => f.NonNull(n.Get(f))).ToArray(), Arg(0) is DateTime ? KindOf(f, args[0]) : DateKind.Unknown),
                "tonumber" or "cdbl" or "ccur" => Number(Arg(0)), "cint" => decimal.Round(Number(Arg(0)), 0, MidpointRounding.AwayFromZero), "cbool" => Boolean(Arg(0)),
                "val" => decimal.TryParse(Regex.Match(Str(0).TrimStart(), @"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) ? val : 0m,
                "isnumber" or "isnumeric" => decimal.TryParse(Str(0), NumberStyles.Number, CultureInfo.CurrentCulture, out _),
                "isdate" or "isdatetime" => Arg(0) is DateTime || DateTime.TryParse(Str(0), CultureInfo.CurrentCulture, DateTimeStyles.None, out _),
                "istime" => Arg(0) is TimeSpan or DateTime || DateTime.TryParse(Str(0), CultureInfo.CurrentCulture, DateTimeStyles.None, out _) || TimeSpan.TryParse(Str(0), CultureInfo.CurrentCulture, out _),
                "date" or "datevalue" => args.Length switch { 1 => Date(Arg(0)).Date, 3 => CalendarDate(Int(0), Int(1), Int(2)), _ => throw new InvalidDataException($"{call.Name} requires 1 or 3 arguments.") },
                "datetime" or "cdatetime" or "datetimevalue" => args.Length switch
                {
                    1 => Date(Arg(0)), 2 => Date(Arg(0)).Date + Time(Arg(1)),
                    3 or 6 => CalendarDate(Int(0), Int(1), Int(2)) + (args.Length == 6 ? ClockTime(Int(3), Int(4), Int(5)) : TimeSpan.Zero),
                    _ => throw new InvalidDataException($"{call.Name} requires 1, 2, 3 or 6 arguments.")
                },
                "todate" or "cdate" => args.Length == 3 ? CalendarDate(Int(0), Int(1), Int(2)) : Date(Arg(0)).Date,
                "time" or "timevalue" or "ctime" => args.Length switch { 1 => Time(Arg(0)), 3 => ClockTime(Int(0), Int(1), Int(2)), _ => throw new InvalidDataException($"{call.Name} requires 1 or 3 arguments.") },
                "dateserial" => DateSerial(Int(0), Int(1), Int(2)),
                "timeserial" => TimeOfDay(Int(0) * 3600d + Int(1) * 60d + (double)Number(Arg(2))),
                "datediff" => DateDiff(Str(0), Date(Arg(1)), Date(Arg(2)), Has(3) ? Int(3) : 1),
                "dateadd" => DateAdd(Str(0), Number(Arg(1)), Date(Arg(2))),
                "datepart" => DatePart(Str(0), Date(Arg(1)), Has(2) ? Int(2) : 1, Has(3) ? Int(3) : 1),
                "year" => Date(Arg(0)).Year, "month" => Date(Arg(0)).Month, "day" => Date(Arg(0)).Day,
                "hour" => Time(Arg(0)).Hours, "minute" => Time(Arg(0)).Minutes, "second" => Time(Arg(0)).Seconds,
                "dayofweek" or "weekday" => Weekday(Date(Arg(0)), Has(1) ? Int(1) : 1),
                "haslowerbound" or "hasupperbound" => Ranges(Arg(0), call.Name).All(r => (name == "haslowerbound" ? r.Start : r.End) is not null),
                "includeslowerbound" => Ranges(Arg(0), call.Name).Select(Normalize).OrderBy(r => r.Start is null ? 0 : 1).ThenBy(r => r.Start, Comparer<object?>.Create(Compare)).FirstOrDefault() is { Start: not null, IncludeStart: true },
                "includesupperbound" => Ranges(Arg(0), call.Name).Select(Normalize).OrderBy(r => r.End is null ? 0 : 1).ThenByDescending(r => r.End, Comparer<object?>.Create(Compare)).FirstOrDefault() is { End: not null, IncludeEnd: true },
                "getlowerbound" => Arg(0) is CrystalRange lower ? Normalize(lower).Start : throw new InvalidDataException("GetLowerBound requires a range."),
                "getupperbound" => Arg(0) is CrystalRange upper ? Normalize(upper).End : throw new InvalidDataException("GetUpperBound requires a range."),
                "monthname" => Int(0) is var month and >= 1 and <= 12 ? (Has(1) && Boolean(Arg(1)) ? CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedMonthNames : CultureInfo.CurrentCulture.DateTimeFormat.MonthNames)[month - 1]
                    : throw new InvalidDataException("MonthName requires a month number between 1 and 12."),
                "weekdayname" => WeekdayName(Int(0), Has(1) && Boolean(Arg(1)), Has(2) ? Int(2) : 1),
                "left" => Str(0)[..Math.Clamp(Int(1), 0, Str(0).Length)], "right" => Str(0)[(Str(0).Length - Math.Clamp(Int(1), 0, Str(0).Length))..],
                "mid" => Str(0).Substring(Math.Clamp(Int(1) - 1, 0, Str(0).Length), Has(2) ? Math.Clamp(Int(2), 0, Str(0).Length - Math.Clamp(Int(1) - 1, 0, Str(0).Length)) : Str(0).Length - Math.Clamp(Int(1) - 1, 0, Str(0).Length)),
                "len" or "length" => Str(0).Length, "trim" => Str(0).Trim(), "ltrim" or "trimleft" => Str(0).TrimStart(), "rtrim" or "trimright" => Str(0).TrimEnd(),
                "uppercase" or "ucase" => Str(0).ToUpper(CultureInfo.CurrentCulture), "lowercase" or "lcase" => Str(0).ToLower(CultureInfo.CurrentCulture),
                "propercase" => ProperCase(Str(0)), "strreverse" => new string(Str(0).Reverse().ToArray()), "picture" => Picture(Str(0), Str(1)),
                "towords" => ToWords(Number(Arg(0)), Has(1) ? Int(1) : 2),
                "replace" => Replace(f, Str(0), Str(1), Str(2), Has(3) ? Int(3) : 1, Has(4) ? Int(4) : -1, Has(5) && Compare01(Int(5))),
                "instr" => args.Length is < 2 or > 4 ? throw new InvalidDataException("InStr requires 2 to 4 arguments.")
                    : args.Length == 4 || args.Length == 3 && Arg(0) is not string ? InStr(f, Int(0), Str(1), Str(2), args.Length == 4 && Int(3) == 1)
                    : InStr(f, 1, Str(0), Str(1), args.Length == 3 && Int(2) == 1),
                "instrrev" => InStrRev(f, Str(0), Str(1), Has(2) ? Int(2) : -1, Has(3) && Compare01(Int(3))),
                "chr" or "chrw" => char.ConvertFromUtf32(Int(0)), "asc" or "ascw" => Str(0).Length == 0 ? 0 : char.ConvertToUtf32(Str(0), 0),
                "space" => Int(0) is var spaces && spaces > MaxTextLength ? throw TextTooLong() : new string(' ', Math.Max(0, spaces)),
                "replicate" or "replicatestring" => Replicate(Str(0), Int(1)),
                "split" => Split(f, Str(0), Has(1) ? Str(1) : " ", Has(2) ? Int(2) : -1, Has(3) && Int(3) == 1),
                "join" => Join(ToArray(Arg(0)), Has(1) ? Str(1) : " "),
                "filter" => Filter(f, ToArray(Arg(0)), Str(1), !Has(2) || Boolean(Arg(2)), Has(3) && Int(3) == 1),
                "ubound" => (decimal)ToArray(Arg(0)).Length,
                "makearray" or "array" => args.Length is < 1 or > MaxArrayLength ? throw new InvalidDataException($"{call.Name} requires 1 to {MaxArrayLength:N0} values.") : Enumerable.Range(0, args.Length).Select(Arg).ToArray(),
                "round" => Round(Number(Arg(0)), Has(1) ? Int(1) : 0),
                "roundup" => RoundUp(Number(Arg(0)), Has(1) ? Int(1) : 0),
                "truncate" or "fix" => Truncate(Number(Arg(0)), Has(1) ? Int(1) : 0), "int" or "floor" => decimal.Floor(Number(Arg(0))),
                "ceiling" => decimal.Ceiling(Number(Arg(0))), "abs" => Math.Abs(Number(Arg(0))), "sgn" => Math.Sign(Number(Arg(0))),
                "remainder" or "mod" => Number(Arg(1)) == 0 ? throw new InvalidDataException("Division by zero.") : Number(Arg(0)) % Number(Arg(1)),
                "sqr" => Number(Arg(0)) < 0 ? throw new InvalidDataException("Sqr requires a non-negative number.") : Real(Math.Sqrt((double)Number(Arg(0)))),
                "exp" => Real(Math.Exp((double)Number(Arg(0)))),
                "log" => Number(Arg(0)) <= 0 ? throw new InvalidDataException("Log requires a positive number.") : Real(Math.Log((double)Number(Arg(0)))),
                "atn" => Real(Math.Atan((double)Number(Arg(0)))), "sin" => Real(Math.Sin((double)Number(Arg(0)))),
                "cos" => Real(Math.Cos((double)Number(Arg(0)))), "tan" => Real(Math.Tan((double)Number(Arg(0)))),
                "pi" => (decimal)Math.PI, "rnd" => (decimal)Random(f.Context).Next(Has(0) ? (double)Number(Arg(0)) : 1),
                "rgb" or "color" => Math.Clamp(Int(0), 0, 255) + Math.Clamp(Int(1), 0, 255) * 256 + Math.Clamp(Int(2), 0, 255) * 65536,
                "barcodec39" => Code39(Str(0), false), "barcodec39ascii" => Code39(Str(0), true),
                "counthierarchicalchildren" => (decimal)f.Context.HierarchicalChildren(Int(0)),
                "evaluateafter" => Arg(0),
                _ => throw new NotSupportedException($"Crystal function '{call.Name}' is not implemented.")
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidDataException($"Crystal function {call.Name}: {ex.Message}", ex);
        }
    }

    private static decimal Real(double value) => double.IsFinite(value) ? (decimal)value : throw new InvalidDataException("Crystal formula numeric overflow.");

    private static object? RangeSummary(string name, CrystalRange original)
    {
        var range = Normalize(original);
        object? Zero(object? other) => other switch { string => "", DateTime => DateTime.MinValue, TimeSpan => TimeSpan.Zero, null => null, _ => 0m };
        return name.ToLowerInvariant() switch
        {
            "minimum" => range.Start ?? Zero(range.End), "maximum" => range.End ?? Zero(range.Start),
            "count" => CountRange(original),
            _ => throw new InvalidDataException($"{name} cannot be applied to this range.")
        };
    }
    private static decimal CountRange(CrystalRange range)
    {
        long count = (range.Start, range.End) switch
        {
            (DateTime start, DateTime end) => (end.Date - start.Date).Days + 1L,
            (TimeSpan start, TimeSpan end) => (long)(end - start).TotalSeconds + 1 is var seconds && seconds < 1 ? seconds + 86400 : seconds,
            (DateTime or TimeSpan or null, DateTime or TimeSpan or null) when (range.Start ?? range.End) is not null => 0,
            _ => throw new InvalidDataException("Count requires an array, a date range or a time range.")
        };
        if (!range.IncludeStart) count--;
        if (!range.IncludeEnd) count--;
        return Math.Max(0, count);
    }
    private static object? ArraySummary(Frame f, string name, object?[] items)
    {
        f.Tick(items.Length);
        var present = items.Where(v => v is not (null or DBNull)).ToArray();
        switch (name.ToLowerInvariant())
        {
            case "count": return (decimal)items.Length;
            case "distinctcount": return DistinctCount(f, items);
            case "sum": return present.Sum(Number);
            case "average" or "avg": return present.Length == 0 ? throw new InvalidDataException("Average requires an array with at least one element.") : present.Sum(Number) / present.Length;
            default:
                var maximum = name.Equals("maximum", StringComparison.OrdinalIgnoreCase);
                if (items.Any(v => v is CrystalRange))
                {
                    CrystalRange? best = null;
                    for (var i = 0; i < items.Length; i++)
                        if (ToRange(items[i]) as CrystalRange is var next && (i == 0 || !Keeps(maximum, best, next))) best = next;
                    return best is null ? null : RangeSummary(name, best);
                }
                if (present.Length == 0) throw new InvalidDataException($"{name} requires an array with at least one element.");
                return present.Aggregate((best, next) => Compare(next, best) is var order && (maximum ? order > 0 : order < 0) ? next : best);
        }
    }
    /// <summary>Crystal's choice between range array elements for Maximum (the larger end) and Minimum (the smaller start): true keeps <paramref name="current"/>.
    /// An open end is the largest, an open start the smallest; on a tie an inclusive bound wins.</summary>
    private static bool Keeps(bool maximum, CrystalRange? current, CrystalRange? next)
    {
        if (current is null) return !maximum && next is not null;
        if (next is null) return maximum;
        (current, next) = (Normalize(current), Normalize(next));
        var (mine, theirs, includeMine, includeTheirs) = maximum ? (current.End, next.End, current.IncludeEnd, next.IncludeEnd) : (current.Start, next.Start, current.IncludeStart, next.IncludeStart);
        if (mine is null) return theirs is not null;
        if (theirs is null) return false;
        var order = Compare(mine, theirs);
        return (maximum ? order > 0 : order < 0) || order == 0 && includeMine && !includeTheirs;
    }

    private static object? HostValue(object? value) => value is string or byte[] or not System.Collections.IEnumerable ? value : AsArray(value);
    private static object?[]? AsArray(object? value) => value switch
    {
        object?[] array when array.GetType() == typeof(object[]) => array,
        null or DBNull or string or byte[] or CrystalRange => null,
        System.Collections.IEnumerable items => Materialize(items),
        _ => null
    };
    private static object?[] Materialize(System.Collections.IEnumerable items)
    {
        var list = new List<object?>();
        foreach (var item in items) { if (list.Count >= MaxHostArrayLength) throw new InvalidDataException("Array value exceeds the formula engine limit."); list.Add(item); }
        return list.ToArray();
    }
    private static object?[] ToArray(object? value) => AsArray(value) ?? [value];

    private static bool Like(Frame f, string text, string pattern)
    {
        int t = 0, p = 0, star = -1, resume = 0; long work = 0;
        static bool Same(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
        while (t < text.Length)
        {
            if (++work == 4096) { f.Tick(256); work = 0; }
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] != '*' && Same(pattern[p], text[t]))) { t++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; resume = t; }
            else if (star >= 0) { p = star + 1; t = ++resume; }
            else break;
        }
        f.Tick((int)(work >> 4));
        if (t < text.Length) return false;
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
    private static bool AnyOf(object? patterns, Func<object?, bool> test) => AsArray(patterns) is { } items ? items.Any(test) : test(patterns);

    private static int RoundInt(decimal value) => (int)Math.Clamp(decimal.Round(value, 0, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
    private static int ArrayIndex(object? index, int length)
    {
        var position = RoundInt(Number(index));
        if (position < 0) position += length + 1;
        return position <= 0 || position > length ? throw new InvalidDataException("A subscript must be between 1 and the size of the array.") : position - 1;
    }
    private static (int Start, int End)? SliceBounds(CrystalRange range, int length, int limit)
    {
        bool hasStart = range.Start is not null, hasEnd = range.End is not null, includeStart = range.IncludeStart, includeEnd = range.IncludeEnd;
        var start = hasStart ? RoundInt(Number(range.Start)) : 1;
        if (start < 0) start += length + 1;
        var end = hasEnd ? RoundInt(Number(range.End)) : length;
        if (end < 0) end += length + 1;
        if (start <= 0 || start > limit || end <= 0 || end > limit) return null;
        if (start > end)
        {
            if (hasStart && hasEnd) { (start, end) = (end, start); (includeStart, includeEnd) = (includeEnd, includeStart); }
            else end = start;
        }
        if (hasStart && !includeStart) start++;
        if (hasEnd && !includeEnd) end--;
        start--;
        end = Math.Min(end, length);
        return (Math.Min(start, end), end);
    }

    private static string Text(object? value) => value switch
    {
        string text => text,
        CrystalRange or byte[] or System.Collections.IEnumerable => throw new InvalidDataException("A string is required here, not an array, a range or a binary value."),
        _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? ""
    };
    private static DateTime Date(object? value) => value is DateTime date ? date : Convert.ToDateTime(value, CultureInfo.CurrentCulture);
    private static TimeSpan Time(object? value) => value switch
    {
        TimeSpan time => time, DateTime date => date.TimeOfDay,
        string text when DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var parsed) => parsed.TimeOfDay,
        string text when TimeSpan.TryParse(text, CultureInfo.CurrentCulture, out var parsed) && parsed >= TimeSpan.Zero && parsed < TimeSpan.FromDays(1) => parsed,
        _ => throw new InvalidDataException($"'{Text(value)}' is not a Crystal time value.")
    };
    private static TimeSpan TimeOfDay(double seconds)
    {
        if (!double.IsFinite(seconds)) throw new InvalidDataException("Crystal formula numeric overflow.");
        var wrapped = seconds % 86400; if (wrapped < 0) wrapped += 86400;
        return TimeSpan.FromTicks((long)Math.Round(wrapped * TimeSpan.TicksPerSecond));
    }
    private static DateTime CalendarDate(int year, int month, int day)
    {
        if (year is < 1 or > 9999) throw new InvalidDataException("The year must be between 1 and 9999.");
        if (month is < 1 or > 12) throw new InvalidDataException("The month must be between 1 and 12.");
        if (day < 1 || day > DateTime.DaysInMonth(year, month)) throw new InvalidDataException("The day number is not valid for the month.");
        return new(year, month, day);
    }
    private static TimeSpan ClockTime(int hour, int minute, int second) =>
        hour is < 0 or > 23 ? throw new InvalidDataException("Hours must be between 0 and 23.")
        : minute is < 0 or > 59 ? throw new InvalidDataException("Minutes must be between 0 and 59.")
        : second is < 0 or > 59 ? throw new InvalidDataException("Seconds must be between 0 and 59.")
        : new(hour, minute, second);
    private static DateTime DateSerial(int year, int month, int day)
    {
        if (year is < 1 or > 9999) throw new InvalidDataException("The year must be between 1 and 9999.");
        var months = year * 12L + month - 1;
        var targetYear = Math.Floor(months / 12d);
        if (targetYear is < 1 or > 9999) throw new InvalidDataException("DateSerial produced a year outside 1 to 9999.");
        var ticks = new DateTime((int)targetYear, (int)(months - (long)targetYear * 12) + 1, 1).Ticks + Math.Clamp(day - 1L, -4_000_000L, 4_000_000L) * TimeSpan.TicksPerDay;
        return ticks < DateTime.MinValue.Ticks || ticks > new DateTime(9999, 12, 31).Ticks ? throw new InvalidDataException("DateSerial produced a year outside 1 to 9999.") : new DateTime(ticks);
    }
    private static int Weekday(DateTime date, int firstDay)
    {
        if (firstDay == 0) firstDay = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 1;
        if (firstDay is < 1 or > 7) throw new InvalidDataException("The first day of the week must be between 0 and 7.");
        return ((int)date.DayOfWeek - (firstDay - 1) + 7) % 7 + 1;
    }
    private static string WeekdayName(int weekday, bool abbreviate, int firstDay)
    {
        if (weekday is < 1 or > 7) throw new InvalidDataException("WeekdayName requires a weekday number between 1 and 7.");
        if (firstDay == 0) firstDay = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 1;
        if (firstDay is < 1 or > 7) throw new InvalidDataException("The first day of the week must be between 0 and 7.");
        var names = abbreviate ? CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames : CultureInfo.CurrentCulture.DateTimeFormat.DayNames;
        return names[(5 + weekday + firstDay) % 7];
    }
    private static decimal DatePart(string interval, DateTime date, int firstDay, int firstWeek) => interval.ToLowerInvariant() switch
    {
        "yyyy" => date.Year, "q" => (date.Month - 1) / 3 + 1, "m" => date.Month, "y" => date.DayOfYear, "d" => date.Day,
        "w" => Weekday(date, firstDay),
        "ww" => CultureInfo.CurrentCulture.Calendar.GetWeekOfYear(date, firstWeek switch { 2 => CalendarWeekRule.FirstFourDayWeek, 3 => CalendarWeekRule.FirstFullWeek, _ => CalendarWeekRule.FirstDay },
            (DayOfWeek)((firstDay == 0 ? (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek + 1 : firstDay) - 1)),
        "h" => date.Hour, "n" => date.Minute, "s" => date.Second,
        _ => throw new NotSupportedException($"DatePart interval '{interval}' is not implemented.")
    };
    private static string ProperCase(string text)
    {
        var builder = new StringBuilder(text); var wordStart = true;
        for (var i = 0; i < builder.Length; i++)
        {
            var c = builder[i];
            if (!char.IsLetter(c)) { wordStart = true; continue; }
            if (wordStart && char.IsLower(c)) builder[i] = char.ToUpper(c, CultureInfo.CurrentCulture);
            else if (!wordStart && char.IsUpper(c)) builder[i] = char.ToLower(c, CultureInfo.CurrentCulture);
            wordStart = false;
        }
        return builder.ToString();
    }
    private static string Picture(string text, string template)
    {
        var builder = new StringBuilder(); var next = 0;
        foreach (var c in template)
            if (c is 'x' or 'X') { if (next < text.Length) builder.Append(text[next++]); }
            else builder.Append(c);
        return builder.Append(text[next..]).ToString();
    }
    private static readonly string[] Ones = "zero one two three four five six seven eight nine".Split(' ');
    private static readonly string[] Teens = "ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen".Split(' ');
    private static readonly string[] Tens = ",ten,twenty,thirty,forty,fifty,sixty,seventy,eighty,ninety".Split(',');
    private static readonly string[] Scales = ", thousand, million, billion, trillion, quadrillion, quintillion, sextillion, septillion, octillion, nonillion, decillion, zillion".Split(',');
    private static string ToWords(decimal value, int places)
    {
        if (places is < 0 or > 10) throw new InvalidDataException("ToWords supports 0 to 10 decimal places.");
        if (CultureInfo.CurrentCulture.TwoLetterISOLanguageName != "en") throw new NotSupportedException($"ToWords is implemented for English only, not '{CultureInfo.CurrentCulture.Name}'.");
        var negative = value < 0;
        var rounded = decimal.Round(Math.Abs(value), places, MidpointRounding.AwayFromZero);
        var whole = decimal.Truncate(rounded);
        var cents = (long)((rounded - whole) * (decimal)Math.Pow(10, places));
        var fraction = places == 0 ? "" : " and " + (cents == 0 ? new string('x', places) : cents.ToString(CultureInfo.InvariantCulture).PadLeft(places, '0')) + " / 1" + new string('0', places);
        if (whole == 0) return (places > 0 && negative ? "negative " : "") + "zero" + fraction;
        var groups = new List<string>();
        for (; whole > 0 && groups.Count < Scales.Length; whole = decimal.Floor(whole / 1000))
        {
            var n = (int)(whole % 1000); int hundreds = n / 100, tens = n / 10 % 10, ones = n % 10;
            var words = new StringBuilder();
            if (hundreds > 0) words.Append(Ones[hundreds]).Append(" hundred").Append(tens > 0 || ones > 0 ? " " : "");
            if (tens >= 2) words.Append(Tens[tens]).Append(ones > 0 ? "-" : "");
            if (tens == 1) words.Append(Teens[ones]); else if (ones > 0) words.Append(Ones[ones]);
            groups.Add(words.ToString());
        }
        var parts = Enumerable.Range(0, groups.Count).Reverse().Where(i => groups[i].Length > 0).Select(i => groups[i] + Scales[i]);
        return (negative ? "negative " : "") + string.Join(" ", parts) + fraction;
    }
    private static bool Compare01(int compare) => compare switch { 0 => false, 1 => true, _ => throw new InvalidDataException("The compare argument must be 0 (case-sensitive) or 1 (case-insensitive).") };
    private static bool SameChar(char a, char b, bool ignoreCase) =>
        a == b || ignoreCase && CultureInfo.CurrentCulture.TextInfo.ToLower(a) == CultureInfo.CurrentCulture.TextInfo.ToLower(b);
    /// <summary>Metered substring search: the first match at or after <paramref name="from"/> (0-based), or -1.</summary>
    private static int Find(Frame f, string text, string find, int from, bool ignoreCase)
    {
        from = Math.Max(0, from);
        if (find.Length == 0) return from <= text.Length ? from : -1;
        long work = 0;
        for (var i = from; i <= text.Length - find.Length; i++)
        {
            if (!ignoreCase)
            {
                var skip = text.AsSpan(i, text.Length - find.Length - i + 1).IndexOf(find[0]);
                work += skip < 0 ? text.Length - i : skip;
                if (skip < 0) break;
                i += skip;
            }
            var k = 0;
            while (k < find.Length && SameChar(text[i + k], find[k], ignoreCase)) k++;
            if (k == find.Length) { f.Tick((int)(work >> 4)); return i; }
            if ((work += k + 1) >= 4096) { f.Tick((int)(work >> 4)); work &= 15; }
        }
        f.Tick((int)(work >> 4));
        return -1;
    }
    /// <summary>Metered reverse search: the last match starting at or before <paramref name="from"/> (0-based), or -1.</summary>
    private static int FindLast(Frame f, string text, string find, int from, bool ignoreCase)
    {
        long work = 0;
        for (var i = Math.Min(from, text.Length - find.Length); i >= 0; i--)
        {
            var k = 0;
            while (k < find.Length && SameChar(text[i + k], find[k], ignoreCase)) k++;
            if (k == find.Length) { f.Tick((int)(work >> 4)); return i; }
            if ((work += k + 1) >= 4096) { f.Tick((int)(work >> 4)); work &= 15; }
        }
        f.Tick((int)(work >> 4));
        return -1;
    }
    private static decimal InStr(Frame f, int start, string text, string find, bool ignoreCase) =>
        start <= 0 ? throw new InvalidDataException("InStr requires a start position of at least 1.")
        : start - 1 < text.Length ? Find(f, text, find, start - 1, ignoreCase) + 1 : 0;
    private static decimal InStrRev(Frame f, string text, string find, int start, bool ignoreCase)
    {
        if (start is 0 or < -1) throw new InvalidDataException("InStrRev requires a start position of -1 or at least 1.");
        if (start > text.Length) return 0;
        if (start != -1) text = text[..start]; else start = text.Length;
        return FindLast(f, text, find, start, ignoreCase) + 1;
    }
    private static string Replace(Frame f, string text, string find, string replacement, int start, int count, bool ignoreCase)
    {
        if (start is 0 or < -1) throw new InvalidDataException("Replace requires a start position of -1 or at least 1.");
        if (count < -1) throw new InvalidDataException("Replace requires a count of -1 or more.");
        text = start > 1 ? start - 1 < text.Length ? text[(start - 1)..] : "" : text;
        if (find.Length == 0) return text;
        var builder = new StringBuilder(); var position = 0;
        void Add(int length) { if ((long)builder.Length + length > MaxTextLength) throw TextTooLong(); }
        while (count != 0 && Find(f, text, find, position, ignoreCase) is var found and >= 0)
        {
            // Each match costs a step and each 16 characters copied another, whatever Find charged for its scan.
            f.Tick(1 + (found - position + replacement.Length) / 16);
            Add(found - position + replacement.Length);
            builder.Append(text, position, found - position).Append(replacement);
            position = found + find.Length;
            if (count > 0) count--;
        }
        Add(text.Length - position);
        return builder.Append(text, position, text.Length - position).ToString();
    }
    private static string Replicate(string text, int copies)
    {
        if (copies <= 0 || text.Length == 0) return "";
        if ((long)text.Length * copies > MaxTextLength) throw TextTooLong();
        return new StringBuilder(text.Length * copies).Insert(0, text, copies).ToString();
    }
    private static string Join(object?[] items, string separator)
    {
        var parts = items.Where(v => v is not (null or DBNull)).Select(Text).ToArray();
        var length = parts.Sum(p => (long)p.Length) + (long)separator.Length * Math.Max(0, parts.Length - 1);
        return length > MaxTextLength ? throw TextTooLong() : string.Join(separator, parts);
    }
    private static object?[] Split(Frame f, string text, string delimiter, int limit, bool ignoreCase)
    {
        if (text.Length == 0) return [];
        if (delimiter.Length == 0) return [text];
        var parts = new List<object?>(); var unlimited = limit == -1; int position = 0, found;
        while ((unlimited || limit > 1) && (found = Find(f, text, delimiter, position, ignoreCase)) != -1)
        {
            if (parts.Count >= MaxArrayLength) throw new InvalidDataException($"Crystal arrays are limited to {MaxArrayLength:N0} elements.");
            limit--; parts.Add(text[position..found]); position = found + delimiter.Length;
        }
        parts.Add(text[position..]);
        return parts.Count > MaxArrayLength ? throw new InvalidDataException($"Crystal arrays are limited to {MaxArrayLength:N0} elements.") : parts.ToArray();
    }
    private static object?[] Filter(Frame f, object?[] items, string find, bool include, bool ignoreCase) =>
        items.Where(v => v is not (null or DBNull) && Find(f, Text(v), find, 0, ignoreCase) >= 0 == include).ToArray();
    private static decimal DistinctCount(Frame f, object?[] items)
    {
        var kinds = items.Select(Kind).Where(k => k is not null).Distinct().Count();
        if (kinds > 1)
        {
            var distinct = new List<object?>();
            foreach (var item in items)
            {
                f.Tick(distinct.Count / 16 + 1);
                if (!distinct.Any(d => Equal(d, item))) distinct.Add(item);
            }
            return distinct.Count;
        }
        var keys = new HashSet<object>();
        foreach (var item in items)
            keys.Add(item switch
            {
                null or DBNull => DBNull.Value,
                string text => text.TrimEnd(' ').ToUpperInvariant(),
                _ => Kind(item) == "number" ? Number(item) : item
            });
        return keys.Count;
    }
    private static decimal Round(decimal number, int places)
    {
        if (places >= 0) return decimal.Round(number, Math.Min(places, 28), MidpointRounding.AwayFromZero);
        var factor = (decimal)Math.Pow(10, Math.Min(-(long)places, 28));
        return decimal.Round(number / factor, 0, MidpointRounding.AwayFromZero) * factor;
    }
    private static decimal RoundUp(decimal number, int places)
    {
        var factor = (decimal)Math.Pow(10, Math.Clamp(places, -28, 28));
        var scaled = number * factor;
        return (scaled >= 0 ? decimal.Ceiling(scaled) : decimal.Floor(scaled)) / factor;
    }
    private static decimal Truncate(decimal number, int places)
    {
        var factor = (decimal)Math.Pow(10, Math.Clamp(places, -28, 28));
        return decimal.Truncate(number * factor) / factor;
    }
    private static readonly string[] Code39Ascii = ("%U $A $B $C $D $E $F $G $H $I $J $K $L $M $N $O $P $Q $R $S $T $U $V $W $X $Y $Z %A %B %C %D %E _ /A /B /C /D /E /F /G /H /I /J /K /L - . /O " +
        "0 1 2 3 4 5 6 7 8 9 /Z %F %G %H %I %J %V A B C D E F G H I J K L M N O P Q R S T U V W X Y Z %K %L %M %N %O %W +A +B +C +D +E +F +G +H +I +J +K +L +M +N +O +P +Q +R +S +T +U +V +W +X +Y +Z %P %Q %R %S %T").Split(' ');
    private static string Code39(string text, bool fullAscii)
    {
        var builder = new StringBuilder("*");
        foreach (var c in text)
        {
            if (fullAscii) { if (c < 128) builder.Append(Code39Ascii[c]); }
            else if (c is ' ') builder.Append('_');
            else if (c is >= '0' and <= '9' or >= 'A' and <= 'Z' or '$' or '%' or '+' or '-' or '.' or '/') builder.Append(c);
        }
        return builder.Append('*').ToString();
    }
    private static string ToText(object? value, object?[] args, DateKind kind = DateKind.Unknown)
    {
        if (value is string text) return text;
        if (value is object?[] or CrystalRange) throw new InvalidDataException("A single value is required here, not an array or a range.");
        if (value is TimeSpan time)
            return args.Length > 0 && args[0] is string timeFormat ? DateTime.MinValue.Add(time).ToString(timeFormat, CultureInfo.CurrentCulture) : DateTime.MinValue.Add(time).ToLongTimeString();
        if (args.Length > 0 && args[0] is string format)
            return value is IFormattable formattable ? formattable.ToString(format, CultureInfo.CurrentCulture) : Text(value);
        if (value is DateTime date)
            return kind == DateKind.Date || kind != DateKind.DateTime && date.TimeOfDay == TimeSpan.Zero ? date.ToShortDateString() : date.ToShortDateString() + " " + date.ToLongTimeString();
        if (value is null) return "";
        if (value is bool boolean) return boolean ? "True" : "False";
        var digits = args.Length == 0 ? 2 : Math.Clamp((int)Number(args[0]), 0, 28);
        var culture = (CultureInfo)CultureInfo.CurrentCulture.Clone();
        if (args.Length > 1) culture.NumberFormat.NumberGroupSeparator = Text(args[1]);
        // An empty decimal separator (CStr (x, 0, "", "")) joins the digits; .NET rejects it, so a placeholder is removed afterwards.
        var separator = args.Length > 2 ? Text(args[2]) : null;
        if (separator is not null) culture.NumberFormat.NumberDecimalSeparator = separator.Length > 0 ? separator : "\uFFFF";
        var formatted = Number(value).ToString("N" + digits, culture);
        return separator is "" ? formatted.Replace("\uFFFF", "", StringComparison.Ordinal) : formatted;
    }
    private static IEnumerable<CrystalRange> Ranges(object? value, string function) =>
        value is CrystalRange range ? [range] : ToArray(value).Select(v => v as CrystalRange ?? throw new InvalidDataException($"{function} requires a range or an array of ranges."));
    private static decimal DateDiff(string interval, DateTime start, DateTime end, int firstDay) => interval.ToLowerInvariant() switch
    {
        "yyyy" => end.Year - start.Year, "q" => (end.Year - start.Year) * 4 + (end.Month - 1) / 3 - (start.Month - 1) / 3,
        "m" => (end.Year - start.Year) * 12 + end.Month - start.Month, "d" or "y" => (end.Date - start.Date).Days,
        "w" => (end.Date - start.Date).Days / 7,
        "ww" => (end.Date.AddDays(-(Weekday(end, firstDay) - 1)) - start.Date.AddDays(-(Weekday(start, firstDay) - 1))).Days / 7,
        "h" => end.Ticks / TimeSpan.TicksPerHour - start.Ticks / TimeSpan.TicksPerHour,
        "n" => end.Ticks / TimeSpan.TicksPerMinute - start.Ticks / TimeSpan.TicksPerMinute,
        "s" => end.Ticks / TimeSpan.TicksPerSecond - start.Ticks / TimeSpan.TicksPerSecond,
        _ => throw new NotSupportedException($"DateDiff interval '{interval}' is not implemented.")
    };
    private static DateTime DateAdd(string interval, decimal amount, DateTime date) => interval.ToLowerInvariant() switch
    {
        "yyyy" => date.AddYears((int)amount), "q" => date.AddMonths((int)amount * 3), "m" => date.AddMonths((int)amount),
        "d" or "y" or "w" => date.AddDays((int)amount), "h" => date.AddHours((double)amount), "n" => date.AddMinutes((double)amount),
        "s" => date.AddSeconds((double)amount), "ww" => date.AddDays((int)amount * 7),
        _ => throw new NotSupportedException($"DateAdd interval '{interval}' is not implemented.")
    };
}
