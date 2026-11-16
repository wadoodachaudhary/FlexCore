using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Logging;

/// <summary>
/// Standard structured control telemetry row emitted by FlexKit controls.
/// Compatible out-of-the-box with Serilog (structured message templates + CLEF)
/// and AWS CloudWatch Logs Insights / OpenTelemetry single-line NDJSON.
/// </summary>
public sealed record FlexControlLogRecord
{
    private static long _nextSequence;

    [JsonPropertyName("eventId")]
    public long EventId { get; init; } = Interlocked.Increment(ref _nextSequence);

    [JsonPropertyName("@timestamp")]
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("level")]
    public string Level { get; init; } = "INFO";

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = "FlexControlAction";

    [JsonPropertyName("app")]
    public string Application { get; init; } = "FlexKitApp";

    [JsonPropertyName("tenant")]
    public string Tenant { get; init; } = "";

    [JsonPropertyName("divisionId")]
    public int DivisionId { get; init; }

    [JsonPropertyName("userId")]
    public string UserId { get; init; } = "anonymous";

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = "";

    [JsonPropertyName("clientIp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientIp { get; init; }

    [JsonPropertyName("screen")]
    public string Screen { get; init; } = "";

    [JsonPropertyName("route")]
    public string Route { get; init; } = "";

    [JsonPropertyName("location")]
    public string Location { get; init; } = "";

    [JsonPropertyName("controlType")]
    public string ControlType { get; init; } = "";

    [JsonPropertyName("controlId")]
    public string ControlId { get; init; } = "";

    [JsonPropertyName("tag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tag { get; init; }

    [JsonPropertyName("action")]
    public string Action { get; init; } = "";

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; init; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; init; }

    private static readonly JsonSerializerOptions CloudWatchJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Formats a single-line JSON log row ready for AWS CloudWatch Logs Insights, Grafana Loki, Datadog, or ELK.
    /// </summary>
    public string ToCloudWatchJson() => JsonSerializer.Serialize(this, CloudWatchJsonOptions);

    /// <summary>
    /// Formats a Serilog Compact Log Event Format (CLEF) single-line JSON row (@t, @mt, @l + properties).
    /// </summary>
    public string ToSerilogClefJson()
    {
        var payload = new Dictionary<string, object?>(18, StringComparer.Ordinal)
        {
            ["@t"] = TimestampUtc.ToString("O"),
            ["@l"] = "Information",
            ["@mt"] = "FlexControl {ControlType} {ControlId} {Action} at {Location}",
            ["EventId"] = EventId,
            ["EventType"] = EventType,
            ["App"] = Application,
            ["Tenant"] = Tenant,
            ["DivisionId"] = DivisionId,
            ["UserId"] = UserId,
            ["SessionId"] = SessionId,
            ["Screen"] = Screen,
            ["Route"] = Route,
            ["Location"] = Location,
            ["ControlType"] = ControlType,
            ["ControlId"] = ControlId,
            ["Action"] = Action
        };
        if (!string.IsNullOrEmpty(Tag)) payload["Tag"] = Tag;
        if (Value is not null) payload["Value"] = Value;
        if (!string.IsNullOrEmpty(Detail)) payload["Detail"] = Detail;
        if (!string.IsNullOrEmpty(ClientIp)) payload["ClientIp"] = ClientIp;
        if (DurationMs.HasValue) payload["DurationMs"] = DurationMs.Value;
        return JsonSerializer.Serialize(payload, CloudWatchJsonOptions);
    }

    public override string ToString() => ToCloudWatchJson();
}

/// <summary>
/// Live registration entry tracking where a FlexKit control lives in the visual hierarchy and its usage statistics.
/// </summary>
public sealed class FlexControlRegistration
{
    public required string LocationKey { get; init; }
    public required string Screen { get; init; }
    public required string Route { get; init; }
    public required string Location { get; init; }
    public required string ControlType { get; init; }
    public required string ControlId { get; init; }
    public string? Tag { get; set; }
    public DateTime FirstSeenUtc { get; init; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public long ActionCount;
    public string LastAction { get; set; } = "Registered";
    /// <summary>
    /// Always null in <see cref="FlexControlLogStore"/>: the registry is one for the whole server and holds no
    /// user or tenant, so it never keeps what was typed. A value belongs to its session's own records.
    /// </summary>
    public string? LastValue { get; set; }
}

/// <summary>
/// Hierarchical parent location node cascaded with IsFixed="true" from container controls
/// (MdiHost, DialogControl, ToolbarControl, CardControl, TabStripControl, WizardControl, etc.)
/// so child controls resolve their full location path at action time with zero render overhead.
/// </summary>
public sealed class FlexControlLocation
{
    private readonly Func<string?> _nameResolver;
    private readonly Func<string?>? _routeResolver;

    public FlexControlLocation? Parent { get; }
    public string ContainerType { get; }
    public bool IsScreenRoot { get; }

    public FlexControlLocation(
        FlexControlLocation? parent,
        string containerType,
        Func<string?> nameResolver,
        Func<string?>? routeResolver = null,
        bool isScreenRoot = false)
    {
        Parent = parent;
        ContainerType = containerType;
        _nameResolver = nameResolver;
        _routeResolver = routeResolver;
        IsScreenRoot = isScreenRoot;
    }

    public string ResolveName()
    {
        try { return _nameResolver()?.Trim() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public string ResolveRoute()
    {
        if (_routeResolver is null) return string.Empty;
        try { return _routeResolver()?.Trim() ?? string.Empty; }
        catch { return string.Empty; }
    }

    public (string Screen, string Route, string ContainerPath) ResolveHierarchy()
    {
        var segments = new List<string>(6);
        string screen = string.Empty;
        string route = string.Empty;

        var current = this;
        int guard = 0;
        while (current is not null && guard++ < 16)
        {
            var name = current.ResolveName();
            var r = current.ResolveRoute();
            if (string.IsNullOrEmpty(route) && !string.IsNullOrEmpty(r))
                route = r;

            if (current.IsScreenRoot)
            {
                if (string.IsNullOrEmpty(screen) && !string.IsNullOrEmpty(name))
                    screen = name;
                if (!string.IsNullOrEmpty(name))
                    segments.Add(name);
            }
            else if (!string.IsNullOrEmpty(name))
            {
                segments.Add($"{current.ContainerType}:{name}");
            }
            else if (!string.IsNullOrEmpty(current.ContainerType))
            {
                segments.Add(current.ContainerType);
            }

            current = current.Parent;
        }

        segments.Reverse();
        return (screen, route, string.Join(" > ", segments));
    }
}

/// <summary>
/// Per-control state &amp; action log buffer attached to every FlexKitControlBase ("Log" field).
/// Constantly updated in memory as actions occur on the control and flushed asynchronously in batches
/// to the underlying IFlexControlLogSink without adding any client-server round trips.
/// </summary>
public sealed class FlexControlLog
{
    private const int MaxControlHistory = 32;
    // Records waiting for the end of the server turn. A control hands them to its sink at once when this
    // many build up in one turn, so the queue stays bounded without losing any.
    internal const int MaxPendingRecords = 256;
    private readonly object _sync = new();
    private readonly Queue<FlexControlLogRecord> _history = new(MaxControlHistory);
    private readonly List<FlexControlLogRecord> _pending = new(8);

    public FlexControlLogRecord? LastRecord { get; private set; }
    public string? LastAction => LastRecord?.Action;
    public string? LastValue => LastRecord?.Value;
    public string? LastDetail => LastRecord?.Detail;
    public DateTime? LastTimestampUtc => LastRecord?.TimestampUtc;
    public DateTime? LastFlushedUtc { get; private set; }
    public long TotalActions { get; private set; }

    public int PendingCount
    {
        get { lock (_sync) return _pending.Count; }
    }

    public IReadOnlyList<FlexControlLogRecord> Entries
    {
        get { lock (_sync) return _history.ToArray(); }
    }

    /// <summary>Latest standard CloudWatch/Serilog JSON row for this control, or empty if no action yet.</summary>
    public string CurrentStandardRow => LastRecord?.ToCloudWatchJson() ?? string.Empty;

    /// <param name="forSink">False when no enabled sink will flush the record: it is kept in this control's
    /// own history only, not queued.</param>
    /// <returns>The number of records now queued for the sink.</returns>
    internal int Append(FlexControlLogRecord record, bool forSink = true)
    {
        lock (_sync)
        {
            LastRecord = record;
            TotalActions++;
            if (_history.Count >= MaxControlHistory)
                _history.Dequeue();
            _history.Enqueue(record);
            if (!forSink)
                return _pending.Count;
            _pending.Add(record);
            return _pending.Count;
        }
    }

    /// <summary>
    /// Flushes any pending entries from this control's Log field into the underlying log sink in memory.
    /// </summary>
    public int FlushTo(IFlexControlLogSink? sink)
    {
        FlexControlLogRecord[] batch;
        lock (_sync)
        {
            if (_pending.Count == 0)
                return 0;
            batch = _pending.ToArray();
            _pending.Clear();
            LastFlushedUtc = DateTime.UtcNow;
        }

        sink?.ReceiveBatch(batch);
        return batch.Length;
    }

    public override string ToString() => CurrentStandardRow;
}

/// <summary>
/// Optional scoped context provider implemented by the host application so FlexKit controls
/// enrich standard log rows with user, tenant/database, division, session, and active route metadata
/// without FlexKit taking any dependency on host application types.
/// </summary>
public readonly record struct FlexControlHostContext(
    string Application,
    string Tenant,
    int DivisionId,
    string UserId,
    string SessionId,
    string? ClientIp,
    string FallbackScreen,
    string FallbackRoute);

public interface IFlexControlContextProvider
{
    FlexControlHostContext GetCurrentContext();
}

public sealed class FlexControlLoggingOptions
{
    public bool Enabled { get; set; } = true;
    public string ApplicationName { get; set; } = "FlexKitApp";
    public int MaxInMemoryRecords { get; set; } = 50_000;
    /// <summary>
    /// Most control locations the registry keeps (at least 500). Locations carry window titles, so a
    /// long-running server keeps meeting new ones; past this the least recently seen tenth is dropped.
    /// </summary>
    public int MaxRegisteredControls { get; set; } = 10_000;
    public int ChannelCapacity { get; set; } = 50_000;
    public int FlushIntervalSeconds { get; set; } = 3;
    public string? TempDirectory { get; set; }
    public string? LogDirectory { get; set; }
    public bool WriteStandardStructuredILogger { get; set; } = true;
    public bool UseClefFileFormat { get; set; }
}

/// <summary>
/// Central non-blocking sink receiving flushed control logs in memory.
/// </summary>
public interface IFlexControlLogSink
{
    bool IsEnabled { get; }
    void RegisterControlLocation(string screen, string route, string location, string controlType, string controlId, string? tag);
    void ReceiveBatch(IReadOnlyList<FlexControlLogRecord> records);
    IReadOnlyList<FlexControlLogRecord> GetRecentRecords(int maxCount = 5_000);
    IReadOnlyList<FlexControlRegistration> GetRegisteredControls();
    IDisposable Subscribe(Action<IReadOnlyList<FlexControlLogRecord>> observer);
    Task FlushToDiskAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// High-throughput, lock-free in-memory + temp-staged + rolling NDJSON/Serilog control telemetry store.
/// Never blocks UI or Blazor event threads and never adds client-server network trips.
/// </summary>
public sealed class FlexControlLogStore : IFlexControlLogSink, IAsyncDisposable, IDisposable
{
    private readonly FlexControlLoggingOptions _options;
    private readonly ILogger<FlexControlLogStore>? _logger;
    private readonly Channel<FlexControlLogRecord> _channel;
    private readonly ConcurrentQueue<FlexControlLogRecord> _ring = new();
    private int _ringCount;
    private readonly ConcurrentDictionary<string, FlexControlRegistration> _registry = new(StringComparer.OrdinalIgnoreCase);
    private int _registryTrimming;
    private readonly ConcurrentDictionary<Guid, Action<IReadOnlyList<FlexControlLogRecord>>> _observers = new();
    private readonly SemaphoreSlim _diskFlushGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _backgroundPump;
    private bool _disposed;

    public string TempBufferFilePath { get; }
    public string LogDirectoryPath { get; }
    public bool IsEnabled => _options.Enabled;

    public FlexControlLogStore(FlexControlLoggingOptions? options = null, ILogger<FlexControlLogStore>? logger = null)
    {
        _options = options ?? new FlexControlLoggingOptions();
        _logger = logger;

        var tempDir = string.IsNullOrWhiteSpace(_options.TempDirectory)
            ? Path.Combine(Path.GetTempPath(), "flexkit-control-logs")
            : _options.TempDirectory!;
        TempBufferFilePath = Path.Combine(tempDir, $"buffer-{Environment.ProcessId}.tmp.ndjson");

        LogDirectoryPath = string.IsNullOrWhiteSpace(_options.LogDirectory)
            ? tempDir
            : _options.LogDirectory!;

        _channel = Channel.CreateBounded<FlexControlLogRecord>(new BoundedChannelOptions(Math.Max(1_024, _options.ChannelCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        if (_options.Enabled)
        {
            _backgroundPump = Task.Run(() => RunBackgroundFlushLoopAsync(_cts.Token));
        }
    }

    public void RegisterControlLocation(
        string screen,
        string route,
        string location,
        string controlType,
        string controlId,
        string? tag)
    {
        if (!_options.Enabled)
            return;

        var key = $"{screen}|{location}|{controlType}|{controlId}";
        var now = DateTime.UtcNow;
        var added = false;
        _registry.AddOrUpdate(
            key,
            _ =>
            {
                added = true;
                return new FlexControlRegistration
                {
                    LocationKey = key,
                    Screen = screen,
                    Route = route,
                    Location = location,
                    ControlType = controlType,
                    ControlId = controlId,
                    Tag = tag,
                    FirstSeenUtc = now,
                    LastSeenUtc = now
                };
            },
            (_, existing) =>
            {
                existing.LastSeenUtc = now;
                if (!string.IsNullOrEmpty(tag))
                    existing.Tag = tag;
                return existing;
            });
        if (added)
            TrimRegistryIfFull();
    }

    public void ReceiveBatch(IReadOnlyList<FlexControlLogRecord> records)
    {
        if (!_options.Enabled || records.Count == 0)
            return;

        var maxRing = Math.Max(1_000, _options.MaxInMemoryRecords);
        var added = false;
        for (int i = 0; i < records.Count; i++)
        {
            var rec = records[i];

            _ring.Enqueue(rec);
            if (Interlocked.Increment(ref _ringCount) > maxRing)
            {
                while (_ringCount > maxRing && _ring.TryDequeue(out _))
                    Interlocked.Decrement(ref _ringCount);
            }

            var key = $"{rec.Screen}|{rec.Location}|{rec.ControlType}|{rec.ControlId}";
            var reg = _registry.GetOrAdd(key, _ =>
            {
                added = true;
                return new FlexControlRegistration
                {
                    LocationKey = key,
                    Screen = rec.Screen,
                    Route = rec.Route,
                    Location = rec.Location,
                    ControlType = rec.ControlType,
                    ControlId = rec.ControlId,
                    Tag = rec.Tag,
                    FirstSeenUtc = rec.TimestampUtc,
                    LastSeenUtc = rec.TimestampUtc
                };
            });
            Interlocked.Increment(ref reg.ActionCount);
            reg.LastSeenUtc = rec.TimestampUtc;
            reg.LastAction = rec.Action;
            // No value: the registry is shared by every user and tenant (see FlexControlRegistration.LastValue).
            if (!string.IsNullOrEmpty(rec.Tag))
                reg.Tag = rec.Tag;

            _channel.Writer.TryWrite(rec);
        }
        // The count is read only when a location was just added (Count takes every lock of the dictionary).
        if (added)
            TrimRegistryIfFull();

        if (!_observers.IsEmpty)
        {
            foreach (var observer in _observers.Values)
            {
                try { observer(records); }
                catch { /* Observer exceptions must never affect UI control execution. */ }
            }
        }
    }

    public IReadOnlyList<FlexControlLogRecord> GetRecentRecords(int maxCount = 5_000)
    {
        var all = _ring.ToArray();
        if (all.Length <= maxCount)
            return all;
        var slice = new FlexControlLogRecord[maxCount];
        Array.Copy(all, all.Length - maxCount, slice, 0, maxCount);
        return slice;
    }

    /// <summary>Number of control locations the registry holds now.</summary>
    public int RegisteredControlCount => _registry.Count;

    private void TrimRegistryIfFull()
    {
        var cap = Math.Max(500, _options.MaxRegisteredControls);
        if (_registry.Count <= cap || Interlocked.CompareExchange(ref _registryTrimming, 1, 0) != 0)
            return;
        try
        {
            var keep = cap - cap / 10;
            var excess = _registry.Count - keep;
            if (excess <= 0)
                return;
            foreach (var stale in _registry.Values.OrderBy(r => r.LastSeenUtc).Take(excess).ToList())
                _registry.TryRemove(stale.LocationKey, out _);
        }
        finally
        {
            Volatile.Write(ref _registryTrimming, 0);
        }
    }

    public IReadOnlyList<FlexControlRegistration> GetRegisteredControls() =>
        _registry.Values
            .OrderByDescending(r => Interlocked.Read(ref r.ActionCount))
            .ThenByDescending(r => r.LastSeenUtc)
            .ToList();

    public IDisposable Subscribe(Action<IReadOnlyList<FlexControlLogRecord>> observer)
    {
        var id = Guid.NewGuid();
        _observers[id] = observer;
        return new Subscription(_observers, id);
    }

    private async Task RunBackgroundFlushLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_options.FlushIntervalSeconds, 1, 60));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await FlushToDiskAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task FlushToDiskAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
            return;

        var drained = new List<FlexControlLogRecord>(128);
        while (_channel.Reader.TryRead(out var item))
            drained.Add(item);

        if (drained.Count == 0)
            return;

        await _diskFlushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sb = new StringBuilder(drained.Count * 220);
            for (int i = 0; i < drained.Count; i++)
            {
                var r = drained[i];
                sb.AppendLine(_options.UseClefFileFormat ? r.ToSerilogClefJson() : r.ToCloudWatchJson());

                if (_options.WriteStandardStructuredILogger && _logger is not null)
                {
                    _logger.LogInformation(
                        "FlexControl {ControlType} {ControlId} {Action} at {Location} | Value={Value} Tag={Tag} Screen={Screen} Route={Route} User={UserId} Tenant={Tenant} Division={DivisionId}",
                        r.ControlType,
                        r.ControlId,
                        r.Action,
                        r.Location,
                        r.Value ?? "",
                        r.Tag ?? "",
                        r.Screen,
                        r.Route,
                        r.UserId,
                        r.Tenant,
                        r.DivisionId);
                }
            }

            var payload = sb.ToString();

            // 1. Stage to temp location first so crashes never lose an in-flight batch, then promote to rolling log file.
            var tempDir = Path.GetDirectoryName(TempBufferFilePath)!;
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(TempBufferFilePath, payload, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(LogDirectoryPath);
            var targetLogPath = Path.Combine(
                LogDirectoryPath,
                $"flexkit-controls-{DateTime.UtcNow:yyyyMMdd}.ndjson");
            await File.AppendAllTextAsync(targetLogPath, payload, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

            try { File.Delete(TempBufferFilePath); } catch { }
        }
        catch
        {
            // Telemetry disk write is strictly non-intrusive; memory ring buffer retains records.
        }
        finally
        {
            _diskFlushGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _channel.Writer.TryComplete();
        if (_backgroundPump is not null)
        {
            try { await _backgroundPump.ConfigureAwait(false); } catch { }
        }
        try { await FlushToDiskAsync().ConfigureAwait(false); } catch { }
        _cts.Dispose();
        _diskFlushGate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Subscription(ConcurrentDictionary<Guid, Action<IReadOnlyList<FlexControlLogRecord>>> map, Guid id) : IDisposable
    {
        public void Dispose() => map.TryRemove(id, out _);
    }
}

public static class FlexControlLoggingServiceCollectionExtensions
{
    public static IServiceCollection AddFlexControlLogging(
        this IServiceCollection services,
        Action<FlexControlLoggingOptions>? configure = null)
    {
        var opts = new FlexControlLoggingOptions();
        configure?.Invoke(opts);
        services.TryAddSingleton(opts);
        services.TryAddSingleton<FlexControlLogStore>();
        services.TryAddSingleton<IFlexControlLogSink>(sp => sp.GetRequiredService<FlexControlLogStore>());
        return services;
    }
}
