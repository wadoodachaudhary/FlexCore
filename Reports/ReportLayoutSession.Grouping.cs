using System.Data;
using System.Globalization;

namespace Fx.ControlKit.Reports;

// Crystal group conditions and group ordering: a date, date-time or time group changes per period (ConditionCrystalValueComparator), a
// specified-order group per named selection (GroupOptions' group-order formula), and a group sort by summary reorders the group instances
// inside their parent, keeping the Top/Bottom N and discarding or merging the rest (TopNGroupInfo).
public sealed partial class ReportLayoutSession
{
    private static readonly string[] DateConditionNames = ["daily", "weekly", "biweekly", "semimonthly", "monthly", "quarterly", "semiannually", "annually"];
    private static readonly string[] TimeConditionNames = ["by second", "by minute", "by hour", "by AMPM"];
    private static readonly string[] BooleanConditionNames = ["any change", "change to Yes", "change to No", "every Yes", "every No", "next is Yes", "next is No"];
    private sealed record SpecifiedKey(int Index, object? Value);
    private sealed record OthersKey(string Name);
    private readonly Dictionary<(int Level, DataRow Row), int> _specified = new();
    private readonly Dictionary<int, Dictionary<DataRow, object?>> _others = new();

    /// <summary>Whether a Crystal group condition name ("monthly", "by hour", "change to Yes", ...) names condition <paramref name="kind"/> of a group on a
    /// value of <paramref name="type"/>; when the type is unknown, any reading of the kind counts.</summary>
    internal static bool ConditionNamed(int kind, string name, CrystalBaseType? type = null)
    {
        bool Is(string[] names, int index) => index >= 0 && index < names.Length && string.Equals(names[index], name.Trim(), StringComparison.OrdinalIgnoreCase);
        return type switch
        {
            CrystalBaseType.Date => Is(DateConditionNames, kind),
            CrystalBaseType.DateTime => Is(DateConditionNames, kind) || Is(TimeConditionNames, kind - DateConditionNames.Length),
            CrystalBaseType.Time => Is(TimeConditionNames, kind),
            CrystalBaseType.Boolean => Is(BooleanConditionNames, kind),
            null => Is(DateConditionNames, kind) || Is(TimeConditionNames, kind - DateConditionNames.Length) || Is(TimeConditionNames, kind) || Is(BooleanConditionNames, kind),
            _ => kind == 0 && Is(BooleanConditionNames, 0)
        };
    }

    /// <summary>Whether a text is a Crystal group condition name.</summary>
    internal static bool IsConditionName(string name) =>
        DateConditionNames.Concat(TimeConditionNames).Concat(BooleanConditionNames).Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The condition kind a Crystal condition name gives a group on a value of <paramref name="type"/>: 0-7 for a date period, the time period
    /// (0-3 on a time, 8-11 on a date-time); null for a Boolean change or a name that does not fit the type.</summary>
    internal static int? ConditionKindNamed(string name, CrystalBaseType? type)
    {
        int Index(string[] names) => Array.FindIndex(names, n => n.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (Index(DateConditionNames) is >= 0 and var date && type is null or CrystalBaseType.Date or CrystalBaseType.DateTime) return date;
        return Index(TimeConditionNames) is >= 0 and var time ? type switch { CrystalBaseType.Time => time, CrystalBaseType.DateTime => DateConditionNames.Length + time, _ => null } : null;
    }

    private ReportLayoutGroupOptions? Options(int level) => _layout.GroupOptions.GetValueOrDefault(level);

    // The value a group compares: the period of a date, date-time or time condition, the named group of a specified order, or the value itself.
    private object? GroupKey(int level, int row)
    {
        if (row >= 0 && row < _rows.Length && _others.TryGetValue(level, out var others) && others.ContainsKey(_rows[row])) return new OthersKey(_layout.GroupSorts[level].OthersName);
        var options = Options(level);
        if (options?.SpecifiedOrder == true)
        {
            var index = SpecifiedIndex(level, row);
            return new SpecifiedKey(index, index < options.SpecifiedGroups.Count || options.UnspecifiedValues != "separateValues" ? null : Period(Value(_layout.Document.Groups[level].Condition, row), options, false));
        }
        return Period(Value(_layout.Document.Groups[level].Condition, row), options, false);
    }

    // Before a group sort merges groups into "Others", the merged records keep their own groups below that level.
    private bool SameGroup(int left, int right, int level)
    {
        for (var index = 0; index <= level; index++)
        {
            if (!Equals(GroupKey(index, left), GroupKey(index, right))) return false;
            if (index < level && _others.TryGetValue(index, out var others) && left >= 0 && left < _rows.Length && right >= 0 && right < _rows.Length
                && others.TryGetValue(_rows[left], out var leftKey) && others.TryGetValue(_rows[right], out var rightKey) && !Equals(leftKey, rightKey)) return false;
        }
        return true;
    }

    // Crystal GroupName (FetchGroupNameHelper): the Others name of a Top/Bottom N group, else the group-name formula on the group's first record
    // (a null result names it ""), else the specified group's name, the first (or last) date of the period, or the condition value.
    // A date period names its group by the date alone (FetchGroupNameHelper formats it dateOnly).
    private object? GroupNameValue(int level, int row)
    {
        if (level < 0 || level >= _layout.Document.Groups.Count) throw new InvalidDataException("GroupName requires a field the report groups on.");
        var options = Options(level);
        object? Named(object? value) => value is DateTime date && (options?.ConditionKind ?? 0) < DateConditionNames.Length ? date.ToString("d", CultureInfo.CurrentCulture) : value;
        var key = GroupKey(level, row);
        if (key is OthersKey others) return others.Name;
        if (options is { NameFormulaError.Length: > 0 })
            throw new InvalidDataException($"Group {_layout.Document.Groups[level].Condition}: the group-name formula cannot be compiled: {options.NameFormulaError}");
        if (options?.NameFormula is { } formula)
        {
            if (!_namingLevels.Add(level)) throw new InvalidDataException($"Group {_layout.Document.Groups[level].Condition}: the group-name formula reads its own group name.");
            try { return formula.Evaluate(Context(GroupStart(level, row))) ?? ""; }
            finally { _namingLevels.Remove(level); }
        }
        switch (key)
        {
            case SpecifiedKey specified:
                return specified.Index < options!.SpecifiedGroups.Count ? options.SpecifiedGroups[specified.Index].Name
                    : options.UnspecifiedValues == "separateValues" ? Named(Period(Value(_layout.Document.Groups[level].Condition, row), options, options.ShowLastDateInPeriod)) : options.OthersName;
        }
        return Named(Period(Value(_layout.Document.Groups[level].Condition, row), options, options?.ShowLastDateInPeriod ?? false));
    }

    private readonly HashSet<int> _namingLevels = [];

    // The first record of the group instance holding a record, by level (recomputed when the record order changes).
    private readonly Dictionary<int, (DataRow[] Rows, int[] Starts)> _groupStarts = new();
    private int GroupStart(int level, int row)
    {
        if (row < 0 || row >= _rows.Length) return row;
        if (!_groupStarts.TryGetValue(level, out var cached) || !ReferenceEquals(cached.Rows, _rows))
        {
            var starts = new int[_rows.Length];
            for (var index = 0; index < starts.Length; index++) starts[index] = index > 0 && SameGroup(index - 1, index, level) ? starts[index - 1] : index;
            _groupStarts[level] = cached = (_rows, starts);
        }
        return cached.Starts[row];
    }

    private int GroupLevel(string field) => _layout.Document.Groups.FindIndex(g => g.Condition.Trim().Equals(field.Trim(), StringComparison.OrdinalIgnoreCase));

    // Named groups first, in order; then the records no named group selects: merged, discarded or in their own groups (after the named ones).
    private int SpecifiedIndex(int level, int row)
    {
        if (row < 0 || row >= _rows.Length) return int.MaxValue;
        if (_specified.TryGetValue((level, _rows[row]), out var cached)) return cached;
        var options = Options(level)!;
        var index = options.SpecifiedGroups.Count;
        if (Value(_layout.Document.Groups[level].Condition, row) is not null and not DBNull)
            for (var named = 0; named < options.SpecifiedGroups.Count; named++)
                if (CrystalFormula.Boolean(options.SpecifiedGroups[named].Selection.Evaluate(Context(row)))) { index = named; break; }
        return _specified[(level, _rows[row])] = index;
    }

    // The period a value falls in, as its first (or last) value; condition 0 on a date-time groups by day.
    private static object? Period(object? value, ReportLayoutGroupOptions? options, bool last)
    {
        var kind = options?.ConditionKind ?? 0;
        switch (value)
        {
            case DateTime at when kind < DateConditionNames.Length:
                var start = PeriodStart(at.Date, kind);
                return last ? PeriodEnd(start, kind) : start;
            case DateTime at:
                return at.Date + TimePeriod(at.TimeOfDay, kind - DateConditionNames.Length, last);
            case TimeSpan time when kind < TimeConditionNames.Length:
                return TimePeriod(time, kind, last);
            case bool when kind != 0:
                throw new NotSupportedException($"Boolean group condition '{BooleanConditionNames[Math.Clamp(kind, 0, BooleanConditionNames.Length - 1)]}' is not implemented.");
            case null or DBNull:
                return value;
            default:
                return kind == 0 ? value : throw new InvalidDataException($"Group condition {kind} applies only to date, time and Boolean values, not to '{value}'.");
        }
    }

    private static DateTime PeriodStart(DateTime date, int kind) => kind switch
    {
        0 => date,
        1 => date.AddDays(-(int)date.DayOfWeek),
        // DateTimeUtil.DateToBiweekDay: fortnights counted from the Crystal day number (1899-12-30 is 2415018).
        2 => date.AddDays(-(int)(((long)date.ToOADate() + 2415018 + 2) % 14)),
        3 => new DateTime(date.Year, date.Month, date.Day <= 15 ? 1 : 16),
        4 => new DateTime(date.Year, date.Month, 1),
        5 => new DateTime(date.Year, (date.Month - 1) / 3 * 3 + 1, 1),
        6 => new DateTime(date.Year, date.Month <= 6 ? 1 : 7, 1),
        _ => new DateTime(date.Year, 1, 1)
    };

    private static DateTime PeriodEnd(DateTime start, int kind) => kind switch
    {
        0 => start,
        1 => start.AddDays(6),
        2 => start.AddDays(13),
        3 => start.Day == 1 ? start.AddDays(14) : start.AddMonths(1).AddDays(-16),
        4 => start.AddMonths(1).AddDays(-1),
        5 => start.AddMonths(3).AddDays(-1),
        6 => start.AddMonths(6).AddDays(-1),
        _ => start.AddYears(1).AddDays(-1)
    };

    private static TimeSpan TimePeriod(TimeSpan time, int kind, bool last)
    {
        var (unit, start) = kind switch
        {
            0 => (TimeSpan.FromSeconds(1), new TimeSpan(time.Hours, time.Minutes, time.Seconds)),
            1 => (TimeSpan.FromMinutes(1), new TimeSpan(time.Hours, time.Minutes, 0)),
            2 => (TimeSpan.FromHours(1), new TimeSpan(time.Hours, 0, 0)),
            _ => (TimeSpan.FromHours(12), new TimeSpan(time.Hours < 12 ? 0 : 12, 0, 0))
        };
        return last ? start + unit - TimeSpan.FromTicks(1) : start;
    }

    // Record sort keys for one group level: the specified order (then the value for separate unspecified values), else the value.
    private IEnumerable<Func<int, object?>> GroupSortKeys(int level)
    {
        var condition = _layout.Document.Groups[level].Condition;
        if (Options(level) is not { SpecifiedOrder: true } options)
        {
            yield return row => Value(condition, row);
            yield break;
        }
        yield return row => SpecifiedIndex(level, row) is var index && index < options.SpecifiedGroups.Count ? index : options.SpecifiedGroups.Count;
        if (options.UnspecifiedValues == "separateValues") yield return row => SpecifiedIndex(level, row) < options.SpecifiedGroups.Count ? null : Value(condition, row);
    }

    // Specified order with "discard" drops the records no named group selects (GroupOptions.a(IRow)).
    private void DiscardUnspecified()
    {
        for (var level = 0; level < _layout.Document.Groups.Count; level++)
        {
            if (Options(level) is not { SpecifiedOrder: true, UnspecifiedValues: "discardValues" } options) continue;
            _rows = _rows.Where((_, row) => SpecifiedIndex(level, row) < options.SpecifiedGroups.Count).ToArray();
            ClearValues();
        }
    }

    // Group sorts, outermost level first: each parent's group instances are ordered by their summary; Top/Bottom N keeps the first N
    // (and ties when asked) and discards the rest or merges them into one Others group at the end. Returns the discarded records.
    private HashSet<DataRow> ApplyGroupSorts()
    {
        var discarded = new HashSet<DataRow>();
        for (var level = 0; level < _layout.Document.Groups.Count; level++)
        {
            if (!_layout.GroupSorts.TryGetValue(level, out var sort)) continue;
            if (IsHierarchical(level)) throw new NotSupportedException($"Group {_layout.Document.Groups[level].Condition}: sorting hierarchical groups by a summary is not implemented.");
            if (sort.TopBottomN.EndsWith("Percentage", StringComparison.Ordinal))
                throw new NotSupportedException($"Group {_layout.Document.Groups[level].Condition}: Top/Bottom N percentage group sorting is not implemented.");
            var ordered = new List<DataRow>(_rows.Length);
            var others = new Dictionary<DataRow, object?>();
            foreach (var (first, last) in Runs(0, _rows.Length, level - 1))
            {
                var instances = Runs(first, last, level);
                var values = instances.Select(instance => SummaryCore(sort.Summary, instance.Start, null)).ToArray();
                var order = Enumerable.Range(0, instances.Count).ToList();
                order.Sort((left, right) =>
                {
                    var compare = CrystalFormula.Compare(values[left], values[right]);
                    return compare != 0 ? sort.Descending ? -compare : compare : left.CompareTo(right);
                });
                var keep = order.Count;
                if (sort.TopBottomN.Length > 0)
                {
                    var count = sort.CountFormula is { } formula ? (int)Math.Clamp(CrystalFormula.Number(formula.Evaluate(Context(first))), 0, int.MaxValue) : sort.Count;
                    keep = Math.Min(Math.Max(count, 0), order.Count);
                    while (sort.WithTies && keep > 0 && keep < order.Count && CrystalFormula.Compare(values[order[keep]], values[order[keep - 1]]) == 0) keep++;
                }
                foreach (var instance in order.Take(keep))
                    for (var row = instances[instance].Start; row < instances[instance].End; row++) ordered.Add(_rows[row]);
                foreach (var instance in order.Skip(keep))
                    for (var row = instances[instance].Start; row < instances[instance].End; row++)
                    {
                        ordered.Add(_rows[row]);
                        if (sort.DiscardOthers) discarded.Add(_rows[row]);
                        else others[_rows[row]] = GroupKey(level, row);
                    }
            }
            _rows = ordered.ToArray(); ClearValues();
            if (others.Count > 0) { _others[level] = others; ClearValues(); }
        }
        return discarded;
    }
}
