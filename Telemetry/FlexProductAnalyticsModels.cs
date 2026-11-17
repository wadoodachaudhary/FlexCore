using Fx.ControlKit.Charts;

namespace Fx.ControlKit.Telemetry;

/// <summary>
/// Top-level workspace perspective inside <see cref="TelemetryControl"/>.
/// </summary>
public enum TelemetryWorkspaceMode
{
    Observability = 0,
    ProductAnalytics = 1
}

/// <summary>
/// Matching rule used for a step in a Product Analytics conversion funnel.
/// </summary>
public enum TelemetryFunnelMatchKind
{
    Screen = 0,
    Action = 1,
    Tag = 2,
    ControlLocation = 3,
    EventKind = 4
}

/// <summary>
/// Single step specification in a preset or interactive conversion funnel.
/// </summary>
public sealed record TelemetryFunnelStepSpec(
    string Label,
    TelemetryFunnelMatchKind MatchKind,
    string Pattern);

/// <summary>
/// Named business workflow funnel supplied optionally by a host application via <see cref="ITelemetryWorkflowProvider"/>.
/// </summary>
public sealed record TelemetryWorkflowPreset(
    string Key,
    string Name,
    string Description,
    IReadOnlyList<TelemetryFunnelStepSpec> Steps);

/// <summary>
/// Optional host provider supplying domain-specific conversion funnel presets to <see cref="FunnelAnalyticsControl"/>.
/// When unregistered, <see cref="FunnelAnalyticsControl"/> synthesizes generic control funnels automatically and
/// provides a full interactive step builder.
/// </summary>
public interface ITelemetryWorkflowProvider
{
    IReadOnlyList<TelemetryWorkflowPreset> GetWorkflowPresets();
}

/// <summary>
/// 2x2 Product Analytics feature adoption classification (Breadth of Users vs. Frequency per User).
/// </summary>
public enum FeatureAdoptionQuadrant
{
    CoreDriver = 0,
    PowerNiche = 1,
    BroadUtility = 2,
    Dormant = 3
}

/// <summary>
/// Per-screen/feature behavioral adoption metrics plotted on the 2x2 Feature Adoption Matrix.
/// </summary>
public sealed class FeatureAdoptionRow
{
    public string ScreenTitle { get; init; } = string.Empty;
    public string Route { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public int UniqueUsers { get; init; }
    public double UserSharePercent { get; init; }
    public int OpenCount { get; init; }
    public int TotalActions { get; init; }
    public double ActionsPerUser { get; init; }
    public double AvgDwellSeconds { get; init; }
    public FeatureAdoptionQuadrant Quadrant { get; init; }
    public string QuadrantLabel => Quadrant switch
    {
        FeatureAdoptionQuadrant.CoreDriver => "Core Driver",
        FeatureAdoptionQuadrant.PowerNiche => "Power Niche",
        FeatureAdoptionQuadrant.BroadUtility => "Broad Utility",
        _ => "Dormant / Low"
    };
    public double? PriorPeriodDeltaPercent { get; init; }
}

/// <summary>
/// Habitual usage stickiness (DAU / WAU / MAU) and period-over-period delta summary.
/// </summary>
public sealed class StickinessSummary
{
    public int PeakDau { get; init; }
    public double AverageDau { get; init; }
    public int Wau { get; init; }
    public int Mau { get; init; }
    public double DauOverWauPercent { get; init; }
    public double WauOverMauPercent { get; init; }
    public double? EventsDeltaPercent { get; init; }
    public double? UsersDeltaPercent { get; init; }
    public double? ScreenOpensDeltaPercent { get; init; }
    public double? ActiveMinutesDeltaPercent { get; init; }
    public IReadOnlyList<double> DailyActiveUsersSparkline { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> DailyEventsSparkline { get; init; } = Array.Empty<double>();
}

/// <summary>
/// Tabular summary row for a single conversion funnel stage.
/// </summary>
public sealed class ComputedFunnelStepRow
{
    public int StepNumber { get; init; }
    public string StepLabel { get; init; } = string.Empty;
    public string MatchRule { get; init; } = string.Empty;
    public int ConvertedActors { get; init; }
    public double OverallRatePercent { get; init; }
    public double StepRatePercent { get; init; }
    public int DroppedActors { get; init; }
    public double DropOffPercent { get; init; }
    public double MedianSecondsFromPrev { get; init; }
}

/// <summary>
/// Actor (session or user) that entered a funnel and dropped off before completing a subsequent step.
/// </summary>
public sealed class FunnelDropOffRow
{
    public string ActorKey { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public int LastCompletedStep { get; init; }
    public string DroppedBeforeStep { get; init; } = string.Empty;
    public string LastScreen { get; init; } = string.Empty;
    public string LastAction { get; init; } = string.Empty;
    public DateTime LastSeenLocal { get; init; }
}

/// <summary>
/// Complete evaluated result of a multi-step Product Analytics conversion funnel.
/// </summary>
public sealed class ComputedFunnelReport
{
    public string Name { get; init; } = "Conversion Funnel";
    public string ScopeLabel { get; init; } = "Sessions";
    public int TotalEntered { get; init; }
    public int TotalCompleted { get; init; }
    public double OverallConversionPercent { get; init; }
    public double MedianTotalSeconds { get; init; }
    public IReadOnlyList<FunnelStepItem> VisualSteps { get; init; } = Array.Empty<FunnelStepItem>();
    public IReadOnlyList<ComputedFunnelStepRow> StepRows { get; init; } = Array.Empty<ComputedFunnelStepRow>();
    public IReadOnlyList<FunnelDropOffRow> DropOffRows { get; init; } = Array.Empty<FunnelDropOffRow>();
}

/// <summary>
/// Ranked screen-to-screen navigation transition.
/// </summary>
public sealed class UserTransitionRow
{
    public string FromScreen { get; init; } = string.Empty;
    public string ToScreen { get; init; } = string.Empty;
    public int TransitionCount { get; init; }
    public int UniqueSessions { get; init; }
    public int UniqueUsers { get; init; }
    public double MedianTransitionSeconds { get; init; }
}

/// <summary>
/// Detected A-B-A-B screen oscillation indicating split-screen UX friction.
/// </summary>
public sealed class PingPongLoopRow
{
    public string ScreenA { get; init; } = string.Empty;
    public string ScreenB { get; init; } = string.Empty;
    public int LoopCount { get; init; }
    public int AffectedSessions { get; init; }
    public int AffectedUsers { get; init; }
    public double AvgLoopDurationSeconds { get; init; }
    public DateTime LastOccurredLocal { get; init; }
}

/// <summary>
/// Multi-stage user journey graph and navigation transition report.
/// </summary>
public sealed class UserJourneyReport
{
    public IReadOnlyList<string> AvailableScreens { get; init; } = Array.Empty<string>();
    public IReadOnlyList<PathFlowNode> Nodes { get; init; } = Array.Empty<PathFlowNode>();
    public IReadOnlyList<PathFlowLink> Links { get; init; } = Array.Empty<PathFlowLink>();
    public IReadOnlyList<string> StageTitles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<UserTransitionRow> TopTransitions { get; init; } = Array.Empty<UserTransitionRow>();
    public IReadOnlyList<PingPongLoopRow> PingPongLoops { get; init; } = Array.Empty<PingPongLoopRow>();
}

/// <summary>
/// Period user-lifecycle breakdown (New, Current/Retained, Resurrected, Dormant).
/// </summary>
public sealed class UserLifecycleBucketRow
{
    public string PeriodLabel { get; init; } = string.Empty;
    public int NewUsers { get; init; }
    public int RetainedUsers { get; init; }
    public int ResurrectedUsers { get; init; }
    public int DormantUsers { get; init; }
}

/// <summary>
/// RFM-style behavioral engagement profile per user.
/// </summary>
public sealed class UserEngagementSegmentRow
{
    public string UserId { get; init; } = string.Empty;
    public string Segment { get; init; } = "Regular";
    public int ActiveDays { get; init; }
    public int DistinctScreens { get; init; }
    public int DistinctModules { get; init; }
    public int TotalActions { get; init; }
    public double DaysSinceLastSeen { get; init; }
    public string TopModule { get; init; } = string.Empty;
}

/// <summary>
/// Cohort retention matrix and user-lifecycle report.
/// </summary>
public sealed class CohortRetentionReport
{
    public string Granularity { get; init; } = "Weekly";
    public IReadOnlyList<string> PeriodHeaders { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CohortMatrixRow> CohortRows { get; init; } = Array.Empty<CohortMatrixRow>();
    public IReadOnlyList<UserLifecycleBucketRow> LifecycleBuckets { get; init; } = Array.Empty<UserLifecycleBucketRow>();
    public IReadOnlyList<UserEngagementSegmentRow> UserSegments { get; init; } = Array.Empty<UserEngagementSegmentRow>();
}

/// <summary>
/// Category of automatically detected UX struggle or digital experience pain.
/// </summary>
public enum UxFrictionKind
{
    RageClick = 0,
    AbandonedForm = 1,
    FilterThrash = 2,
    SlowInteraction = 3,
    SqlFailure = 4,
    UnhandledError = 5
}

/// <summary>
/// Individual UX friction incident detected automatically from server-side FlexKit control and SQL telemetry.
/// </summary>
public sealed class UxFrictionIncident
{
    public DateTime TimestampUtc { get; init; }
    public DateTime TimestampLocal { get; init; }
    public UxFrictionKind Kind { get; init; }
    public string KindLabel => Kind switch
    {
        UxFrictionKind.RageClick => "Rage Click",
        UxFrictionKind.AbandonedForm => "Abandoned Form",
        UxFrictionKind.FilterThrash => "Grid Filter Thrash",
        UxFrictionKind.SlowInteraction => "Slow Response",
        UxFrictionKind.SqlFailure => "SQL Error",
        _ => "Unhandled Error"
    };
    public string Severity { get; init; } = "Warning";
    public string UserId { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Screen { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string? Tag { get; init; }
    public string Summary { get; init; } = string.Empty;
    public int EventCount { get; init; } = 1;
    public long? LatencyMs { get; init; }
    public string? RelatedSql { get; init; }
}

/// <summary>
/// Click-to-SQL Apdex responsiveness score per interactive FlexKit control.
/// </summary>
public sealed class ControlApdexRow
{
    public string Screen { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string Control { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public int SampleCount { get; init; }
    public double AvgMs { get; init; }
    public double P95Ms { get; init; }
    public int SatisfiedCount { get; init; }
    public int ToleratingCount { get; init; }
    public int FrustratedCount { get; init; }
    public double ApdexScore { get; init; }
    public int CorrelatedSqlCount { get; init; }
    public int CorrelatedSqlWrites { get; init; }
}

/// <summary>
/// Summary of an individual user session available for flight-recorder timeline inspection.
/// </summary>
public sealed class SessionSummaryRow
{
    public string SessionId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string Tenant { get; init; } = string.Empty;
    public DateTime StartLocal { get; init; }
    public DateTime EndLocal { get; init; }
    public double DurationMinutes { get; init; }
    public int EventCount { get; init; }
    public int ScreenCount { get; init; }
    public int SqlCount { get; init; }
    public int FrictionCount { get; init; }
    public string EntryScreen { get; init; } = string.Empty;
    public string ExitScreen { get; init; } = string.Empty;
}

/// <summary>
/// Single chronological entry inside a <see cref="SessionTimelineControl"/> flight-recorder trace.
/// </summary>
public sealed class SessionTimelineItem
{
    public DateTime TimestampUtc { get; init; }
    public DateTime TimestampLocal { get; init; }
    public double OffsetSeconds { get; init; }
    public double SpanSeconds { get; init; }
    public string EntryKind { get; init; } = "Action";
    public string Screen { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Location { get; init; } = string.Empty;
    public string? Tag { get; init; }
    public string? Value { get; init; }
    public long? ElapsedMs { get; init; }
    public bool IsFriction { get; init; }
    public bool IsError { get; init; }
    public bool IsSqlWrite { get; init; }
}

/// <summary>
/// Complete chronological flight-recorder session trace merging UI actions, SQL queries, and friction pins.
/// </summary>
public sealed class SessionReplayTrace
{
    public required SessionSummaryRow Summary { get; init; }
    public IReadOnlyList<SessionTimelineItem> Items { get; init; } = Array.Empty<SessionTimelineItem>();
    public IReadOnlyList<UxFrictionIncident> FrictionIncidents { get; init; } = Array.Empty<UxFrictionIncident>();
}

/// <summary>
/// Flattened strongly-typed row consumed by <see cref="TelemetryPivotControl"/> (<c>PivotControl&lt;TelemetryPivotRow&gt;</c>).
/// </summary>
public sealed class TelemetryPivotRow
{
    public string Tenant { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public string Screen { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string EventKind { get; init; } = string.Empty;
    public string ControlType { get; init; } = string.Empty;
    public string Control { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string DayOfWeek { get; init; } = string.Empty;
    public string DateLabel { get; init; } = string.Empty;
    public string HourBlock { get; init; } = string.Empty;
    public int Events { get; init; } = 1;
    public double DurationSeconds { get; init; }
}

/// <summary>
/// Aggregated Product Analytics snapshot computed in-memory by <see cref="FlexProductAnalyticsEngine"/>.
/// </summary>
public sealed class FlexProductAnalyticsSnapshot
{
    public DateTime GeneratedAtLocal { get; init; } = DateTime.Now;
    public string TimeWindowLabel { get; init; } = "Last 30d";
    public StickinessSummary Stickiness { get; init; } = new();
    public double MedianUserSharePercent { get; init; }
    public double MedianActionsPerUser { get; init; }
    public IReadOnlyList<FeatureAdoptionRow> AdoptionRows { get; init; } = Array.Empty<FeatureAdoptionRow>();
    public SunburstNode? HierarchySunburstRoot { get; init; }
    public IReadOnlyList<ChartDataPoint> ModuleScreenTreemapPoints { get; init; } = Array.Empty<ChartDataPoint>();
    public IReadOnlyList<TelemetryWorkflowPreset> AvailableWorkflowPresets { get; init; } = Array.Empty<TelemetryWorkflowPreset>();
    public ComputedFunnelReport DefaultFunnel { get; init; } = new();
    public UserJourneyReport Journeys { get; init; } = new();
    public CohortRetentionReport WeeklyCohorts { get; init; } = new();
    public CohortRetentionReport DailyCohorts { get; init; } = new();
    public double OverallApdexScore { get; init; } = 1.0;
    public int RageClickCount { get; init; }
    public int AbandonedFormCount { get; init; }
    public int FilterThrashCount { get; init; }
    public int SlowInteractionCount { get; init; }
    public IReadOnlyList<UxFrictionIncident> FrictionIncidents { get; init; } = Array.Empty<UxFrictionIncident>();
    public IReadOnlyList<ControlApdexRow> ControlApdexRows { get; init; } = Array.Empty<ControlApdexRow>();
    public IReadOnlyList<SessionSummaryRow> Sessions { get; init; } = Array.Empty<SessionSummaryRow>();
    public IReadOnlyList<TelemetryPivotRow> PivotRows { get; init; } = Array.Empty<TelemetryPivotRow>();
    public IReadOnlyList<FlexTelemetryEvent> WindowEvents { get; init; } = Array.Empty<FlexTelemetryEvent>();
    public IReadOnlyList<FlexSqlTelemetryEntry> WindowSqlEntries { get; init; } = Array.Empty<FlexSqlTelemetryEntry>();
}
