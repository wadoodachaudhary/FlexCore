using System.Runtime.CompilerServices;

namespace Fx.ControlKit.Reports;

public sealed partial class CrystalFormula
{
    private static readonly HashSet<string> PrintStateNames = new(("whileprintingrecords whilereadingrecords beforereadingrecords evaluateafter pagenumber totalpagecount pagenofm " +
        "recordnumber groupnumber onfirstrecord onlastrecord drilldowngrouplevel inrepeatedgroupheader currentfieldvalue previous next previousvalue nextvalue previousisnull nextisnull " +
        "groupinglevel counthierarchicalchildren").Split(' '), StringComparer.OrdinalIgnoreCase);

    /// <summary>The part of a Crystal static type that decides Date against DateTime comparisons.</summary>
    private enum DateKind : byte { Unknown, Neutral, Other, Time, Date, DateTime }
    private int _resultKind = -1;

    /// <summary>The two operand expressions of a comparison; their Crystal types are looked up only when a result depends on them.</summary>
    private readonly struct Sides(Frame? frame, Node? left, Node? right)
    {
        public Node? Left => left;
        public Node? Right => right;
        public Sides Swap() => new(frame, right, left);
        public bool LeftIsDate => IsDate(frame, left);
        public bool RightIsDate => IsDate(frame, right);
        public DateKind LeftKind => KindOf(frame, left);
    }
    private static bool IsDate(Frame? f, Node? node) => KindOf(f, node) switch
    {
        DateKind.Date => true,
        DateKind.Unknown or DateKind.Neutral => throw new InvalidDataException($"Crystal compares a date-time with a Date by whole days but with a DateTime exactly, and whether {Describe(node)} is a Date or a DateTime is not known here (declare it through CrystalFormulaContext.ReferenceType)."),
        _ => false
    };
    private static string Describe(Node? node) =>
        node is null ? "this value" : Walk(node).OfType<Reference>().Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToArray() is { Length: > 0 } names ? string.Join(", ", names) : "this value";

    private static DateKind KindOf(Frame? f, Node? node, int depth = 0)
    {
        if (node is null || ++depth > MaxNesting || !RuntimeHelpers.TryEnsureSufficientExecutionStack()) return DateKind.Unknown;
        switch (node)
        {
            case Literal { Value: null } or DefaultValue: return DateKind.Neutral;
            case Literal { Value: DateTime }: return DateKind.DateTime;
            case Literal { Value: TimeSpan }: return DateKind.Time;
            case Literal: return DateKind.Other;
            case Reference reference: return f?.Context.ReferenceType(reference.Name) is { } type ? Kind(type.BaseType) : DateKind.Unknown;
            case Symbol symbol: return SymbolKind(symbol.Name);
            case VarRef v: return BindingKind(v.Binding);
            case Variable v: return BindingKind(v.Binding);
            case ElementAssign e: return BindingKind(e.Binding);
            case Redim r: return BindingKind(r.Binding);
            case Assign a: return BindingKind(a.Binding) is var bound and not DateKind.Unknown ? bound : KindOf(f, a.Value, depth);
            case Subscript s: return KindOf(f, s.Target, depth);
            case RangeNode r: return Combine([KindOf(f, r.Start, depth), KindOf(f, r.End, depth)]);
            case Values v: return Combine(v.Nodes.Select(n => KindOf(f, n, depth)));
            case Sequence s: return s.Nodes.Length == 0 ? DateKind.Neutral : KindOf(f, s.Nodes[^1], depth);
            case Select s: return Combine(s.Cases.Select(c => KindOf(f, c.Body, depth)).Append(KindOf(f, s.Otherwise, depth)));
            case Conditional c:
            {
                var kinds = new List<DateKind>(); Node current = c;
                while (current is Conditional branch) { kinds.Add(KindOf(f, branch.Yes, depth)); current = branch.No; }
                kinds.Add(KindOf(f, current, depth));
                return Combine(kinds);
            }
            case Binary b when b.Steps.All(s => s.Op is "+" or "-"):
            {
                var kinds = b.Steps.Select(s => KindOf(f, s.Right, depth)).Prepend(KindOf(f, b.First, depth)).ToArray();
                if (kinds.Contains(DateKind.DateTime) || kinds.Contains(DateKind.Date) && kinds.Contains(DateKind.Time)) return DateKind.DateTime;
                return kinds.Contains(DateKind.Unknown) ? DateKind.Unknown : kinds.Contains(DateKind.Date) ? DateKind.Date : kinds.Contains(DateKind.Time) ? DateKind.Time : DateKind.Other;
            }
            case Call call: return CallKind(f, call, depth);
            case CustomCall call: return call.Functions.TryGetValue(call.Name, out var function) ? function.Body.ResultKind(depth) : DateKind.Unknown;
            case FunctionBody body:
                return BindingKind(body.Result) is var result and not DateKind.Unknown ? result
                    : Combine(Walk(body.Body).OfType<Assign>().Where(a => ReferenceEquals(a.Binding, body.Result)).Select(a => KindOf(f, a.Value, depth)));
            default: return DateKind.Other;
        }
    }
    private DateKind ResultKind(int depth)
    {
        if (_resultKind < 0) _resultKind = (int)KindOf(null, _root, depth);
        return (DateKind)_resultKind;
    }
    private static DateKind Combine(IEnumerable<DateKind> kinds)
    {
        bool date = false, time = false, unknown = false, other = false;
        foreach (var kind in kinds)
            switch (kind)
            {
                case DateKind.DateTime: return DateKind.DateTime;
                case DateKind.Date: date = true; break;
                case DateKind.Time: time = true; break;
                case DateKind.Unknown: unknown = true; break;
                case DateKind.Other: other = true; break;
            }
        return unknown ? DateKind.Unknown : date ? DateKind.Date : time ? DateKind.Time : other ? DateKind.Other : DateKind.Neutral;
    }
    private static DateKind Kind(CrystalBaseType type) => type switch { CrystalBaseType.Date => DateKind.Date, CrystalBaseType.DateTime => DateKind.DateTime, CrystalBaseType.Time => DateKind.Time, _ => DateKind.Other };
    private static DateKind BindingKind(Binding binding) => binding.Type switch
    {
        "datevar" => DateKind.Date, "datetimevar" => DateKind.DateTime, "timevar" => DateKind.Time, "var" or "variant" => DateKind.Unknown, _ => DateKind.Other
    };
    private static DateKind SymbolKind(string name) => name.ToLowerInvariant() switch
    {
        "currentdate" or "today" or "printdate" or "datadate" or "filecreationdate" or "modificationdate" => DateKind.Date,
        "currentdatetime" or "now" => DateKind.DateTime,
        "currenttime" or "printtime" or "datatime" or "modificationtime" => DateKind.Time,
        "null" => DateKind.Neutral,
        var range when DateRange(range, new DateTime(2000, 1, 1)) is not null => DateKind.Date,
        var known when KnownSymbol(known) => DateKind.Other,
        _ => DateKind.Unknown
    };
    private static DateKind CallKind(Frame? f, Call call, int depth) => call.Name.ToLowerInvariant() switch
    {
        "date" or "datevalue" or "cdate" or "todate" or "dateserial" => DateKind.Date,
        "datetime" or "cdatetime" or "datetimevalue" or "dateadd" => DateKind.DateTime,
        "time" or "timevalue" or "ctime" or "timeserial" => DateKind.Time,
        "iif" => Combine(call.Args.Skip(1).Select(n => KindOf(f, n, depth))),
        "switch" => Combine(call.Args.Where((_, i) => i % 2 == 1).Select(n => KindOf(f, n, depth))),
        "choose" or "makearray" or "array" => Combine(call.Args.Skip(call.Name.Equals("choose", StringComparison.OrdinalIgnoreCase) ? 1 : 0).Select(n => KindOf(f, n, depth))),
        "minimum" or "maximum" or "previous" or "next" or "previousvalue" or "nextvalue" or "getlowerbound" or "getupperbound" =>
            call.Args.Length > 0 ? KindOf(f, call.Args[0], depth) : DateKind.Unknown,
        _ => DateKind.Other
    };

    /// <summary>Applies a reference's declared Crystal type to the value the host supplied.</summary>
    private static object? Declared(Frame f, string name, object? value)
    {
        if (value is null or DBNull || f.Context.ReferenceType(name) is not { } type) return value;
        var date = type.BaseType == CrystalBaseType.Date;
        object? Element(object? item)
        {
            if (type.IsRange && ToRange(item) is CrystalRange range) item = DateFlag(Kind(type.BaseType)) is { } bounds ? range with { StartIsDate = bounds, EndIsDate = bounds } : range;
            else if (item is CrystalRange) throw new InvalidDataException($"{name} is not a range, but a range value was supplied.");
            return date ? DateOnly(item) : item;
        }
        if (AsArray(value) is { } items)
        {
            if (!type.IsArray) throw new InvalidDataException($"{name} holds a single value, but {items.Length:N0} values were supplied.");
            return type.IsRange || date ? items.Select(Element).ToArray() : items;
        }
        return type.IsArray ? new[] { Element(value) } : Element(value);
    }
    private static object? DateOnly(object? value) => value switch
    {
        DateTime { TimeOfDay.Ticks: not 0 } at => at.Date,
        CrystalRange range => range with { Start = DateOnly(range.Start), End = DateOnly(range.End) },
        _ => value
    };

    /// <summary>A Crystal array stored in a variable: its text is bounded like a string's, so variables cannot pin unbounded memory.</summary>
    private static object?[] Stored(object?[] items)
    {
        long text = 0;
        foreach (var item in items)
            text += item switch { string s => s.Length, CrystalRange r => ((r.Start as string)?.Length ?? 0) + ((r.End as string)?.Length ?? 0), _ => 0 };
        return text > MaxArrayText ? throw new InvalidDataException("A Crystal array variable cannot hold more than 1 MB of text.") : items;
    }

    private static DateTime DayEnd(DateTime day) => day.Date == DateTime.MaxValue.Date ? DateTime.MaxValue : day.Date.AddDays(1).AddTicks(-1);
    /// <summary>True when a date-time with a time of day falls on the day of a midnight value: the one case where reading that value as a Crystal Date
    /// (its whole day) instead of a DateTime changes a comparison.</summary>
    private static bool SameDay(object? value, object? day) =>
        value is DateTime at && day is DateTime midnight && midnight.TimeOfDay == TimeSpan.Zero && at.TimeOfDay != TimeSpan.Zero && at.Date == midnight;

    private static bool Same(object? left, object? right, Sides sides)
    {
        if (left is string a && right is string b) return Equal(a, b);
        if (SameDay(left, right)) return sides.RightIsDate;
        if (SameDay(right, left)) return sides.LeftIsDate;
        return Compare(left, right) == 0;
    }
    private static bool Equal(object? left, object? right) =>
        left is string a && right is string b ? string.Equals(a.TrimEnd(' '), b.TrimEnd(' '), StringComparison.OrdinalIgnoreCase) : Compare(left, right) == 0;
    private static bool Member(object? value, object? set, Sides sides)
    {
        if (set is not CrystalRange && AsArray(set) is null && (value is CrystalRange || AsArray(value) is not null)) { (value, set) = (set, value); sides = sides.Swap(); }
        if (set is CrystalRange range) return InRange(value, range, sides.Right);
        if (AsArray(set) is not { } items) return Same(value, set, sides);
        foreach (var item in items)
            if (item is CrystalRange r ? InRange(value, r, sides.Right) : Same(value, item, sides)) return true;
        return false;
    }
    /// <summary>Crystal <c>DateTime in Date</c>: the date-time falls on that day. Any other scalar pair is a Crystal type error.</summary>
    private static bool InDay(DateTime value, DateTime day, Sides sides) =>
        sides.RightIsDate && sides.LeftKind != DateKind.Date ? value.Date == day.Date
        : throw new InvalidDataException("Crystal 'in' requires a range or an array on its right (a single date only for a date-time on its left).");
    private static CrystalRange Normalize(CrystalRange range) =>
        range.Start is not null && range.End is not null && Compare(range.Start, range.End) > 0
            ? new(range.End, range.Start, range.IncludeEnd, range.IncludeStart) { StartIsDate = range.EndIsDate, EndIsDate = range.StartIsDate } : range;

    private static bool? DateFlag(DateKind kind) => kind switch { DateKind.Date => true, DateKind.Unknown or DateKind.Neutral => null, _ => false };
    /// <summary>A range built from two expressions, typed as Crystal types it: <c>Date to Date</c> stays a Date range, <c>Date to DateTime</c> starts at the
    /// start of its day and <c>DateTime to Date</c> ends at the end of its day, whatever the inclusion.</summary>
    private static CrystalRange DateBounds(Frame f, CrystalRange range, Node? startNode, Node? endNode)
    {
        var (start, end) = (range.Start is null ? null : DateFlag(KindOf(f, startNode)), range.End is null ? null : DateFlag(KindOf(f, endNode)));
        return (start, end, range.Start, range.End) switch
        {
            (false, true, _, DateTime day) => range with { End = DayEnd(day), StartIsDate = false, EndIsDate = false },
            (true, false, DateTime day, _) => range with { Start = day.Date, StartIsDate = false, EndIsDate = false },
            _ => range with { StartIsDate = start, EndIsDate = end }
        };
    }
    /// <summary>A range stored as a Crystal type: a Date range keeps whole days, a DateTime range takes a Date range's bounds as Crystal converts them.</summary>
    private static object? Retype(object? value, bool? date) => value is not CrystalRange range || date is null ? value
        : date.Value ? range with { StartIsDate = range.StartIsDate ?? true, EndIsDate = range.EndIsDate ?? true }
        : range with
        {
            Start = range is { StartIsDate: true, Start: DateTime start } ? range.IncludeStart ? start.Date : DayEnd(start) : range.Start,
            End = range is { EndIsDate: true, End: DateTime end } ? range.IncludeEnd ? DayEnd(end) : end.Date : range.End,
            StartIsDate = range.StartIsDate is null ? null : false, EndIsDate = range.EndIsDate is null ? null : false
        };

    /// <summary>Compares a value with a bound: exactly, or for a Date bound at the start or the end of its day.</summary>
    private static int Edge(object value, object bound, bool date, bool endOfDay) =>
        date && value is DateTime at && bound is DateTime day ? at.CompareTo(endOfDay ? DayEnd(day) : day.Date) : Compare(value, bound);
    /// <summary>The value against a range read with the given bound types. A Date range converts as Crystal's RangeValue.coerceToDateTime: an inclusive
    /// start or exclusive end at the start of its day, an exclusive start or inclusive end at its end. In a mixed range a Date start is the start of its day and a
    /// Date end the end of its day.</summary>
    private static (int Start, int End) Edges(object value, CrystalRange range, bool startDate, bool endDate)
    {
        var whole = (range.Start is null || startDate) && (range.End is null || endDate);
        return (range.Start is null ? 1 : Edge(value, range.Start, startDate, whole && !range.IncludeStart),
            range.End is null ? -1 : Edge(value, range.End, endDate, !whole || range.IncludeEnd));
    }
    /// <summary>Answers a range comparison under every bound typing the range allows; when a bound's type is unknown the readings must agree.</summary>
    private static bool Decide(object value, CrystalRange range, Node? node, string relation, Func<string, CrystalRange, (int Start, int End), bool> test)
    {
        if (!SameDay(value, range.Start) && !SameDay(value, range.End)) return test(relation, range, Edges(value, range, false, false));
        bool? answer = null;
        foreach (var start in range.StartIsDate is { } s ? [s] : new[] { false, true })
            foreach (var end in range.EndIsDate is { } e ? [e] : new[] { false, true })
                if (test(relation, range, Edges(value, range, start, end)) is var result && answer != result)
                    answer = answer is null ? result : throw new InvalidDataException($"Crystal compares a date-time with a Date by whole days but with a DateTime exactly, and whether the bounds of {Describe(node)} are Dates or DateTimes is not known here (declare the types through CrystalFormulaContext.ReferenceType).");
        return answer!.Value;
    }
    private static bool InRange(object? value, CrystalRange range, Node? node)
    {
        if (value is null or DBNull) return false;
        range = Normalize(range);
        return Decide(value, range, node, "in", static (_, range, edges) =>
            !(range.Start is not null && (edges.Start < 0 || edges.Start == 0 && !range.IncludeStart)) && !(range.End is not null && (edges.End > 0 || edges.End == 0 && !range.IncludeEnd)));
    }
    private static bool RangeOrder(string op, object? left, object? right, Sides sides)
    {
        if (left is CrystalRange && right is CrystalRange) throw new NotSupportedException("Comparing two Crystal ranges is not implemented.");
        var valueFirst = right is CrystalRange;
        var range = Normalize((CrystalRange)(valueFirst ? right : left)!);
        var value = (valueFirst ? left : right)!;
        var relation = (op, valueFirst) switch { ("<", true) or (">", false) => "before", ("<=", true) or (">=", false) => "notafter", (">", true) or ("<", false) => "after", _ => "notbefore" };
        return Decide(value, range, valueFirst ? sides.Right : sides.Left, relation, static (relation, range, edges) => relation switch
        {
            "before" => range.Start is not null && (edges.Start < 0 || edges.Start == 0 && !range.IncludeStart),
            "after" => range.End is not null && (edges.End > 0 || edges.End == 0 && !range.IncludeEnd),
            "notafter" => range.End is null || edges.End < 0 || edges.End == 0 && range.IncludeEnd,
            _ => range.Start is null || edges.Start > 0 || edges.Start == 0 && range.IncludeStart
        });
    }
    /// <summary>A scalar Date compared with a date-time on its day reads as the whole day, a Crystal range of one value.</summary>
    private static CrystalRange Day(Frame f, object? value, Node? node)
    {
        var date = DateFlag(KindOf(f, node));
        return new(value, value) { StartIsDate = date, EndIsDate = date };
    }
}
