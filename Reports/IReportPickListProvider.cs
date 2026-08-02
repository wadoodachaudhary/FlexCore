namespace Fx.ControlKit.Reports;

/// <summary>
/// Optional source of pick-list values for the parameter-prompt dialog.
///
/// <para>
/// The primary code path is <see cref="GetPickList(ReportParameter)"/>:
/// the loader populates each parameter with a <see cref="ReportParameter.PickListSql"/>
/// auto-derived from the Crystal XML's record-selection-formula binding,
/// and the provider just runs that SQL. No host-side per-parameter table
/// mapping required.
/// </para>
///
/// <para>
/// The name-based overload (<see cref="GetPickList(string)"/>) remains
/// for legacy reports that don't have a clean Crystal binding (subreport
/// parameters, free-form formulas, etc.) where the host still needs to
/// supply a query.
/// </para>
/// </summary>
public interface IReportPickListProvider
{
    /// <summary>
    /// Returns the pick-list options for the given parameter — preferred
    /// over <see cref="GetPickList(string)"/>. When
    /// <see cref="ReportParameter.PickListSql"/> is populated the
    /// implementation executes that query directly; otherwise it falls
    /// back to the name-based lookup.
    /// </summary>
    IReadOnlyList<PickListItem>? GetPickList(ReportParameter parameter);

    /// <summary>
    /// Returns the pick-list options for the given parameter name, or
    /// <c>null</c> if there is no host-side lookup for that name.
    /// Legacy entry point — prefer the <see cref="ReportParameter"/>
    /// overload so picklists can come from the auto-derived
    /// <see cref="ReportParameter.PickListSql"/>.
    /// </summary>
    IReadOnlyList<PickListItem>? GetPickList(string parameterName);

    /// <summary>
    /// Returns <c>true</c> when this provider knows how to source a
    /// pick-list for the given parameter name. Cheap "is-supported" probe
    /// used by routing code that needs to decide between a dual-list-box
    /// page and a dialog before paying the cost of running the actual
    /// query.
    /// </summary>
    bool HasPickList(string parameterName);
}

/// <summary>One entry in a parameter pick-list.</summary>
public sealed record PickListItem(string Value, string Display);
