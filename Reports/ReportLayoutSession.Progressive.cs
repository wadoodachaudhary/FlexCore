namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    private int _paginationPageLimit;
    private Dictionary<string, ReportTextMetrics>? _progressiveMetrics;
    private sealed class PagePrefixComplete : Exception;

    // Layouts with global physical-page replay or retrospective underlays must settle as a whole.
    // Ordinary read-time grouping and summaries are safe: this session already owns all selected rows.
    public bool SupportsProgressivePagination => !_state.PhysicalSchedule && _state.Sessions.Count == 1
        && _layout.Columns is null && _layout.Subreports.Count == 0
        && !_layout.Document.Sections.Any(s => s.UnderlayFollowingSections)
        && !_layout.Formulas.Values.Concat(_layout.SectionConditions.Values.SelectMany(c => c.Values))
            .Concat(_layout.AreaConditions.Values.SelectMany(c => c.Values))
            .Concat(_layout.ObjectConditions.Values.SelectMany(c => c.Values))
            .Any(f => f.UsesPageContext || f.CustomFunctions.Count > 0
                || f.References.Any(r => SpecialName(r) is "totalpagecount" or "pagenofm"))
        && !_layout.Document.Elements.Any(e => SpecialName(e.Binding) is "totalpagecount" or "pagenofm"
            || e.Visual.Runs.Any(r => SpecialName(r.Binding) is "totalpagecount" or "pagenofm"));

    /// <summary>Formats a stable page prefix, retaining measured fonts for the next request.
    /// A report requiring final-page formulas is completed eagerly. A zero limit completes any report.</summary>
    public Task<ReportLayoutResult> PaginateThroughAsync(int pageCount,
        Func<IReadOnlyList<ReportTextMeasurement>, CancellationToken, Task<IReadOnlyList<ReportTextMetrics>>> measure,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        return MeasurePagesAsync(measure, cancellationToken, SupportsProgressivePagination ? pageCount : 0, retainMetrics: true);
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
