namespace Fx.ControlKit.Reports;

/// <summary>Starting values the page estimator uses before a report has shown its own: how much sections of each
/// kind grow past their designed height, how often a section with a conditional new-page format starts a page, and
/// how far the estimate has been from the real count at each stage. Fitted on full runs of sample reports.</summary>
public sealed partial record ReportPageEstimateWeights
{
    /// <summary>Printed height over designed height by section class: plain, growing, suppress, crosstab, subreport.</summary>
    public IReadOnlyDictionary<string, double> Growth { get; init; } = new Dictionary<string, double>();
    /// <summary>Share of a section's instances that start a page when it has a conditional new-page format and the
    /// input does not say which instances do.</summary>
    public double ConditionalBreakRate { get; init; }
    /// <summary>How far outside the span of the two estimates the real count has been, as a share, keyed by stage
    /// ("0", "1-9", "10-49", "50-99", "100-149", "150+"), whether the two estimates agree ("|agree", "|differ") and
    /// whether every printed section keeps its designed height with no formula page break ("|plain", "|complex").</summary>
    public IReadOnlyDictionary<string, double> Range { get; init; } = new Dictionary<string, double>();

    /// <summary>No starting values: designed heights, no assumed page breaks, and a 10% range (50% while the two
    /// estimates differ).</summary>
    public static ReportPageEstimateWeights None { get; } = new();

    /// <summary>The values fitted on the sample runs (see <c>ReportPageEstimateWeights.Default.cs</c>).</summary>
    public static ReportPageEstimateWeights Default => Fitted;
}

/// <summary>A page-count estimate at one point of a run.</summary>
/// <param name="PagesSeen">Pages laid out when the estimate was made.</param>
/// <param name="Dry">The count from walking the remaining section instances at their learned heights.</param>
/// <param name="Ratio">The count from scaling the pages so far by the share of the report still to print.</param>
/// <param name="Agree">The two counts are within 3% of each other.</param>
/// <param name="Low">Lower end of the range the real count is expected in.</param>
/// <param name="High">Upper end of that range.</param>
public sealed record ReportPageEstimate(int PagesSeen, double Dry, double Ratio, bool Agree, int Low, int High)
{
    /// <summary>Some listed sections may not print (their suppress formula is decided while printing) and instances of
    /// them are still to come: the range covers none of them printing through all of them printing.</summary>
    public bool Unsettled { get; init; }

    /// <summary>The estimated page count.</summary>
    public int Pages => (int)Math.Max(Math.Max(1, PagesSeen), Math.Round(Dry));
}

/// <summary>Estimates how many pages a report will have, before layout and while pages arrive. It reads the estimate
/// input (every section instance in print order) and the bands of the pages laid out so far; it formats nothing.
/// A page's bands carry the section's name only, so sections that share a name are learned together.
/// Two estimates check each other: a walk over the remaining instances that breaks pages where a section does not
/// fit or a rule starts a page, and the pages so far scaled by the share of the report's height still to print.
/// <para>One estimator serves one run and is not thread-safe: <see cref="Estimate"/> updates its own counts. Each
/// estimate walks the instances still to print, so call it at checkpoints (for example pages 1, 10, 25, 50, 100,
/// 150 and then at growing intervals), not for every page.</para></summary>
public sealed class ReportPageEstimator
{
    private const double Shrink = 4.0;        // a designed height counts as this many observed instances
    private const double BreakShrink = 2.0;   // a learned break rate is damped as if this many instances had not broken
    private const byte Idle = ReportPageEstimateInput.Suppressed | ReportPageEstimateInput.Blank;   // listed, prints nothing

    private readonly ReportPageEstimateInput _input;
    private readonly ReportPageEstimateWeights _weights;
    private readonly ReportPageEstimateSection[] _sections;
    private readonly int[] _order;
    private readonly int[] _rows;
    private readonly byte[] _flags;
    private readonly bool _instanceFlags;
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);
    private readonly int[] _group;             // per section: the first section with its name
    private readonly int[] _learnedCount;      // per name group, as of the last Learn
    private readonly double[] _learnedExpected;
    private readonly double _agreeWithin;
    private readonly HashSet<string> _furniture;
    private readonly double[] _flowDesign;
    private readonly double[] _expected;       // per instance: its expected flow height
    private readonly int[] _totals;
    private readonly double[] _totalExpected;
    private readonly int _across;
    private readonly int _recordsPerPage;
    private readonly double _designCap;
    private readonly bool _plain;

    // What the pages seen so far showed.
    private readonly double[] _printedHeight;
    private readonly int[] _prints;
    private readonly int[] _breaksBefore;
    private readonly int[] _consumed;
    private readonly double[] _consumedExpected;
    private int _consumedThrough;
    private double _furnitureHeight;
    private int _pages;
    private double _previousRoom;
    private int _previousStart = -1;
    private int _runStart = -1;
    private double _runPrinted;

    public ReportPageEstimator(ReportPageEstimateInput input, ReportPageEstimateWeights? weights = null, ReportPageEstimateOptions? options = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _weights = weights ?? ReportPageEstimateWeights.None;
        _agreeWithin = Math.Max(0, (options ?? ReportPageEstimateOptions.Default).AgreeWithin);
        _sections = input.Sections.ToArray();
        _order = input.Order ?? throw new ArgumentException("The estimate input has no order.", nameof(input));
        _rows = input.Rows ?? throw new ArgumentException("The estimate input has no rows.", nameof(input));
        if (input.ContentHeightTwips <= 0) throw new ArgumentException("The estimate input's page has no height.", nameof(input));
        if (_rows.Length != _order.Length) throw new ArgumentException("The estimate input's rows and order differ in length.", nameof(input));
        foreach (var section in _order)
            if ((uint)section >= (uint)_sections.Length) throw new ArgumentException("The estimate input's order names a section it does not list.", nameof(input));
        _across = Math.Max(1, input.ColumnsAcross);
        _recordsPerPage = Math.Max(0, input.VisibleRecordsPerPage);
        _designCap = Math.Max(1, input.ContentHeightTwips - input.PageHeaderTwips - input.PageFooterTwips);
        _furniture = new HashSet<string>(input.FurnitureSections, StringComparer.Ordinal);
        for (var index = 0; index < _sections.Length; index++) _byName.TryAdd(_sections[index].Name, index);
        _group = _sections.Select(section => _byName[section.Name]).ToArray();
        _learnedCount = new int[_sections.Length];
        _learnedExpected = new double[_sections.Length];
        _instanceFlags = input.Flags.Length == _order.Length && _order.Length > 0;
        if (_instanceFlags) _flags = input.Flags;
        else
        {
            // Only the sections' own formats are known.
            var own = _sections.Select(section => (byte)((section.NewPageBefore ? ReportPageEstimateInput.NewPageBefore : 0)
                | (section.NewPageAfter ? ReportPageEstimateInput.NewPageAfter : 0)
                | (section.PrintAtBottomOfPage ? ReportPageEstimateInput.PrintAtBottom : 0)
                | (section.Underlay ? ReportPageEstimateInput.Underlay : 0))).ToArray();
            _flags = new byte[_order.Length];
            for (var j = 0; j < _order.Length; j++) _flags[j] = own[_order[j]];
        }
        _flowDesign = _sections.Select(section => Flow(section, section.HeightTwips)).ToArray();
        // Each instance's expected flow height: measured for its record when the input has it, else the designed one.
        var measured = input.Heights.Length == _order.Length && _order.Length > 0;
        _expected = new double[_order.Length];
        for (var j = 0; j < _order.Length; j++)
            _expected[j] = measured ? Flow(_sections[_order[j]], input.Heights[j]) : _flowDesign[_order[j]];
        // Instances that print: a suppressed one is listed for its formulas only, a blank one prints nothing.
        _totals = new int[_sections.Length];
        _totalExpected = new double[_sections.Length];
        for (var j = 0; j < _order.Length; j++)
            if ((_flags[j] & Idle) == 0) { _totals[_order[j]]++; _totalExpected[_order[j]] += _expected[j]; }
        _plain = true;
        for (var index = 0; index < _sections.Length; index++)
            if (_totals[index] > 0 && (SectionClass(_sections[index]) != "plain" || HasBreakCondition(_sections[index]))) _plain = false;
        _printedHeight = new double[_sections.Length];
        _prints = new int[_sections.Length];
        _breaksBefore = new int[_sections.Length];
        _consumed = new int[_sections.Length];
        _consumedExpected = new double[_sections.Length];
    }

    /// <summary>Pages observed so far.</summary>
    public int PagesSeen => _pages;

    /// <summary>True when every section the report prints keeps its designed height and no formula decides a page
    /// break: the estimate before layout is then close to exact.</summary>
    public bool IsPlain => _plain;

    // The height a section takes out of the page's flow: columns share it.
    private double Flow(ReportPageEstimateSection section, double height) => section.InColumns ? height / _across : height;

    private static string SectionClass(ReportPageEstimateSection section) =>
        section.Objects.Contains("Subreport:", StringComparison.Ordinal) ? "subreport"
        : section.Objects.Contains("CrossTab:", StringComparison.Ordinal) ? "crosstab"
        : section.SuppressIfBlank || section.Conditions.Any(condition => condition is "EnableSuppress" or "Area.EnableSuppress") ? "suppress"
        : section.GrowingObjects > 0 ? "growing" : "plain";

    private static bool HasBreakCondition(ReportPageEstimateSection section)
    {
        foreach (var condition in section.Conditions)
        {
            var name = condition.StartsWith("Area.", StringComparison.Ordinal) ? condition[5..] : condition;
            if (name is "EnableNewPageAfter" or "EnableNewPageBefore") return true;
        }
        return false;
    }

    /// <summary>Adds one laid-out page, in page order. <paramref name="start"/> is the index, in the input's order,
    /// of the section instance the page started with. Observe one snapshot per page of the report: a page that
    /// prints as several sheets side by side counts once.</summary>
    public void Observe(ReportPageSnapshot page, int start)
    {
        ArgumentNullException.ThrowIfNull(page);
        Observe(page.Bands.Select(band => (band.Name, band.TopTwips, band.HeightTwips)), start);
    }

    /// <summary>Adds one laid-out page from its bands (section name, top and height in twips), in page order.</summary>
    public void Observe(IEnumerable<(string Name, int Top, int Height)> bands, int start)
    {
        ArgumentNullException.ThrowIfNull(bands);
        start = Math.Clamp(start, 0, _order.Length);
        if (start < _previousStart) start = _previousStart;
        int? top = null, bottom = null;
        double furniture = 0, ofStart = 0;
        var atStart = start < _order.Length ? _order[start] : -1;
        foreach (var (name, bandTop, height) in bands)
        {
            if (!_byName.TryGetValue(name, out var index))
            {
                if (_furniture.Contains(name)) furniture += height;
                continue;
            }
            _printedHeight[index] += height; _prints[index]++;
            if (index == atStart) ofStart += height;
            if (_sections[index].Underlay) continue;
            if (top is null || bandTop < top) top = bandTop;
            if (bottom is null || bandTop + height > bottom) bottom = bandTop + height;
        }
        var extent = top is null ? 0 : bottom!.Value - top.Value;
        // The page before this one ended with room for the instance this page starts with: a rule ended it. Rules the
        // walk applies itself (a new page known for the instance, keep group together, records per page) are left to it.
        if (_pages > 0 && atStart >= 0 && start != _previousStart)
        {
            var section = _sections[atStart];
            var known = (_flags[start] & ReportPageEstimateInput.NewPageBefore) != 0 || (_flags[start - 1] & ReportPageEstimateInput.NewPageAfter) != 0;
            if (_previousRoom > _expected[start] + 20 && !known && !section.KeepGroupTogether
                && !(_recordsPerPage > 0 && section.Kind == "Detail"))
                _breaksBefore[atStart]++;
        }
        // An instance that pages keep starting with is still printing: what it has printed on those pages.
        if (start == _runStart) _runPrinted += ofStart;
        else { _runStart = start; _runPrinted = ofStart; }
        _furnitureHeight += furniture;
        _previousRoom = _input.ContentHeightTwips - furniture - extent;
        _previousStart = start;
        _pages++;
    }

    private void ConsumeThrough(int position)
    {
        if (position < _consumedThrough)
        {
            Array.Clear(_consumed);
            Array.Clear(_consumedExpected);
            _consumedThrough = 0;
        }
        for (; _consumedThrough < position; _consumedThrough++)
            if ((_flags[_consumedThrough] & Idle) == 0)
            {
                _consumed[_order[_consumedThrough]]++;
                _consumedExpected[_order[_consumedThrough]] += _expected[_consumedThrough];
            }
    }

    // Per section: a factor and an offset that turn an instance's expected height into its learned one
    // (expected * factor + offset), and a break rate; and the page's body room. The factor is the section's printed
    // height over its expected height, damped toward the starting weight as if Shrink instances had printed at that
    // weight. A section expected to take no height learns an offset instead.
    private (Learned Heights, double[] Rates, double Cap) Learn()
    {
        var factors = new double[_sections.Length];
        var offsets = new double[_sections.Length];
        var rates = new double[_sections.Length];
        Array.Clear(_learnedCount);
        Array.Clear(_learnedExpected);
        for (var index = 0; index < _sections.Length; index++)
        {
            _learnedCount[_group[index]] += _consumed[index];
            _learnedExpected[_group[index]] += _consumedExpected[index];
        }
        for (var index = 0; index < _sections.Length; index++)
        {
            var section = _sections[index];
            var weight = _weights.Growth.GetValueOrDefault(SectionClass(section), 1.0);
            var group = _group[index];
            if (_pages == 0 || _learnedCount[group] == 0)
            {
                factors[index] = weight;
                // With the formats known per instance, a formula's page breaks are already in the list.
                rates[index] = HasBreakCondition(section) && !_instanceFlags ? _weights.ConditionalBreakRate : 0;
                continue;
            }
            // A header repeated on later pages prints more often than it is listed: its height is per print.
            double n = _learnedCount[group];
            var divisor = section.RepeatHeader && _prints[group] > 0 ? _prints[group] : _learnedCount[group];
            var printed = Flow(section, _printedHeight[group]) / divisor * n;
            var mean = _learnedExpected[group] / n;
            if (mean > 0) factors[index] = (printed + weight * Shrink * mean) / (_learnedExpected[group] + Shrink * mean);
            else offsets[index] = printed / (n + Shrink);
            rates[index] = _breaksBefore[index] / (_consumed[index] + BreakShrink);
        }
        var cap = _pages > 0 ? Math.Max(1.0, _input.ContentHeightTwips - _furnitureHeight / _pages) : _designCap;
        return (new(factors, offsets), rates, cap);
    }

    private readonly record struct Learned(double[] Factors, double[] Offsets);

    // Sections whose listed instances may not print, with instances still to come: the factors if none of the rest
    // print and if all of them print. Null when every listed instance is expected to print.
    private (Learned Low, Learned High)? Unsettled(Learned learned)
    {
        double[]? low = null, high = null;
        for (var index = 0; index < _sections.Length; index++)
        {
            var section = _sections[index];
            if (section.Underlay || section.RepeatHeader || _totals[index] - _consumed[index] <= 0) continue;
            var group = _group[index];
            double n = _learnedCount[group];
            var weight = _weights.Growth.GetValueOrDefault(SectionClass(section), 1.0);
            double lowFactor, highFactor = learned.Factors[index];
            if (_pages == 0 || n == 0)
            {
                if (!section.SuppressAtPrintTime) continue;
                lowFactor = 0;
            }
            else
            {
                var rate = Math.Min(1.0, _prints[group] / n);
                if (rate < 1.0)
                {
                    // Some did not print: the rest may all vanish, or all print at the height the printed ones had.
                    lowFactor = 0;
                    highFactor = Math.Max(Math.Max(learned.Factors[index], weight), rate > 0 ? learned.Factors[index] / rate : weight);
                }
                else if (section.SuppressAtPrintTime)
                    // None has vanished in n instances: fewer than about 3 in n are expected to.
                    lowFactor = learned.Factors[index] * (1 - 3.0 / (n + 3.0));
                else continue;
            }
            low ??= (double[])learned.Factors.Clone();
            high ??= (double[])learned.Factors.Clone();
            low[index] = lowFactor; high[index] = highFactor;
        }
        return low is null ? null : (new(low, learned.Offsets), new(high!, learned.Offsets));
    }

    private double Height(Learned learned, int j)
    {
        var s = _order[j];
        return _expected[j] * learned.Factors[s] + learned.Offsets[s];
    }

    /// <summary>The estimate after the pages observed so far. <paramref name="position"/> is the index, in the
    /// input's order, of the section instance the next page starts with (0 before any page).</summary>
    public ReportPageEstimate Estimate(int position)
    {
        position = Math.Clamp(position, 0, _order.Length);
        if (_pages == 0) position = 0;
        else if (position < _previousStart) position = _previousStart;
        ConsumeThrough(position);
        var (heights, rates, cap) = Learn();
        var dry = Dry(position, heights, rates, cap);
        var ratio = Ratio(position, heights, cap);
        var agree = Math.Abs(dry - ratio) <= _agreeWithin * Math.Max(dry, ratio);
        var spread = _weights.Range.GetValueOrDefault(Stage(_pages) + (agree ? "|agree" : "|differ") + (_plain ? "|plain" : "|complex"), agree ? 0.10 : 0.5);
        var low = Math.Max(Math.Max(1.0, _pages), Math.Min(dry, ratio) * (1 - spread));
        var high = Math.Max(dry, ratio) * (1 + spread);
        var unsettled = Unsettled(heights);
        if (unsettled is { } bounds)
        {
            low = Math.Min(low, Math.Max(Math.Max(1.0, _pages), Dry(position, bounds.Low, rates, cap) * (1 - spread)));
            high = Math.Max(high, Dry(position, bounds.High, rates, cap) * (1 + spread));
        }
        return new(_pages, dry, ratio, agree, (int)Math.Floor(low), (int)Math.Ceiling(high)) { Unsettled = unsettled is not null };
    }

    private static string Stage(int pages) => pages < 1 ? "0" : pages < 10 ? "1-9" : pages < 50 ? "10-49" : pages < 100 ? "50-99" : pages < 150 ? "100-149" : "150+";

    private double Ratio(int position, Learned heights, double cap)
    {
        double total = 0, consumed = 0;
        for (var index = 0; index < _sections.Length; index++)
        {
            total += _totalExpected[index] * heights.Factors[index] + _totals[index] * heights.Offsets[index];
            consumed += _consumedExpected[index] * heights.Factors[index] + _consumed[index] * heights.Offsets[index];
        }
        if (_pages == 0 || position == 0) return Math.Max(Math.Max(1.0, _pages), total / cap);
        return consumed <= 0 ? _pages : Math.Max(_pages, _pages * total / consumed);
    }

    private double Dry(int position, Learned heights, double[] rates, double cap)
    {
        const byte Hidden = ReportPageEstimateInput.Underlay | Idle;
        var count = _order.Length;
        if (position >= count && _pages > 0) return _pages;
        // Down-then-across columns are walked column by column; across-then-down (labels) flows at height / columns.
        var columns = _across > 1 && !_input.ColumnsAcrossThenDown ? _across : 1;
        // Groups open at the position: their repeated headers start every later page, each at the height of its own
        // instance (none when that instance is suppressed, blank or an underlay).
        var open = new Dictionary<int, Dictionary<int, int>>();
        // A header of another record at its level starts a new group: the group before it, and the groups inside that
        // one, are over, whether or not their footers are listed.
        var current = new Dictionary<int, int>();   // per group level: the record its current group started with
        // The instance the walk resumes with closes the groups that ended before it, but is not itself repeated yet.
        var resumed = -1;
        void Enter(int j)
        {
            var s = _order[j]; var level = _sections[s].GroupLevel;
            if (!current.TryGetValue(level, out var row) || row != _rows[j])
            {
                foreach (var inner in open.Keys.Where(key => key >= level).ToArray()) open.Remove(inner);
                foreach (var inner in current.Keys.Where(key => key > level).ToArray()) current.Remove(inner);
                current[level] = _rows[j];
            }
            if (!_sections[s].RepeatHeader || j == resumed) return;
            if (!open.TryGetValue(level, out var set)) open[level] = set = [];
            set[s] = j;
        }
        for (var j = 0; j < position; j++)
        {
            var section = _sections[_order[j]];
            if (section.Kind == "GroupHeader") Enter(j);
            else if (section.Kind == "GroupFooter") open.Remove(section.GroupLevel);
        }
        if (position < count && _sections[_order[position]].Kind == "GroupHeader") { resumed = position; Enter(position); resumed = -1; }
        var keepsGroups = _sections.Any(section => section.KeepGroupTogether && section.Kind == "GroupHeader");
        var extents = keepsGroups ? GroupExtents(position, heights) : null;
        // A page that starts with a group's footer closes that group: only the headers of the groups outside it repeat.
        double Carried(int exceptLevel = -1, int exceptSection = -1, int closing = int.MaxValue)
        {
            double sum = 0;
            foreach (var (level, set) in open)
            {
                if (level >= closing) continue;
                foreach (var (header, at) in set)
                    if (!(level == exceptLevel && header == exceptSection) && (_flags[at] & Hidden) == 0) sum += Height(heights, at);
            }
            return sum;
        }
        // The page the walk resumes on follows the same rule.
        var resumes = int.MaxValue;
        if (_pages > 0 && position < count && (_flags[position] & (ReportPageEstimateInput.NewPageBefore | ReportPageEstimateInput.NewPageAfter)) == 0)
            resumes = _sections[_order[position]].Kind switch { "GroupFooter" => _sections[_order[position]].GroupLevel, "ReportFooter" => 0, _ => int.MaxValue };
        var carried = Carried(closing: resumes);
        long pages = 1; var cursor = carried; var body = false; var details = 0; var lastRow = int.MinValue; var pending = false;
        var accumulated = new double[_sections.Length];
        var block = false; var column = 0; double columnCursor = 0, blockTop = 0, blockMax = 0;
        for (var j = position; j < count; j++)
        {
            var s = _order[j]; var section = _sections[s]; var flags = _flags[j];
            var h = (flags & Hidden) != 0 ? 0 : Height(heights, j);
            // The instance the next page starts with has already printed part of itself on the pages before.
            if (j == position && _pages > 0 && position == _runStart) h = Math.Max(0.0, h - _runPrinted);
            var header = section.Kind == "GroupHeader";
            var inColumn = columns > 1 && section.InColumns;
            if (block && !inColumn) { cursor = blockTop + blockMax; block = false; }
            if (header) Enter(j);
            var start = pending;
            if ((flags & ReportPageEstimateInput.NewPageBefore) != 0 && body) start = true;
            if (rates[s] > 0 && (flags & Idle) == 0)
            {
                accumulated[s] += rates[s];
                if (accumulated[s] >= 1.0)
                {
                    accumulated[s] -= 1.0;
                    if (body) start = true;
                }
            }
            if (_recordsPerPage > 0 && section.Kind == "Detail" && _rows[j] != lastRow && details >= _recordsPerPage && body) start = true;
            if (!start && extents is not null && header && section.KeepGroupTogether && body)
            {
                var extent = extents.GetValueOrDefault(j);
                if (cursor + extent > cap && extent <= cap - carried) start = true;
            }
            if (!start && !inColumn && h > 0 && body && cursor + h > cap + 1) start = true;
            if (start && body)
            {
                pages++;
                var closing = (flags & (ReportPageEstimateInput.NewPageBefore | ReportPageEstimateInput.NewPageAfter)) != 0 ? int.MaxValue
                    : section.Kind == "GroupFooter" ? section.GroupLevel : section.Kind == "ReportFooter" ? 0 : int.MaxValue;
                carried = header ? Carried(section.GroupLevel, s) : Carried(closing: closing);
                cursor = carried; body = false; details = 0; block = false;
            }
            pending = false;
            if (inColumn)
            {
                var full = h * columns;
                if (!block) { block = true; blockTop = cursor; column = 0; columnCursor = 0; blockMax = 0; }
                if (full > 0)
                {
                    if (columnCursor > 0 && columnCursor + full > cap - blockTop + 1)
                    {
                        column++; columnCursor = 0;
                        if (column >= columns)
                        {
                            pages++;
                            carried = Carried();
                            cursor = carried; details = 0; blockTop = cursor; column = 0; blockMax = 0;
                        }
                    }
                    columnCursor += full; blockMax = Math.Max(blockMax, columnCursor); body = true;
                }
                if ((flags & ReportPageEstimateInput.PrintAtBottom) != 0) { columnCursor = Math.Max(columnCursor, cap - blockTop); blockMax = Math.Max(blockMax, columnCursor); }
            }
            else if (h > 0)
            {
                var room = cap - cursor;
                if (h > room && h > cap - carried)
                {
                    // Taller than a page: it continues over as many pages as it needs.
                    var rest = h - Math.Max(0.0, room);
                    var full = cap - carried > 0 ? cap - carried : cap;
                    var extra = (long)Math.Ceiling(rest / full);
                    pages += extra; cursor = carried + (rest - (extra - 1) * full);
                }
                else cursor += h;
                body = true;
                if ((flags & ReportPageEstimateInput.PrintAtBottom) != 0) cursor = Math.Max(cursor, cap);
            }
            if (section.Kind == "Detail" && _rows[j] != lastRow) { details++; lastRow = _rows[j]; }
            if (section.Kind == "GroupFooter") open.Remove(section.GroupLevel);
            if ((flags & ReportPageEstimateInput.NewPageAfter) != 0 && j < count - 1) pending = true;
        }
        return _pages + pages;
    }

    // For each group header from the position on: the height of its group through its footer.
    private Dictionary<int, double> GroupExtents(int position, Learned heights)
    {
        const byte Hidden = ReportPageEstimateInput.Underlay | Idle;
        var extents = new Dictionary<int, double>();
        var stack = new Stack<(int Level, int Start, double Base)>();
        double running = 0;
        for (var j = position; j < _order.Length; j++)
        {
            var s = _order[j]; var section = _sections[s];
            if (section.Kind == "GroupHeader") stack.Push((section.GroupLevel, j, running));
            if ((_flags[j] & Hidden) == 0) running += Height(heights, j);
            if (section.Kind != "GroupFooter") continue;
            while (stack.Count > 0 && stack.Peek().Level >= section.GroupLevel)
            {
                var (level, start, at) = stack.Pop();
                if (level == section.GroupLevel) { extents[start] = running - at; break; }
            }
        }
        return extents;
    }
}
