using System.Data;
using System.Globalization;
using System.Text;

namespace Fx.ControlKit.Reports;

public sealed class ReportPositionedLayout
{
    public ReportDesignerDocument Document { get; init; } = ReportDesignerDocument.CreateBlank();
    public Dictionary<string, string> Bindings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> GroupNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ReportLayoutSummary> Summaries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ReportLayoutArea> Areas { get; } = new();
    public Dictionary<string, CrystalFormula> Formulas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FormulaErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ReportLayoutSummary> RunningTotals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, CrystalFormula>> SectionConditions { get; } = new();
    public Dictionary<string, Dictionary<string, CrystalFormula>> AreaConditions { get; } = new();
    public Dictionary<string, Dictionary<string, CrystalFormula>> ObjectConditions { get; } = new();
    public Dictionary<string, ReportLayoutSubreport> Subreports { get; } = new();
    public List<string> Diagnostics { get; } = [];
    public CrystalFormula? RecordSelection { get; set; }
    public CrystalFormula? GroupSelection { get; set; }
    internal List<string> Projections { get; } = [];
}

public sealed record ReportLayoutSummary(string Operation, string Field, string Group)
{
    public ReportRunningTotalCondition Evaluation { get; init; } = new("NoCondition");
    public ReportRunningTotalCondition Reset { get; init; } = new("NoCondition");
}
public sealed record ReportRunningTotalCondition(string Type, string Field = "", int Group = 0, CrystalFormula? Formula = null);
public sealed record ReportLayoutArea(bool Suppressed, bool RepeatHeader, bool NewPageBefore, bool NewPageAfter, bool Hidden = false, bool ResetPageNumberAfter = false, bool KeepGroupTogether = false);
public sealed record ReportTextMeasurement(string Style, string Html);
public sealed record ReportLayoutResult(List<string> Pages, List<string> Diagnostics)
{
    public List<ReportPageSnapshot> Snapshots { get; init; } = [];
}
public sealed record ReportLayoutParameterLink(string Parameter, string MainField, string ChildField);
public sealed record ReportLayoutSubreport(ReportDefinition Definition, List<ReportLayoutParameterLink> Links);

/// <summary>Detail rows -> measured bands -> fixed-size pages. No browser, SQL, or Crystal dependency.</summary>
public sealed partial class ReportLayoutSession
{
    private readonly ReportPositionedLayout _layout;
    private readonly ReportLayoutSession? _parent;
    private DataRow[] _rows;
    private DataRow[]? _summaryRows;
    private Dictionary<DataRow, int>? _summaryRowIndexes;
    private readonly IReadOnlyDictionary<string, object> _parameters;
    private readonly List<Band> _bands = [];
    private readonly Dictionary<string, int> _measurementKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _diagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (int Start, int End)[]> _groupRanges = new();
    private readonly Dictionary<(ReportLayoutSummary Summary, int Start, int End), object?> _summaryCache = new();
    private readonly Dictionary<(string Element, int ScopeStart), ReportAnalysisSnapshot> _analysisSnapshots = new();
    private readonly string _pageToken = Guid.NewGuid().ToString("N") + "-page";
    private readonly string _countToken = Guid.NewGuid().ToString("N") + "-count";
    private readonly DateTime _printTime;
    public List<ReportTextMeasurement> Measurements { get; } = [];
    private sealed record Item(ReportDesignerElement Element, string Html, int Measurement, ReportLayoutSession? Child = null, int ChildMeasurementOffset = 0, ReportLayoutSession? Owner = null, int Row = 0, string? TemplateHtml = null, string? ConsumedText = null, bool OmittedContinuation = false, ReportAnalysisSnapshot? Analysis = null, ReportTableFragment? Table = null);
    private sealed record InlineHeader(int Start, int End, Band Header, int KeepThrough = 0, bool Repeat = true);
    private sealed record InlineSection(int Start, int End, Band Source, ReportDesignerSection? Format = null);
    private sealed record InlineCut(Band Source, int RetainedHeight);
    private sealed record Band(ReportDesignerSection Section, List<Item> Items, int Row, List<Band> Repeats, bool Suppressed = false,
        List<int>? Breaks = null, List<int>? ForcedBreaks = null, List<InlineHeader>? InlineHeaders = null, bool RepeatedHeader = false,
        ReportLayoutSession? Owner = null, List<InlineSection>? InlineSections = null, List<InlineCut>? InlineCuts = null);
    private sealed record Placement(Band Band, int Top, int Offset, int Height);
    private sealed class Page
    {
        public int Number { get; init; }
        public List<Placement> Placements { get; } = [];
        public int Cursor { get; set; }
        public int BodyStart { get; set; }
        public int Bottom { get; set; }
        public bool HasBody { get; set; }
        public bool HasHeaders { get; set; }
    }

    public ReportLayoutSession(ReportPositionedLayout layout, DataTable data, IReadOnlyDictionary<string, object>? parameters = null,
        Func<ReportDefinition, IReadOnlyDictionary<string, object>, DataTable>? executeSubreport = null, DateTime? printTime = null)
        : this(layout, data, parameters, executeSubreport, new RuntimeState { PrintTime = printTime ?? DateTime.Now }, 0) { }

    private ReportLayoutSession(ReportPositionedLayout layout, DataTable data, IReadOnlyDictionary<string, object>? parameters,
        Func<ReportDefinition, IReadOnlyDictionary<string, object>, DataTable>? executeSubreport, RuntimeState state, int depth, ReportLayoutSession? parent = null)
    {
        _layout = layout;
        _parent = parent;
        _printTime = state.PrintTime;
        _diagnostics.UnionWith(layout.Diagnostics);
        // Authored layouts can bypass the XML loader. Report capabilities even for hidden/empty bands.
        var reportName = layout.Document.SourceDocument?.Root is { } root ? (string?)root.Attribute("Name") ?? "Report" : layout.Document.Title;
        foreach (var section in layout.Document.Sections)
        foreach (var element in section.Elements)
            if (ReportObjectCapabilities.UnsupportedCrystalKind(element.Kind) is { } kind && element.Analysis is null)
                _diagnostics.Add(ReportObjectCapabilities.Diagnostic(reportName, section.Name, element.Name, kind, true,
                    layout.Document.SourceObjects.GetValueOrDefault(element.SourceKey)));
        _rows = data.Rows.Cast<DataRow>().ToArray();
        _parameters = new Dictionary<string, object>(parameters ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
        _executeSubreport = executeSubreport; _state = state; _depth = depth;
        _state.Sessions.Add(this);
        _state.HasGrowingText |= layout.Document.Elements.Any(e => e.CanGrow);
        _state.HasPageConditions |= layout.SectionConditions.Values.Concat(layout.AreaConditions.Values).Concat(layout.ObjectConditions.Values).SelectMany(c => c.Values).Any(UsesPage);
        _state.HasPageConditions |= layout.Formulas.Values.Any(f => f.UsesPageContext || f.References.Any(IsSpecial))
            || layout.Document.Elements.Any(e => IsSpecial(e.Binding) || e.Visual.Runs.Any(r => IsSpecial(r.Binding)));
        if (ReportDesignerEditing.ValidatePage(layout.Document.Page) is { } error) throw new InvalidDataException(error);
        PrepareFormulaSchedule();
        PrepareRows();
        PreparePhysicalPageSchedule();
        if (_physicalPageSchedule) { _formulaPage = 1; _formulaPages = 1; }
        try { BuildBands(); }
        finally { _formulaPage = null; _formulaPages = null; }
    }

    public static bool IsSpecial(string reference) => SpecialName(reference) is "pagenumber" or "totalpagecount" or "pagenofm" or "reporttitle" or "reportfilename" or "printdate" or "printtime" or "recordnumber";
    private static string SpecialName(string reference) => reference.Trim('{', '}').Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    private object? Value(string reference, int row)
    {
        reference = reference.Trim();
        if (_layout.Formulas.ContainsKey(reference)) return FormulaValue(reference, row);
        if (_layout.FormulaErrors.TryGetValue(reference, out var error)) throw new InvalidDataException($"{reference}: {error}");
        if (_layout.RunningTotals.TryGetValue(reference, out var running)) return RunningTotal(reference, running, row);
        if (_layout.GroupNames.TryGetValue(reference, out var condition)) return Value(condition, row);
        if (reference.StartsWith("{?", StringComparison.Ordinal))
        {
            var name = reference.Trim('{', '}').TrimStart('?');
            if (_parameters.TryGetValue(name, out var parameter)) return parameter;
            var sqlName = new string(name.Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray());
            if (_parameters.TryGetValue(sqlName, out parameter)) return parameter;
            _diagnostics.Add($"Parameter '{name}' has no value.");
            return "[?" + name + "]";
        }
        if (IsSpecial(reference)) return SpecialName(reference) switch
        {
            "pagenumber" => _formulaPage ?? _pageToken, "totalpagecount" => _formulaPages ?? _countToken, "pagenofm" => (_formulaPage ?? _pageToken) + " / " + (_formulaPages ?? _countToken),
            "reporttitle" => _layout.Document.Title, "reportfilename" => _layout.Document.SourceName,
            "printdate" => _printTime.ToShortDateString(), "printtime" => _printTime.ToShortTimeString(), "recordnumber" => row + 1, _ => ""
        };
        if (_layout.Bindings.TryGetValue(reference, out var alias))
        {
            if (row < 0 || row >= _rows.Length) return null;
            if (_rows[row].Table.Columns.Contains(alias)) return _rows[row][alias] is DBNull ? null : _rows[row][alias];
            _diagnostics.Add($"Result column '{alias}' for '{reference}' is missing.");
        }
        else if (_layout.Summaries.TryGetValue(reference, out var summary)) return Summary(summary, row);
        _diagnostics.Add($"Field '{reference}' could not be resolved.");
        return "[" + reference + "]";
    }

    private object? Summary(ReportLayoutSummary summary, int row, int? stop = null)
    {
        if (_summaryRows is null || ReferenceEquals(_rows, _summaryRows)) return SummaryCore(summary, row, stop);
        // Group selection is a print-pass filter. First-pass summaries still include the excluded groups.
        var displayed = _rows;
        _summaryRowIndexes ??= _summaryRows.Select((data, index) => (data, index)).ToDictionary(pair => pair.data, pair => pair.index);
        var sourceRow = row >= 0 && row < displayed.Length ? _summaryRowIndexes[displayed[row]] : row;
        try { _rows = _summaryRows; return SummaryCore(summary, sourceRow, stop); }
        finally { _rows = displayed; }
    }

    private object? SummaryCore(ReportLayoutSummary summary, int row, int? stop)
    {
        if (_layout.Formulas.TryGetValue(summary.Field, out var formula)) RequireReadTime(formula, "Summary");
        if (_layout.Summaries.ContainsKey(summary.Field)) { _diagnostics.Add("Nested summaries are not supported."); return "[Nested summary]"; }
        var start = 0;
        var end = Math.Clamp(stop ?? _rows.Length, 0, _rows.Length);
        if (summary.Group.Length > 0 && row >= 0 && row < _rows.Length)
        {
            var level = _layout.Document.Groups.FindIndex(group => group.Condition.Equals(summary.Group, StringComparison.OrdinalIgnoreCase));
            if (level < 0) { _diagnostics.Add($"Unknown summary group '{summary.Group}'."); return "[Unknown summary group]"; }
            if (!_groupRanges.TryGetValue(level, out var ranges))
            {
                ranges = new (int Start, int End)[_rows.Length];
                for (var first = 0; first < _rows.Length;)
                {
                    var last = first + 1;
                    while (last < _rows.Length && SameGroup(last, first, level)) last++;
                    for (var index = first; index < last; index++) ranges[index] = (first, last);
                    first = last;
                }
                _groupRanges[level] = ranges;
            }
            (start, end) = ranges[row];
        }
        var key = (summary, start, end);
        if (_summaryCache.TryGetValue(key, out var cached)) return cached;
        var values = Enumerable.Range(start, end - start).Select(index => Value(summary.Field, index)).Where(value => value is not null).ToList();
        if (summary.Operation is "Count") return _summaryCache[key] = values.Count;
        if (summary.Operation is "DistinctCount") return _summaryCache[key] = values.Distinct().Count();
        if (values.Count == 0) return _summaryCache[key] = summary.Operation == "Sum" ? 0m : null;
        if (summary.Operation is "Minimum" or "Maximum")
            return _summaryCache[key] = values.Aggregate((left, right) => (CrystalFormula.Compare(left, right) < 0) == (summary.Operation == "Minimum") ? left : right);
        try
        {
            var numbers = values.Select(value => Convert.ToDecimal(value, CultureInfo.InvariantCulture)).ToArray();
            return _summaryCache[key] = summary.Operation switch { "Sum" => numbers.Sum(), "Average" => numbers.Average(), "Minimum" => numbers.Min(), "Maximum" => numbers.Max(), _ => Unsupported() };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        { _diagnostics.Add($"Summary '{summary.Operation} {summary.Field}' has nonnumeric values."); return "[Invalid summary]"; }
        object Unsupported() { _diagnostics.Add($"Summary operation '{summary.Operation}' is not implemented."); return "[Unsupported summary]"; }
    }

    private bool SameGroup(int left, int right, int level)
    {
        for (var index = 0; index <= level; index++)
            if (!Equals(Value(_layout.Document.Groups[index].Condition, left), Value(_layout.Document.Groups[index].Condition, right))) return false;
        return true;
    }

    private string Format(string reference, int row, string format)
    {
        try
        {
            if (_formulaPage is null && DependsOn(reference, true)) return DeferredPageValue(reference, row, format);
            var value = Value(reference, row);
            return value is IFormattable formattable ? formattable.ToString(string.IsNullOrEmpty(format) ? null : format, CultureInfo.CurrentCulture) ?? "" : value?.ToString() ?? "";
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or InvalidCastException or OverflowException or DivideByZeroException or NotSupportedException or ArgumentException)
        { _diagnostics.Add($"{reference}: {ex.Message}"); return "[Formula error: " + reference + "]"; }
    }

    private bool Visible(ReportDesignerSection section) => !section.IsSuppressed && !section.HideForDrillDown;

    private Band CreateBand(ReportDesignerSection section, int row, List<Band> repeats)
    {
        _bandFormulaCache.Clear();
        section = ApplySectionConditions(section, row);
        if (!Visible(section)) return new(section, [], row, repeats.ToList(), true, Owner: this);
        var items = new List<Item>();
        foreach (var original in section.Elements)
        {
            var element = ApplyObjectConditions(original, row);
            if (element.IsSuppressed) continue;
            if (element.Kind == "Picture" && element.Visual.VectorImage is null && !ReportObjectVisual.IsEmbeddedImage(element.Visual.ImageDataUrl))
                _diagnostics.Add($"{element.Name}: embedded image bytes are missing. Replace the picture in the designer.");
            var child = element.Kind == "Subreport" ? CreateInlineSubreport(element, row) : null;
            var childOffset = Measurements.Count;
            if (child is not null) Measurements.AddRange(child.Measurements);
            if (ReportObjectCapabilities.UnsupportedCrystalKind(element.Kind) is null
                && element.Kind is not ("Text" or "Field" or "FieldHeading" or "Picture" or "Line" or "Box" or "Subreport" or "Table"))
                _diagnostics.Add($"{element.Name}: {element.Kind} objects are not implemented in positioned rendering.");
            var html = ReportObjectRenderer.Content(element, reference => Format(reference, row, element.FormatString));
            var measure = -1;
            if (element.CanGrow && element.Kind is "Text" or "Field" or "FieldHeading")
            {
                var style = ReportObjectRenderer.Style(element, false);
                var measuredHtml = ResolvePageValues(html.Replace(_pageToken, "88888", StringComparison.Ordinal).Replace(_countToken, "88888", StringComparison.Ordinal), 88888, 88888);
                var key = style + "\n" + measuredHtml;
                if (!_measurementKeys.TryGetValue(key, out measure))
                {
                    measure = Measurements.Count;
                    _measurementKeys[key] = measure;
                    Measurements.Add(new(style, measuredHtml));
                }
            }
            ReportAnalysisSnapshot? analysis = null;
            if (element.Analysis is { } definition && element.Kind is "Chart" or "CrossTab" or "Table")
            {
                try
                {
                    if (definition.Validate(element.Kind) is { } invalid) throw new InvalidDataException(invalid);
                    foreach (var reference in definition.References)
                    {
                        if (_layout.Formulas.TryGetValue(reference, out var formula)) RequireReadTime(formula, "Analytical item");
                        if (_layout.RunningTotals.ContainsKey(reference) || _layout.Summaries.ContainsKey(reference) || IsSpecial(reference))
                            throw new InvalidDataException("Analytical fields must be record-time values, not page values or accumulated summaries.");
                    }
                    var indexes = Enumerable.Range(0, _rows.Length);
                    if (definition.GroupScope.Length > 0)
                    {
                        var level = _layout.Document.Groups.FindIndex(group => group.Condition.Equals(definition.GroupScope, StringComparison.OrdinalIgnoreCase));
                        if (level < 0 || row < 0 || row >= _rows.Length) throw new InvalidDataException("The analytical group scope is unavailable.");
                        indexes = indexes.Where(index => SameGroup(index, row, level));
                    }
                    var scopeStart = definition.GroupScope.Length == 0 ? -1 : indexes.FirstOrDefault(-1);
                    if (!_analysisSnapshots.TryGetValue((original.Id, scopeStart), out analysis))
                    {
                        var data = indexes.Select(index => definition.References.ToDictionary(reference => reference,
                            reference => Value(reference, index) ?? DBNull.Value, StringComparer.OrdinalIgnoreCase)).ToList();
                        analysis = new(definition.Clone(), data);
                        if (element.Kind is "CrossTab" or "Table") analysis = analysis with { Table = ReportTabularData.Create(element, analysis) };
                        _analysisSnapshots[(original.Id, scopeStart)] = analysis;
                    }
                    html = "[Analytical report item: use the component viewer or asynchronous HTML export]";
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or FormatException)
                { _diagnostics.Add(element.Name + ": " + error.Message); html = ReportObjectRenderer.Encode("[" + element.Name + ": " + error.Message + "]"); }
            }
            items.Add(new(element, html, measure, child, childOffset, this, row, Analysis: analysis));
        }
        return new(section, items, row, repeats.ToList(), Owner: this);
    }

    private void BuildBands()
    {
        var sections = _layout.Document.Sections.ToList();
        var repeats = new List<Band>();
        void Add(string kind, int row, int? group = null)
        {
            foreach (var section in sections.Where(section => section.Kind == kind && (group is null || ReportDesignerEditing.GetGroupNumber(_layout.Document, section) == group + 1)))
            {
                var band = CreateBand(section, row, repeats);
                if (band.Suppressed && !HasVisibilityEvent(section.Id)) continue;
                _bands.Add(band);
                if (kind == "GroupHeader" && (_layout.Areas.GetValueOrDefault(section.Id)?.RepeatHeader ?? false)) repeats.Add(band);
            }
        }
        Add("ReportHeader", _rows.Length > 0 ? 0 : -1);
        _rowBefore[-1] = _rowAfter[-1] = CaptureVariables();
        for (var row = 0; row < _rows.Length; row++)
        {
            _rowBefore[row] = CaptureVariables();
            for (var group = 0; group < _layout.Document.Groups.Count; group++)
                if (row == 0 || !SameGroup(row - 1, row, group)) Add("GroupHeader", row, group);
            Add("Detail", row);
            for (var group = _layout.Document.Groups.Count - 1; group >= 0; group--)
                if (row + 1 == _rows.Length || !SameGroup(row, row + 1, group))
                {
                    Add("GroupFooter", row, group);
                    repeats.RemoveAll(band => ReportDesignerEditing.GetGroupNumber(_layout.Document, band.Section) == group + 1);
                }
            _rowAfter[row] = CaptureVariables();
        }
        Add("ReportFooter", _rows.Length - 1);
        _rowAfter[_rows.Length - 1] = CaptureVariables();
        // Page furniture can bind to the first/last row on each page. Prepare all variants before DOM measurement.
        foreach (var section in sections.Where(section => section.Kind is "PageHeader" or "PageFooter"))
            for (var row = -1; row < _rows.Length; row++) _furniture[(section.Id, row)] = CreateFurniture(section, row);
    }

    private readonly Dictionary<(string Id, int Row), Band> _furniture = new();

    public ReportLayoutResult Paginate(IReadOnlyList<int>? measuredHeights = null)
        => Paginate(measuredHeights, 1, 0, null);

    private ReportLayoutResult Paginate(IReadOnlyList<int>? measuredHeights, int expectedPages, int attempt, IReadOnlyList<int>? expectedCounts,
        Dictionary<Band, (int Page, int Count)>? inlineContexts = null, IReadOnlyList<int>? footerRows = null, int scheduleAttempt = 0)
    {
        if (_textMetrics is null && Measurements.Count > 0 && _state.HasGrowingText && (measuredHeights is null || measuredHeights.Count != Measurements.Count))
            _diagnostics.Add("Browser text measurement is unavailable; CanGrow pagination uses an approximate font metric.");
        var size = _layout.Document.Page;
        var bands = _bands.Select(band => Expand(band, 0)).ToList();
        var pages = new List<Page>();
        var headers = _layout.Document.Sections.Where(section => section.Kind == "PageHeader").ToList();
        var footers = _layout.Document.Sections.Where(section => section.Kind == "PageFooter").ToList();
        var pendingHidden = new List<Band>();
        var underlays = new List<(int Index, int Page, int Top)>();
        var resetPageNumber = false;
        var page = NewPage(bands.FirstOrDefault()?.Row ?? -1, [], bands.FirstOrDefault()?.Section.Kind != "ReportHeader");
        for (var bandIndex = 0; bandIndex < bands.Count; bandIndex++)
        {
            var band = PageBand(bandIndex);
            if (band.Suppressed) { HiddenEvent(band); continue; }
            if (band.Section.SuppressIfBlank && band.Items.All(item => item.Element.Kind is not ("Line" or "Box" or "Picture") &&
                string.IsNullOrWhiteSpace(System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(item.Html, "<[^>]+>", ""))))) continue;
            var height = Height(band);
            var area = _layout.Areas.GetValueOrDefault(band.Section.Id);
            var withHeaders = band.Section.Kind != "ReportHeader";
            if ((band.Section.NewPageBefore || area?.NewPageBefore == true) && page.HasBody) page = NewPage(band.Row, band.Repeats, withHeaders);
            if (withHeaders && !page.HasHeaders)
            {
                var headerHeight = headers.Sum(section => Height(_furniture[(section.Id, band.Row)]));
                if (page.Cursor + headerHeight + height > page.Bottom && page.HasBody) page = NewPage(band.Row, band.Repeats);
                else AddHeaders(page, band.Row);
            }
            if (band.Section.Kind == "GroupHeader" && page.HasBody)
            {
                var keepHeight = height;
                for (var next = bandIndex + 1; next < bands.Count && bands[next].Row == band.Row; next++)
                {
                    keepHeight += Height(bands[next]);
                    if (bands[next].Section.Kind != "GroupHeader") break;
                }
                if (area?.KeepGroupTogether == true)
                {
                    var wholeGroup = height;
                    var groupNumber = ReportDesignerEditing.GetGroupNumber(_layout.Document, _layout.Document.Sections.First(s => s.Id == band.Section.Id));
                    for (var next = bandIndex + 1; next < bands.Count; next++)
                    {
                        wholeGroup += Height(bands[next]);
                        if (bands[next].Section.Kind == "GroupFooter" && ReportDesignerEditing.GetGroupNumber(_layout.Document, _layout.Document.Sections.First(s => s.Id == bands[next].Section.Id)) == groupNumber) break;
                    }
                    var freshRoom = page.Bottom - headers.Sum(s => Height(_furniture[(s.Id, band.Row)])) - band.Repeats.Sum(Height);
                    if (wholeGroup <= freshRoom) keepHeight = wholeGroup;
                }
                if (keepHeight > page.Bottom - page.Cursor) page = NewPage(band.Row, band.Repeats);
            }
            if (band.Section.UnderlayFollowingSections)
            {
                underlays.Add((bandIndex, pages.Count - 1, page.Cursor)); page.HasBody = true;
                if (band.Section.PrintAtBottomOfPage || band.Section.NewPageAfter || area?.NewPageAfter == true
                    || band.Section.ResetPageNumberAfter || area?.ResetPageNumberAfter == true)
                    _diagnostics.Add($"{band.Section.Name}: underlay placement ignores bottom alignment and ending page-break/reset flags; foreground flow is retained.");
                continue;
            }
            if (height > page.Bottom - page.Cursor && page.HasBody && band.Section.KeepTogether) page = NewPage(band.Row, band.Repeats, withHeaders);
            band = PageBand(bandIndex);
            if (band.Suppressed) { HiddenEvent(band); continue; }
            height = Height(band);
            var offset = 0;
            do
            {
                if (page.Bottom <= page.Cursor) page = NewPage(band.Row, band.Repeats, withHeaders);
                if (page.HasBody && band.InlineSections?.Any(s => s.Start == offset && s.End > s.Start
                    && ((s.Format ?? s.Source.Section).NewPageBefore || s.Source.Owner!._layout.Areas.GetValueOrDefault(s.Source.Section.Id)?.NewPageBefore == true)) == true)
                {
                    page = NewPage(band.Row, band.Repeats, withHeaders);
                    if (offset == 0) { band = PageBand(bandIndex); height = Height(band); }
                    AddInlineHeaders(page, band, offset);
                }
                var available = page.Bottom - page.Cursor;
                if (available <= 0) throw new InvalidDataException("Page headers and footers leave no room for report content.");
                var slice = Math.Min(height - offset, available);
                var forced = band.ForcedBreaks?.Where(point => point > offset && point <= offset + slice).DefaultIfEmpty(0).Min() ?? 0;
                foreach (var ending in band.InlineSections?.Where(s => s.End > offset && s.End <= offset + slice) ?? [])
                {
                    var owner = ending.Source.Owner!;
                    if (owner.Conditions(owner._layout.SectionConditions, ending.Source.Section.Id, true)
                        .Concat(owner.Conditions(owner._layout.AreaConditions, ending.Source.Section.Id, true))
                        .Any(c => c.Key.Equals("EnableNewPageAfter", StringComparison.OrdinalIgnoreCase))
                        && ForPage(ending.Source, page.Number, ExpectedCount(pages.Count - 1)).Section.NewPageAfter)
                        forced = forced == 0 ? ending.End : Math.Min(forced, ending.End);
                }
                var kept = band.InlineSections?.Where(s => (s.Format ?? s.Source.Section).KeepTogether && s.Start >= offset
                    && s.Start < offset + slice && s.End > offset + slice).OrderBy(s => s.Start).FirstOrDefault();
                if (kept is not null && (forced == 0 || forced >= kept.End))
                {
                    // Honor the child's section boundary, not just the outer subreport object's KeepTogether.
                    var freshRoom = page.Bottom - headers.Sum(s => Height(_furniture[(s.Id, band.Row)])) - band.Repeats.Sum(Height)
                        - (band.InlineHeaders?.Where(h => h.Repeat && h.Start < kept.Start && h.End > kept.Start).Sum(h => Height(h.Header)) ?? 0);
                    if (kept.End - kept.Start <= freshRoom)
                    {
                        if (kept.Start > offset && SafeBreaks(band).Contains(kept.Start)) slice = kept.Start - offset;
                        else if (kept.Start == offset && page.HasBody)
                        { page = NewPage(band.Row, band.Repeats, withHeaders); AddInlineHeaders(page, band, offset); continue; }
                    }
                }
                if (forced > offset + slice) forced = 0;
                if (forced > 0) slice = forced - offset;
                else if (slice < height - offset && band.Breaks is { Count: > 0 })
                {
                    var boundary = SafeAt(band, offset + slice) ? offset + slice
                        : SafeBreaks(band).Where(point => point > offset && point <= offset + slice).DefaultIfEmpty(0).Max();
                    if (boundary > 0) slice = boundary - offset;
                    else if (page.HasBody && band.Breaks.FirstOrDefault(point => point > offset) - offset <= page.Bottom)
                    { page = NewPage(band.Row, band.Repeats, withHeaders); AddInlineHeaders(page, band, offset); continue; }
                }
                if ((offset > 0 || slice < height) && !SafeAt(band, offset + slice) && !CanContinueAt(band, offset + slice))
                    _diagnostics.Add($"{band.Section.Name}: a band spans pages; overflowing objects are clipped at the page boundary.");
                foreach (var inline in band.InlineSections?.Where(s => s.Start < offset + slice && s.End > offset + slice) ?? [])
                {
                    var owner = inline.Source.Owner!;
                    if (owner.Conditions(owner._layout.SectionConditions, inline.Source.Section.Id, true)
                        .Concat(owner.Conditions(owner._layout.AreaConditions, inline.Source.Section.Id, true))
                        .Any(c => c.Key.ToLowerInvariant() is not ("enablesuppress" or "enablehidefordrilldown" or "enablekeeptogether" or "enablenewpagebefore" or "enablenewpageafter")))
                        _diagnostics.Add($"{inline.Source.Section.Name}: continuation retains its section-entry layout settings for unsupported mid-section format changes.");
                }
                var atBottom = band.Section.PrintAtBottomOfPage && offset + slice == height;
                foreach (var inline in band.InlineSections?.Where(s => (s.Format ?? s.Source.Section).PrintAtBottomOfPage && s.End > offset && s.End <= offset + slice) ?? [])
                {
                    if (inline.Start <= offset && inline.End == offset + slice && band.Items.Where(i => i.Element.TopTwips < offset + slice && i.Element.TopTwips + ItemHeight(i) > offset)
                        .All(i => i.Owner == inline.Source.Owner && i.Row == inline.Source.Row && i.Element.SectionId == inline.Source.Section.Id
                            || i.Owner?.IsDescendantOf(inline.Source.Owner!) == true)) atBottom = true;
                    else _diagnostics.Add($"{inline.Source.Section.Name}: inline bottom alignment shares space with other content; normal flow is retained.");
                }
                var top = atBottom ? page.Bottom - slice : page.Cursor;
                page.Placements.Add(new(band, top, offset, slice));
                page.Cursor = top + slice;
                if (forced > 0 && forced == offset + slice) page.Cursor = page.Bottom;
                page.HasBody = true;
                offset += slice;
                if (offset < height)
                {
                    var resetBeforeContinuation = resetPageNumber;
                    page = NewPage(band.Row, band.Repeats, withHeaders);
                    var continued = PageBand(bandIndex);
                    continued = ApplyInlineCuts(continued, band.InlineCuts, ItemHeight);
                    band = ContinueText(band, continued, offset, ItemHeight);
                    band = SuppressInlineContinuations(band, offset, page.Number, ExpectedCount(pages.Count - 1), ItemHeight);
                    band = band with { Breaks = SafeBreaks(band) };
                    height = Height(band);
                    if (band.Suppressed || offset >= height)
                    {
                        pages.RemoveAt(pages.Count - 1); page = pages[^1]; resetPageNumber = resetBeforeContinuation;
                        break;
                    }
                    AddInlineHeaders(page, band, offset);
                }
            } while (offset < height);
            if (band.Section.NewPageAfter || area?.NewPageAfter == true) page.Cursor = page.Bottom;
            resetPageNumber |= band.Section.ResetPageNumberAfter || area?.ResetPageNumberAfter == true;
        }

        // Underlays do not consume foreground flow. Place their fragments behind the completed flow,
        // extending the page sequence only when the underlay itself still has unprinted content.
        foreach (var underlay in underlays)
        {
            var offset = 0; Band? previous = null;
            for (var index = underlay.Page; ; index++)
            {
                var created = index == pages.Count;
                var resetBeforeUnderlay = resetPageNumber;
                page = !created ? pages[index] : NewPage(_bands[underlay.Index].Row, _bands[underlay.Index].Repeats);
                var band = PageBand(underlay.Index, index);
                if (previous is not null)
                {
                    band = ApplyInlineCuts(band, previous.InlineCuts, ItemHeight);
                    band = ContinueText(previous, band, offset, ItemHeight);
                    band = SuppressInlineContinuations(band, offset, page.Number, ExpectedCount(index), ItemHeight);
                }
                var height = Height(band);
                if (band.Suppressed || offset >= height)
                {
                    if (created) { pages.RemoveAt(index); resetPageNumber = resetBeforeUnderlay; }
                    break;
                }
                var top = index == underlay.Page ? underlay.Top : page.BodyStart;
                var room = page.Bottom - top;
                if (room <= 0) { previous = band; continue; }
                var slice = Math.Min(height - offset, room);
                if (slice < height - offset && !SafeAt(band, offset + slice))
                {
                    var safe = SafeBreaks(band).Where(p => p > offset && p <= offset + slice).DefaultIfEmpty(0).Max();
                    if (safe > offset) slice = safe - offset;
                    else if (!CanContinueAt(band, offset + slice)) _diagnostics.Add($"{band.Section.Name}: oversized underlay objects are clipped at the page boundary.");
                }
                var insertion = page.Placements.TakeWhile(p => p.Band.Section.Kind == "PageHeader" || p.Band.Section.UnderlayFollowingSections).Count();
                page.Placements.Insert(insertion, new(band, top, offset, slice)); page.HasBody = true;
                offset += slice; if (offset >= height) break;
                previous = band;
            }
        }
        page = pages[^1];
        foreach (var hidden in pendingHidden) page.Placements.Add(new(hidden, page.Cursor, 0, 0));
        pendingHidden.Clear();
        var counts = new int[pages.Count];
        for (var start = 0; start < pages.Count;)
        {
            var end = start + 1;
            while (end < pages.Count && pages[end].Number != 1) end++;
            for (var index = start; index < end; index++) counts[index] = end - start;
            start = end;
        }
        var nextInlineContexts = new Dictionary<Band, (int Page, int Count)>();
        var lastOffsets = LastFragmentOffsets(pages);
        for (var index = 0; index < pages.Count; index++)
        foreach (var placement in pages[index].Placements)
        foreach (var inline in placement.Band.InlineSections ?? [])
            if (inline.Start >= placement.Offset && (inline.Start < placement.Offset + placement.Height
                || inline.Start == inline.End && inline.Start == placement.Offset + placement.Height && placement.Offset == lastOffsets[FragmentKey(placement.Band)]))
                nextInlineContexts.TryAdd(inline.Source, (pages[index].Number, counts[index]));
        var inlineChanged = nextInlineContexts.Any(pair => inlineContexts is null || !inlineContexts.TryGetValue(pair.Key, out var context) || context != pair.Value);
        var nextFooterRows = pages.Select(p => p.Placements.LastOrDefault(p => p.Band.Section.Kind != "PageHeader")?.Band.Row ?? -1).ToArray();
        var footerChanged = footers.Count > 0 && (footerRows is null || !nextFooterRows.SequenceEqual(footerRows));
        if (footerChanged || _state.HasPageConditions && (inlineChanged || pages.Count != expectedPages || expectedCounts is null || !counts.SequenceEqual(expectedCounts)))
        {
            if (attempt >= 7) _diagnostics.Add("Page-dependent formatting did not stabilize within eight passes; the last computed layout is retained.");
            else return Paginate(measuredHeights, pages.Count, attempt + 1, counts, nextInlineContexts, nextFooterRows, scheduleAttempt);
        }
        for (var index = 0; index < pages.Count; index++)
        {
            var current = pages[index];
            var lastRow = current.Placements.LastOrDefault(placement => placement.Band.Section.Kind != "PageHeader")?.Band.Row ?? -1;
            var footerTop = current.Bottom;
            foreach (var section in footers)
            {
                var footer = ForPage(_furniture[(section.Id, lastRow)], current.Number, counts[index], physicalPage: index + 1);
                var height = Height(footer);
                current.Placements.Add(new(footer, footerTop, 0, height)); footerTop += height;
            }
        }
        if (ReplayPhysicalPages(pages, counts))
        {
            if (scheduleAttempt >= 15) _diagnostics.Add("Mutable page formula layout did not stabilize within sixteen replays; the last computed layout is retained.");
            else return Paginate(measuredHeights, pages.Count, 0, counts, nextInlineContexts, nextFooterRows, scheduleAttempt + 1);
        }
        var output = new List<string>();
        var snapshots = new List<ReportPageSnapshot>();
        for (var index = 0; index < pages.Count; index++)
        {
            var current = pages[index];
            var pageBands = new List<ReportPageBand>();
            var html = new StringBuilder();
            html.Append("<article class=\"fx-report-positioned-page\" aria-label=\"").Append(ReportObjectRenderer.Encode(_layout.Document.Title)).Append("\" data-page=\"").Append(index + 1).Append("\" style=\"position:relative;box-sizing:content-box;overflow:hidden;background:white;color:black;letter-spacing:0;width:")
                .Append(Px(size.ContentWidthTwips + size.MarginLeftTwips + size.MarginRightTwips)).Append("px;height:")
                .Append(Px(size.ContentHeightTwips + size.MarginTopTwips + size.MarginBottomTwips)).Append("px;break-after:page;\">");
            html.Append("<div style=\"position:absolute;left:").Append(Px(size.MarginLeftTwips)).Append("px;top:").Append(Px(size.MarginTopTwips)).Append("px;width:").Append(Px(size.ContentWidthTwips)).Append("px;height:").Append(Px(size.ContentHeightTwips)).Append("px;overflow:hidden;\">");
            foreach (var placement in current.Placements)
            {
                if (placement.Band.Suppressed) continue;
                var objects = new List<ReportPageObject>();
                html.Append("<section data-section=\"").Append(ReportObjectRenderer.Encode(placement.Band.Section.Name)).Append("\" style=\"position:absolute;overflow:hidden;left:0;width:100%;top:").Append(Px(placement.Top))
                    .Append("px;height:").Append(Px(placement.Height)).Append("px;background:").Append(ReportObjectRenderer.Color(placement.Band.Section.BackgroundColor)).Append(";\">");
                foreach (var item in placement.Band.Items)
                {
                    if (item.Element.IsSuppressed) continue;
                    var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name);
                    element.LeftTwips = item.Element.LeftTwips; element.TopTwips = item.Element.TopTwips - placement.Offset;
                    element.HeightTwips = ItemHeight(item);
                    if (element.CanGrow && TextMetrics(item) is { } metric
                        && metric.Lines.Any(l => l.Top < placement.Height - element.TopTwips && l.Bottom > placement.Height - element.TopTwips))
                    {
                        // A mandatory nested break may intersect a neighboring text line. Render only
                        // complete lines here; ContinueText carries the unprinted characters to the next page.
                        element.HeightTwips = metric.Lines.Where(l => l.Bottom <= placement.Height - element.TopTwips)
                            .Select(l => l.Bottom).DefaultIfEmpty(0).Max();
                    }
                    if (element.TopTwips >= placement.Height || element.TopTwips + element.HeightTwips <= 0) continue;
                    if (element.Kind == "Box")
                    {
                        var end = element.TopTwips + element.HeightTwips;
                        if (!element.Visual.CloseAtPageBreak)
                        {
                            if (element.TopTwips < 0) element.Visual.TopLine = "NoLine";
                            if (end > placement.Height) element.Visual.BottomLine = "NoLine";
                        }
                        // Each fragment owns its borders; clipping the original box loses page-edge closure.
                        element.TopTwips = Math.Max(0, element.TopTwips);
                        element.HeightTwips = Math.Min(placement.Height, end) - element.TopTwips;
                    }
                    var content = ResolvePageValues(item.Html.Replace(_pageToken, current.Number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace(_countToken, counts[index].ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), current.Number, counts[index], placement.Band.RepeatedHeader);
                    objects.Add(new(element, content, item.Analysis) { Table = item.Table });
                    html.Append("<div data-object=\"").Append(ReportObjectRenderer.Encode(element.Name)).Append("\" style=\"").Append(ReportObjectRenderer.Style(element)).Append("\">")
                        .Append(content).Append("</div>");
                }
                pageBands.Add(new(placement.Band.Section.Name, placement.Top, placement.Height, placement.Band.Section.BackgroundColor, objects));
                html.Append("</section>");
            }
            snapshots.Add(new(_layout.Document.Title, index + 1, new ReportDesignerPage
            {
                Orientation = size.Orientation, PaperSize = size.PaperSize, ContentWidthTwips = size.ContentWidthTwips, ContentHeightTwips = size.ContentHeightTwips,
                MarginLeftTwips = size.MarginLeftTwips, MarginRightTwips = size.MarginRightTwips, MarginTopTwips = size.MarginTopTwips, MarginBottomTwips = size.MarginBottomTwips
            }, pageBands));
            html.Append("</div></article>"); output.Add(html.ToString());
        }
        // Nested sessions' own diagnostics reach the result, except those a parent already copied
        // with its "<element>: " prefix (ReportLayoutSession.Runtime), which would show twice.
        var diagnostics = new List<string>(_diagnostics);
        foreach (var session in _state.Sessions.Where(session => !ReferenceEquals(session, this)))
            foreach (var diagnostic in session._diagnostics)
                if (!diagnostics.Any(d => d == diagnostic || d.EndsWith(": " + diagnostic, StringComparison.Ordinal)))
                    diagnostics.Add(diagnostic);
        return new(output, diagnostics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()) { Snapshots = snapshots };

        int ExpectedCount(int index) => expectedCounts is not null && index < expectedCounts.Count ? expectedCounts[index] : expectedPages;
        void HiddenEvent(Band hidden)
        {
            if (!HasVisibilityEvent(hidden.Section.Id)) return;
            if (page.Cursor >= page.Bottom) pendingHidden.Add(hidden);
            else page.Placements.Add(new(hidden, page.Cursor, 0, 0));
        }
        Band PageBand(int index, int? physicalIndex = null) => ForPage(Expand(ForPage(_bands[index], page.Number, ExpectedCount(physicalIndex ?? pages.Count - 1)), 0, page.Number), page.Number, ExpectedCount(physicalIndex ?? pages.Count - 1));
        Page NewPage(int row, List<Band> repeats, bool withHeaders = true)
        {
            if (pages.Count >= 10000) throw new InvalidDataException("Report exceeds the 10,000-page layout limit.");
            var number = resetPageNumber || pages.Count == 0 ? 1 : pages[^1].Number + 1;
            var footerRow = footerRows is not null && pages.Count < footerRows.Count ? footerRows[pages.Count] : row;
            var reserve = footers.Sum(section => Height(ForPage(_furniture[(section.Id, footerRow)], number, ExpectedCount(pages.Count), physicalPage: pages.Count + 1)));
            var next = new Page { Number = number, Bottom = size.ContentHeightTwips - reserve };
            resetPageNumber = false;
            if (withHeaders) AddHeaders(next, row);
            foreach (var repeat in repeats)
            {
                var header = ForPage(repeat, next.Number, ExpectedCount(pages.Count), true, pages.Count + 1);
                var height = Height(header);
                next.Placements.Add(new(header, next.Cursor, 0, height)); next.Cursor += height;
            }
            if (next.Cursor >= next.Bottom) throw new InvalidDataException("Page and repeating group headers plus footers leave no space for content.");
            next.BodyStart = next.Cursor;
            foreach (var hidden in pendingHidden) next.Placements.Add(new(hidden, next.Cursor, 0, 0));
            pendingHidden.Clear();
            pages.Add(next); return next;
        }
        void AddHeaders(Page target, int row)
        {
            foreach (var section in headers)
            {
                var physical = pages.Contains(target) ? pages.Count : pages.Count + 1;
                var band = ForPage(_furniture[(section.Id, row)], target.Number, ExpectedCount(physical - 1), physicalPage: physical); var height = Height(band);
                target.Placements.Add(new(band, target.Cursor, 0, height)); target.Cursor += height;
            }
            target.HasHeaders = true;
            if (!target.HasBody) target.BodyStart = target.Cursor;
        }
        void AddInlineHeaders(Page target, Band band, int offset)
        {
            foreach (var repeat in (band.InlineHeaders ?? []).Where(h => h.Repeat && h.Start < offset && h.End > offset).OrderBy(h => h.Start))
            {
                var header = ForPage(repeat.Header, target.Number, ExpectedCount(pages.Count - 1), true, pages.Count);
                var height = Height(header);
                if (target.Cursor + height >= target.Bottom) throw new InvalidDataException("Repeating inline subreport headers leave no space for content.");
                target.Placements.Add(new(header, target.Cursor, 0, height)); target.Cursor += height;
            }
        }
        Band Expand(Band source, int measurementOffset, int? objectPage = null, int? printableWidth = null)
        {
            source = ApplyPrintedItems(source, 0, false);
            if (source.Suppressed) return source;
            var items = new List<Item>();
            var breaks = new List<int>();
            var forcedBreaks = new List<int>();
            var inlineHeaders = new List<InlineHeader>();
            var inlineSections = new List<InlineSection>();
            var growth = new List<(int Bottom, int Amount, int Left, int Right)>();
            foreach (var item in source.Items.OrderBy(i => i.Element.TopTwips))
            {
                if (item.Element.IsSuppressed) { items.Add(item); continue; }
                var top = item.Element.TopTwips + growth.Where(g => g.Bottom <= item.Element.TopTwips
                    && (source.Section.RelativePositions || g.Left < item.Element.LeftTwips + item.Element.WidthTwips && item.Element.LeftTwips < g.Right))
                    .Select(g => g.Amount).DefaultIfEmpty(0).Max();
                if (item.Analysis?.Table is { } table)
                {
                    // Crystal clips a cross-tab that runs past the printable edge; never ask for panes
                    // narrower than the pager's minimum, which would fail the whole report.
                    var available = Math.Min(item.Element.WidthTwips, (printableWidth ?? size.ContentWidthTwips) - item.Element.LeftTwips);
                    var width = Math.Max(ReportTabularData.MinimumPaneWidthTwips, available);
                    if (width > available)
                        _diagnostics.Add($"{item.Element.Name}: table is narrower than the 0.25-inch minimum pane; content past its edge is clipped.");
                    var panes = table.PaginateColumns(width, item.Element.FontSize, item.Analysis.Definition.RowHeightTwips);
                    var regionCursor = top;
                    foreach (var pane in panes)
                    {
                        if (regionCursor > top) forcedBreaks.Add(regionCursor);
                        var start = regionCursor;
                        var headerItems = new List<Item>();
                        int? nestedHeaderStart = null;
                        foreach (var fragment in pane.Headers.Concat(pane.Rows))
                        {
                            if (fragment.KeepWithNext) nestedHeaderStart ??= regionCursor;
                            else if (nestedHeaderStart is { } keepStart)
                            {
                                inlineHeaders.Add(new(keepStart, regionCursor + fragment.HeightTwips,
                                    new(source.Section, [], item.Row, [], Owner: item.Owner), regionCursor + fragment.HeightTwips, false));
                                nestedHeaderStart = null;
                            }
                            var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name);
                            element.Id = item.Element.Id + $"-pane{fragment.Pane}-row{fragment.Row}-line{fragment.Line}";
                            element.LeftTwips = item.Element.LeftTwips; element.TopTwips = regionCursor;
                            element.WidthTwips = fragment.Columns.Sum(column => column.WidthTwips);
                            element.HeightTwips = fragment.HeightTwips; element.CanGrow = false;
                            var rowItem = item with { Element = element, Analysis = null, Table = fragment with { RegionId = item.Element.Id + "-" + item.Row }, Measurement = -1,
                                Html = ReportObjectRenderer.Encode(string.Join(" ", fragment.Cells)) };
                            items.Add(rowItem);
                            if (fragment.ContinuationHeaders.Count > 0)
                            {
                                var nestedHeaders = fragment.ContinuationHeaders.Select((header, index) =>
                                {
                                    var headerElement = element.CloneFor(element.SectionId, element.Name);
                                    headerElement.Id = element.Id + "-nested-header-" + index;
                                    headerElement.LeftTwips = element.LeftTwips;
                                    headerElement.TopTwips = index * fragment.HeightTwips;
                                    return rowItem with { Element = headerElement, Table = header with { RegionId = rowItem.Table!.RegionId } };
                                }).ToList();
                                var nestedSection = new ReportDesignerSection { Id = source.Section.Id, Name = item.Element.Name + " nested headers",
                                    Kind = source.Section.Kind, HeightTwips = nestedHeaders.Count * fragment.HeightTwips };
                                inlineHeaders.Add(new(regionCursor - 1, regionCursor + fragment.HeightTwips,
                                    new(nestedSection, nestedHeaders, item.Row, [], Owner: item.Owner)));
                            }
                            if (fragment.IsHeader) headerItems.Add(Shift(rowItem, 0, -start, element.WidthTwips + element.LeftTwips));
                            regionCursor += fragment.HeightTwips;
                            breaks.Add(regionCursor);
                        }
                        if (headerItems.Count > 0 && pane.Rows.Count > 0)
                        {
                            var headerSection = new ReportDesignerSection { Id = source.Section.Id, Name = item.Element.Name + " headers",
                                Kind = source.Section.Kind, HeightTwips = pane.Headers.Sum(header => header.HeightTwips) };
                            inlineHeaders.Add(new(start, regionCursor, new(headerSection, headerItems, item.Row, [], Owner: item.Owner),
                                start + headerSection.HeightTwips + pane.Rows[0].HeightTwips, item.Analysis.Definition.RepeatHeaders));
                        }
                    }
                    growth.Add((item.Element.TopTwips + item.Element.HeightTwips,
                        Math.Max(0, regionCursor - item.Element.TopTwips - item.Element.HeightTwips), item.Element.LeftTwips, item.Element.LeftTwips + width));
                    continue;
                }
                if (item.Child is not { } child)
                {
                    var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name); element.Id = item.Element.Id; element.TopTwips = top; element.LeftTwips = item.Element.LeftTwips;
                    var positioned = item with { Element = element, Measurement = item.Measurement < 0 ? item.Measurement : item.Measurement + measurementOffset };
                    items.Add(positioned);
                    if (source.Section.RelativePositions)
                        growth.Add((item.Element.TopTwips + item.Element.HeightTwips, top - item.Element.TopTwips + Math.Max(0, ItemHeight(positioned) - item.Element.HeightTwips),
                            item.Element.LeftTwips, item.Element.LeftTwips + item.Element.WidthTwips));
                    continue;
                }
                var cursor = top;
                var extent = top;
                var active = new Dictionary<Band, (int Start, Band Header)>();
                foreach (var original in child._bands)
                {
                    var context = inlineContexts?.GetValueOrDefault(original) ?? (Page: 1, Count: ExpectedCount(0));
                    if (context.Page == 0) context = (1, ExpectedCount(0));
                    var childWidth = Math.Min(item.Element.WidthTwips, (printableWidth ?? size.ContentWidthTwips) - item.Element.LeftTwips);
                    var nested = Expand(ForPage(original, context.Page, context.Count, objectPage: objectPage), measurementOffset + item.ChildMeasurementOffset, objectPage, childWidth);
                    if (nested.Suppressed) { inlineSections.Add(new(cursor, cursor, original, nested.Section)); continue; }
                    foreach (var previous in active.Keys.Where(k => !original.Repeats.Contains(k)).ToArray())
                    { var header = active[previous]; inlineHeaders.Add(new(header.Start, cursor, header.Header)); active.Remove(previous); }
                    foreach (var repeat in original.Repeats.Where(r => !active.ContainsKey(r)))
                    {
                        var header = Expand(repeat, measurementOffset + item.ChildMeasurementOffset, objectPage, childWidth);
                        active[repeat] = (cursor, header with { Items = header.Items.Select(i => Shift(i, item.Element.LeftTwips, 0, item.Element.WidthTwips)).ToList() });
                    }
                    var nestedArea = child._layout.Areas.GetValueOrDefault(original.Section.Id);
                    if ((nested.Section.NewPageBefore || nestedArea?.NewPageBefore == true) && cursor > 0) forcedBreaks.Add(cursor);
                    breaks.Add(cursor);
                    items.AddRange(nested.Items.Select(i => Shift(i with { Html = i.Html.Replace(child._pageToken, _pageToken, StringComparison.Ordinal).Replace(child._countToken, _countToken, StringComparison.Ordinal) }, item.Element.LeftTwips, cursor, item.Element.WidthTwips)));
                    breaks.AddRange(nested.Breaks?.Select(b => cursor + b) ?? []);
                    forcedBreaks.AddRange(nested.ForcedBreaks?.Select(b => cursor + b) ?? []);
                    inlineHeaders.AddRange(nested.InlineHeaders?.Select(h => new InlineHeader(cursor + h.Start, cursor + h.End,
                        h.Header with { Items = h.Header.Items.Select(i => Shift(i, item.Element.LeftTwips, 0, item.Element.WidthTwips)).ToList() },
                        h.KeepThrough == 0 ? 0 : cursor + h.KeepThrough, h.Repeat)) ?? []);
                    inlineSections.AddRange(nested.InlineSections?.Select(s => s with { Start = cursor + s.Start, End = cursor + s.End }) ?? []);
                    var end = cursor + Height(nested);
                    inlineSections.Add(new(cursor, end, original, nested.Section));
                    extent = Math.Max(extent, end);
                    if (!nested.Section.UnderlayFollowingSections) cursor = end;
                    var conditionalAfter = child.Conditions(child._layout.SectionConditions, original.Section.Id, true)
                        .Concat(child.Conditions(child._layout.AreaConditions, original.Section.Id, true))
                        .Any(c => c.Key.Equals("EnableNewPageAfter", StringComparison.OrdinalIgnoreCase));
                    if (!conditionalAfter && (nested.Section.NewPageAfter || nestedArea?.NewPageAfter == true)) forcedBreaks.Add(end);
                    breaks.Add(end);
                }
                foreach (var header in active.Values) inlineHeaders.Add(new(header.Start, cursor, header.Header));
                growth.Add((item.Element.TopTwips + item.Element.HeightTwips, Math.Max(0, extent - item.Element.TopTwips - item.Element.HeightTwips),
                    item.Element.LeftTwips, item.Element.LeftTwips + item.Element.WidthTwips));
            }
            var section = new ReportDesignerSection
            {
                Id = source.Section.Id, Name = source.Section.Name, Kind = source.Section.Kind, HeightTwips = source.Section.HeightTwips + growth.Select(g => g.Amount).DefaultIfEmpty(0).Max(),
                BackgroundColor = source.Section.BackgroundColor, KeepTogether = source.Section.KeepTogether, NewPageBefore = source.Section.NewPageBefore,
                NewPageAfter = source.Section.NewPageAfter, PrintAtBottomOfPage = source.Section.PrintAtBottomOfPage,
                ResetPageNumberAfter = source.Section.ResetPageNumberAfter, RelativePositions = source.Section.RelativePositions,
                SuppressIfBlank = source.Section.SuppressIfBlank, UnderlayFollowingSections = source.Section.UnderlayFollowingSections
            };
            var expanded = source with { Section = section, Items = items, Breaks = breaks.Distinct().Order().ToList(), ForcedBreaks = forcedBreaks.Distinct().Order().ToList(), InlineHeaders = inlineHeaders, InlineSections = inlineSections };
            // Only break between child bands when no neighboring object crosses that boundary.
            return expanded with { Breaks = SafeBreaks(expanded) };
        }
        static Item Shift(Item item, int left, int top, int width)
        {
            var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name);
            element.Id = item.Element.Id;
            element.LeftTwips = item.Element.LeftTwips; element.TopTwips = item.Element.TopTwips;
            element.WidthTwips = Math.Min(element.WidthTwips, Math.Max(0, width - element.LeftTwips));
            element.LeftTwips += left; element.TopTwips += top;
            return item with { Element = element };
        }
        int Height(Band band) => band.Suppressed ? 0 : Math.Max(band.Section.HeightTwips, band.Items.Where(i => !i.Element.IsSuppressed).Select(item => item.Element.TopTwips + Math.Max(15, ItemHeight(item))).DefaultIfEmpty(0).Max());
        int ItemHeight(Item item)
        {
            if (item.Element.IsSuppressed) return 0;
            var metric = TextMetrics(item);
            if (metric is not null && item.Element.CanGrow) return Math.Max(item.Element.HeightTwips, metric.Height);
            if (!item.Element.CanGrow || item.Measurement == -1) return item.Element.HeightTwips;
            if (item.Measurement >= 0 && measuredHeights is not null && measuredHeights.Count == Measurements.Count && measuredHeights[item.Measurement] is >= 0 and <= 10000000)
                return Math.Max(item.Element.HeightTwips, measuredHeights[item.Measurement]);
            var text = System.Text.RegularExpressions.Regex.Replace(item.Html, "<[^>]+>", "");
            var font = Math.Max(item.Element.FontSize, item.Element.Visual.Runs.Select(run => run.FontSize).DefaultIfEmpty(1).Max());
            var capacity = Math.Max(1, (int)(item.Element.WidthTwips / Math.Max(1, font * 10)));
            return Math.Max(item.Element.HeightTwips, (int)Math.Ceiling(text.Split('\n').Sum(line => Math.Max(1, Math.Ceiling(line.Length / (double)capacity))) * (double)font * 23));
        }
        List<int> SafeBreaks(Band band)
        {
            var candidates = new List<int>(band.Breaks ?? []);
            foreach (var item in band.Items)
            {
                if (item.Element.IsSuppressed) continue;
                var top = item.Element.TopTwips;
                candidates.Add(top); candidates.Add(top + ItemHeight(item));
                if (TextMetrics(item) is { } metric)
                    foreach (var line in metric.Lines.Where(l => l.Bottom <= ItemHeight(item)))
                    { candidates.Add(top + line.Top); candidates.Add(top + line.Bottom); }
            }
            return candidates.Distinct().Where(point => point > 0 && SafeAt(band, point)).Order().ToList();
        }
        bool SafeAt(Band band, int point) => !(band.InlineHeaders?.Any(header => header.Start < point && point < header.KeepThrough) ?? false)
            && band.Items.All(item =>
            {
                var relative = point - item.Element.TopTwips;
                if (item.Element.IsSuppressed || relative <= 0 || relative >= ItemHeight(item) || item.Element.Kind is "Line" or "Box") return true;
                if (!item.Element.CanGrow && ItemHeight(item) <= size.ContentHeightTwips) return false;
                return TextMetrics(item) is { } metric && metric.Lines.All(line => relative <= line.Top || relative >= line.Bottom);
            });
        bool CanContinueAt(Band band, int point) => band.Items.All(item =>
        {
            var relative = point - item.Element.TopTwips;
            return relative <= 0 || relative >= ItemHeight(item) || item.Element.Kind is "Line" or "Box"
                || item.Element.CanGrow && TextMetrics(item) is { } metric && metric.Lines.All(l => l.Start >= 0 && l.End >= l.Start);
        });
        static string Px(int twips) => ReportObjectRenderer.Number(twips / 15d);
    }
}
