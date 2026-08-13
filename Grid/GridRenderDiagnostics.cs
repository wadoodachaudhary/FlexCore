using System.Diagnostics;

namespace Fx.ControlKit.Grid;

/// <summary>Process-wide render metrics for the perf benches (FlexKitTester).
/// Counted inside GridControl's render pass; a handful of increments per
/// render, so production hosts that never read it pay nothing measurable.
/// Metrics are cumulative across every grid instance in the process — benches
/// with a single grid on the page read them as that grid's numbers.</summary>
public static class GridRenderDiagnostics
{
    /// <summary>Completed GridControl render passes (BuildRenderTree executions).</summary>
    public static long RenderCount;

    /// <summary>Markup build time of the most recent render pass, in ms.</summary>
    public static double LastBuildMs;

    /// <summary>Cumulative markup build time across all render passes, in ms.</summary>
    public static double TotalBuildMs;

    public static void Reset()
    {
        RenderCount = 0;
        LastBuildMs = 0;
        TotalBuildMs = 0;
    }

    internal static long BeginPass() => Stopwatch.GetTimestamp();

    internal static void EndPass(long startTimestamp)
    {
        RenderCount++;
        var ms = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        LastBuildMs = ms;
        TotalBuildMs += ms;
    }
}
