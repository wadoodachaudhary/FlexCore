namespace Fx.ControlKit.Reports;

public sealed record ReportTextLine(int Top, int Bottom, int Start = -1, int End = -1);
/// <summary>Text bounds from the browser, or from <see cref="CrystalDeviceText"/> when <paramref name="Native"/> is set.</summary>
public sealed record ReportTextMetrics(int Height, IReadOnlyList<ReportTextLine> Lines, double Width = -1, double HashWidth = -1, bool Native = false);

public sealed partial class ReportLayoutSession
{
    private Dictionary<string, ReportTextMetrics>? _textMetrics;
    private CancellationToken _paginationCancellationToken;
    private readonly Dictionary<string, ReportTextMeasurement> _measurementRequests = new(StringComparer.Ordinal);
    private static string MeasurementKey(ReportTextMeasurement measurement) => measurement.MaxNumberOfLines + "\n" + measurement.MeasureNumericWidth + "\n" + measurement.Style + "\n" + measurement.Html;

    /// <summary>Measures actual page-conditioned text until both font metrics and pagination settle.</summary>
    public async Task<ReportLayoutResult> PaginateAsync(
        Func<IReadOnlyList<ReportTextMeasurement>, CancellationToken, Task<IReadOnlyList<ReportTextMetrics>>> measure,
        CancellationToken cancellationToken = default)
        => await MeasurePagesAsync(measure, cancellationToken, 0, retainMetrics: false);

    private async Task<ReportLayoutResult> MeasurePagesAsync(
        Func<IReadOnlyList<ReportTextMeasurement>, CancellationToken, Task<IReadOnlyList<ReportTextMetrics>>> measure,
        CancellationToken cancellationToken, int pageLimit, bool retainMetrics)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (_textMetrics is not null) throw new InvalidOperationException("Concurrent pagination of a report session is not supported.");
        _textMetrics = retainMetrics ? _progressiveMetrics ??= new(StringComparer.Ordinal) : new(StringComparer.Ordinal);
        _paginationPageLimit = pageLimit;
        _paginationCancellationToken = cancellationToken;
        try
        {
            if (!retainMetrics)
                foreach (var item in Measurements)
                {
                    var key = MeasurementKey(item);
                    if (_nativeMetrics.TryGetValue(key, out var native)) _textMetrics.TryAdd(key, native);
                    else _measurementRequests.TryAdd(key, item);
                }
            for (var pass = 0; pass < 128; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var batch in MeasurementBatches(_measurementRequests.ToArray()))
                {
                    var metrics = await measure(batch.Select(p => p.Value).ToArray(), cancellationToken);
                    if (metrics.Count != batch.Length) throw new InvalidDataException("Browser report measurement returned the wrong number of results.");
                    for (var index = 0; index < batch.Length; index++)
                    {
                        var metric = metrics[index];
                        if (metric.Height is < 0 or > 10000000 || !double.IsFinite(metric.Width) || !double.IsFinite(metric.HashWidth)
                            || metric.Width is < -1 or > 10000000 || metric.HashWidth is < -1 or > 10000000
                            || metric.Lines is null || metric.Lines.Count > 100000
                            || metric.Lines.Any(l => l.Top < 0 || l.Bottom < l.Top || l.Bottom > 10000000
                                || l.Start < -1 || l.End < l.Start || (l.Start == -1) != (l.End == -1)))
                            throw new InvalidDataException("Browser report measurement returned invalid line bounds.");
                        _textMetrics[batch[index].Key] = metric;
                    }
                }
                _measurementRequests.Clear();
                var diagnostics = _diagnostics.ToArray();
                ReportLayoutResult? result = null;
                try { result = Paginate(); }
                catch (Exception error) when (_measurementRequests.Count > 0 && error is InvalidDataException or NotSupportedException)
                {
                    // Provisional font estimates can overflow; retry with the requested real metrics first.
                }
                if (_measurementRequests.Count == 0) return result!;
                _diagnostics.Clear(); _diagnostics.UnionWith(diagnostics);
                if (_textMetrics.Count + _measurementRequests.Count > 100000) throw new InvalidDataException("Report exceeds the 100,000 text measurement limit.");
            }
            throw new InvalidDataException("Page-conditioned text did not converge within 128 measurement passes.");
        }
        finally { _textMetrics = null; _measurementRequests.Clear(); _paginationCancellationToken = default; _paginationPageLimit = 0; }
    }

    // Every measured line comes back as about 45 bytes, and Blazor Server's default hub message limit
    // is 32 KB. Batches are therefore bounded by an estimate of the lines they return (about 20 characters
    // of markup a line, deliberately high), not only by count, so hosts on the default limit stay connected.
    private static IEnumerable<KeyValuePair<string, ReportTextMeasurement>[]> MeasurementBatches(KeyValuePair<string, ReportTextMeasurement>[] requests)
    {
        var batch = new List<KeyValuePair<string, ReportTextMeasurement>>();
        var lines = 0;
        foreach (var request in requests)
        {
            var estimate = Math.Max(1, request.Value.Html.Length / 20);
            if (batch.Count > 0 && (batch.Count == 250 || lines + estimate > 600))
            {
                yield return batch.ToArray();
                batch.Clear();
                lines = 0;
            }
            batch.Add(request);
            lines += estimate;
        }
        if (batch.Count > 0) yield return batch.ToArray();
    }

    private ReportTextMetrics? TextMetrics(Item item)
    {
        if (_textMetrics is null || item.Element.Kind is not ("Text" or "Field" or "FieldHeading")) return null;
        var measurement = new ReportTextMeasurement(ReportObjectRenderer.Style(item.Element, false), item.Html,
            ReportObjectRenderer.TextLineLimit(item.Element), NeedsNumericMask(item.Element));
        var key = MeasurementKey(measurement);
        if (_textMetrics.TryGetValue(key, out var metric)) return metric;
        // Crystal's printer-device line breaks are known without a browser round trip for native text in a measurable font.
        if (!measurement.MeasureNumericWidth && CrystalDeviceText.Measure(item.Element, item.Html) is { } native)
        {
            _textMetrics[key] = native;
            return native;
        }
        _measurementRequests.TryAdd(key, measurement);
        return null;
    }

    private static bool NeedsNumericMask(ReportDesignerElement element) => element.Kind == "Field"
        && string.IsNullOrEmpty(element.FormatString)
        && element.NativeNumericFormat is { AllowFieldClipping: false, UnsupportedReason: null };

    private string MaskNumericOverflow(Item item)
    {
        if (!NeedsNumericMask(item.Element) || string.IsNullOrEmpty(item.Html)) return item.Html;
        var text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(item.Html, "<[^>]+>", ""));
        if (text.StartsWith("[Formula error:", StringComparison.Ordinal) || text.StartsWith("[Invalid summary]", StringComparison.Ordinal)
            || text.StartsWith("[Unsupported", StringComparison.Ordinal)) return item.Html;
        var probe = item.Element.NativeNumericFormat!.OverflowProbe(text, item.Element.HorizontalAlignment);
        // Numeric fit belongs to the authored point/twip layout, before screen-font realization.
        // Rounding a 10pt screen font down to 13px must not make an overflowing value fit.
        var fitElement = item.Element;
        if (fitElement.UseCrystalScreenMetrics)
        {
            fitElement = fitElement.CloneFor(fitElement.SectionId, fitElement.Name);
            fitElement.UseCrystalScreenMetrics = false;
        }
        var metrics = TextMetrics(item with
        {
            Element = fitElement,
            Html = ReportObjectRenderer.Content(fitElement, _ => probe)
        });
        if (metrics is not { Width: >= 0, HashWidth: > 0 })
        {
            if (_textMetrics is null || metrics is not null)
                _diagnostics.Add($"{item.Element.Name}: native numeric overflow masking requires browser font measurements.");
            return item.Html;
        }
        var width = Math.Max(0, item.Element.WidthTwips - item.Element.IndentTwips);
        if (metrics.Width <= width) return item.Html;
        var count = Math.Clamp((int)Math.Floor(width / metrics.HashWidth), 0, 10000);
        // Replace only the display text. Formulas, totals and typed data exports keep the original value.
        return ReportObjectRenderer.Content(item.Element, _ => new string('#', count));
    }
}
