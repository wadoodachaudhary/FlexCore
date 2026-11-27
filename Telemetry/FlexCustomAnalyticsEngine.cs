using System.Globalization;
using Fx.ControlKit.Charts;
using Fx.ControlKit.Logging;

namespace Fx.ControlKit.Telemetry;

public sealed class CustomEventCountRow
{
    public string EventName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string SubKind { get; init; } = string.Empty;
    public int CaptureCount { get; init; }
    public int UniqueUsers { get; init; }
    public int UniqueScreens { get; init; }
    public int ChainedCount { get; init; }
    public double TotalSelectedRows { get; init; }
    public double TotalUpdatedOrMatched { get; init; }
    public double AvgPrimaryMetric { get; init; }
    public double MaxPrimaryMetric { get; init; }
    public string PrimaryMetricLabel { get; init; } = string.Empty;
    public DateTime LastCapturedLocal { get; init; }
}

public sealed class CustomCapturedEventRow
{
    public DateTime TimestampUtc { get; init; }
    public DateTime TimestampLocal { get; init; }
    public string OperationId { get; init; } = string.Empty;
    public string? ParentOperationId { get; init; }
    public string EventName { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string SubKind { get; init; } = string.Empty;
    public string Outcome { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string Screen { get; init; } = string.Empty;
    public string ControlId { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public double SelectedRows { get; init; }
    public double UpdatedOrMatched { get; init; }
    public int NodeCount { get; init; }
    public int EdgeCount { get; init; }
    public string Summary { get; init; } = string.Empty;
    public FlexTelemetryGraphPayload Graph { get; init; } = new();
    public string GraphJsonIndented { get; init; } = string.Empty;
}

public sealed class FlexCustomAnalyticsSnapshot
{
    public DateTime GeneratedAtLocal { get; init; } = DateTime.Now;
    public string TimeWindowLabel { get; init; } = "Last 30d";
    public int TotalCustomCaptures { get; init; }
    public int DistinctEventNames { get; init; }

    // 1. Grid Sort capture metrics
    public int GridSortCount { get; init; }
    public int MultiSortCount { get; init; }
    public int MaxSortLevels { get; init; }

    // 2. Grid Find capture metrics
    public int GridFindCount { get; init; }
    public int FindMatchedCount { get; init; }
    public int FindNoMatchCount { get; init; }
    public int TotalFindMatchedRows { get; init; }

    // 3. Grid Drag capture metrics
    public int GridDragCount { get; init; }
    public int RowReorderDragCount { get; init; }
    public int RangeSelectDragCount { get; init; }
    public int ColumnOrGroupDragCount { get; init; }
    public int TotalDragSelectedRows { get; init; }

    // 4. Grid Multi-Select Batch Edit capture metrics
    public int GridMultiSelectEditCount { get; init; }
    public int TotalBatchSelectedRows { get; init; }
    public int TotalBatchUpdatedCells { get; init; }
    public double AvgBatchRowsPerEdit { get; init; }
    public int MaxBatchRowsInSingleEdit { get; init; }
    public int ChainedDragToEditCount { get; init; }

    // Other custom developer events
    public int OtherCustomEventCount { get; init; }

    public IReadOnlyList<CustomEventCountRow> CountRows { get; init; } = Array.Empty<CustomEventCountRow>();
    public IReadOnlyList<CustomCapturedEventRow> RecentCaptures { get; init; } = Array.Empty<CustomCapturedEventRow>();
}

/// <summary>
/// Aggregates <see cref="FlexTelemetryGraphPayload"/> events into feature capture counts, metric rollups,
/// and multi-stage <see cref="PathFlowNode"/> / <see cref="PathFlowLink"/> visualizations.
/// </summary>
public static class FlexCustomAnalyticsEngine
{
    public static FlexCustomAnalyticsSnapshot BuildSnapshot(
        IReadOnlyList<FlexTelemetryEvent> rawEvents,
        FlexTelemetryQueryFilter filter,
        string defaultTenant = "")
    {
        var nowUtc = DateTime.UtcNow;
        int daysBack = Math.Clamp(filter.DaysBack <= 0 ? 30 : filter.DaysBack, 1, 365);
        int? minutesBack = filter.MinutesBack is > 0 ? Math.Clamp(filter.MinutesBack.Value, 1, 525_600) : null;
        var cutoffUtc = minutesBack.HasValue ? nowUtc.AddMinutes(-minutesBack.Value) : nowUtc.AddDays(-daysBack);
        string windowLabel = minutesBack.HasValue
            ? (minutesBack.Value < 60
                ? $"Last {minutesBack.Value}m"
                : (minutesBack.Value % 1440 == 0 ? $"Last {minutesBack.Value / 1440}d" : $"Last {minutesBack.Value / 60}h"))
            : $"Last {daysBack}d";

        var targetTenant = (filter.Tenant ?? string.Empty).Trim();
        bool allTenants = string.IsNullOrEmpty(targetTenant) || string.Equals(targetTenant, "*", StringComparison.Ordinal);
        var userFilter = (filter.UserId ?? string.Empty).Trim();
        bool hasUser = !string.IsNullOrEmpty(userFilter) && !string.Equals(userFilter, "All", StringComparison.OrdinalIgnoreCase);
        var search = (filter.SearchText ?? string.Empty).Trim();

        var captured = new List<CustomCapturedEventRow>();

        for (int i = 0; i < rawEvents.Count; i++)
        {
            var e = rawEvents[i];
            if (e.TimestampUtc < cutoffUtc)
                continue;
            if (!FlexTelemetryAggregator.MatchesTenant(e.Tenant, targetTenant, allTenants, defaultTenant))
                continue;
            if (hasUser && !string.Equals(e.UserId, userFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!FlexCustomAnalyticsCodec.TryUnpack(e.Detail, out var humanDetail, out var graph) || graph is null)
                continue;

            if (!string.IsNullOrEmpty(search)
                && !graph.EventName.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !(graph.SubKind?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                && !humanDetail.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !e.Screen.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !e.Control.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !e.UserId.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            double selRows = Math.Max(
                graph.GetMetric(FlexCustomAnalyticsEvents.Metrics.SelectedRows),
                graph.GetMetric(FlexCustomAnalyticsEvents.Metrics.SelectedCells));
            double updOrMatch = PrimaryEffectMetric(graph);

            captured.Add(new CustomCapturedEventRow
            {
                TimestampUtc = e.TimestampUtc,
                TimestampLocal = e.TimestampUtc.ToLocalTime(),
                OperationId = graph.OperationId,
                ParentOperationId = graph.ParentOperationId,
                EventName = graph.EventName,
                Category = graph.Category,
                SubKind = graph.SubKind ?? "Standard",
                Outcome = graph.Outcome,
                UserId = string.IsNullOrWhiteSpace(e.UserId) ? "anonymous" : e.UserId,
                Screen = string.IsNullOrWhiteSpace(e.Screen) ? "Main" : e.Screen,
                ControlId = string.IsNullOrWhiteSpace(e.Control) ? e.Category : e.Control,
                Location = e.Location,
                SelectedRows = selRows,
                UpdatedOrMatched = updOrMatch,
                NodeCount = graph.Nodes?.Count ?? 0,
                EdgeCount = graph.Edges?.Count ?? 0,
                Summary = !string.IsNullOrWhiteSpace(humanDetail) ? humanDetail : (graph.Summary ?? graph.EventName),
                Graph = graph,
                GraphJsonIndented = FlexCustomAnalyticsCodec.ToJson(graph, indented: true)
            });
        }

        captured.Sort((a, b) => b.TimestampUtc.CompareTo(a.TimestampUtc));

        int sortCnt = 0, multiSortCnt = 0, maxSortLvls = 0;
        int findCnt = 0, findHitCnt = 0, findMissCnt = 0, totalFindRows = 0;
        int dragCnt = 0, rowReorderCnt = 0, rangeSelCnt = 0, colGroupDragCnt = 0, totalDragSelRows = 0;
        int multiEditCnt = 0, totalBatchSelRows = 0, totalBatchCells = 0, maxBatchRows = 0, chainedCnt = 0;
        int otherCnt = 0;

        foreach (var c in captured)
        {
            var g = c.Graph;
            if (string.Equals(c.EventName, FlexCustomAnalyticsEvents.GridSort, StringComparison.OrdinalIgnoreCase))
            {
                sortCnt++;
                int lvls = (int)Math.Round(g.GetMetric(FlexCustomAnalyticsEvents.Metrics.SortLevels));
                if (lvls > 1 || string.Equals(c.SubKind, "MultiSortDialog", StringComparison.OrdinalIgnoreCase))
                    multiSortCnt++;
                if (lvls > maxSortLvls)
                    maxSortLvls = lvls;
            }
            else if (string.Equals(c.EventName, FlexCustomAnalyticsEvents.GridFind, StringComparison.OrdinalIgnoreCase))
            {
                findCnt++;
                int m = (int)Math.Round(g.GetMetric(FlexCustomAnalyticsEvents.Metrics.MatchCount));
                totalFindRows += m;
                if (m > 0 || string.Equals(c.Outcome, "Matched", StringComparison.OrdinalIgnoreCase))
                    findHitCnt++;
                else if (string.Equals(c.Outcome, "NoMatch", StringComparison.OrdinalIgnoreCase))
                    findMissCnt++;
            }
            else if (string.Equals(c.EventName, FlexCustomAnalyticsEvents.GridDrag, StringComparison.OrdinalIgnoreCase))
            {
                dragCnt++;
                int sr = (int)Math.Round(Math.Max(
                    g.GetMetric(FlexCustomAnalyticsEvents.Metrics.SelectedRows),
                    g.GetMetric(FlexCustomAnalyticsEvents.Metrics.SelectedCells)));
                totalDragSelRows += sr;
                if (string.Equals(c.SubKind, "RowReorder", StringComparison.OrdinalIgnoreCase))
                    rowReorderCnt++;
                else if (c.SubKind is "RowRangeSelect" or "CellRangeSelect")
                    rangeSelCnt++;
                else
                    colGroupDragCnt++;
            }
            else if (string.Equals(c.EventName, FlexCustomAnalyticsEvents.GridMultiSelectEdit, StringComparison.OrdinalIgnoreCase))
            {
                multiEditCnt++;
                int sr = (int)Math.Round(c.SelectedRows);
                int uc = (int)Math.Round(g.GetMetric(FlexCustomAnalyticsEvents.Metrics.UpdatedCells));
                totalBatchSelRows += sr;
                totalBatchCells += uc;
                if (sr > maxBatchRows)
                    maxBatchRows = sr;
                if (!string.IsNullOrWhiteSpace(c.ParentOperationId))
                    chainedCnt++;
            }
            else
            {
                otherCnt++;
            }
        }

        double avgBatchRows = multiEditCnt > 0 ? Math.Round((double)totalBatchSelRows / multiEditCnt, 1) : 0;

        var countRows = captured
            .GroupBy(c => (c.EventName, c.Category, c.SubKind))
            .Select(grp =>
            {
                var list = grp.ToList();
                var metrics = list.Select(x => Math.Max(x.SelectedRows, x.UpdatedOrMatched)).ToList();
                return new CustomEventCountRow
                {
                    EventName = grp.Key.EventName,
                    Category = grp.Key.Category,
                    SubKind = grp.Key.SubKind,
                    CaptureCount = list.Count,
                    UniqueUsers = list.Select(x => x.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    UniqueScreens = list.Select(x => x.Screen).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    ChainedCount = list.Count(x => !string.IsNullOrWhiteSpace(x.ParentOperationId)),
                    TotalSelectedRows = Math.Round(list.Sum(x => x.SelectedRows), 1),
                    TotalUpdatedOrMatched = Math.Round(list.Sum(x => x.UpdatedOrMatched), 1),
                    AvgPrimaryMetric = metrics.Count > 0 ? Math.Round(metrics.Average(), 1) : 0,
                    MaxPrimaryMetric = metrics.Count > 0 ? Math.Round(metrics.Max(), 1) : 0,
                    PrimaryMetricLabel = ResolvePrimaryMetricLabel(grp.Key.EventName),
                    LastCapturedLocal = list.Max(x => x.TimestampLocal)
                };
            })
            .OrderByDescending(r => r.CaptureCount)
            .ThenBy(r => r.EventName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FlexCustomAnalyticsSnapshot
        {
            GeneratedAtLocal = DateTime.Now,
            TimeWindowLabel = windowLabel,
            TotalCustomCaptures = captured.Count,
            DistinctEventNames = captured.Select(c => c.EventName).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            GridSortCount = sortCnt,
            MultiSortCount = multiSortCnt,
            MaxSortLevels = maxSortLvls,
            GridFindCount = findCnt,
            FindMatchedCount = findHitCnt,
            FindNoMatchCount = findMissCnt,
            TotalFindMatchedRows = totalFindRows,
            GridDragCount = dragCnt,
            RowReorderDragCount = rowReorderCnt,
            RangeSelectDragCount = rangeSelCnt,
            ColumnOrGroupDragCount = colGroupDragCnt,
            TotalDragSelectedRows = totalDragSelRows,
            GridMultiSelectEditCount = multiEditCnt,
            TotalBatchSelectedRows = totalBatchSelRows,
            TotalBatchUpdatedCells = totalBatchCells,
            AvgBatchRowsPerEdit = avgBatchRows,
            MaxBatchRowsInSingleEdit = maxBatchRows,
            ChainedDragToEditCount = chainedCnt,
            OtherCustomEventCount = otherCnt,
            CountRows = countRows,
            RecentCaptures = captured.Take(500).ToList()
        };
    }

    /// <summary>
    /// Converts either a single captured operation's graph or an aggregated set of captured graphs
    /// into topological stage-assigned <see cref="PathFlowNode"/> and <see cref="PathFlowLink"/> collections.
    /// </summary>
    public static (IReadOnlyList<PathFlowNode> Nodes, IReadOnlyList<PathFlowLink> Links, IReadOnlyList<string> StageTitles)
        BuildPathFlowGraph(IReadOnlyList<CustomCapturedEventRow> rows)
    {
        if (rows.Count == 0)
            return (Array.Empty<PathFlowNode>(), Array.Empty<PathFlowLink>(), Array.Empty<string>());

        var nodeStage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nodeLabel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nodeVal = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var edgeWeights = new Dictionary<(string Src, string Dst), double>();

        foreach (var row in rows.Take(100))
        {
            var g = row.Graph;
            if (g.Nodes is null || g.Edges is null || g.Nodes.Count == 0 || g.Edges.Count == 0)
                continue;

            // Compute topological depth within this event's graph
            var localDepth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in g.Nodes)
                localDepth[n.Id] = 0;

            for (int pass = 0; pass < g.Nodes.Count; pass++)
            {
                bool changed = false;
                foreach (var edge in g.Edges)
                {
                    if (string.Equals(edge.SourceId, edge.TargetId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    int srcD = localDepth.GetValueOrDefault(edge.SourceId, 0);
                    int dstD = localDepth.GetValueOrDefault(edge.TargetId, 0);
                    if (dstD < srcD + 1 && srcD + 1 <= 5)
                    {
                        localDepth[edge.TargetId] = srcD + 1;
                        changed = true;
                    }
                }
                if (!changed)
                    break;
            }

            string KeyFor(string rawId)
            {
                var nodeObj = g.Nodes.FirstOrDefault(x => string.Equals(x.Id, rawId, StringComparison.OrdinalIgnoreCase));
                int st = localDepth.GetValueOrDefault(rawId, 0);
                var lbl = nodeObj?.Label ?? rawId;
                return $"s{st}:{lbl}";
            }

            foreach (var n in g.Nodes)
            {
                var k = KeyFor(n.Id);
                nodeStage[k] = localDepth.GetValueOrDefault(n.Id, 0);
                nodeLabel[k] = n.Label;
                nodeVal[k] = nodeVal.GetValueOrDefault(k, 0) + Math.Max(1, n.Value ?? 1);
            }

            foreach (var edge in g.Edges)
            {
                var sk = KeyFor(edge.SourceId);
                var tk = KeyFor(edge.TargetId);
                if (string.Equals(sk, tk, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (nodeStage.GetValueOrDefault(tk) <= nodeStage.GetValueOrDefault(sk))
                    continue;

                var pair = (sk, tk);
                edgeWeights[pair] = edgeWeights.GetValueOrDefault(pair, 0) + Math.Max(1, edge.Weight);
            }
        }

        if (edgeWeights.Count == 0)
            return (Array.Empty<PathFlowNode>(), Array.Empty<PathFlowLink>(), Array.Empty<string>());

        var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (s, t) in edgeWeights.Keys)
        {
            activeIds.Add(s);
            activeIds.Add(t);
        }

        int maxStage = activeIds.Max(id => nodeStage.GetValueOrDefault(id, 0));
        var nodes = activeIds
            .Select(id => new PathFlowNode
            {
                Id = id,
                Stage = nodeStage.GetValueOrDefault(id, 0),
                Label = nodeLabel.GetValueOrDefault(id, id),
                Value = Math.Round(nodeVal.GetValueOrDefault(id, 1), 1)
            })
            .ToList();

        var links = edgeWeights
            .Select(kv => new PathFlowLink
            {
                SourceId = kv.Key.Src,
                TargetId = kv.Key.Dst,
                Value = Math.Round(kv.Value, 1)
            })
            .ToList();

        var titles = Enumerable.Range(0, maxStage + 1)
            .Select(s => s == 0 ? "Origin / Trigger" : (s == maxStage ? "Committed Outcome" : $"Graph Stage {s}"))
            .ToList();

        return (nodes, links, titles);
    }

    private static double PrimaryEffectMetric(FlexTelemetryGraphPayload g)
    {
        if (string.Equals(g.EventName, FlexCustomAnalyticsEvents.GridMultiSelectEdit, StringComparison.OrdinalIgnoreCase))
            return g.GetMetric(FlexCustomAnalyticsEvents.Metrics.UpdatedCells);
        if (string.Equals(g.EventName, FlexCustomAnalyticsEvents.GridFind, StringComparison.OrdinalIgnoreCase))
            return g.GetMetric(FlexCustomAnalyticsEvents.Metrics.MatchCount);
        if (string.Equals(g.EventName, FlexCustomAnalyticsEvents.GridSort, StringComparison.OrdinalIgnoreCase))
            return g.GetMetric(FlexCustomAnalyticsEvents.Metrics.SortLevels);
        if (string.Equals(g.EventName, FlexCustomAnalyticsEvents.GridDrag, StringComparison.OrdinalIgnoreCase))
            return Math.Max(g.GetMetric(FlexCustomAnalyticsEvents.Metrics.DragSpanRows), g.GetMetric(FlexCustomAnalyticsEvents.Metrics.GroupLevels));
        return g.Metrics?.Values.FirstOrDefault() ?? 1;
    }

    private static string ResolvePrimaryMetricLabel(string eventName) => eventName switch
    {
        FlexCustomAnalyticsEvents.GridMultiSelectEdit => "Updated Cells",
        FlexCustomAnalyticsEvents.GridFind => "Matched Rows",
        FlexCustomAnalyticsEvents.GridSort => "Sort Levels",
        FlexCustomAnalyticsEvents.GridDrag => "Row Span / Depth",
        _ => "Metric Total"
    };
}
