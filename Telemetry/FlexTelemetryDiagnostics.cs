using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fx.ControlKit.Logging;

namespace Fx.ControlKit.Telemetry;

/// <summary>
/// Input parameters for capturing a live diagnostic telemetry snapshot when a support ticket,
/// screen capture, or error report is created.
/// </summary>
public sealed class FlexDiagnosticCaptureRequest
{
    public string? ActivePageType { get; init; }
    public string? ActivePageTitle { get; init; }
    public string? ActivePageRoute { get; init; }
    public string? DomFocusedControl { get; init; }
    public string? NavigationPath { get; init; }
    public string? SessionId { get; init; }
    public string? Tenant { get; init; }
    public object? SessionObject { get; init; }
    public IDictionary<string, object?>? SessionStateOverride { get; init; }
    public object? ActiveComponentInstance { get; init; }
    public IDictionary<string, object?>? FormStateOverride { get; init; }
    public IEnumerable<FlexSqlTelemetryEntry>? FallbackSqlHistory { get; init; }
    public IEnumerable<string>? AdditionalEventLines { get; init; }
    public int MaxControlEvents { get; init; } = 60;
    public int MaxSqlEntries { get; init; } = 60;
}

/// <summary>
/// Structured FlexKit control action step embedded inside diagnostic snapshots and recording trails.
/// </summary>
public sealed class FlexDiagnosticControlStep
{
    public string Timestamp { get; init; } = string.Empty;
    public long EpochMs { get; init; }
    public string Kind { get; init; } = string.Empty;
    public string Screen { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string ControlType { get; init; } = string.Empty;
    public string ControlId { get; init; } = string.Empty;
    public string? Tag { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? Value { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// Metadata header for rendering a diagnostic ticket Markdown attachment via <see cref="FlexDiagnosticMarkdownFormatter"/>.
/// </summary>
public sealed class FlexDiagnosticTicketHeader
{
    public string DocumentTitlePrefix { get; init; } = "Diagnostic Telemetry Report";
    public int TicketNumber { get; init; }
    public string Topic { get; init; } = string.Empty;
    public string Reporter { get; init; } = string.Empty;
    public DateTime CreatedDate { get; init; } = DateTime.Now;
    public string Url { get; init; } = string.Empty;
    public string ScreenName { get; init; } = string.Empty;
    public string FocusedControl { get; init; } = string.Empty;
    public string? EnvironmentInfo { get; init; }
    public string IssueType { get; init; } = string.Empty;
    public string Priority { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string? Assignee { get; init; }
    public string? Sprint { get; init; }
    public string? Labels { get; init; }
    public string? LegacyFormContext { get; init; }
    public string Description { get; init; } = string.Empty;
    public string? Prerequisites { get; init; }
    public string? StepsToReproduce { get; init; }
    public string? ExpectedResult { get; init; }
    public string? ActualResult { get; init; }
    public string? ScreenshotPath { get; init; }
}

/// <summary>
/// Service that captures unified server-side FlexKit control telemetry, clean SQL query history,
/// and secret-redacted component/session state into a standard JSON snapshot and Markdown report.
/// </summary>
public interface IFlexDiagnosticSnapshotService
{
    (string DiagnosticsJson, string ResolvedFocusedControl) CaptureDiagnostics(FlexDiagnosticCaptureRequest request);
    string CaptureDiagnosticsJson(FlexDiagnosticCaptureRequest request);
    string BuildActionTrailJson(string? sessionId, DateTime startedAtUtc, DateTime? endedAtUtc = null, int maxSteps = 300);
}

public sealed class FlexDiagnosticSnapshotService : IFlexDiagnosticSnapshotService
{
    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Parameter names that carry a secret: password and its short forms (Passwd, Pswd, Pwd: NewPswd,
    // WebPWD, OldPswd), secrets, tokens, API keys, connection strings, credentials, security codes.
    private static readonly Regex SecretParamPattern = new(
        @"(?i)(@?[\w]*(?:password|passwd|pswd|pwd|passcode|securitycode|secret|token|apikey|api_key|connectionstring|credential)[\w]*)\s*=\s*([^,;]+)",
        RegexOptions.Compiled);

    private readonly IFlexControlLogSink? _controlSink;
    private readonly ITelemetryDataSource? _telemetryDataSource;
    private readonly IFlexSqlTelemetryProvider? _sqlProvider;

    public FlexDiagnosticSnapshotService(
        IFlexControlLogSink? controlSink = null,
        ITelemetryDataSource? telemetryDataSource = null,
        IFlexSqlTelemetryProvider? sqlProvider = null)
    {
        _controlSink = controlSink;
        _telemetryDataSource = telemetryDataSource;
        _sqlProvider = sqlProvider;
    }

    public string CaptureDiagnosticsJson(FlexDiagnosticCaptureRequest request) =>
        CaptureDiagnostics(request).DiagnosticsJson;

    public (string DiagnosticsJson, string ResolvedFocusedControl) CaptureDiagnostics(FlexDiagnosticCaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var controlSteps = GatherUnifiedControlSteps(request.SessionId, request.MaxControlEvents);
            var resolvedFocus = ResolveFocusedControl(request.DomFocusedControl, controlSteps);

            var eventsSequence = new List<string>(controlSteps.Count + 8);
            if (request.AdditionalEventLines is not null)
            {
                foreach (var line in request.AdditionalEventLines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        eventsSequence.Add(line.Trim());
                }
            }

            foreach (var step in controlSteps)
            {
                var tagPart = !string.IsNullOrWhiteSpace(step.Tag) ? $" [tag:{step.Tag}]" : string.Empty;
                var valPart = !string.IsNullOrWhiteSpace(step.Value) ? $" = {step.Value}" : string.Empty;
                var detPart = !string.IsNullOrWhiteSpace(step.Detail) ? $" ({step.Detail})" : string.Empty;
                var locPart = !string.IsNullOrWhiteSpace(step.Location)
                    ? step.Location
                    : $"{step.Screen} > {step.ControlType}:{step.ControlId}";
                var formatted = $"{step.Timestamp} - [{step.Kind}] {locPart}{tagPart} -> {step.Action}{valPart}{detPart}";
                if (!eventsSequence.Contains(formatted, StringComparer.Ordinal))
                    eventsSequence.Add(formatted);
            }

            var sqlEntries = GatherCleanSqlHistory(request);
            var sessionState = request.SessionStateOverride is not null
                ? RedactDictionary(request.SessionStateOverride)
                : ReflectObjectState(request.SessionObject, includeNonPublicFields: false);
            var formState = request.FormStateOverride is not null
                ? RedactDictionary(request.FormStateOverride)
                : ReflectObjectState(request.ActiveComponentInstance, includeNonPublicFields: true);

            var lastFlexLoc = controlSteps.LastOrDefault()?.Location ?? string.Empty;

            var payload = new Dictionary<string, object?>(14, StringComparer.Ordinal)
            {
                ["Timestamp"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                ["ActivePageType"] = string.IsNullOrWhiteSpace(request.ActivePageType) ? "Unknown" : request.ActivePageType,
                ["ActivePageTitle"] = string.IsNullOrWhiteSpace(request.ActivePageTitle) ? "Unknown" : request.ActivePageTitle,
                ["ActivePageRoute"] = string.IsNullOrWhiteSpace(request.ActivePageRoute) ? "Unknown" : request.ActivePageRoute,
                ["FocusedControl"] = resolvedFocus,
                ["FocusedFlexControlLocation"] = lastFlexLoc,
                ["NavigationPath"] = request.NavigationPath ?? string.Empty,
                ["SessionState"] = sessionState,
                ["FormState"] = formState,
                ["SqlHistory"] = sqlEntries,
                ["ControlEvents"] = controlSteps,
                ["EventsSequence"] = eventsSequence
            };

            return (JsonSerializer.Serialize(payload, IndentedOptions), resolvedFocus);
        }
        catch (Exception ex)
        {
            var fallback = JsonSerializer.Serialize(
                new { Error = $"Failed to compile diagnostics: {ex.Message}" },
                IndentedOptions);
            return (fallback, request.DomFocusedControl ?? "none");
        }
    }

    public string BuildActionTrailJson(
        string? sessionId,
        DateTime startedAtUtc,
        DateTime? endedAtUtc = null,
        int maxSteps = 300)
    {
        var endUtc = endedAtUtc ?? DateTime.UtcNow;
        var steps = GatherUnifiedControlSteps(sessionId, Math.Clamp(maxSteps, 10, 1_000))
            .Where(s =>
            {
                var minEpoch = new DateTimeOffset(startedAtUtc.Kind == DateTimeKind.Utc ? startedAtUtc : startedAtUtc.ToUniversalTime()).ToUnixTimeMilliseconds() - 1_000;
                var maxEpoch = new DateTimeOffset(endUtc.Kind == DateTimeKind.Utc ? endUtc : endUtc.ToUniversalTime()).ToUnixTimeMilliseconds() + 1_000;
                return s.EpochMs >= minEpoch && s.EpochMs <= maxEpoch;
            })
            .Select(s => new
            {
                at = s.EpochMs,
                action = s.Action,
                control = !string.IsNullOrWhiteSpace(s.Location) ? s.Location : $"{s.ControlType} {s.ControlId}".Trim(),
                tag = s.Tag,
                value = s.Value,
                page = s.Screen,
                url = s.Route
            })
            .ToList();

        return steps.Count == 0 ? string.Empty : JsonSerializer.Serialize(steps);
    }

    private List<FlexDiagnosticControlStep> GatherUnifiedControlSteps(string? sessionId, int maxCount)
    {
        // Only the given session's own records: another session's actions and values never go into this
        // session's snapshot, even when it has none of its own to show, and without a session there is none.
        if (string.IsNullOrWhiteSpace(sessionId))
            return new List<FlexDiagnosticControlStep>();

        int cap = Math.Clamp(maxCount, 5, 500);
        var combined = new List<(DateTime Utc, FlexDiagnosticControlStep Step)>(cap * 2);
        var dedupe = new HashSet<string>(StringComparer.Ordinal);

        if (_telemetryDataSource is not null)
        {
            foreach (var e in _telemetryDataSource.GetRecentSessionEvents(sessionId, cap))
            {
                var local = e.TimestampUtc.ToLocalTime();
                var epoch = new DateTimeOffset(DateTime.SpecifyKind(e.TimestampUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                var safeValue = IsSensitiveMemberName(e.Control) || IsSensitiveMemberName(e.Tag)
                    ? "[REDACTED]"
                    : e.Value;
                var key = $"{epoch}|{e.Category}|{e.Control}|{e.Action}|{safeValue}";
                if (!dedupe.Add(key))
                    continue;

                combined.Add((e.TimestampUtc, new FlexDiagnosticControlStep
                {
                    Timestamp = local.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    EpochMs = epoch,
                    Kind = e.Kind.ToString(),
                    Screen = e.Screen,
                    Route = e.Route,
                    Location = !string.IsNullOrWhiteSpace(e.Location)
                        ? e.Location
                        : (!string.IsNullOrWhiteSpace(e.Screen) && !string.IsNullOrWhiteSpace(e.Control)
                            ? $"{e.Screen} > {e.Category}:{e.Control}"
                            : e.Screen),
                    ControlType = e.Category,
                    ControlId = e.Control,
                    Tag = e.Tag,
                    Action = e.Action,
                    Value = safeValue,
                    Detail = string.IsNullOrWhiteSpace(e.Detail) ? null : e.Detail
                }));
            }
        }

        if (_controlSink is not null)
        {
            // The session is picked out of the whole buffer before the tail is taken, so other sessions'
            // newer traffic does not push this session's own records out.
            var relevant = _controlSink.GetRecentRecords()
                .Where(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
                .TakeLast(cap)
                .ToList();

            foreach (var r in relevant)
            {
                var local = r.TimestampUtc.ToLocalTime();
                var epoch = new DateTimeOffset(DateTime.SpecifyKind(r.TimestampUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                var safeValue = IsSensitiveMemberName(r.ControlId) || IsSensitiveMemberName(r.Tag)
                    ? "[REDACTED]"
                    : r.Value;
                var key = $"{epoch}|{r.ControlType}|{r.ControlId}|{r.Action}|{safeValue}";
                if (!dedupe.Add(key))
                    continue;

                combined.Add((r.TimestampUtc, new FlexDiagnosticControlStep
                {
                    Timestamp = local.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    EpochMs = epoch,
                    Kind = DefaultFlexTelemetryDataSource.ClassifyControlRecord(r.ControlType, r.Action).ToString(),
                    Screen = r.Screen,
                    Route = r.Route,
                    Location = r.Location,
                    ControlType = r.ControlType,
                    ControlId = r.ControlId,
                    Tag = r.Tag,
                    Action = r.Action,
                    Value = safeValue,
                    Detail = r.Detail
                }));
            }
        }

        return combined
            .OrderBy(x => x.Utc)
            .TakeLast(cap)
            .Select(x => x.Step)
            .ToList();
    }

    private static string ResolveFocusedControl(string? domFocusedControl, IReadOnlyList<FlexDiagnosticControlStep> steps)
    {
        var dom = (domFocusedControl ?? string.Empty).Trim();
        bool domIsGeneric = string.IsNullOrEmpty(dom)
            || string.Equals(dom, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dom, "body", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dom, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(dom, "div", StringComparison.OrdinalIgnoreCase);

        var latestControl = steps.LastOrDefault(s =>
            !string.IsNullOrWhiteSpace(s.ControlType)
            && !string.Equals(s.Kind, nameof(TelemetryEventKind.Session), StringComparison.Ordinal)
            && !string.Equals(s.Kind, nameof(TelemetryEventKind.ScreenOpen), StringComparison.Ordinal));

        if (latestControl is null)
            return string.IsNullOrEmpty(dom) ? "none" : dom;

        var tagSuffix = !string.IsNullOrWhiteSpace(latestControl.Tag) ? $" [tag:{latestControl.Tag}]" : string.Empty;
        var flexDescriptor = !string.IsNullOrWhiteSpace(latestControl.Location)
            ? $"{latestControl.Location}{tagSuffix}"
            : $"{latestControl.ControlType}:{latestControl.ControlId}{tagSuffix}";

        return domIsGeneric ? flexDescriptor : $"{dom} ({flexDescriptor})";
    }

    private List<object> GatherCleanSqlHistory(FlexDiagnosticCaptureRequest request)
    {
        int cap = Math.Clamp(request.MaxSqlEntries, 5, 250);
        IReadOnlyList<FlexSqlTelemetryEntry> entries = Array.Empty<FlexSqlTelemetryEntry>();

        if (_sqlProvider is not null)
        {
            entries = _sqlProvider.GetRecentQueries(
                sessionId: request.SessionId,
                tenant: request.Tenant,
                excludeBackgroundPolls: true,
                maxCount: cap);
        }

        if (entries.Count == 0 && request.FallbackSqlHistory is not null)
        {
            entries = request.FallbackSqlHistory
                .Where(q => !q.IsBackgroundPoll)
                .TakeLast(cap)
                .ToList();
        }
        else if (entries.Count > 0)
        {
            // Order oldest-to-newest so chronological reading matches execution order.
            entries = entries.OrderBy(q => q.TimestampUtc).ToList();
        }

        var result = new List<object>(entries.Count);
        foreach (var q in entries)
        {
            result.Add(new
            {
                Sql = q.Sql,
                Parameters = RedactSqlParameters(q.Parameters),
                Timestamp = q.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                ElapsedMs = q.ElapsedMs,
                RowCount = q.RowCount,
                ErrorMessage = q.ErrorMessage
            });
        }
        return result;
    }

    public static string RedactSqlParameters(string? rawParams)
    {
        if (string.IsNullOrWhiteSpace(rawParams))
            return string.Empty;
        return SecretParamPattern.Replace(rawParams, "$1=[REDACTED]");
    }

    public static bool IsSensitiveMemberName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return name.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Passwd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Pswd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Pwd", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Passcode", StringComparison.OrdinalIgnoreCase)
            || name.Contains("SecurityCode", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Api_Key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Credential", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, object?> RedactDictionary(IDictionary<string, object?> source)
    {
        var clean = new Dictionary<string, object?>(source.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in source)
        {
            clean[kv.Key] = IsSensitiveMemberName(kv.Key) ? "[REDACTED]" : kv.Value;
        }
        return clean;
    }

    private static Dictionary<string, object?> ReflectObjectState(object? target, bool includeNonPublicFields)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (target is null)
            return dict;

        try
        {
            var type = target.GetType();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetIndexParameters().Length > 0 || !prop.CanRead)
                    continue;
                if (!includeNonPublicFields || IsScalarInspectable(prop.PropertyType))
                {
                    if (IsSensitiveMemberName(prop.Name))
                    {
                        dict[prop.Name] = "[REDACTED]";
                        continue;
                    }

                    if (!IsScalarInspectable(prop.PropertyType))
                        continue;

                    try { dict[prop.Name] = prop.GetValue(target); }
                    catch { /* Skip throwing computed properties */ }
                }
            }

            if (includeNonPublicFields)
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    // Skip compiler-generated backing fields (<Prop>k__BackingField) to prevent duplicate entries.
                    if (field.Name.StartsWith('<'))
                        continue;
                    if (!IsScalarInspectable(field.FieldType))
                        continue;

                    if (IsSensitiveMemberName(field.Name))
                    {
                        dict[field.Name] = "[REDACTED]";
                        continue;
                    }

                    try { dict[field.Name] = field.GetValue(target); }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            dict["InspectionNotice"] = ex.Message;
        }

        return dict;
    }

    private static bool IsScalarInspectable(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsPrimitive
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(DateOnly)
            || underlying == typeof(Guid)
            || underlying.IsEnum;
    }
}

/// <summary>
/// Generalized Markdown formatter turning a <see cref="FlexDiagnosticTicketHeader"/> and a
/// <see cref="FlexDiagnosticSnapshotService"/> JSON payload (or legacy diagnostic JSON) into a
/// structured developer diagnostic `.md` attachment.
/// </summary>
public static class FlexDiagnosticMarkdownFormatter
{
    public static string BuildTicketMarkdown(FlexDiagnosticTicketHeader header, string? diagnosticsJson)
    {
        ArgumentNullException.ThrowIfNull(header);

        var sb = new StringBuilder(4_096);
        var titlePrefix = string.IsNullOrWhiteSpace(header.DocumentTitlePrefix)
            ? "Diagnostic Telemetry Report"
            : header.DocumentTitlePrefix.Trim();

        sb.AppendLine($"# {titlePrefix}: Ticket #{header.TicketNumber:D5}");
        sb.AppendLine();
        sb.AppendLine($"**Topic:** {header.Topic}");
        sb.AppendLine($"**Reporter:** {header.Reporter}");
        sb.AppendLine($"**Timestamp:** {header.CreatedDate:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"**Active URL:** {header.Url}");
        sb.AppendLine($"**Screen/Form:** `{header.ScreenName}`");
        sb.AppendLine($"**Focused Control:** `{header.FocusedControl}`");
        if (!string.IsNullOrWhiteSpace(header.EnvironmentInfo))
            sb.AppendLine($"**Environment:** {header.EnvironmentInfo}");
        sb.AppendLine($"**Issue Type / Priority / Severity:** {header.IssueType} / {header.Priority} / {header.Severity}");
        sb.AppendLine($"**Assignee / Sprint:** {(string.IsNullOrWhiteSpace(header.Assignee) ? "Unassigned" : header.Assignee)} / {(string.IsNullOrWhiteSpace(header.Sprint) ? "—" : header.Sprint)}");
        if (!string.IsNullOrWhiteSpace(header.Labels))
            sb.AppendLine($"**Labels:** {header.Labels}");
        if (!string.IsNullOrWhiteSpace(header.LegacyFormContext))
            sb.AppendLine($"**Migration Context:** {header.LegacyFormContext}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 1. Problem Description");
        sb.AppendLine(header.Description);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(header.Prerequisites)
            || !string.IsNullOrWhiteSpace(header.StepsToReproduce)
            || !string.IsNullOrWhiteSpace(header.ExpectedResult)
            || !string.IsNullOrWhiteSpace(header.ActualResult))
        {
            sb.AppendLine("### Reproduction");
            if (!string.IsNullOrWhiteSpace(header.Prerequisites))
                sb.AppendLine($"- **Prerequisites:** {header.Prerequisites}");
            if (!string.IsNullOrWhiteSpace(header.StepsToReproduce))
                sb.AppendLine($"- **Steps to reproduce:** {header.StepsToReproduce}");
            if (!string.IsNullOrWhiteSpace(header.ExpectedResult))
                sb.AppendLine($"- **Expected:** {header.ExpectedResult}");
            if (!string.IsNullOrWhiteSpace(header.ActualResult))
                sb.AppendLine($"- **Actual:** {header.ActualResult}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 2. Screenshot & Visual Context");
        if (!string.IsNullOrEmpty(header.ScreenshotPath))
            sb.AppendLine($"![Screenshot Companion]({header.ScreenshotPath})");
        else
            sb.AppendLine("*(No screenshot companion attached)*");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        if (string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            sb.AppendLine("## 3. Environment Context");
            sb.AppendLine("*(No diagnostics logs gathered)*");
            return sb.ToString();
        }

        try
        {
            using var doc = JsonDocument.Parse(diagnosticsJson);
            var root = doc.RootElement;

            sb.AppendLine("## 3. Active Program & Variables Context");
            if (root.TryGetProperty("ActivePageType", out var activePage))
                sb.AppendLine($"- **Active Page Component Type:** `{activePage.GetString()}`");
            if (root.TryGetProperty("ActivePageTitle", out var pageTitle))
                sb.AppendLine($"- **Page Title:** `{pageTitle.GetString()}`");
            if (root.TryGetProperty("ActivePageRoute", out var route))
                sb.AppendLine($"- **Route:** `{route.GetString()}`");
            if (root.TryGetProperty("NavigationPath", out var navPath) && !string.IsNullOrWhiteSpace(navPath.GetString()))
                sb.AppendLine($"- **Navigation Path:** `{navPath.GetString()}`");
            if (root.TryGetProperty("FocusedFlexControlLocation", out var flexLoc) && !string.IsNullOrWhiteSpace(flexLoc.GetString()))
                sb.AppendLine($"- **Last Active FlexKit Control Hierarchy:** `{flexLoc.GetString()}`");
            sb.AppendLine();

            if (root.TryGetProperty("FormState", out var formState) && formState.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine("### Form State Variables:");
                sb.AppendLine("| Variable | Value |");
                sb.AppendLine("|---|---|");
                foreach (var prop in formState.EnumerateObject())
                {
                    if (prop.Name.StartsWith('<'))
                        continue;
                    var val = FlexDiagnosticSnapshotService.IsSensitiveMemberName(prop.Name)
                        ? "\"[REDACTED]\""
                        : SanitizeCell(prop.Value.GetRawText(), 240);
                    sb.AppendLine($"| `{prop.Name}` | `{val}` |");
                }
                sb.AppendLine();
            }

            if (root.TryGetProperty("SessionState", out var sessionState) && sessionState.ValueKind == JsonValueKind.Object)
            {
                sb.AppendLine("### Active Session State:");
                sb.AppendLine("| Session Key | Value |");
                sb.AppendLine("|---|---|");
                foreach (var prop in sessionState.EnumerateObject())
                {
                    var val = FlexDiagnosticSnapshotService.IsSensitiveMemberName(prop.Name)
                        ? "\"[REDACTED]\""
                        : SanitizeCell(prop.Value.GetRawText(), 240);
                    sb.AppendLine($"| `{prop.Name}` | `{val}` |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();

            sb.AppendLine("## 4. SQL Query Execution History (Last Queries)");
            if (root.TryGetProperty("SqlHistory", out var sqlHistory) && sqlHistory.ValueKind == JsonValueKind.Array)
            {
                var sqlArray = sqlHistory.EnumerateArray().ToList();
                if (sqlArray.Count > 0)
                {
                    sb.AppendLine("| Timestamp | Elapsed (ms) | Rows | SQL Query | Parameters | Error |");
                    sb.AppendLine("|---|---|---|---|---|---|");
                    foreach (var sqlVal in sqlArray)
                    {
                        var timestamp = ReadStringOrRaw(sqlVal, "Timestamp");
                        var elapsed = ReadStringOrRaw(sqlVal, "ElapsedMs");
                        var rowCount = ReadStringOrRaw(sqlVal, "RowCount");
                        var sqlText = ReadStringOrRaw(sqlVal, "Sql");
                        var parms = FlexDiagnosticSnapshotService.RedactSqlParameters(ReadStringOrRaw(sqlVal, "Parameters"));
                        var errMsg = ReadStringOrRaw(sqlVal, "ErrorMessage");

                        var cleanSql = SanitizeCell(sqlText, 220);
                        sb.AppendLine($"| `{SanitizeCell(timestamp, 32)}` | `{SanitizeCell(elapsed, 16)}` | `{SanitizeCell(rowCount, 16)}` | `{cleanSql}` | `{SanitizeCell(parms, 160)}` | `{SanitizeCell(errMsg, 140)}` |");
                    }
                }
                else
                {
                    sb.AppendLine("*(No SQL query history recorded)*");
                }
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();

            sb.AppendLine("## 5. Sequence of User Interaction Events");
            bool renderedStructured = false;
            if (root.TryGetProperty("ControlEvents", out var ctlEvents) && ctlEvents.ValueKind == JsonValueKind.Array)
            {
                var steps = ctlEvents.EnumerateArray().ToList();
                if (steps.Count > 0)
                {
                    renderedStructured = true;
                    sb.AppendLine("| Timestamp | Stream | Control Location Hierarchy | Control | Tag | Action | Value |");
                    sb.AppendLine("|---|---|---|---|---|---|---|");
                    foreach (var st in steps)
                    {
                        var ts = SanitizeCell(ReadStringOrRaw(st, "Timestamp"), 28);
                        var kind = SanitizeCell(ReadStringOrRaw(st, "Kind"), 24);
                        var loc = SanitizeCell(ReadStringOrRaw(st, "Location"), 120);
                        var ctl = SanitizeCell($"{ReadStringOrRaw(st, "ControlType")}:{ReadStringOrRaw(st, "ControlId")}".Trim(':'), 64);
                        var tag = SanitizeCell(ReadStringOrRaw(st, "Tag"), 40);
                        var act = SanitizeCell(ReadStringOrRaw(st, "Action"), 40);
                        var val = SanitizeCell(ReadStringOrRaw(st, "Value"), 80);
                        sb.AppendLine($"| `{ts}` | `{kind}` | `{loc}` | `{ctl}` | `{tag}` | `{act}` | `{val}` |");
                    }
                    sb.AppendLine();
                }
            }

            if (root.TryGetProperty("EventsSequence", out var eventsSeq) && eventsSeq.ValueKind == JsonValueKind.Array)
            {
                var events = eventsSeq.EnumerateArray().ToList();
                if (events.Count > 0)
                {
                    if (renderedStructured)
                        sb.AppendLine("### Event Stream Summary:");
                    foreach (var ev in events)
                    {
                        sb.AppendLine($"- {ev.GetString()}");
                    }
                }
                else if (!renderedStructured)
                {
                    sb.AppendLine("*(No event sequence recorded)*");
                }
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("## 3. Environment Context");
            sb.AppendLine($"*(Error parsing diagnostics payload: {ex.Message})*");
            sb.AppendLine();
            sb.AppendLine("### Raw JSON Payload:");
            sb.AppendLine("```json");
            sb.AppendLine(diagnosticsJson);
            sb.AppendLine("```");
        }

        return sb.ToString();
    }

    private static string ReadStringOrRaw(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var el) || el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return string.Empty;
        return el.ValueKind == JsonValueKind.String ? (el.GetString() ?? string.Empty) : el.GetRawText();
    }

    private static string SanitizeCell(string? raw, int maxLen)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;
        var clean = raw.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Replace("`", "'").Trim();
        return clean.Length > maxLen ? clean[..(maxLen - 3)] + "..." : clean;
    }
}
