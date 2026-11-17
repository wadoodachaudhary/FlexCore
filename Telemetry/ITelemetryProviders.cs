using System.Collections.Concurrent;
using Fx.ControlKit.Logging;

namespace Fx.ControlKit.Telemetry;

/// <summary>
/// Optional host catalog provider supplying known screens, tasks, and reports so
/// <see cref="TelemetryControl"/> can calculate UI coverage percentages and highlight 0-use items.
/// </summary>
public interface ITelemetryCatalogProvider
{
    IReadOnlyList<TelemetryCatalogScreen> GetKnownScreens();
    IReadOnlyList<TelemetryCatalogTask> GetKnownTasksAndReports();
    (string ScreenTitle, string Module) ResolveScreenInfo(string route, string fallbackScreenTitle);
}

/// <summary>
/// Optional multi-tenant / multi-database selector contract for <see cref="TelemetryControl"/>.
/// </summary>
public interface ITelemetryTenantProvider
{
    string TenantLabel => "Database";
    Task<string> GetDefaultTenantAsync(CancellationToken cancellationToken = default);
    Task<bool> CanSelectAcrossTenantsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional file export handler. When unregistered, <see cref="TelemetryControl"/> and <see cref="LogControl"/>
/// automatically download exported CSV / NDJSON / CLEF files directly via FlexKit's built-in BrowserInterop.
/// </summary>
public interface ITelemetryExportHandler
{
    Task ExportBytesAsync(byte[] content, string fileName, string contentType, CancellationToken cancellationToken = default);
}

/// <summary>
/// Generalized telemetry data source queried by <see cref="TelemetryControl"/>, <see cref="LogControl"/>,
/// and <see cref="IFlexDiagnosticSnapshotService"/>.
/// Falls back automatically to <see cref="DefaultFlexTelemetryDataSource"/> when not overridden by the host.
/// </summary>
public interface ITelemetryDataSource
{
    FlexTelemetrySnapshot BuildSnapshot(FlexTelemetryQueryFilter filter, string defaultTenant = "");
    IReadOnlyList<FlexTelemetryEvent> GetRecentSessionEvents(string? sessionId, int maxCount = 50);
    void RecordEvent(FlexTelemetryEvent telemetryEvent);
    Task FlushAsync(CancellationToken cancellationToken = default);

    IReadOnlyList<FlexTelemetryEvent> QueryEvents(
        DateTime sinceUtc,
        string? tenant = null,
        string defaultTenant = "",
        int maxCount = 50_000)
    {
        var days = Math.Clamp((int)Math.Ceiling((DateTime.UtcNow - sinceUtc).TotalDays), 1, 365);
        var snap = BuildSnapshot(new FlexTelemetryQueryFilter { Tenant = tenant, DaysBack = days }, defaultTenant);
        return snap.RecentEvents
            .Where(e => e.TimestampUtc >= sinceUtc)
            .Select(e => new FlexTelemetryEvent
            {
                TimestampUtc = e.TimestampUtc,
                Level = e.Level,
                Tenant = e.Tenant,
                DivisionId = e.DivisionId,
                UserId = e.UserId,
                SessionId = e.SessionId,
                Kind = Enum.TryParse<TelemetryEventKind>(e.Kind, ignoreCase: true, out var k) ? k : TelemetryEventKind.ControlChange,
                Category = e.ControlType,
                Action = e.Action,
                Screen = e.Screen,
                Route = e.Route,
                Location = e.Location,
                Control = e.Control,
                Tag = e.Tag,
                Value = e.Value,
                Detail = e.Detail,
                DurationMs = e.DurationSeconds.HasValue ? (long)(e.DurationSeconds.Value * 1000.0) : null
            })
            .OrderBy(e => e.TimestampUtc)
            .Take(Math.Clamp(maxCount, 1, 250_000))
            .ToList();
    }
}

/// <summary>
/// Generalized SQL query execution telemetry provider shared between <see cref="SqlTelemetryControl"/>
/// and diagnostic ticket snapshots (<see cref="IFlexDiagnosticSnapshotService"/>).
/// </summary>
public interface IFlexSqlTelemetryProvider
{
    void RecordSql(FlexSqlTelemetryEntry entry);
    IReadOnlyList<FlexSqlTelemetryEntry> GetRecentQueries(
        string? sessionId = null,
        string? tenant = null,
        bool excludeBackgroundPolls = false,
        int maxCount = 200);
    void Clear(string? sessionId = null);
}

/// <summary>
/// Thread-safe, bounded in-memory SQL execution telemetry store with per-session and global ring buffers.
/// </summary>
public sealed class FlexSqlTelemetryStore : IFlexSqlTelemetryProvider
{
    private readonly ConcurrentQueue<FlexSqlTelemetryEntry> _global = new();
    private int _globalCount;
    private readonly ConcurrentDictionary<string, SessionSqlRing> _sessions = new(StringComparer.Ordinal);
    private readonly int _maxGlobalEntries;
    private readonly int _maxPerSessionEntries;
    private long _nextSessionPruneTicks;
    private int _sessionPruning;

    public FlexSqlTelemetryStore(int maxGlobalEntries = 2_500, int maxPerSessionEntries = 150)
    {
        _maxGlobalEntries = Math.Clamp(maxGlobalEntries, 100, 50_000);
        _maxPerSessionEntries = Math.Clamp(maxPerSessionEntries, 25, 2_000);
        FlexSqlRecorder.Sink ??= this;
    }

    /// <summary>Hourly counters per table, screen, user and kind; they outlive the statements in the ring buffers.</summary>
    public FlexSqlRollup Rollup { get; } = new();

    /// <summary>
    /// A session's own ring is dropped once it has recorded nothing for this long (a closed circuit never
    /// records again); its statements stay in the global ring, which a session lookup then reads.
    /// </summary>
    public TimeSpan SessionIdleTimeout { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Most session rings kept; past this the least recently used are dropped.</summary>
    public int MaxSessions { get; set; } = 1_024;

    /// <summary>How often the session rings are looked at for idle ones (at most once per this interval).</summary>
    public TimeSpan SessionPruneInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Number of session rings held now.</summary>
    public int SessionCount => _sessions.Count;

    public void RecordSql(FlexSqlTelemetryEntry entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Sql))
            return;

        Rollup.Add(entry);

        _global.Enqueue(entry);
        if (Interlocked.Increment(ref _globalCount) > _maxGlobalEntries)
        {
            while (_globalCount > _maxGlobalEntries && _global.TryDequeue(out _))
                Interlocked.Decrement(ref _globalCount);
        }

        if (!string.IsNullOrWhiteSpace(entry.SessionId))
        {
            var added = false;
            var ring = _sessions.GetOrAdd(entry.SessionId, _ => { added = true; return new SessionSqlRing(_maxPerSessionEntries); });
            ring.Enqueue(entry);
            PruneSessionsIfDue(added);
        }
        else
        {
            PruneSessionsIfDue(false);
        }
    }

    public IReadOnlyList<FlexSqlTelemetryEntry> GetRecentQueries(
        string? sessionId = null,
        string? tenant = null,
        bool excludeBackgroundPolls = false,
        int maxCount = 200)
    {
        int cap = Math.Clamp(maxCount, 1, 5_000);
        IEnumerable<FlexSqlTelemetryEntry> source;

        if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.TryGetValue(sessionId, out var sessionRing))
        {
            source = sessionRing.Snapshot();
        }
        else
        {
            source = _global.ToArray();
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                source = source.Where(q => string.Equals(q.SessionId, sessionId, StringComparison.Ordinal));
            }
        }

        // A tenant's view holds that tenant's statements only; a statement recorded without a tenant shows
        // in the all-tenants view alone.
        if (!string.IsNullOrWhiteSpace(tenant) && !string.Equals(tenant, "*", StringComparison.Ordinal))
        {
            source = source.Where(q => string.Equals(q.Tenant, tenant, StringComparison.OrdinalIgnoreCase));
        }

        if (excludeBackgroundPolls)
        {
            source = source.Where(q => !q.IsBackgroundPoll);
        }

        return source
            .OrderByDescending(q => q.TimestampUtc)
            .Take(cap)
            .ToList();
    }

    public void Clear(string? sessionId = null)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _sessions.TryRemove(sessionId, out _);
            return;
        }

        while (_global.TryDequeue(out _))
            Interlocked.Decrement(ref _globalCount);
        _sessions.Clear();
    }

    // Never throws: telemetry must not fail the statement it records.
    private void PruneSessionsIfDue(bool sessionAdded)
    {
        try
        {
            var now = Environment.TickCount64;
            var cap = Math.Max(1, MaxSessions);
            // The count is read only when a ring was just added (Count takes every lock of the dictionary).
            if (now < Interlocked.Read(ref _nextSessionPruneTicks) && !(sessionAdded && _sessions.Count > cap))
                return;
            // One caller prunes at a time; the others carry on recording.
            if (Interlocked.CompareExchange(ref _sessionPruning, 1, 0) != 0)
                return;
            try
            {
                var interval = (long)Math.Clamp(SessionPruneInterval.TotalMilliseconds, 0, TimeSpan.FromHours(1).TotalMilliseconds);
                Interlocked.Exchange(ref _nextSessionPruneTicks, now + interval);

                // A copy taken under the dictionary's locks: sorting the live dictionary while sessions are
                // added or removed throws.
                var snapshot = _sessions.ToArray();
                var idle = SessionIdleTimeout < TimeSpan.Zero ? TimeSpan.Zero : SessionIdleTimeout;
                var cutoff = idle >= TimeSpan.FromDays(3650) ? DateTime.MinValue : DateTime.UtcNow - idle;
                var kept = new List<KeyValuePair<string, SessionSqlRing>>(snapshot.Length);
                foreach (var kv in snapshot)
                {
                    // Removed only while it is still the same ring.
                    if (kv.Value.LastUpdatedUtc < cutoff)
                        _sessions.TryRemove(kv);
                    else
                        kept.Add(kv);
                }

                if (kept.Count > cap)
                {
                    // Down to nine tenths, so the next new sessions do not each sort the whole set again.
                    var excess = kept.Count - (cap - cap / 10);
                    foreach (var kv in kept.OrderBy(k => k.Value.LastUpdatedUtc).Take(excess))
                        _sessions.TryRemove(kv);
                }
            }
            finally
            {
                Volatile.Write(ref _sessionPruning, 0);
            }
        }
        catch
        {
            // Pruning is best effort; the rings stay bounded each, and the next prune tries again.
        }
    }

    private sealed class SessionSqlRing(int capacity)
    {
        private readonly ConcurrentQueue<FlexSqlTelemetryEntry> _queue = new();
        private int _count;
        public DateTime LastUpdatedUtc { get; private set; } = DateTime.UtcNow;

        public void Enqueue(FlexSqlTelemetryEntry entry)
        {
            LastUpdatedUtc = DateTime.UtcNow;
            _queue.Enqueue(entry);
            if (Interlocked.Increment(ref _count) > capacity)
            {
                while (_count > capacity && _queue.TryDequeue(out _))
                    Interlocked.Decrement(ref _count);
            }
        }

        public FlexSqlTelemetryEntry[] Snapshot() => _queue.ToArray();
    }
}

/// <summary>
/// Built-in zero-configuration <see cref="ITelemetryDataSource"/> backed directly by FlexKit's
/// <see cref="IFlexControlLogSink"/> plus an in-memory semantic event buffer.
/// Allows any Blazor host to drop in <c>&lt;TelemetryControl /&gt;</c> or <c>&lt;LogControl /&gt;</c>
/// without implementing any application-specific telemetry store.
/// </summary>
public sealed class DefaultFlexTelemetryDataSource : ITelemetryDataSource, IDisposable
{
    private readonly IFlexControlLogSink? _sink;
    private readonly ITelemetryCatalogProvider? _catalog;
    private readonly IDisposable? _subscription;
    private readonly ConcurrentQueue<FlexTelemetryEvent> _events = new();
    private int _eventCount;
    private readonly int _maxEvents;

    public DefaultFlexTelemetryDataSource(
        IFlexControlLogSink? sink = null,
        ITelemetryCatalogProvider? catalog = null,
        int maxEvents = 50_000)
    {
        _sink = sink;
        _catalog = catalog;
        _maxEvents = Math.Clamp(maxEvents, 2_000, 250_000);

        if (_sink is not null)
        {
            foreach (var existing in _sink.GetRecentRecords(_maxEvents))
            {
                EnqueueInternal(ConvertFromControlRecord(existing));
            }
            _subscription = _sink.Subscribe(OnControlBatch);
        }
    }

    public void Dispose() => _subscription?.Dispose();

    private void OnControlBatch(IReadOnlyList<FlexControlLogRecord> batch)
    {
        for (int i = 0; i < batch.Count; i++)
        {
            EnqueueInternal(ConvertFromControlRecord(batch[i]));
        }
    }

    public void RecordEvent(FlexTelemetryEvent telemetryEvent)
    {
        if (telemetryEvent is not null)
            EnqueueInternal(telemetryEvent);
    }

    private void EnqueueInternal(FlexTelemetryEvent evt)
    {
        _events.Enqueue(evt);
        if (Interlocked.Increment(ref _eventCount) > _maxEvents)
        {
            while (_eventCount > _maxEvents && _events.TryDequeue(out _))
                Interlocked.Decrement(ref _eventCount);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        _sink is not null ? _sink.FlushToDiskAsync(cancellationToken) : Task.CompletedTask;

    public IReadOnlyList<FlexTelemetryEvent> GetRecentSessionEvents(string? sessionId, int maxCount = 50)
    {
        int cap = Math.Clamp(maxCount, 1, 1_000);
        var all = _events.ToArray();
        // A session gets its own events only, and no session gets none.
        if (string.IsNullOrWhiteSpace(sessionId))
            return Array.Empty<FlexTelemetryEvent>();
        IEnumerable<FlexTelemetryEvent> filtered =
            all.Where(e => string.Equals(e.SessionId, sessionId, StringComparison.Ordinal));

        return filtered
            .OrderByDescending(e => e.TimestampUtc)
            .Take(cap)
            .OrderBy(e => e.TimestampUtc)
            .ToList();
    }

    public FlexTelemetrySnapshot BuildSnapshot(FlexTelemetryQueryFilter filter, string defaultTenant = "")
    {
        var storageHint = _sink is FlexControlLogStore store
            ? $"NDJSON: {Path.GetFileName(store.LogDirectoryPath)}/flexkit-controls-{DateTime.UtcNow:yyyyMMdd}.ndjson"
            : "In-Memory FlexControlLogStore";

        return FlexTelemetryAggregator.BuildSnapshot(
            _events.ToArray(),
            _sink?.GetRegisteredControls() ?? Array.Empty<FlexControlRegistration>(),
            _catalog,
            filter ?? new FlexTelemetryQueryFilter(),
            defaultTenant,
            storageHint);
    }

    public IReadOnlyList<FlexTelemetryEvent> QueryEvents(
        DateTime sinceUtc,
        string? tenant = null,
        string defaultTenant = "",
        int maxCount = 50_000)
    {
        int cap = Math.Clamp(maxCount, 1, 250_000);
        var targetTenant = (tenant ?? string.Empty).Trim();
        bool allTenants = string.IsNullOrEmpty(targetTenant) || string.Equals(targetTenant, "*", StringComparison.Ordinal);
        var arr = _events.ToArray();
        var list = new List<FlexTelemetryEvent>(Math.Min(cap, arr.Length));
        for (int i = 0; i < arr.Length; i++)
        {
            var e = arr[i];
            if (e.TimestampUtc < sinceUtc)
                continue;
            if (!FlexTelemetryAggregator.MatchesTenant(e.Tenant, targetTenant, allTenants, defaultTenant))
                continue;
            list.Add(e);
        }
        if (list.Count > cap)
        {
            list = list.OrderByDescending(x => x.TimestampUtc).Take(cap).OrderBy(x => x.TimestampUtc).ToList();
        }
        else
        {
            list.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        }
        return list;
    }

    public static FlexTelemetryEvent ConvertFromControlRecord(FlexControlLogRecord rec)
    {
        var kind = ClassifyControlRecord(rec.ControlType, rec.Action);
        return new FlexTelemetryEvent
        {
            TimestampUtc = rec.TimestampUtc,
            Level = string.IsNullOrWhiteSpace(rec.Level) ? "INFO" : rec.Level,
            Application = rec.Application,
            Tenant = rec.Tenant ?? string.Empty,
            DivisionId = rec.DivisionId,
            UserId = rec.UserId ?? string.Empty,
            SessionId = rec.SessionId ?? string.Empty,
            ClientIp = rec.ClientIp ?? string.Empty,
            Kind = kind,
            Category = rec.ControlType ?? "Control",
            Action = rec.Action ?? string.Empty,
            Screen = string.IsNullOrWhiteSpace(rec.Screen) ? "Main" : rec.Screen,
            Route = string.IsNullOrWhiteSpace(rec.Route) ? "/" : rec.Route,
            Location = rec.Location ?? string.Empty,
            Control = rec.ControlId ?? string.Empty,
            Tag = rec.Tag,
            Value = rec.Value,
            Detail = rec.Detail ?? string.Empty,
            DurationMs = rec.DurationMs
        };
    }

    public static TelemetryEventKind ClassifyControlRecord(string? controlType, string? action) =>
        controlType switch
        {
            "GridControl" or "TreeGridControl" => TelemetryEventKind.GridOperation,
            "MenuActionControl" or "ContextMenuItemControl" => TelemetryEventKind.MenuAction,
            "MdiHost" when string.Equals(action, "WindowActivated", StringComparison.Ordinal) => TelemetryEventKind.ScreenOpen,
            "MdiHost" when string.Equals(action, "WindowClosed", StringComparison.Ordinal) => TelemetryEventKind.ScreenClose,
            "MdiErrorBoundary" => TelemetryEventKind.UnhandledError,
            "ButtonControl" or "ToolbarButtonControl" or "SplitButtonControl" or "TabStripControl" or "MdiHost" => TelemetryEventKind.ButtonClick,
            "ReportWriterControl" => action is "Printed" or "Exported" or "PageChanged" or "Searched"
                ? TelemetryEventKind.ButtonClick
                : TelemetryEventKind.ControlChange,
            _ => TelemetryEventKind.ControlChange
        };
}

/// <summary>
/// Shared rollup engine converting raw <see cref="FlexTelemetryEvent"/> streams and
/// <see cref="FlexControlRegistration"/> entries into a <see cref="FlexTelemetrySnapshot"/>.
/// </summary>
public static class FlexTelemetryAggregator
{
    public static FlexTelemetrySnapshot BuildSnapshot(
        IReadOnlyList<FlexTelemetryEvent> allEvents,
        IReadOnlyList<FlexControlRegistration> registeredControls,
        ITelemetryCatalogProvider? catalog,
        FlexTelemetryQueryFilter filter,
        string defaultTenant = "",
        string storageHint = "")
    {
        var nowUtc = DateTime.UtcNow;
        var daysBack = Math.Clamp(filter.DaysBack <= 0 ? 30 : filter.DaysBack, 1, 365);
        int? minutesBack = filter.MinutesBack is > 0
            ? Math.Clamp(filter.MinutesBack.Value, 1, 525_600)
            : null;
        var cutoffUtc = minutesBack.HasValue
            ? nowUtc.AddMinutes(-minutesBack.Value)
            : nowUtc.AddDays(-daysBack);
        var timeWindowLabel = minutesBack.HasValue
            ? (minutesBack.Value < 60
                ? $"Last {minutesBack.Value}m"
                : (minutesBack.Value % 1440 == 0 ? $"Last {minutesBack.Value / 1440}d" : $"Last {minutesBack.Value / 60}h"))
            : $"Last {daysBack}d";
        var totalSpanMinutes = minutesBack ?? (daysBack * 1440);

        var targetTenant = (filter.Tenant ?? string.Empty).Trim();
        var allTenants = string.IsNullOrEmpty(targetTenant) || string.Equals(targetTenant, "*", StringComparison.Ordinal);

        var windowEvents = new List<FlexTelemetryEvent>(allEvents.Count);
        for (int i = 0; i < allEvents.Count; i++)
        {
            var e = allEvents[i];
            if (e.TimestampUtc < cutoffUtc)
                continue;
            if (!MatchesTenant(e.Tenant, targetTenant, allTenants, defaultTenant))
                continue;
            if (!string.IsNullOrWhiteSpace(filter.SessionId)
                && !string.Equals(e.SessionId, filter.SessionId, StringComparison.Ordinal))
                continue;
            windowEvents.Add(e);
        }

        var availableUsers = windowEvents
            .Select(e => e.UserId)
            .Where(u => !string.IsNullOrWhiteSpace(u) && !string.Equals(u, "anonymous", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var userFilter = (filter.UserId ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(userFilter) && !string.Equals(userFilter, "All", StringComparison.OrdinalIgnoreCase))
        {
            windowEvents = windowEvents
                .Where(e => string.Equals(e.UserId, userFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var moduleFilter = (filter.ModuleFilter ?? string.Empty).Trim();
        var hasModuleFilter = !string.IsNullOrEmpty(moduleFilter)
            && !string.Equals(moduleFilter, "All", StringComparison.OrdinalIgnoreCase);
        var search = (filter.SearchText ?? string.Empty).Trim();

        var knownScreens = catalog?.GetKnownScreens() ?? Array.Empty<TelemetryCatalogScreen>();
        var knownTasks = catalog?.GetKnownTasksAndReports() ?? Array.Empty<TelemetryCatalogTask>();

        var screenMap = new Dictionary<string, ScreenAcc>(StringComparer.OrdinalIgnoreCase);
        foreach (var ks in knownScreens)
            screenMap[ks.RouteKey] = new ScreenAcc(ks.RouteKey, ks.Title, ks.Module, isCatalog: true);

        var taskMap = new Dictionary<string, TaskAcc>(StringComparer.OrdinalIgnoreCase);
        foreach (var kt in knownTasks)
            taskMap[kt.Key] = new TaskAcc(kt.KindLabel, kt.Title, kt.Module);

        var controlMap = new Dictionary<string, ControlAcc>(StringComparer.OrdinalIgnoreCase);
        var controlTypeMap = new Dictionary<string, ControlTypeAcc>(StringComparer.OrdinalIgnoreCase);
        var gridVerbMap = new Dictionary<string, GridVerbAcc>(StringComparer.OrdinalIgnoreCase);
        var gridOps = new List<FlexTelemetryEvent>();
        var observedControls = new Dictionary<string, ObservedAcc>(StringComparer.OrdinalIgnoreCase);
        var userMap = new Dictionary<string, UserAcc>(StringComparer.OrdinalIgnoreCase);

        var (bucketCount, bucketDurationMinutes, bucketStartUtc, bucketDateFormat) = ConfigureBuckets(nowUtc, totalSpanMinutes);
        var tsBuckets = new (int Screens, int Clicks, int Grids, int Changes, int Tasks)[bucketCount];

        int totalScreenOpens = 0;
        int totalTaskRuns = 0;
        int totalButtonClicks = 0;
        int totalGridOps = 0;
        int totalControlChanges = 0;

        foreach (var evt in windowEvents)
        {
            var localDt = evt.TimestampUtc.ToLocalTime();
            var normRoute = NormalizeRoute(evt.Route);
            var (resolvedTitle, resolvedModule) = catalog is not null
                ? catalog.ResolveScreenInfo(normRoute, evt.Screen)
                : (string.IsNullOrWhiteSpace(evt.Screen) ? normRoute : evt.Screen, "General");

            if (hasModuleFilter && !string.Equals(resolvedModule, moduleFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            int tsIdx = -1;
            if (evt.TimestampUtc >= bucketStartUtc)
            {
                var offsetMin = (evt.TimestampUtc - bucketStartUtc).TotalMinutes;
                tsIdx = Math.Clamp((int)(offsetMin / bucketDurationMinutes), 0, bucketCount - 1);
            }

            var userKey = string.IsNullOrWhiteSpace(evt.UserId) || string.Equals(evt.UserId, "anonymous", StringComparison.OrdinalIgnoreCase)
                ? "(anonymous)"
                : evt.UserId;
            if (!userMap.TryGetValue(userKey, out var uAcc))
            {
                uAcc = new UserAcc(userKey);
                userMap[userKey] = uAcc;
            }
            uAcc.TotalActions++;
            if (localDt > uAcc.LastSeenLocal)
                uAcc.LastSeenLocal = localDt;
            if (evt.DurationMs is > 0)
                uAcc.TotalDwellMs += evt.DurationMs.Value;

            if (!IsNoiseRoute(normRoute))
            {
                var heatScreen = GetOrAddScreen(screenMap, normRoute, resolvedTitle, resolvedModule);
                heatScreen.HourBlocks[Math.Clamp(localDt.Hour / 3, 0, 7)]++;
            }

            if (!string.IsNullOrWhiteSpace(evt.Location)
                || evt.Kind is TelemetryEventKind.ButtonClick or TelemetryEventKind.ControlChange or TelemetryEventKind.GridOperation or TelemetryEventKind.MenuAction)
            {
                var cType = string.IsNullOrWhiteSpace(evt.Category) ? "Control" : evt.Category;
                if (!controlTypeMap.TryGetValue(cType, out var ctAcc))
                {
                    ctAcc = new ControlTypeAcc(cType);
                    controlTypeMap[cType] = ctAcc;
                }
                ctAcc.Count++;
                if (!string.IsNullOrWhiteSpace(evt.Control))
                    ctAcc.Controls.Add($"{evt.Screen}|{evt.Control}");
                AddUser(ctAcc.Users, evt.UserId);

                var resolvedLoc = !string.IsNullOrWhiteSpace(evt.Location)
                    ? evt.Location
                    : $"{resolvedTitle} > {cType}:{evt.Control}";
                var obsKey = $"{resolvedTitle}|{resolvedLoc}|{cType}|{evt.Control}";
                if (!observedControls.TryGetValue(obsKey, out var obs))
                {
                    obs = new ObservedAcc(resolvedTitle, normRoute, resolvedLoc, cType, evt.Control);
                    observedControls[obsKey] = obs;
                }
                obs.ActionCount++;
                if (!string.IsNullOrEmpty(evt.Tag))
                    obs.Tag = evt.Tag;
                if (localDt >= obs.LastSeenLocal)
                {
                    obs.LastSeenLocal = localDt;
                    obs.LastAction = evt.Action;
                    obs.LastValue = evt.Value;
                }
            }

            switch (evt.Kind)
            {
                case TelemetryEventKind.Session:
                    if (string.Equals(evt.Action, "Login", StringComparison.OrdinalIgnoreCase))
                        uAcc.LoginCount++;
                    break;

                case TelemetryEventKind.ScreenOpen:
                    if (!IsNoiseRoute(normRoute))
                    {
                        totalScreenOpens++;
                        if (tsIdx >= 0) tsBuckets[tsIdx].Screens++;
                        uAcc.ScreenOpens++;
                        uAcc.IncrementScreen(resolvedTitle);

                        var sAcc = GetOrAddScreen(screenMap, normRoute, resolvedTitle, resolvedModule);
                        sAcc.Opens++;
                        AddUser(sAcc.Users, evt.UserId);
                        if (sAcc.LastUsedLocal is null || localDt > sAcc.LastUsedLocal)
                            sAcc.LastUsedLocal = localDt;
                    }
                    break;

                case TelemetryEventKind.ScreenFocus:
                    if (!IsNoiseRoute(normRoute))
                    {
                        var sAcc = GetOrAddScreen(screenMap, normRoute, resolvedTitle, resolvedModule);
                        sAcc.Focuses++;
                        if (evt.DurationMs is > 0)
                            sAcc.TotalDwellMs += evt.DurationMs.Value;
                        AddUser(sAcc.Users, evt.UserId);
                        if (sAcc.LastUsedLocal is null || localDt > sAcc.LastUsedLocal)
                            sAcc.LastUsedLocal = localDt;
                    }
                    break;

                case TelemetryEventKind.ScreenClose:
                    if (!IsNoiseRoute(normRoute) && evt.DurationMs is > 0)
                    {
                        var sAcc = GetOrAddScreen(screenMap, normRoute, resolvedTitle, resolvedModule);
                        sAcc.TotalDwellMs += evt.DurationMs.Value;
                        sAcc.DwellSamples++;
                    }
                    break;

                case TelemetryEventKind.TaskLaunch:
                case TelemetryEventKind.ReportRun:
                case TelemetryEventKind.InquiryRun:
                    {
                        totalTaskRuns++;
                        if (tsIdx >= 0) tsBuckets[tsIdx].Tasks++;
                        uAcc.TaskAndReportRuns++;

                        var kindLabel = evt.Kind switch
                        {
                            TelemetryEventKind.ReportRun => "Report",
                            TelemetryEventKind.InquiryRun => "Inquiry",
                            _ => "Task"
                        };
                        var itemName = !string.IsNullOrWhiteSpace(evt.Detail)
                            ? evt.Detail
                            : (!string.IsNullOrWhiteSpace(evt.Control) ? evt.Control : evt.Action);
                        var key = $"{kindLabel}:{itemName}";
                        if (!taskMap.TryGetValue(key, out var tAcc))
                        {
                            tAcc = new TaskAcc(kindLabel, itemName, resolvedModule);
                            taskMap[key] = tAcc;
                        }
                        tAcc.Runs++;
                        AddUser(tAcc.Users, evt.UserId);
                        if (tAcc.LastRunLocal is null || localDt > tAcc.LastRunLocal)
                            tAcc.LastRunLocal = localDt;
                        break;
                    }

                case TelemetryEventKind.GridOperation:
                    {
                        totalGridOps++;
                        if (tsIdx >= 0) tsBuckets[tsIdx].Grids++;
                        uAcc.GridOperations++;

                        if (!IsNoiseRoute(normRoute))
                        {
                            var sAcc = GetOrAddScreen(screenMap, normRoute, resolvedTitle, resolvedModule);
                            sAcc.GridOps++;
                            AddUser(sAcc.Users, evt.UserId);
                            if (sAcc.LastUsedLocal is null || localDt > sAcc.LastUsedLocal)
                                sAcc.LastUsedLocal = localDt;
                        }

                        var gAct = string.IsNullOrWhiteSpace(evt.Action) ? "Action" : evt.Action;
                        if (!gridVerbMap.TryGetValue(gAct, out var gAcc))
                        {
                            gAcc = new GridVerbAcc(gAct);
                            gridVerbMap[gAct] = gAcc;
                        }
                        gAcc.Count++;
                        gAcc.Grids.Add($"{evt.Screen}|{evt.Control}");
                        AddUser(gAcc.Users, evt.UserId);

                        gridOps.Add(evt);
                        break;
                    }

                case TelemetryEventKind.MenuAction:
                case TelemetryEventKind.ButtonClick:
                case TelemetryEventKind.ControlChange:
                case TelemetryEventKind.ShortcutKey:
                case TelemetryEventKind.UnhandledError:
                    {
                        if (evt.Kind == TelemetryEventKind.ControlChange)
                        {
                            totalControlChanges++;
                            if (tsIdx >= 0) tsBuckets[tsIdx].Changes++;
                            uAcc.ControlChanges++;
                        }
                        else
                        {
                            totalButtonClicks++;
                            if (tsIdx >= 0) tsBuckets[tsIdx].Clicks++;
                            uAcc.ButtonClicks++;
                        }

                        if (!IsNoiseRoute(normRoute))
                        {
                            var sAcc = GetOrAddScreen(screenMap, normRoute, resolvedTitle, resolvedModule);
                            sAcc.ControlActions++;
                            AddUser(sAcc.Users, evt.UserId);
                            if (sAcc.LastUsedLocal is null || localDt > sAcc.LastUsedLocal)
                                sAcc.LastUsedLocal = localDt;
                        }

                        var screenDisplay = !string.IsNullOrWhiteSpace(evt.Screen) ? evt.Screen : resolvedTitle;
                        var controlDisplay = !string.IsNullOrWhiteSpace(evt.Control) ? evt.Control : evt.Action;
                        var locDisplay = !string.IsNullOrWhiteSpace(evt.Location)
                            ? evt.Location
                            : $"{screenDisplay} > {evt.Category}:{controlDisplay}";
                        var cKey = $"{screenDisplay}||{locDisplay}||{evt.Category}||{controlDisplay}||{evt.Action}";
                        if (!controlMap.TryGetValue(cKey, out var cAcc))
                        {
                            cAcc = new ControlAcc(screenDisplay, normRoute, locDisplay, evt.Category, controlDisplay, evt.Action);
                            controlMap[cKey] = cAcc;
                        }
                        cAcc.Count++;
                        if (!string.IsNullOrEmpty(evt.Tag))
                            cAcc.Tag = evt.Tag;
                        AddUser(cAcc.Users, evt.UserId);
                        if (localDt >= cAcc.LastActionLocal)
                        {
                            cAcc.LastActionLocal = localDt;
                            if (evt.Value is not null)
                                cAcc.LastValue = evt.Value;
                        }
                        break;
                    }
            }
        }

        // The control registry is process-wide (no tenant), so it joins the all-tenants view only, and its
        // last typed value is never shown.
        foreach (var reg in allTenants ? registeredControls : Array.Empty<FlexControlRegistration>())
        {
            var normRoute = NormalizeRoute(reg.Route);
            var (resolvedTitle, resolvedModule) = catalog is not null
                ? catalog.ResolveScreenInfo(normRoute, reg.Screen)
                : (string.IsNullOrWhiteSpace(reg.Screen) ? normRoute : reg.Screen, "General");
            if (hasModuleFilter && !string.Equals(resolvedModule, moduleFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var screenLabel = !string.IsNullOrWhiteSpace(reg.Screen) ? reg.Screen : resolvedTitle;
            var obsKey = $"{screenLabel}|{reg.Location}|{reg.ControlType}|{reg.ControlId}";
            if (!observedControls.TryGetValue(obsKey, out var obs))
            {
                obs = new ObservedAcc(screenLabel, normRoute, reg.Location, reg.ControlType, reg.ControlId)
                {
                    Tag = reg.Tag,
                    ActionCount = Interlocked.Read(ref reg.ActionCount),
                    LastAction = reg.LastAction,
                    LastValue = null,
                    LastSeenLocal = reg.LastSeenUtc.ToLocalTime()
                };
                observedControls[obsKey] = obs;
            }
            else
            {
                var liveCount = Interlocked.Read(ref reg.ActionCount);
                if (liveCount > obs.ActionCount)
                    obs.ActionCount = liveCount;
                if (string.IsNullOrEmpty(obs.Tag) && !string.IsNullOrEmpty(reg.Tag))
                    obs.Tag = reg.Tag;
            }
        }

        var availableModules = screenMap.Values
            .Select(s => s.Module)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var screenRows = screenMap.Values
            .Where(s => !hasModuleFilter || string.Equals(s.Module, moduleFilter, StringComparison.OrdinalIgnoreCase))
            .Where(s => string.IsNullOrEmpty(search)
                || s.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                || s.Route.Contains(search, StringComparison.OrdinalIgnoreCase)
                || s.Module.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(s =>
            {
                var dwellMinutes = Math.Round(s.TotalDwellMs / 60000.0, 1);
                var avgSeconds = s.Opens > 0
                    ? Math.Round((s.TotalDwellMs / 1000.0) / s.Opens, 1)
                    : (s.DwellSamples > 0 ? Math.Round((s.TotalDwellMs / 1000.0) / s.DwellSamples, 1) : 0);
                var status = s.Opens == 0 && s.ControlActions == 0 && s.GridOps == 0
                    ? "Unused (0 opens)"
                    : s.Opens <= 2
                        ? "Rarely Used"
                        : s.Opens <= 15
                            ? "Moderate"
                            : "Active";

                return new FlexTelemetryScreenRow
                {
                    Route = s.Route,
                    ScreenTitle = s.Title,
                    Module = s.Module,
                    OpenCount = s.Opens,
                    FocusCount = s.Focuses,
                    ControlActionsOnScreen = s.ControlActions,
                    GridOpsOnScreen = s.GridOps,
                    UniqueUsers = s.Users.Count,
                    TotalDwellMinutes = dwellMinutes,
                    AvgDwellSeconds = avgSeconds,
                    LastUsedLocal = s.LastUsedLocal,
                    UsageStatus = status
                };
            })
            .OrderByDescending(r => r.OpenCount)
            .ThenByDescending(r => r.ControlActionsOnScreen + r.GridOpsOnScreen)
            .ThenBy(r => r.ScreenTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var taskRows = taskMap.Values
            .Where(t => !hasModuleFilter || string.Equals(t.Module, moduleFilter, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrEmpty(search)
                || t.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || t.Module.Contains(search, StringComparison.OrdinalIgnoreCase)
                || t.KindLabel.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(t => new FlexTelemetryTaskRow
            {
                KindLabel = t.KindLabel,
                Name = t.Name,
                Module = t.Module,
                RunCount = t.Runs,
                UniqueUsers = t.Users.Count,
                LastRunLocal = t.LastRunLocal,
                UsageStatus = t.Runs == 0 ? "Unused (0 runs)" : t.Runs <= 2 ? "Rarely Used" : "Active"
            })
            .OrderByDescending(r => r.RunCount)
            .ThenBy(r => r.KindLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var controlRows = controlMap.Values
            .Where(c => string.IsNullOrEmpty(search)
                || c.Control.Contains(search, StringComparison.OrdinalIgnoreCase)
                || c.Screen.Contains(search, StringComparison.OrdinalIgnoreCase)
                || c.Location.Contains(search, StringComparison.OrdinalIgnoreCase)
                || c.ControlType.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(c.Tag) && c.Tag.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Select(c => new FlexTelemetryControlRow
            {
                Screen = c.Screen,
                Route = c.Route,
                Location = c.Location,
                ControlType = c.ControlType,
                Control = c.Control,
                Tag = c.Tag,
                Action = c.Action,
                LastValue = c.LastValue,
                ActionCount = c.Count,
                UniqueUsers = c.Users.Count,
                LastActionLocal = c.LastActionLocal
            })
            .OrderByDescending(c => c.ActionCount)
            .ThenByDescending(c => c.LastActionLocal)
            .Take(1000)
            .ToList();

        var userRows = userMap.Values
            .Select(u => new FlexTelemetryUserRow
            {
                UserId = u.UserId,
                LoginCount = u.LoginCount,
                ScreenOpens = u.ScreenOpens,
                TaskAndReportRuns = u.TaskAndReportRuns,
                ButtonClicks = u.ButtonClicks,
                GridOperations = u.GridOperations,
                ControlChanges = u.ControlChanges,
                TotalActions = u.TotalActions,
                ActiveMinutes = Math.Round(u.TotalDwellMs / 60000.0, 1),
                TopScreen = u.GetTopScreen(),
                LastSeenLocal = u.LastSeenLocal
            })
            .OrderByDescending(u => u.TotalActions)
            .ToList();

        var controlTypeRows = controlTypeMap.Values
            .Select(c => new FlexTelemetryControlTypeRow
            {
                ControlType = c.ControlType,
                EventCount = c.Count,
                UniqueControls = c.Controls.Count,
                UniqueUsers = c.Users.Count
            })
            .OrderByDescending(c => c.EventCount)
            .ToList();

        var gridActionSummaryRows = gridVerbMap.Values
            .Select(g => new FlexTelemetryGridActionSummaryRow
            {
                Action = g.Action,
                EventCount = g.Count,
                UniqueGrids = g.Grids.Count,
                UniqueUsers = g.Users.Count
            })
            .OrderByDescending(g => g.EventCount)
            .ToList();

        var gridOpRows = gridOps
            .Where(g => string.IsNullOrEmpty(search)
                || g.Screen.Contains(search, StringComparison.OrdinalIgnoreCase)
                || g.Location.Contains(search, StringComparison.OrdinalIgnoreCase)
                || g.Control.Contains(search, StringComparison.OrdinalIgnoreCase)
                || g.Action.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(g.Value) && g.Value.Contains(search, StringComparison.OrdinalIgnoreCase))
                || g.Detail.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(g.Tag) && g.Tag.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(g => g.TimestampUtc)
            .Take(800)
            .Select(g => new FlexTelemetryGridOpRow
            {
                TimestampLocal = g.TimestampUtc.ToLocalTime(),
                UserId = string.IsNullOrWhiteSpace(g.UserId) ? "-" : g.UserId,
                Screen = g.Screen,
                Location = !string.IsNullOrWhiteSpace(g.Location) ? g.Location : $"{g.Screen} > {g.Category}:{g.Control}",
                ControlType = g.Category,
                ControlId = g.Control,
                Tag = g.Tag,
                Action = g.Action,
                Value = g.Value,
                Detail = g.Detail
            })
            .ToList();

        var registeredRows = observedControls.Values
            .Where(r => string.IsNullOrEmpty(search)
                || r.Screen.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Location.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.ControlType.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.ControlId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(r.Tag) && r.Tag.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(r => r.ActionCount)
            .ThenByDescending(r => r.LastSeenLocal)
            .Take(1200)
            .Select(r => new FlexTelemetryRegisteredControlRow
            {
                Screen = r.Screen,
                Route = r.Route,
                Location = r.Location,
                ControlType = r.ControlType,
                ControlId = r.ControlId,
                Tag = r.Tag,
                ActionCount = r.ActionCount,
                LastAction = r.LastAction,
                LastValue = r.LastValue,
                LastSeenLocal = r.LastSeenLocal
            })
            .ToList();

        var heatmapRows = screenMap.Values
            .Where(s => s.HourBlocks.Sum() > 0)
            .OrderByDescending(s => s.HourBlocks.Sum())
            .Take(7)
            .Select(s => new FlexTelemetryHeatmapRow
            {
                Screen = s.Title,
                BlockCounts = (int[])s.HourBlocks.Clone()
            })
            .ToList();

        var timeSeriesPoints = new List<FlexTelemetryTimeBucketRow>(bucketCount);
        for (int i = 0; i < bucketCount; i++)
        {
            var slotLocal = bucketStartUtc.AddMinutes(i * bucketDurationMinutes).ToLocalTime();
            var b = tsBuckets[i];
            timeSeriesPoints.Add(new FlexTelemetryTimeBucketRow
            {
                BucketLabel = slotLocal.ToString(bucketDateFormat),
                ScreenOpens = b.Screens,
                ButtonClicks = b.Clicks,
                GridOperations = b.Grids,
                ControlChanges = b.Changes,
                TasksAndReports = b.Tasks
            });
        }

        var recentEvents = windowEvents
            .Where(e => string.IsNullOrEmpty(search)
                || e.Screen.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Location.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Control.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Action.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(e.Value) && e.Value.Contains(search, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(e.Tag) && e.Tag.Contains(search, StringComparison.OrdinalIgnoreCase))
                || e.Detail.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.UserId.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.TimestampUtc)
            .Take(500)
            .Select(e => new FlexTelemetryEventRow
            {
                TimestampUtc = e.TimestampUtc,
                TimestampLocal = e.TimestampUtc.ToLocalTime(),
                Level = string.IsNullOrWhiteSpace(e.Level) ? "INFO" : e.Level,
                UserId = string.IsNullOrWhiteSpace(e.UserId) ? "-" : e.UserId,
                Tenant = e.Tenant,
                DivisionId = e.DivisionId,
                SessionId = e.SessionId,
                Kind = e.Kind.ToString(),
                ControlType = e.Category,
                Action = e.Action,
                Screen = e.Screen,
                Route = e.Route,
                Location = !string.IsNullOrWhiteSpace(e.Location)
                    ? e.Location
                    : (!string.IsNullOrWhiteSpace(e.Screen) && !string.IsNullOrWhiteSpace(e.Control)
                        ? $"{e.Screen} > {e.Category}:{e.Control}"
                        : e.Screen),
                Control = e.Control,
                Tag = e.Tag,
                Value = e.Value,
                Detail = e.Detail,
                DurationSeconds = e.DurationMs.HasValue ? Math.Round(e.DurationMs.Value / 1000.0, 1) : null,
                CloudWatchJson = e.ToCloudWatchJson(indented: false),
                SerilogClefJson = e.ToSerilogClefJson(indented: false),
                CloudWatchJsonIndented = e.ToCloudWatchJson(indented: true),
                SerilogClefJsonIndented = e.ToSerilogClefJson(indented: true)
            })
            .ToList();

        var activeScreensCount = screenMap.Values.Count(s => s.Opens > 0 || s.ControlActions > 0 || s.GridOps > 0);
        var catalogCount = knownScreens.Count > 0 ? knownScreens.Count : screenMap.Count;
        var visitedCatalog = knownScreens.Count > 0
            ? screenMap.Values.Count(s => s.IsCatalog && (s.Opens > 0 || s.ControlActions > 0 || s.GridOps > 0))
            : activeScreensCount;

        return new FlexTelemetrySnapshot
        {
            GeneratedAtLocal = DateTime.Now,
            TenantScope = string.Equals(targetTenant, "*", StringComparison.Ordinal)
                ? "All"
                : (string.IsNullOrEmpty(targetTenant) ? (string.IsNullOrEmpty(defaultTenant) ? "Default" : defaultTenant) : targetTenant),
            StorageHint = storageHint,
            DaysBack = daysBack,
            MinutesBack = minutesBack,
            TimeWindowLabel = timeWindowLabel,
            TotalEvents = windowEvents.Count,
            TotalScreenOpens = totalScreenOpens,
            TotalTaskAndReportRuns = totalTaskRuns,
            TotalButtonClicks = totalButtonClicks,
            TotalGridOperations = totalGridOps,
            TotalControlChanges = totalControlChanges,
            RegisteredControlCount = observedControls.Count,
            UniqueUsers = availableUsers.Count,
            UniqueScreensVisited = activeScreensCount,
            CatalogScreenCount = catalogCount,
            UnusedCatalogScreenCount = Math.Max(0, catalogCount - visitedCatalog),
            AvailableUsers = availableUsers,
            AvailableModules = availableModules,
            ScreenRows = screenRows,
            TaskAndReportRows = taskRows,
            ControlRows = controlRows,
            UserRows = userRows,
            ControlTypeRows = controlTypeRows,
            GridOperationRows = gridOpRows,
            GridActionSummaryRows = gridActionSummaryRows,
            RegisteredControlRows = registeredRows,
            ScreenHourHeatmap = heatmapRows,
            TimeSeriesBuckets = timeSeriesPoints,
            RecentEvents = recentEvents
        };
    }

    private static (int BucketCount, double BucketMinutes, DateTime StartUtc, string LabelFormat) ConfigureBuckets(DateTime nowUtc, int totalSpanMinutes)
    {
        if (totalSpanMinutes <= 15) return (15, 1.0, nowUtc.AddMinutes(-15), "HH:mm");
        if (totalSpanMinutes <= 60) return (12, 5.0, nowUtc.AddMinutes(-60), "HH:mm");
        if (totalSpanMinutes <= 360) return (18, 20.0, nowUtc.AddMinutes(-360), "HH:mm");
        if (totalSpanMinutes <= 1440) return (24, 60.0, nowUtc.AddMinutes(-1440), "HH:mm");
        if (totalSpanMinutes <= 10080) return (14, 720.0, nowUtc.AddMinutes(-10080), "MM/dd HH'h'");
        var chartDays = Math.Min(Math.Max(1, totalSpanMinutes / 1440), 30);
        return (chartDays, 1440.0, nowUtc.AddDays(-chartDays), "MM/dd");
    }

    internal static bool MatchesTenant(string eventTenant, string filterTenant, bool allTenants, string defaultTenant)
    {
        if (allTenants) return true;
        var effective = string.IsNullOrWhiteSpace(filterTenant) ? defaultTenant : filterTenant;
        if (string.IsNullOrWhiteSpace(effective)) return true;
        if (string.IsNullOrWhiteSpace(eventTenant))
            return string.Equals(effective, defaultTenant, StringComparison.OrdinalIgnoreCase);
        return string.Equals(eventTenant, effective, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRoute(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return "/";
        var r = route.Trim();
        var q = r.IndexOf('?');
        if (q >= 0) r = r[..q];
        if (!r.StartsWith('/')) r = "/" + r;
        return r.Length > 1 ? r.TrimEnd('/') : r;
    }

    private static bool IsNoiseRoute(string normRoute) =>
        normRoute is "/" or "/login" or "/logout";

    private static void AddUser(HashSet<string> set, string? userId)
    {
        if (!string.IsNullOrWhiteSpace(userId) && !string.Equals(userId, "anonymous", StringComparison.OrdinalIgnoreCase))
            set.Add(userId);
    }

    private static ScreenAcc GetOrAddScreen(Dictionary<string, ScreenAcc> map, string routeKey, string title, string module)
    {
        if (!map.TryGetValue(routeKey, out var acc))
        {
            acc = new ScreenAcc(routeKey, title, module, isCatalog: false);
            map[routeKey] = acc;
        }
        return acc;
    }

    private sealed class ScreenAcc(string route, string title, string module, bool isCatalog)
    {
        public string Route { get; } = route;
        public string Title { get; } = title;
        public string Module { get; } = module;
        public bool IsCatalog { get; } = isCatalog;
        public int Opens;
        public int Focuses;
        public int ControlActions;
        public int GridOps;
        public long TotalDwellMs;
        public int DwellSamples;
        public DateTime? LastUsedLocal;
        public HashSet<string> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int[] HourBlocks { get; } = new int[8];
    }

    private sealed class TaskAcc(string kindLabel, string name, string module)
    {
        public string KindLabel { get; } = kindLabel;
        public string Name { get; } = name;
        public string Module { get; } = module;
        public int Runs;
        public DateTime? LastRunLocal;
        public HashSet<string> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ControlAcc(string screen, string route, string location, string controlType, string control, string action)
    {
        public string Screen { get; } = screen;
        public string Route { get; } = route;
        public string Location { get; } = location;
        public string ControlType { get; } = controlType;
        public string Control { get; } = control;
        public string Action { get; } = action;
        public string? Tag;
        public string? LastValue;
        public int Count;
        public DateTime LastActionLocal;
        public HashSet<string> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ControlTypeAcc(string controlType)
    {
        public string ControlType { get; } = controlType;
        public int Count;
        public HashSet<string> Controls { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class GridVerbAcc(string action)
    {
        public string Action { get; } = action;
        public int Count;
        public HashSet<string> Grids { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ObservedAcc(string screen, string route, string location, string controlType, string controlId)
    {
        public string Screen { get; } = screen;
        public string Route { get; } = route;
        public string Location { get; } = location;
        public string ControlType { get; } = controlType;
        public string ControlId { get; } = controlId;
        public string? Tag;
        public long ActionCount;
        public string LastAction = "Registered";
        public string? LastValue;
        public DateTime LastSeenLocal = DateTime.Now;
    }

    private sealed class UserAcc(string userId)
    {
        public string UserId { get; } = userId;
        public int LoginCount;
        public int ScreenOpens;
        public int TaskAndReportRuns;
        public int ButtonClicks;
        public int GridOperations;
        public int ControlChanges;
        public int TotalActions;
        public long TotalDwellMs;
        public DateTime LastSeenLocal = DateTime.Now;
        private readonly Dictionary<string, int> _screens = new(StringComparer.OrdinalIgnoreCase);

        public void IncrementScreen(string screen)
        {
            if (string.IsNullOrWhiteSpace(screen)) return;
            _screens.TryGetValue(screen, out var c);
            _screens[screen] = c + 1;
        }

        public string GetTopScreen() =>
            _screens.Count == 0
                ? "-"
                : _screens.OrderByDescending(kv => kv.Value).First().Key;
    }
}
