namespace Fx.ControlKit.Charts;

/// <summary>
/// Optional 2x2 quadrant reference overlay rendered on <see cref="ChartType.Scatter"/> and <see cref="ChartType.Bubble"/> charts
/// (for example Feature Adoption Matrices plotting User Share % vs. Actions per User).
/// </summary>
public sealed class ChartQuadrantOverlay
{
    public double ReferenceX { get; init; }
    public double ReferenceY { get; init; }
    public string TopRightLabel { get; init; } = "Core Drivers";
    public string TopLeftLabel { get; init; } = "Power Niche";
    public string BottomRightLabel { get; init; } = "Broad Utility";
    public string BottomLeftLabel { get; init; } = "Dormant / Low";
    public string LineColor { get; init; } = "#64748b";
    public string LabelColor { get; init; } = "#475569";
    public bool ShowPointCaptions { get; init; }
}

/// <summary>
/// Single stage in a <see cref="FunnelControl"/> conversion workflow.
/// </summary>
public sealed class FunnelStepItem
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public double Count { get; init; }
    public double? MedianSecondsFromPrevious { get; init; }
    public string? Subtitle { get; init; }
    public string? Color { get; init; }

    public FunnelStepItem() { }

    public FunnelStepItem(string label, double count, double? medianSecondsFromPrevious = null, string? subtitle = null, string? color = null, string? key = null)
    {
        Key = key ?? label;
        Label = label;
        Count = count;
        MedianSecondsFromPrevious = medianSecondsFromPrevious;
        Subtitle = subtitle;
        Color = color;
    }
}

/// <summary>
/// Event payload emitted when a user selects a converted bar or drop-off callout in <see cref="FunnelControl"/>.
/// </summary>
public sealed record FunnelStepClickArgs(
    int StepIndex,
    FunnelStepItem Step,
    double OverallConversionPercent,
    double StepConversionPercent,
    double DropOffCount,
    double DropOffPercent,
    bool IsDropOffClick);

/// <summary>
/// Stage node in a multi-column <see cref="PathFlowControl"/> (Sankey / User-Journey flow graph).
/// </summary>
public sealed class PathFlowNode
{
    public required string Id { get; init; }
    public required int Stage { get; init; }
    public required string Label { get; init; }
    public double Value { get; set; }
    public bool IsDropOff { get; init; }
    public bool IsLoop { get; init; }
    public string? Color { get; init; }
}

/// <summary>
/// Directed flow ribbon connecting two <see cref="PathFlowNode"/> items across adjacent stages.
/// </summary>
public sealed class PathFlowLink
{
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public required double Value { get; init; }
    public bool IsLoop { get; init; }
    public string? Color { get; init; }
}

/// <summary>
/// Cohort retention row rendered in <see cref="CohortMatrixControl"/>.
/// </summary>
public sealed class CohortMatrixRow
{
    public required string CohortLabel { get; init; }
    public required int CohortSize { get; init; }
    public required IReadOnlyList<int> ActiveCounts { get; init; }
    public string? Subtitle { get; init; }
}

/// <summary>
/// Event payload emitted when a cell in <see cref="CohortMatrixControl"/> is clicked.
/// </summary>
public sealed record CohortCellClickArgs(
    int RowIndex,
    int PeriodIndex,
    CohortMatrixRow Row,
    int ActiveCount,
    double RetentionPercent);
