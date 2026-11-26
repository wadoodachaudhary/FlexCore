namespace Fx.ControlKit.WorkTracking;

public sealed record WorkAgentProfile(string Id, string Provider, string Model, string Instructions = "", int MaxOutputTokens = 8192, bool AllowContextSharing = false);
public sealed record WorkSprint(string Key, string Name, string Goal, int Order, int DurationDays);

/// <summary>An existing application used as read-only integration context, never as additional migration scope.</summary>
public sealed record WorkApplicationContext
{
    public string Name { get; init; } = "";
    public string Folder { get; init; } = "";
    public string EntryPoint { get; init; } = "";
    public string Notes { get; init; } = "Already converted. Reuse its integration contracts; do not remigrate or modify this application.";
    public string Overview { get; init; } = "";
    public string Fingerprint { get; init; } = "";
}

/// <summary>A durable planning checkpoint. The source baseline remains independent of model-authored prose.</summary>
public sealed record WorkPlanningDraft
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string SourceFolder { get; init; }
    public string SourceProject { get; init; } = "";
    public string SourceLanguage { get; init; } = "Vb6";
    public required string TargetFolder { get; init; }
    public string TargetStack { get; init; } = "Blazor Server / C# / .NET / FlexCore";
    public string Goals { get; init; } = "Preserve legacy behavior and data semantics.";
    public WorkApplicationContext[] ContextApplications { get; init; } = [];
    public int SprintCount { get; init; } = 4;
    public int SprintDays { get; init; } = 14;
    public required WorkAgentProfile Planner { get; init; }
    public string PlanMarkdown { get; init; } = "";
    public string PlanProvenance { get; init; } = "";
    public bool PlanApproved { get; init; }
    public string PlanApprovedBy { get; init; } = "";
    public DateTimeOffset? PlanApprovedUtc { get; init; }
    public int Revision { get; init; }
    public WorkBoardSnapshot? Baseline { get; init; }
}
