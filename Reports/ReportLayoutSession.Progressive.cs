namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    private int _paginationPageLimit;
    private Dictionary<string, ReportTextMetrics>? _progressiveMetrics;
    private sealed class PagePrefixComplete : Exception;

    // Layouts with global physical-page replay or retrospective underlays must settle as a whole.
    // Ordinary read-time grouping and summaries are safe: this session already owns all selected rows.
    public bool SupportsProgressivePagination => (_subreportsOnDemand || !_state.PhysicalSchedule && _state.Sessions.Count == 1 && _layout.Subreports.Count == 0)
        && _layout.Columns is null
        && !_layout.Document.Sections.Any(s => s.UnderlayFollowingSections)
        && !_layout.Formulas.Values.Concat(_layout.SectionConditions.Values.SelectMany(c => c.Values))
            .Concat(_layout.AreaConditions.Values.SelectMany(c => c.Values))
            .Concat(_layout.ObjectConditions.Values.SelectMany(c => c.Values))
            .Any(f => f.UsesPageContext || f.CustomFunctions.Count > 0
                || f.References.Any(r => SpecialName(r) is "totalpagecount" or "pagenofm"))
        && (EstimatedCountPrints || !_layout.Document.Elements.Any(e => SpecialName(e.Binding) is "totalpagecount" or "pagenofm"
            || e.Visual.Runs.Any(r => SpecialName(r.Binding) is "totalpagecount" or "pagenofm")));

    /// <summary>The page count to print while only a first part of the report is laid out: the host's estimate of the
    /// whole report. Null (the default) keeps a report that prints its page count out of progressive pagination, as
    /// before. Once the report is laid out in full, every page prints the real count.</summary>
    public int? PageCountEstimate { get; set; }

    // An estimated count can stand in for the real one only where the count is nothing but text of the page furniture
    // (it then decides no position and no other value) and the report numbers its pages as one run.
    private bool EstimatedCountPrints => PageCountEstimate is not null && CountIsFurnitureText && !RestartsPageNumbers;
    private bool? _countIsFurnitureText;
    // No formula or conditional format reads the page count, and the TotalPageCount and PageNofM fields sit only in
    // page footers and, at a fixed size, in page headers.
    private bool CountIsFurnitureText => _countIsFurnitureText ??= !PageCountShapesBody || CountOnlyInFixedFurniture();
    private bool CountOnlyInFixedFurniture()
    {
        static bool IsCount(string reference) => SpecialName(reference) is "totalpagecount" or "pagenofm";
        static bool Prints(ReportDesignerElement element) => IsCount(element.Binding) || element.Visual.Runs.Any(run => IsCount(run.Binding));
        return !_layout.Formulas.Values.Concat(_layout.SectionConditions.Values.SelectMany(c => c.Values))
                .Concat(_layout.AreaConditions.Values.SelectMany(c => c.Values))
                .Concat(_layout.ObjectConditions.Values.SelectMany(c => c.Values))
                .Any(formula => formula.UsesTotalPageCount || formula.CustomFunctions.Count > 0 || formula.References.Any(IsCount))
            && _layout.Document.Sections.All(section => section.Kind == "PageFooter"
                || !section.Elements.Any(Prints) || section.Kind == "PageHeader" && section.Elements.Where(Prints).All(element => !element.CanGrow));
    }
    private bool RestartsPageNumbers => _layout.Document.Sections.Any(section => section.ResetPageNumberAfter)
        || _layout.Areas.Values.Any(area => area.ResetPageNumberAfter)
        || _layout.SectionConditions.Values.Concat(_layout.AreaConditions.Values)
            .Any(conditions => conditions.Keys.Any(key => key.Equals("EnableResetPageNumberAfter", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Formats a stable page prefix, retaining measured fonts for the next request.
    /// A report requiring final-page formulas is completed eagerly. A zero limit completes any report.</summary>
    public Task<ReportLayoutResult> PaginateThroughAsync(int pageCount,
        Func<IReadOnlyList<ReportTextMeasurement>, CancellationToken, Task<IReadOnlyList<ReportTextMetrics>>> measure,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        // A report that reads subreports by page lays out one page more, so the last page shown ends as it does in the whole report.
        var through = !SupportsProgressivePagination ? 0 : pageCount > 0 && _subreportsOnDemand ? pageCount + 1 : pageCount;
        return MeasurePagesAsync(measure, cancellationToken, through, retainMetrics: true);
    }

    private sealed class DeferredBands(IReadOnlyList<Band> source, Func<Band, Band> expand) : IReadOnlyList<Band>
    {
        private readonly Dictionary<int, Band> _expanded = [];
        public int Count => source.Count;
        public Band this[int index] => _expanded.TryGetValue(index, out var value) ? value : _expanded[index] = expand(source[index]);
        public IEnumerator<Band> GetEnumerator()
        {
            for (var index = 0; index < Count; index++) yield return this[index];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
