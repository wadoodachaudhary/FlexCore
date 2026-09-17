namespace Fx.ControlKit.Reports;

public sealed record ReportTextLine(int Top, int Bottom, int Start = -1, int End = -1);
public sealed record ReportTextMetrics(int Height, IReadOnlyList<ReportTextLine> Lines);

public sealed partial class ReportLayoutSession
{
    private Dictionary<string, ReportTextMetrics>? _textMetrics;
    private readonly Dictionary<string, ReportTextMeasurement> _measurementRequests = new(StringComparer.Ordinal);
    private static string MeasurementKey(ReportTextMeasurement measurement) => measurement.Style + "\n" + measurement.Html;

    /// <summary>Measures actual page-conditioned text until both font metrics and pagination settle.</summary>
    public async Task<ReportLayoutResult> PaginateAsync(
        Func<IReadOnlyList<ReportTextMeasurement>, CancellationToken, Task<IReadOnlyList<ReportTextMetrics>>> measure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (_textMetrics is not null) throw new InvalidOperationException("Concurrent pagination of a report session is not supported.");
        _textMetrics = new(StringComparer.Ordinal);
        try
        {
            foreach (var item in Measurements) _measurementRequests.TryAdd(MeasurementKey(item), item);
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
                        if (metric.Height is < 0 or > 10000000 || metric.Lines is null || metric.Lines.Count > 100000
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
        finally { _textMetrics = null; _measurementRequests.Clear(); }
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
        var measurement = new ReportTextMeasurement(ReportObjectRenderer.Style(item.Element, false), item.Html);
        var key = MeasurementKey(measurement);
        if (_textMetrics.TryGetValue(key, out var metric)) return metric;
        _measurementRequests.TryAdd(key, measurement);
        return null;
    }
}
