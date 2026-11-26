namespace Fx.ControlKit.WorkTracking;

public enum WorkItemKind { Feature, Epic, Story, Task }
public enum WorkItemState { Draft, Ready, InProgress, Review, Blocked, Done }

public sealed record WorkEvidence(string Path, int Line, string Description);

public sealed record WorkItem
{
    public required string Key { get; init; }
    public required string StableId { get; init; }
    public required string Title { get; init; }
    public required WorkItemKind Kind { get; init; }
    public string? ParentKey { get; init; }
    public string Description { get; init; } = "";
    public string[] AcceptanceCriteria { get; init; } = [];
    public WorkEvidence[] Evidence { get; init; } = [];
    public string[] DependsOn { get; init; } = [];
    public string[] Labels { get; init; } = [];
    public WorkItemState State { get; init; } = WorkItemState.Draft;
    public string Assignee { get; init; } = "";
    public string SourceObligationKey { get; init; } = "";
    public string SprintKey { get; init; } = "";
    public int EstimatePoints { get; init; }
    public WorkAgentProfile? Agent { get; init; }
    public string ResultKind { get; init; } = "";
    public string LastRunError { get; init; } = "";
    public string SuggestedRole { get; init; } = "migration";
    public string ResultRevision { get; init; } = "";
    public string VerificationEvidence { get; init; } = "";
    public int Attempts { get; init; }
    public string? LeaseToken { get; init; }
    public DateTimeOffset? LeaseExpiresUtc { get; init; }
}

public sealed record WorkProject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string SourceFingerprint { get; init; }
    public string SourcePath { get; init; } = "";
    public string AnalysisPath { get; init; } = "";
    public string PlanVersion { get; init; } = "1";
    public string Summary { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Approved { get; init; }
    public WorkPlanningDraft? Planning { get; init; }
    public WorkSprint[] Sprints { get; init; } = [];
}

public sealed record WorkBoardSnapshot(WorkProject Project, IReadOnlyList<WorkItem> Items);
public sealed record WorkBoardEvent(long Id, string ItemKey, string Actor, string Action, string Detail, DateTimeOffset AtUtc);
public sealed record WorkClaim(string ProjectId, WorkItem Item, string Token, DateTimeOffset ExpiresUtc);

/// <summary>Private, host-selected storage; never place this directory under a public static-file root.</summary>
public sealed record LocalWorkBoardOptions(string RootDirectory);
