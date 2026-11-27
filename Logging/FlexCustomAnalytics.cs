using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fx.ControlKit.Logging;

/// <summary>
/// Vertex in a structured FlexKit custom telemetry / interaction graph.
/// Models participating entities such as Controls, Columns, Row Selections, Drop Targets, Search Queries, or Workflow Steps.
/// </summary>
public sealed class FlexTelemetryNode
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "Entity";

    [JsonPropertyName("label")]
    public string Label { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Value { get; init; }
}

/// <summary>
/// Directed edge connecting two <see cref="FlexTelemetryNode"/> vertices in a custom telemetry graph
/// (for example: RowSelection --[batch-edits:15]--> Column:UnitCost, or Column:Vendor --[dropped-into]--> GroupBar).
/// </summary>
public sealed class FlexTelemetryEdge
{
    [JsonPropertyName("from")]
    public string SourceId { get; init; } = string.Empty;

    [JsonPropertyName("to")]
    public string TargetId { get; init; } = string.Empty;

    [JsonPropertyName("rel")]
    public string Relation { get; init; } = "flows-to";

    [JsonPropertyName("weight")]
    public double Weight { get; init; } = 1;
}

/// <summary>
/// Rich custom analytics &amp; directed interaction graph payload attached to a <see cref="FlexControlLogRecord"/>.
/// Captures numeric metrics (e.g. selectedRows, updatedCells, sortLevels, matchCount), dimensional tags,
/// causal operation chaining (<see cref="ParentOperationId"/>), and multi-node / multi-edge graph topology.
/// </summary>
public sealed class FlexTelemetryGraphPayload
{
    [JsonPropertyName("opId")]
    public string OperationId { get; init; } = NewOperationId();

    [JsonPropertyName("parentOpId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentOperationId { get; set; }

    [JsonPropertyName("event")]
    public string EventName { get; set; } = string.Empty;

    [JsonPropertyName("cat")]
    public string Category { get; set; } = "Custom";

    [JsonPropertyName("sub")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SubKind { get; set; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; set; } = "Completed";

    [JsonPropertyName("summary")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; set; }

    [JsonPropertyName("metrics")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, double>? Metrics { get; set; }

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Tags { get; set; }

    [JsonPropertyName("nodes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FlexTelemetryNode>? Nodes { get; set; }

    [JsonPropertyName("edges")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FlexTelemetryEdge>? Edges { get; set; }

    public double GetMetric(string key, double defaultValue = 0) =>
        Metrics is not null && Metrics.TryGetValue(key, out var val) ? val : defaultValue;

    public string? GetTag(string key) =>
        Tags is not null && Tags.TryGetValue(key, out var val) ? val : null;

    public static string NewOperationId() => Guid.NewGuid().ToString("N")[..10];
}

/// <summary>
/// Lossless compact serializer that embeds and extracts <see cref="FlexTelemetryGraphPayload"/> inside
/// flat <c>Detail</c> strings (such as SQLite <c>UsageEvents.Detail</c>) while keeping human-readable prefixes intact.
/// </summary>
public static class FlexCustomAnalyticsCodec
{
    public const string Marker = " @@fxgraph:";

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToJson(FlexTelemetryGraphPayload payload, bool indented = false) =>
        JsonSerializer.Serialize(payload, indented ? IndentedOptions : CompactOptions);

    public static string PackDetail(string? humanDetail, FlexTelemetryGraphPayload payload)
    {
        var prefix = (humanDetail ?? payload.Summary ?? string.Empty).Trim();
        var json = ToJson(payload, indented: false);
        return string.IsNullOrEmpty(prefix) ? $"{Marker.TrimStart()}{json}" : $"{prefix}{Marker}{json}";
    }

    public static bool ContainsGraph(string? detail) =>
        !string.IsNullOrEmpty(detail)
        && (detail.Contains(Marker, StringComparison.Ordinal) || detail.StartsWith("@@fxgraph:", StringComparison.Ordinal));

    public static bool TryUnpack(string? detail, out string humanDetail, out FlexTelemetryGraphPayload? payload)
    {
        humanDetail = detail ?? string.Empty;
        payload = null;
        if (string.IsNullOrWhiteSpace(detail))
            return false;

        int idx = detail.IndexOf(Marker, StringComparison.Ordinal);
        string json;
        if (idx >= 0)
        {
            humanDetail = detail[..idx].Trim();
            json = detail[(idx + Marker.Length)..].Trim();
        }
        else if (detail.StartsWith("@@fxgraph:", StringComparison.Ordinal))
        {
            humanDetail = string.Empty;
            json = detail["@@fxgraph:".Length..].Trim();
        }
        else
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<FlexTelemetryGraphPayload>(json, CompactOptions);
            if (payload is not null && string.IsNullOrWhiteSpace(humanDetail))
                humanDetail = payload.Summary ?? payload.EventName;
            return payload is not null && !string.IsNullOrWhiteSpace(payload.EventName);
        }
        catch
        {
            payload = null;
            return false;
        }
    }
}

/// <summary>
/// Canonical event names and metric keys for built-in FlexKit custom analytics (including Grid Sort, Find, Drag, and Multi-Select Edit).
/// Developers can define arbitrary domain event names (e.g. <c>"Order.BulkApprove"</c>) using the same builder API.
/// </summary>
public static class FlexCustomAnalyticsEvents
{
    public const string GridSort = "Grid.Sort";
    public const string GridFind = "Grid.Find";
    public const string GridDrag = "Grid.Drag";
    public const string GridMultiSelectEdit = "Grid.MultiSelectEdit";

    public static class Metrics
    {
        public const string SelectedRows = "selectedRows";
        public const string SelectedCells = "selectedCells";
        public const string TargetRows = "targetRows";
        public const string UpdatedCells = "updatedCells";
        public const string SortLevels = "sortLevels";
        public const string MatchCount = "matchCount";
        public const string MatchOrdinal = "matchOrdinal";
        public const string DragSpanRows = "dragSpanRows";
        public const string GroupLevels = "groupLevels";
        public const string TotalRows = "totalRows";
    }
}

/// <summary>
/// Fluent builder for constructing rich custom analytics events, numeric metrics, dimensional tags,
/// and directed multi-node interaction graphs without adding client-server round trips.
/// </summary>
public sealed class FlexCustomEventBuilder
{
    private readonly FlexTelemetryGraphPayload _payload;
    private string? _value;
    private string? _detail;
    private long? _durationMs;

    public FlexCustomEventBuilder(string eventName, string category = "Custom", string? operationId = null)
    {
        _payload = new FlexTelemetryGraphPayload
        {
            OperationId = string.IsNullOrWhiteSpace(operationId) ? FlexTelemetryGraphPayload.NewOperationId() : operationId!,
            EventName = eventName.Trim(),
            Category = string.IsNullOrWhiteSpace(category) ? "Custom" : category.Trim()
        };
    }

    public string OperationId => _payload.OperationId;
    internal string? Value => _value;
    internal string? Detail => _detail;
    internal long? DurationMs => _durationMs;

    public FlexCustomEventBuilder WithSubKind(string? subKind)
    {
        if (!string.IsNullOrWhiteSpace(subKind))
            _payload.SubKind = subKind!.Trim();
        return this;
    }

    public FlexCustomEventBuilder WithOutcome(string outcome)
    {
        if (!string.IsNullOrWhiteSpace(outcome))
            _payload.Outcome = outcome.Trim();
        return this;
    }

    public FlexCustomEventBuilder WithSummary(string? summary)
    {
        _payload.Summary = summary?.Trim();
        _detail ??= _payload.Summary;
        return this;
    }

    public FlexCustomEventBuilder WithValue(string? value)
    {
        _value = value;
        return this;
    }

    public FlexCustomEventBuilder WithDetail(string? detail)
    {
        _detail = detail;
        return this;
    }

    public FlexCustomEventBuilder WithDurationMs(long? durationMs)
    {
        _durationMs = durationMs;
        return this;
    }

    /// <summary>Links this custom analytics event causally to an earlier operation span (e.g. Drag Selection -> Multi-Select Batch Edit).</summary>
    public FlexCustomEventBuilder WithParentOperation(string? parentOperationId)
    {
        if (!string.IsNullOrWhiteSpace(parentOperationId))
            _payload.ParentOperationId = parentOperationId!.Trim();
        return this;
    }

    public FlexCustomEventBuilder WithMetric(string name, double value)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            _payload.Metrics ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            _payload.Metrics[name.Trim()] = Math.Round(value, 4);
        }
        return this;
    }

    public FlexCustomEventBuilder WithTag(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
        {
            _payload.Tags ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _payload.Tags[name.Trim()] = value!.Trim();
        }
        return this;
    }

    public FlexCustomEventBuilder AddNode(string id, string kind, string label, double? value = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            return this;

        _payload.Nodes ??= new List<FlexTelemetryNode>(4);
        var cleanId = id.Trim();
        var existingIdx = _payload.Nodes.FindIndex(n => string.Equals(n.Id, cleanId, StringComparison.OrdinalIgnoreCase));
        var node = new FlexTelemetryNode
        {
            Id = cleanId,
            Kind = string.IsNullOrWhiteSpace(kind) ? "Entity" : kind.Trim(),
            Label = string.IsNullOrWhiteSpace(label) ? cleanId : label.Trim(),
            Value = value
        };
        if (existingIdx >= 0)
            _payload.Nodes[existingIdx] = node;
        else
            _payload.Nodes.Add(node);
        return this;
    }

    public FlexCustomEventBuilder AddEdge(string sourceId, string targetId, string relation = "flows-to", double weight = 1)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId))
            return this;

        _payload.Edges ??= new List<FlexTelemetryEdge>(4);
        _payload.Edges.Add(new FlexTelemetryEdge
        {
            SourceId = sourceId.Trim(),
            TargetId = targetId.Trim(),
            Relation = string.IsNullOrWhiteSpace(relation) ? "flows-to" : relation.Trim(),
            Weight = weight <= 0 ? 1 : Math.Round(weight, 2)
        });
        return this;
    }

    public FlexTelemetryGraphPayload Build() => _payload;
}

/// <summary>
/// Timed, disposable interaction scope that captures multi-step custom telemetry graphs across method calls.
/// </summary>
public sealed class FlexInteractionScope : IDisposable
{
    private readonly Action<FlexCustomEventBuilder> _commitCallback;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private bool _completed;

    public FlexCustomEventBuilder Builder { get; }
    public string OperationId => Builder.OperationId;

    internal FlexInteractionScope(
        string eventName,
        string category,
        string? parentOperationId,
        Action<FlexCustomEventBuilder> commitCallback)
    {
        _commitCallback = commitCallback;
        Builder = new FlexCustomEventBuilder(eventName, category).WithParentOperation(parentOperationId);
    }

    public string Complete(string outcome = "Completed", string? summary = null)
    {
        if (_completed)
            return OperationId;
        _completed = true;
        var elapsedMs = (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
        Builder.WithOutcome(outcome).WithDurationMs(elapsedMs);
        if (!string.IsNullOrWhiteSpace(summary))
            Builder.WithSummary(summary);
        _commitCallback(Builder);
        return OperationId;
    }

    public void Dispose()
    {
        if (!_completed)
            Complete("Completed");
    }
}

/// <summary>
/// Public service contract allowing any page, wizard, host service, or custom component to emit
/// structured custom analytics events and multi-node interaction graphs into FlexKit's unified telemetry pipeline.
/// </summary>
public interface IFlexCustomAnalytics
{
    /// <summary>
    /// Records a custom analytics / graph event immediately in memory on the server (zero round trips)
    /// and returns its <c>OperationId</c> so subsequent operations can link via <c>WithParentOperation</c>.
    /// </summary>
    string Track(
        string eventName,
        string category,
        Action<FlexCustomEventBuilder> configure,
        string? screen = null,
        string? route = null,
        string? location = null,
        string? controlType = null,
        string? controlId = null,
        string? tag = null);

    /// <summary>Begins a timed custom analytics graph scope that records automatically on <see cref="FlexInteractionScope.Complete"/> or disposal.</summary>
    FlexInteractionScope BeginScope(
        string eventName,
        string category = "Custom",
        string? parentOperationId = null,
        string? screen = null,
        string? controlType = null,
        string? controlId = null,
        string? tag = null);
}

public sealed class FlexCustomAnalyticsService : IFlexCustomAnalytics
{
    private readonly IFlexControlLogSink? _sink;
    private readonly IFlexControlContextProvider? _contextProvider;

    public FlexCustomAnalyticsService(
        IFlexControlLogSink? sink = null,
        IFlexControlContextProvider? contextProvider = null)
    {
        _sink = sink;
        _contextProvider = contextProvider;
    }

    public string Track(
        string eventName,
        string category,
        Action<FlexCustomEventBuilder> configure,
        string? screen = null,
        string? route = null,
        string? location = null,
        string? controlType = null,
        string? controlId = null,
        string? tag = null)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            return string.Empty;

        var builder = new FlexCustomEventBuilder(eventName, category);
        configure?.Invoke(builder);
        var graph = builder.Build();

        if (_sink is null || !_sink.IsEnabled)
            return graph.OperationId;

        var hostCtx = _contextProvider?.GetCurrentContext() ?? default;
        var resolvedScreen = !string.IsNullOrWhiteSpace(screen)
            ? screen!.Trim()
            : (!string.IsNullOrWhiteSpace(hostCtx.FallbackScreen) ? hostCtx.FallbackScreen : "Main");
        var resolvedRoute = !string.IsNullOrWhiteSpace(route)
            ? route!.Trim()
            : (!string.IsNullOrWhiteSpace(hostCtx.FallbackRoute) ? hostCtx.FallbackRoute : "/");
        var resolvedControlType = !string.IsNullOrWhiteSpace(controlType) ? controlType!.Trim() : "CustomAnalytics";
        var resolvedControlId = !string.IsNullOrWhiteSpace(controlId) ? controlId!.Trim() : graph.EventName;
        var resolvedLocation = !string.IsNullOrWhiteSpace(location)
            ? location!.Trim()
            : $"{resolvedScreen} > {resolvedControlType}:{resolvedControlId}";

        var record = new FlexControlLogRecord
        {
            TimestampUtc = DateTime.UtcNow,
            EventType = "FlexCustomAnalytics",
            Application = !string.IsNullOrWhiteSpace(hostCtx.Application) ? hostCtx.Application : "FlexKitApp",
            Tenant = hostCtx.Tenant ?? string.Empty,
            DivisionId = hostCtx.DivisionId,
            UserId = !string.IsNullOrWhiteSpace(hostCtx.UserId) ? hostCtx.UserId : "anonymous",
            SessionId = hostCtx.SessionId ?? string.Empty,
            ClientIp = hostCtx.ClientIp,
            Screen = resolvedScreen,
            Route = resolvedRoute,
            Location = resolvedLocation,
            ControlType = resolvedControlType,
            ControlId = resolvedControlId,
            Tag = string.IsNullOrWhiteSpace(tag) ? null : tag!.Trim(),
            Action = graph.EventName,
            Value = builder.Value ?? graph.SubKind,
            Detail = FlexCustomAnalyticsCodec.PackDetail(builder.Detail ?? graph.Summary, graph),
            DurationMs = builder.DurationMs,
            CustomAnalytics = graph
        };

        _sink.ReceiveBatch(new[] { record });
        return graph.OperationId;
    }

    public FlexInteractionScope BeginScope(
        string eventName,
        string category = "Custom",
        string? parentOperationId = null,
        string? screen = null,
        string? controlType = null,
        string? controlId = null,
        string? tag = null)
    {
        return new FlexInteractionScope(
            eventName,
            category,
            parentOperationId,
            b => Track(
                eventName,
                category,
                inner =>
                {
                    var built = b.Build();
                    inner.WithSubKind(built.SubKind)
                         .WithOutcome(built.Outcome)
                         .WithSummary(built.Summary)
                         .WithParentOperation(built.ParentOperationId)
                         .WithValue(b.Value)
                         .WithDetail(b.Detail)
                         .WithDurationMs(b.DurationMs);
                    if (built.Metrics is not null)
                        foreach (var kv in built.Metrics) inner.WithMetric(kv.Key, kv.Value);
                    if (built.Tags is not null)
                        foreach (var kv in built.Tags) inner.WithTag(kv.Key, kv.Value);
                    if (built.Nodes is not null)
                        foreach (var n in built.Nodes) inner.AddNode(n.Id, n.Kind, n.Label, n.Value);
                    if (built.Edges is not null)
                        foreach (var e in built.Edges) inner.AddEdge(e.SourceId, e.TargetId, e.Relation, e.Weight);
                },
                screen: screen,
                controlType: controlType,
                controlId: controlId,
                tag: tag));
    }
}
