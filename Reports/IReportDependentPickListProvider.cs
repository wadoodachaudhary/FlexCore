namespace Fx.ControlKit.Reports;

/// <summary>The report's complete parameter metadata and a snapshot of its current prompt/session values.</summary>
public sealed record ReportPickListContext(
    string ReportName,
    IReadOnlyList<ReportParameter> Parameters,
    IReadOnlyDictionary<string, object> Values);

/// <summary>
/// Optional extension to <see cref="IReportPickListProvider"/> for authoritative,
/// dependent choices. The dialog waits for the named parents, refreshes after
/// their values change, and never falls back to unrestricted or custom choices.
/// </summary>
public interface IReportDependentPickListProvider
{
    /// <summary>Null leaves the parameter on the ordinary provider path; an empty list handles a root parameter.</summary>
    IReadOnlyList<string>? GetDependencies(ReportParameter parameter, ReportPickListContext context);

    /// <summary>Returns the allowed choices for this exact context. An empty result is authoritative.</summary>
    Task<IReadOnlyList<PickListItem>> GetPickListAsync(
        ReportParameter parameter, ReportPickListContext context, CancellationToken cancellationToken);
}
