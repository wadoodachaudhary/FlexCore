using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;

namespace Fx.ControlKit.Telemetry;

/// <summary>
/// Visual theme for <see cref="TelemetryControl"/>, <see cref="LogControl"/>, and sub-controls.
/// Defaults to <see cref="VBClassic"/> (Windows / VB6 desktop look and feel), with
/// <see cref="GrafanaDark"/> and <see cref="ModernLight"/> selectable as options.
/// </summary>
public enum TelemetryTheme
{
    VBClassic = 0,
    GrafanaDark = 1,
    ModernLight = 2
}

/// <summary>
/// Semantic category of an application or control telemetry event.
/// </summary>
public enum TelemetryEventKind
{
    Session = 0,
    ScreenOpen = 1,
    ScreenFocus = 2,
    ScreenClose = 3,
    TaskLaunch = 4,
    ReportRun = 5,
    InquiryRun = 6,
    MenuAction = 7,
    ButtonClick = 8,
    ControlChange = 9,
    GridOperation = 10,
    ShortcutKey = 11,
    SqlQuery = 12,
    UnhandledError = 13
}

/// <summary>
/// Generalized telemetry event record normalized across FlexKit control logs and optional host semantic events.
/// </summary>
public sealed class FlexTelemetryEvent
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string Level { get; init; } = "INFO";
    public string Application { get; init; } = "FlexKitApp";
    public string Tenant { get; init; } = string.Empty;
    public int DivisionId { get; init; }
    public string UserId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string ClientIp { get; init; } = string.Empty;
    public TelemetryEventKind Kind { get; init; } = TelemetryEventKind.ControlChange;
    public string Category { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string Screen { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string Control { get; init; } = string.Empty;
    public string? Tag { get; init; }
    public string? Value { get; init; }
    public string Detail { get; init; } = string.Empty;
    public long? DurationMs { get; init; }

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string ToCloudWatchJson(bool indented = false)
    {
        var payload = new Dictionary<string, object?>(20, StringComparer.Ordinal)
        {
            ["@timestamp"] = TimestampUtc.ToString("O"),
            ["level"] = string.IsNullOrWhiteSpace(Level) ? "INFO" : Level,
            ["eventType"] = Kind.ToString(),
            ["app"] = string.IsNullOrWhiteSpace(Application) ? "FlexKitApp" : Application,
            ["tenant"] = Tenant,
            ["divisionId"] = DivisionId,
            ["userId"] = string.IsNullOrWhiteSpace(UserId) ? "anonymous" : UserId,
            ["sessionId"] = SessionId,
            ["screen"] = Screen,
            ["route"] = Route,
            ["location"] = !string.IsNullOrWhiteSpace(Location)
                ? Location
                : (!string.IsNullOrWhiteSpace(Screen) && !string.IsNullOrWhiteSpace(Control)
                    ? $"{Screen} > {Category}:{Control}"
                    : Screen),
            ["controlType"] = Category,
            ["controlId"] = Control,
            ["action"] = Action
        };
        if (!string.IsNullOrWhiteSpace(Tag)) payload["tag"] = Tag;
        if (Value is not null) payload["value"] = Value;
        if (!string.IsNullOrWhiteSpace(Detail)) payload["detail"] = Detail;
        if (!string.IsNullOrWhiteSpace(ClientIp)) payload["clientIp"] = ClientIp;
        if (DurationMs.HasValue) payload["durationMs"] = DurationMs.Value;

        return JsonSerializer.Serialize(payload, indented ? IndentedJsonOptions : CompactJsonOptions);
    }

    public string ToSerilogClefJson(bool indented = false)
    {
        var payload = new Dictionary<string, object?>(20, StringComparer.Ordinal)
        {
            ["@t"] = TimestampUtc.ToString("O"),
            ["@l"] = string.Equals(Level, "ERROR", StringComparison.OrdinalIgnoreCase) ? "Error" : "Information",
            ["@mt"] = "Telemetry {Kind} {Category}:{Control} {Action} on {Screen}",
            ["Kind"] = Kind.ToString(),
            ["App"] = string.IsNullOrWhiteSpace(Application) ? "FlexKitApp" : Application,
            ["Tenant"] = Tenant,
            ["DivisionId"] = DivisionId,
            ["UserId"] = string.IsNullOrWhiteSpace(UserId) ? "anonymous" : UserId,
            ["SessionId"] = SessionId,
            ["Category"] = Category,
            ["Control"] = Control,
            ["Action"] = Action,
            ["Screen"] = Screen,
            ["Route"] = Route,
            ["Location"] = !string.IsNullOrWhiteSpace(Location)
                ? Location
                : (!string.IsNullOrWhiteSpace(Screen) && !string.IsNullOrWhiteSpace(Control)
                    ? $"{Screen} > {Category}:{Control}"
                    : Screen)
        };
        if (!string.IsNullOrWhiteSpace(Tag)) payload["Tag"] = Tag;
        if (Value is not null) payload["Value"] = Value;
        if (!string.IsNullOrWhiteSpace(Detail)) payload["Detail"] = Detail;
        if (!string.IsNullOrWhiteSpace(ClientIp)) payload["ClientIp"] = ClientIp;
        if (DurationMs.HasValue) payload["DurationMs"] = DurationMs.Value;

        return JsonSerializer.Serialize(payload, indented ? IndentedJsonOptions : CompactJsonOptions);
    }
}

/// <summary>
/// Query filter applied when building a <see cref="FlexTelemetrySnapshot"/>.
/// </summary>
public sealed class FlexTelemetryQueryFilter
{
    public string? Tenant { get; set; }
    public int DaysBack { get; set; } = 30;
    public int? MinutesBack { get; set; }
    public string? UserId { get; set; }
    public string? ModuleFilter { get; set; }
    public string? SearchText { get; set; }
    public string? SessionId { get; set; }
}

/// <summary>
/// Optional catalog screen metadata supplied by <see cref="ITelemetryCatalogProvider"/> so zero-open screens surface automatically.
/// </summary>
public sealed record TelemetryCatalogScreen(string RouteKey, string Title, string Module);

/// <summary>
/// Optional catalog task/report metadata supplied by <see cref="ITelemetryCatalogProvider"/> so zero-run tasks/reports surface automatically.
/// </summary>
public sealed record TelemetryCatalogTask(string Key, string KindLabel, string Title, string Module);

public sealed class FlexTelemetryScreenRow
{
    public string Route { get; set; } = string.Empty;
    public string ScreenTitle { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public int OpenCount { get; set; }
    public int FocusCount { get; set; }
    public int ControlActionsOnScreen { get; set; }
    public int GridOpsOnScreen { get; set; }
    public int UniqueUsers { get; set; }
    public double TotalDwellMinutes { get; set; }
    public double AvgDwellSeconds { get; set; }
    public DateTime? LastUsedLocal { get; set; }
    public string UsageStatus { get; set; } = "Active";
}

public sealed class FlexTelemetryTaskRow
{
    public string KindLabel { get; set; } = "Task";
    public string Name { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;
    public int RunCount { get; set; }
    public int UniqueUsers { get; set; }
    public DateTime? LastRunLocal { get; set; }
    public string UsageStatus { get; set; } = "Active";
}

public sealed class FlexTelemetryControlRow
{
    public string Screen { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty;
    public string Control { get; set; } = string.Empty;
    public string? Tag { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? LastValue { get; set; }
    public int ActionCount { get; set; }
    public int UniqueUsers { get; set; }
    public DateTime LastActionLocal { get; set; }
}

public sealed class FlexTelemetryControlTypeRow
{
    public string ControlType { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public int UniqueControls { get; set; }
    public int UniqueUsers { get; set; }
}

public sealed class FlexTelemetryGridActionSummaryRow
{
    public string Action { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public int UniqueGrids { get; set; }
    public int UniqueUsers { get; set; }
}

public sealed class FlexTelemetryGridOpRow
{
    public DateTime TimestampLocal { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Screen { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string ControlType { get; set; } = "GridControl";
    public string ControlId { get; set; } = string.Empty;
    public string? Tag { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string Detail { get; set; } = string.Empty;
}

public sealed class FlexTelemetryRegisteredControlRow
{
    public string Screen { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty;
    public string ControlId { get; set; } = string.Empty;
    public string? Tag { get; set; }
    public long ActionCount { get; set; }
    public string LastAction { get; set; } = string.Empty;
    public string? LastValue { get; set; }
    public DateTime LastSeenLocal { get; set; }
}

public sealed class FlexTelemetryUserRow
{
    public string UserId { get; set; } = string.Empty;
    public int LoginCount { get; set; }
    public int ScreenOpens { get; set; }
    public int TaskAndReportRuns { get; set; }
    public int ButtonClicks { get; set; }
    public int GridOperations { get; set; }
    public int ControlChanges { get; set; }
    public int TotalActions { get; set; }
    public double ActiveMinutes { get; set; }
    public string TopScreen { get; set; } = string.Empty;
    public DateTime LastSeenLocal { get; set; }
}

public sealed class FlexTelemetryHeatmapRow
{
    public string Screen { get; set; } = string.Empty;
    public int[] BlockCounts { get; set; } = new int[8];
}

public sealed class FlexTelemetryTimeBucketRow
{
    public string BucketLabel { get; set; } = string.Empty;
    public int ScreenOpens { get; set; }
    public int ButtonClicks { get; set; }
    public int GridOperations { get; set; }
    public int ControlChanges { get; set; }
    public int TasksAndReports { get; set; }
}

public sealed class FlexTelemetryEventRow
{
    public DateTime TimestampUtc { get; set; }
    public DateTime TimestampLocal { get; set; }
    public string Level { get; set; } = "INFO";
    public string UserId { get; set; } = string.Empty;
    public string Tenant { get; set; } = string.Empty;
    public int DivisionId { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Screen { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string Control { get; set; } = string.Empty;
    public string? Tag { get; set; }
    public string? Value { get; set; }
    public string Detail { get; set; } = string.Empty;
    public double? DurationSeconds { get; set; }
    public string CloudWatchJson { get; set; } = string.Empty;
    public string SerilogClefJson { get; set; } = string.Empty;
    public string CloudWatchJsonIndented { get; set; } = string.Empty;
    public string SerilogClefJsonIndented { get; set; } = string.Empty;
}

/// <summary>
/// Generalized telemetry dashboard snapshot consumed by <see cref="TelemetryControl"/>,
/// <see cref="LogControl"/>, and <see cref="ControlActivityControl"/>.
/// </summary>
public sealed class FlexTelemetrySnapshot
{
    public DateTime GeneratedAtLocal { get; set; } = DateTime.Now;
    public string TenantScope { get; set; } = "Default";
    public string StorageHint { get; set; } = string.Empty;
    public int DaysBack { get; set; } = 30;
    public int? MinutesBack { get; set; }
    public string TimeWindowLabel { get; set; } = "Last 30d";
    public int TotalEvents { get; set; }
    public int TotalScreenOpens { get; set; }
    public int TotalTaskAndReportRuns { get; set; }
    public int TotalButtonClicks { get; set; }
    public int TotalGridOperations { get; set; }
    public int TotalControlChanges { get; set; }
    public int RegisteredControlCount { get; set; }
    public int UniqueUsers { get; set; }
    public int UniqueScreensVisited { get; set; }
    public int CatalogScreenCount { get; set; }
    public int UnusedCatalogScreenCount { get; set; }
    public IReadOnlyList<string> AvailableUsers { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> AvailableModules { get; set; } = Array.Empty<string>();
    public IReadOnlyList<FlexTelemetryScreenRow> ScreenRows { get; set; } = Array.Empty<FlexTelemetryScreenRow>();
    public IReadOnlyList<FlexTelemetryTaskRow> TaskAndReportRows { get; set; } = Array.Empty<FlexTelemetryTaskRow>();
    public IReadOnlyList<FlexTelemetryControlRow> ControlRows { get; set; } = Array.Empty<FlexTelemetryControlRow>();
    public IReadOnlyList<FlexTelemetryUserRow> UserRows { get; set; } = Array.Empty<FlexTelemetryUserRow>();
    public IReadOnlyList<FlexTelemetryControlTypeRow> ControlTypeRows { get; set; } = Array.Empty<FlexTelemetryControlTypeRow>();
    public IReadOnlyList<FlexTelemetryGridOpRow> GridOperationRows { get; set; } = Array.Empty<FlexTelemetryGridOpRow>();
    public IReadOnlyList<FlexTelemetryGridActionSummaryRow> GridActionSummaryRows { get; set; } = Array.Empty<FlexTelemetryGridActionSummaryRow>();
    public IReadOnlyList<FlexTelemetryRegisteredControlRow> RegisteredControlRows { get; set; } = Array.Empty<FlexTelemetryRegisteredControlRow>();
    public IReadOnlyList<FlexTelemetryHeatmapRow> ScreenHourHeatmap { get; set; } = Array.Empty<FlexTelemetryHeatmapRow>();
    public IReadOnlyList<FlexTelemetryTimeBucketRow> TimeSeriesBuckets { get; set; } = Array.Empty<FlexTelemetryTimeBucketRow>();
    public IReadOnlyList<FlexTelemetryEventRow> RecentEvents { get; set; } = Array.Empty<FlexTelemetryEventRow>();
}

/// <summary>
/// Generalized SQL query execution telemetry entry captured via <see cref="IFlexSqlTelemetryProvider"/>.
/// Used both in <see cref="SqlTelemetryControl"/> and in ticket diagnostic snapshots.
/// </summary>
public sealed class FlexSqlTelemetryEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string Sql { get; init; } = string.Empty;
    public string Parameters { get; init; } = string.Empty;
    public long ElapsedMs { get; init; }
    public int? RowCount { get; init; }
    public string? ErrorMessage { get; init; }
    public string Tenant { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Screen { get; init; } = string.Empty;
    public bool IsBackgroundPoll { get; init; }

    /// <summary>A statement, a connection open or failure, or a transaction end (<see cref="FlexSqlEventKinds"/>).</summary>
    public string EventKind { get; init; } = FlexSqlEventKinds.Statement;
    /// <summary>The data layer that made the call (for example "DbControl" or a host service's name).</summary>
    public string Source { get; init; } = string.Empty;
    /// <summary>The provider's error number when the call failed.</summary>
    public int? ErrorNumber { get; init; }

    private string? _errorClass;
    /// <summary>Deadlock, LockTimeout, CommandTimeout, ConnectionFailure, Cancelled or Other; empty when the call succeeded.</summary>
    public string ErrorClass
    {
        get => _errorClass ??= !HasError ? FlexSqlErrorClasses.None : ErrorNumber switch
        {
            1205 => FlexSqlErrorClasses.Deadlock,
            1222 => FlexSqlErrorClasses.LockTimeout,
            -2 => FlexSqlErrorClasses.CommandTimeout,
            _ => FlexSqlErrorClasses.Other
        };
        init => _errorClass = value;
    }

    private FlexSqlShape? _shape;
    private FlexSqlShape Shape => _shape ??= EventKind == FlexSqlEventKinds.Statement
        ? FlexSqlAnalyzer.Analyze(Sql)
        : new FlexSqlShape(EventKind, false, Array.Empty<string>());

    /// <summary>Select, Insert, Update, Delete, Merge, Truncate, Exec, Schema or Other, read from the statement's text.</summary>
    [JsonIgnore]
    public string StatementKind => Shape.StatementKind;
    [JsonIgnore]
    public bool IsWrite => Shape.IsWrite;
    /// <summary>The tables the statement names; a stored procedure shows as "EXEC name".</summary>
    [JsonIgnore]
    public IReadOnlyList<string> TableList => Shape.Tables;
    [JsonIgnore]
    public string Tables => string.Join(", ", Shape.Tables);

    [JsonIgnore]
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    [JsonIgnore]
    public string StatusLabel => HasError ? "Error" : (ElapsedMs >= 500 ? "Slow" : "OK");

    [JsonIgnore]
    public string SqlPreview
    {
        get
        {
            var single = (Sql ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return single.Length > 180 ? single[..177] + "..." : single;
        }
    }

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string ToCloudWatchJson(bool indented = false)
    {
        var payload = new Dictionary<string, object?>(20, StringComparer.Ordinal)
        {
            ["@timestamp"] = TimestampUtc.ToString("O"),
            ["level"] = HasError ? "ERROR" : (ElapsedMs >= 500 ? "WARN" : "INFO"),
            ["eventType"] = "SqlQuery",
            ["tenant"] = Tenant,
            ["userId"] = string.IsNullOrWhiteSpace(UserId) ? "anonymous" : UserId,
            ["sessionId"] = SessionId,
            ["screen"] = Screen,
            ["elapsedMs"] = ElapsedMs,
            ["rowCount"] = RowCount,
            ["isBackgroundPoll"] = IsBackgroundPoll,
            ["eventKind"] = EventKind,
            ["source"] = Source,
            ["statementKind"] = StatementKind,
            ["tables"] = Tables,
            ["errorClass"] = ErrorClass,
            ["errorNumber"] = ErrorNumber,
            ["sql"] = Sql,
            ["parameters"] = Parameters
        };
        if (!string.IsNullOrWhiteSpace(ErrorMessage))
            payload["error"] = ErrorMessage;

        return JsonSerializer.Serialize(payload, indented ? IndentedJson : CompactJson);
    }
}

/// <summary>
/// Optional secondary gauge descriptor rendered alongside UI Screen Coverage on the Overview tab.
/// </summary>
public sealed record TelemetrySecondaryGauge(
    string Title,
    double? ValuePercent,
    string CaptionText,
    string EmptyMessage);

/// <summary>
/// Context passed into host-supplied <see cref="TelemetryCustomTab"/> render fragments so host tabs
/// seamlessly react to the active <see cref="TelemetryTheme"/>, tenant, time range, and search query.
/// </summary>
public sealed record TelemetryCustomTabContext(
    TelemetryTheme Theme,
    string SelectedTenant,
    string SearchText,
    int DaysBack,
    int? MinutesBack,
    FlexTelemetrySnapshot? Snapshot);

/// <summary>
/// Host-supplied extension tab plugged into <see cref="TelemetryControl.AdditionalTabs"/>.
/// </summary>
public sealed class TelemetryCustomTab
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required RenderFragment<TelemetryCustomTabContext> Content { get; init; }
}

/// <summary>
/// Optional KPI card summary injected into the Overview strip by a host extension.
/// </summary>
public sealed record TelemetryCustomKpiCard(
    string Label,
    string ValueText,
    string SubText,
    string AccentKind = "rose",
    bool IsWarning = false,
    RenderFragment? ActionContent = null);
