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
    public CrystalFormula? RecordSelection { get; set; }
    public CrystalFormula? GroupSelection { get; set; }
    internal List<string> Projections { get; } = [];
}

public sealed record ReportLayoutSummary(string Operation, string Field, string Group);
public sealed record ReportLayoutArea(bool Suppressed, bool RepeatHeader, bool NewPageBefore, bool NewPageAfter, bool Hidden = false);
public sealed record ReportTextMeasurement(string Style, string Html);
public sealed record ReportLayoutResult(List<string> Pages, List<string> Diagnostics);
public sealed record ReportLayoutParameterLink(string Parameter, string MainField, string ChildField);
public sealed record ReportLayoutSubreport(ReportDefinition Definition, List<ReportLayoutParameterLink> Links);

/// <summary>Detail rows -> measured bands -> fixed-size pages. No browser, SQL, or Crystal dependency.</summary>
public sealed partial class ReportLayoutSession
{
    private readonly ReportPositionedLayout _layout;
    private DataRow[] _rows;
    private readonly IReadOnlyDictionary<string, object> _parameters;
    private readonly List<Band> _bands = [];
    private readonly Dictionary<string, int> _measurementKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _diagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (int Start, int End)[]> _groupRanges = new();
    private readonly Dictionary<(ReportLayoutSummary Summary, int Start, int End), object?> _summaryCache = new();
    private readonly string _pageToken = Guid.NewGuid().ToString("N") + "-page";
    private readonly string _countToken = Guid.NewGuid().ToString("N") + "-count";
    private readonly DateTime _printTime = DateTime.Now;
    public List<ReportTextMeasurement> Measurements { get; } = [];
    private sealed record Item(ReportDesignerElement Element, string Html, int Measurement, ReportLayoutSession? Child = null, int ChildMeasurementOffset = 0, ReportLayoutSession? Owner = null, int Row = 0);
    private sealed record InlineHeader(int Start, int End, Band Header);
    private sealed record Band(ReportDesignerSection Section, List<Item> Items, int Row, List<Band> Repeats, bool Suppressed = false,
        List<int>? Breaks = null, List<int>? ForcedBreaks = null, List<InlineHeader>? InlineHeaders = null, bool RepeatedHeader = false);
    private sealed record Placement(Band Band, int Top, int Offset, int Height);
    private sealed class Page
    {
        public List<Placement> Placements { get; } = [];
        public int Cursor { get; set; }
        public int Bottom { get; set; }
        public bool HasBody { get; set; }
        public bool HasHeaders { get; set; }
    }

    public ReportLayoutSession(ReportPositionedLayout layout, DataTable data, IReadOnlyDictionary<string, object>? parameters = null,
        Func<ReportDefinition, IReadOnlyDictionary<string, object>, DataTable>? executeSubreport = null)
        : this(layout, data, parameters, executeSubreport, new RuntimeState(), 0) { }

    private ReportLayoutSession(ReportPositionedLayout layout, DataTable data, IReadOnlyDictionary<string, object>? parameters,
        Func<ReportDefinition, IReadOnlyDictionary<string, object>, DataTable>? executeSubreport, RuntimeState state, int depth)
    {
        _layout = layout;
        _rows = data.Rows.Cast<DataRow>().ToArray();
        _parameters = new Dictionary<string, object>(parameters ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
        _executeSubreport = executeSubreport; _state = state; _depth = depth;
        _state.HasPageConditions |= layout.SectionConditions.Values.Concat(layout.AreaConditions.Values).Concat(layout.ObjectConditions.Values).SelectMany(c => c.Values).Any(UsesPage);
        if (ReportDesignerEditing.ValidatePage(layout.Document.Page) is { } error) throw new InvalidDataException(error);
        PrepareRows();
        BuildBands();
    }

    public static bool IsSpecial(string reference) => SpecialName(reference) is "pagenumber" or "totalpagecount" or "pagenofm" or "reporttitle" or "reportfilename" or "printdate" or "printtime" or "recordnumber";
    private static string SpecialName(string reference) => reference.Trim('{', '}').Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    private object? Value(string reference, int row)
    {
        reference = reference.Trim();
        if (_layout.Formulas.ContainsKey(reference)) return FormulaValue(reference, row);
        if (_layout.FormulaErrors.TryGetValue(reference, out var error)) throw new InvalidDataException($"{reference}: {error}");
        if (_layout.RunningTotals.TryGetValue(reference, out var running)) return Summary(running, row, row + 1);
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
        if (!Visible(section)) return new(section, [], row, repeats.ToList(), true);
        var items = new List<Item>();
        foreach (var original in section.Elements)
        {
            var element = ApplyObjectConditions(original, row);
            if (element.IsSuppressed) continue;
            if (element.Kind == "Picture" && !ReportObjectVisual.IsEmbeddedImage(element.Visual.ImageDataUrl))
                _diagnostics.Add($"{element.Name}: embedded image bytes are missing. Replace the picture in the designer.");
            var child = element.Kind == "Subreport" ? CreateInlineSubreport(element, row) : null;
            var childOffset = Measurements.Count;
            if (child is not null) Measurements.AddRange(child.Measurements);
            if (element.Kind is not ("Text" or "Field" or "FieldHeading" or "Picture" or "Line" or "Box" or "Subreport"))
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
            items.Add(new(element, html, measure, child, childOffset, this, row));
        }
        return new(section, items, row, repeats.ToList());
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
                if (band.Suppressed) continue;
                _bands.Add(band);
                if (kind == "GroupHeader" && (_layout.Areas.GetValueOrDefault(section.Id)?.RepeatHeader ?? false)) repeats.Add(band);
            }
        }
        Add("ReportHeader", _rows.Length > 0 ? 0 : -1);
        for (var row = 0; row < _rows.Length; row++)
        {
            for (var group = 0; group < _layout.Document.Groups.Count; group++)
                if (row == 0 || !SameGroup(row - 1, row, group)) Add("GroupHeader", row, group);
            Add("Detail", row);
            for (var group = _layout.Document.Groups.Count - 1; group >= 0; group--)
                if (row + 1 == _rows.Length || !SameGroup(row, row + 1, group))
                {
                    Add("GroupFooter", row, group);
                    repeats.RemoveAll(band => ReportDesignerEditing.GetGroupNumber(_layout.Document, band.Section) == group + 1);
                }
        }
        Add("ReportFooter", _rows.Length - 1);
        // Page furniture can bind to the first/last row on each page. Prepare all variants before DOM measurement.
        foreach (var section in sections.Where(section => section.Kind is "PageHeader" or "PageFooter"))
            for (var row = -1; row < _rows.Length; row++) _furniture[(section.Id, row)] = CreateBand(section, row, []);
    }

    private readonly Dictionary<(string Id, int Row), Band> _furniture = new();

    public ReportLayoutResult Paginate(IReadOnlyList<int>? measuredHeights = null)
        => Paginate(measuredHeights, 1, 0);

    private ReportLayoutResult Paginate(IReadOnlyList<int>? measuredHeights, int expectedPages, int attempt)
    {
        if (Measurements.Count > 0 && (measuredHeights is null || measuredHeights.Count != Measurements.Count))
            _diagnostics.Add("Browser text measurement is unavailable; CanGrow pagination uses an approximate font metric.");
        var size = _layout.Document.Page;
        var bands = _bands.Select(band => Expand(band, 0)).ToList();
        var pages = new List<Page>();
        var headers = _layout.Document.Sections.Where(section => section.Kind == "PageHeader").ToList();
        var footers = _layout.Document.Sections.Where(section => section.Kind == "PageFooter").ToList();
        var footerReserve = footers.Sum(section => _furniture.Where(pair => pair.Key.Id == section.Id).Select(pair => Height(pair.Value)).DefaultIfEmpty(section.HeightTwips).Max());
        var page = NewPage(bands.FirstOrDefault()?.Row ?? -1, [], bands.FirstOrDefault()?.Section.Kind != "ReportHeader");
        for (var bandIndex = 0; bandIndex < bands.Count; bandIndex++)
        {
            var band = ForPage(bands[bandIndex], pages.Count, expectedPages);
            if (band.Suppressed) continue;
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
                if (keepHeight > page.Bottom - page.Cursor) page = NewPage(band.Row, band.Repeats);
            }
            if (band.Section.UnderlayFollowingSections)
            {
                page.Placements.Add(new(band, page.Cursor, 0, height)); page.HasBody = true; continue;
            }
            if (height > page.Bottom - page.Cursor && page.HasBody && band.Section.KeepTogether) page = NewPage(band.Row, band.Repeats, withHeaders);
            band = ForPage(bands[bandIndex], pages.Count, expectedPages);
            if (band.Suppressed) continue;
            height = Height(band);
            var offset = 0;
            do
            {
                if (page.Bottom <= page.Cursor) page = NewPage(band.Row, band.Repeats, withHeaders);
                var available = page.Bottom - page.Cursor;
                if (available <= 0) throw new InvalidDataException("Page headers and footers leave no room for report content.");
                var slice = Math.Min(height - offset, available);
                var forced = band.ForcedBreaks?.Where(point => point > offset && point < offset + slice).DefaultIfEmpty(0).Min() ?? 0;
                if (forced > 0) slice = forced - offset;
                else if (slice < height - offset && band.Breaks is { Count: > 0 })
                {
                    var boundary = band.Breaks.Where(point => point > offset && point <= offset + slice).DefaultIfEmpty(0).Max();
                    if (boundary > 0) slice = boundary - offset;
                    else if (page.HasBody && band.Breaks.FirstOrDefault(point => point > offset) - offset <= page.Bottom)
                    { page = NewPage(band.Row, band.Repeats, withHeaders); AddInlineHeaders(page, band, offset); continue; }
                }
                if ((offset > 0 || slice < height) && (band.Breaks is not { Count: > 0 } || slice < height - offset && !band.Breaks.Contains(offset + slice)))
                    _diagnostics.Add($"{band.Section.Name}: a band spans pages; overflowing objects are clipped at the page boundary.");
                var top = band.Section.PrintAtBottomOfPage && slice == height ? page.Bottom - slice : page.Cursor;
                page.Placements.Add(new(ForPage(bands[bandIndex], pages.Count, expectedPages), top, offset, slice));
                page.Cursor = top + slice;
                page.HasBody = true;
                offset += slice;
                if (offset < height) { page = NewPage(band.Row, band.Repeats, withHeaders); AddInlineHeaders(page, band, offset); }
            } while (offset < height);
            if (band.Section.NewPageAfter || area?.NewPageAfter == true) page.Cursor = page.Bottom;
        }

        if (pages.Count != expectedPages && _state.HasPageConditions)
        {
            if (attempt >= 7) throw new InvalidDataException("Page-dependent formatting did not converge within eight pagination passes.");
            return Paginate(measuredHeights, pages.Count, attempt + 1);
        }
        var output = new List<string>();
        for (var index = 0; index < pages.Count; index++)
        {
            var current = pages[index];
            var lastRow = current.Placements.LastOrDefault(placement => placement.Band.Section.Kind != "PageHeader")?.Band.Row ?? -1;
            var footerTop = size.ContentHeightTwips - footerReserve;
            foreach (var section in footers)
            {
                var footer = ForPage(_furniture[(section.Id, lastRow)], index + 1, pages.Count);
                var height = Height(footer);
                current.Placements.Add(new(footer, footerTop, 0, height)); footerTop += height;
            }
            var html = new StringBuilder();
            html.Append("<article class=\"fx-report-positioned-page\" aria-label=\"").Append(ReportObjectRenderer.Encode(_layout.Document.Title)).Append("\" data-page=\"").Append(index + 1).Append("\" style=\"position:relative;box-sizing:content-box;overflow:hidden;background:white;color:black;letter-spacing:0;width:")
                .Append(Px(size.ContentWidthTwips + size.MarginLeftTwips + size.MarginRightTwips)).Append("px;height:")
                .Append(Px(size.ContentHeightTwips + size.MarginTopTwips + size.MarginBottomTwips)).Append("px;break-after:page;\">");
            html.Append("<div style=\"position:absolute;left:").Append(Px(size.MarginLeftTwips)).Append("px;top:").Append(Px(size.MarginTopTwips)).Append("px;width:").Append(Px(size.ContentWidthTwips)).Append("px;height:").Append(Px(size.ContentHeightTwips)).Append("px;overflow:hidden;\">");
            foreach (var placement in current.Placements)
            {
                if (placement.Band.Suppressed) continue;
                html.Append("<section data-section=\"").Append(ReportObjectRenderer.Encode(placement.Band.Section.Name)).Append("\" style=\"position:absolute;overflow:hidden;left:0;width:100%;top:").Append(Px(placement.Top))
                    .Append("px;height:").Append(Px(placement.Height)).Append("px;background:").Append(ReportObjectRenderer.Color(placement.Band.Section.BackgroundColor)).Append(";\">");
                foreach (var item in placement.Band.Items)
                {
                    var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name);
                    element.LeftTwips = item.Element.LeftTwips; element.TopTwips = item.Element.TopTwips - placement.Offset;
                    element.HeightTwips = ItemHeight(item);
                    if (element.TopTwips >= placement.Height || element.TopTwips + element.HeightTwips <= 0) continue;
                    html.Append("<div data-object=\"").Append(ReportObjectRenderer.Encode(element.Name)).Append("\" style=\"").Append(ReportObjectRenderer.Style(element)).Append("\">")
                        .Append(ResolvePageValues(item.Html.Replace(_pageToken, (index + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace(_countToken, pages.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), index + 1, pages.Count, placement.Band.RepeatedHeader)).Append("</div>");
                }
                html.Append("</section>");
            }
            html.Append("</div></article>"); output.Add(html.ToString());
        }
        return new(output, _diagnostics.Order(StringComparer.Ordinal).ToList());

        Page NewPage(int row, List<Band> repeats, bool withHeaders = true)
        {
            if (pages.Count >= 10000) throw new InvalidDataException("Report exceeds the 10,000-page layout limit.");
            var next = new Page { Bottom = size.ContentHeightTwips - footerReserve };
            if (withHeaders) AddHeaders(next, row);
            foreach (var repeat in repeats)
            {
                var header = ForPage(repeat, pages.Count + 1, expectedPages, true);
                var height = Height(header);
                next.Placements.Add(new(header, next.Cursor, 0, height)); next.Cursor += height;
            }
            if (next.Cursor >= next.Bottom) throw new InvalidDataException("Page and repeating group headers plus footers leave no space for content.");
            pages.Add(next); return next;
        }
        void AddHeaders(Page target, int row)
        {
            foreach (var section in headers)
            {
                var band = ForPage(_furniture[(section.Id, row)], pages.Count + (pages.Contains(target) ? 0 : 1), expectedPages); var height = Height(band);
                target.Placements.Add(new(band, target.Cursor, 0, height)); target.Cursor += height;
            }
            target.HasHeaders = true;
        }
        void AddInlineHeaders(Page target, Band band, int offset)
        {
            foreach (var repeat in band.InlineHeaders?.Where(h => h.Start < offset && h.End > offset) ?? [])
            {
                var header = ForPage(repeat.Header, pages.Count, expectedPages, true);
                var height = Height(header);
                if (target.Cursor + height >= target.Bottom) throw new InvalidDataException("Repeating inline subreport headers leave no space for content.");
                target.Placements.Add(new(header, target.Cursor, 0, height)); target.Cursor += height;
            }
        }
        Band Expand(Band source, int measurementOffset)
        {
            if (source.Suppressed) return source;
            var items = new List<Item>();
            var breaks = new List<int>();
            var forcedBreaks = new List<int>();
            var inlineHeaders = new List<InlineHeader>();
            var growth = new List<(int Bottom, int Amount)>();
            foreach (var item in source.Items.OrderBy(i => i.Element.TopTwips))
            {
                var top = item.Element.TopTwips + growth.Where(g => g.Bottom <= item.Element.TopTwips).Select(g => g.Amount).DefaultIfEmpty(0).Max();
                if (item.Child is not { } child)
                {
                    var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name); element.Id = item.Element.Id; element.TopTwips = top; element.LeftTwips = item.Element.LeftTwips;
                    items.Add(item with { Element = element, Measurement = item.Measurement < 0 ? -1 : item.Measurement + measurementOffset }); continue;
                }
                var cursor = top;
                var active = new Dictionary<Band, (int Start, Band Header)>();
                foreach (var original in child._bands)
                {
                    var nested = Expand(original, measurementOffset + item.ChildMeasurementOffset);
                    if (nested.Suppressed) continue;
                    foreach (var previous in active.Keys.Where(k => !original.Repeats.Contains(k)).ToArray())
                    { var header = active[previous]; inlineHeaders.Add(new(header.Start, cursor, header.Header)); active.Remove(previous); }
                    foreach (var repeat in original.Repeats.Where(r => !active.ContainsKey(r)))
                    {
                        var header = Expand(repeat, measurementOffset + item.ChildMeasurementOffset);
                        active[repeat] = (cursor, header with { Items = header.Items.Select(i => Shift(i, item.Element.LeftTwips, 0, item.Element.WidthTwips)).ToList() });
                    }
                    if (nested.Section.NewPageBefore && cursor > top) forcedBreaks.Add(cursor);
                    breaks.Add(cursor);
                    items.AddRange(nested.Items.Select(i => Shift(i with { Html = i.Html.Replace(child._pageToken, _pageToken, StringComparison.Ordinal).Replace(child._countToken, _countToken, StringComparison.Ordinal) }, item.Element.LeftTwips, cursor, item.Element.WidthTwips)));
                    breaks.AddRange(nested.Breaks?.Select(b => cursor + b) ?? []);
                    forcedBreaks.AddRange(nested.ForcedBreaks?.Select(b => cursor + b) ?? []);
                    inlineHeaders.AddRange(nested.InlineHeaders?.Select(h => new InlineHeader(cursor + h.Start, cursor + h.End,
                        h.Header with { Items = h.Header.Items.Select(i => Shift(i, item.Element.LeftTwips, 0, item.Element.WidthTwips)).ToList() })) ?? []);
                    cursor += Height(nested);
                    if (nested.Section.NewPageAfter) forcedBreaks.Add(cursor);
                    breaks.Add(cursor);
                }
                foreach (var header in active.Values) inlineHeaders.Add(new(header.Start, cursor, header.Header));
                growth.Add((item.Element.TopTwips + item.Element.HeightTwips, Math.Max(0, cursor - item.Element.TopTwips - item.Element.HeightTwips)));
            }
            var section = new ReportDesignerSection
            {
                Id = source.Section.Id, Name = source.Section.Name, Kind = source.Section.Kind, HeightTwips = source.Section.HeightTwips + growth.Select(g => g.Amount).DefaultIfEmpty(0).Max(),
                BackgroundColor = source.Section.BackgroundColor, KeepTogether = source.Section.KeepTogether, NewPageBefore = source.Section.NewPageBefore,
                NewPageAfter = source.Section.NewPageAfter, PrintAtBottomOfPage = source.Section.PrintAtBottomOfPage,
                SuppressIfBlank = source.Section.SuppressIfBlank, UnderlayFollowingSections = source.Section.UnderlayFollowingSections
            };
            var expanded = source with { Section = section, Items = items, Breaks = breaks.Distinct().Order().ToList(), ForcedBreaks = forcedBreaks.Distinct().Order().ToList(), InlineHeaders = inlineHeaders };
            // Only break between child bands when no neighboring object crosses that boundary.
            expanded.Breaks.RemoveAll(point => items.Any(i => i.Element.TopTwips < point && i.Element.TopTwips + ItemHeight(i) > point && i.Element.Kind is not ("Line" or "Box")));
            return expanded;
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
        int Height(Band band) => band.Suppressed ? 0 : Math.Max(band.Section.HeightTwips, band.Items.Select(item => item.Element.TopTwips + Math.Max(15, ItemHeight(item))).DefaultIfEmpty(0).Max());
        int ItemHeight(Item item)
        {
            if (!item.Element.CanGrow || item.Measurement == -1) return item.Element.HeightTwips;
            if (item.Measurement >= 0 && measuredHeights is not null && measuredHeights.Count == Measurements.Count && measuredHeights[item.Measurement] is >= 0 and <= 10000000)
                return Math.Max(item.Element.HeightTwips, measuredHeights[item.Measurement]);
            var text = System.Text.RegularExpressions.Regex.Replace(item.Html, "<[^>]+>", "");
            var font = Math.Max(item.Element.FontSize, item.Element.Visual.Runs.Select(run => run.FontSize).DefaultIfEmpty(1).Max());
            var capacity = Math.Max(1, (int)(item.Element.WidthTwips / Math.Max(1, font * 10)));
            return Math.Max(item.Element.HeightTwips, (int)Math.Ceiling(text.Split('\n').Sum(line => Math.Max(1, Math.Ceiling(line.Length / (double)capacity))) * (double)font * 23));
        }
        static string Px(int twips) => ReportObjectRenderer.Number(twips / 15d);
    }
}
