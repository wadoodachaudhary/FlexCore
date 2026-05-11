namespace Fx.ControlKit.Reports;

/// <summary>
/// Optional source of pick-list values for the parameter-prompt dialog.
/// When the dialog encounters a parameter whose <c>Name</c> matches a known
/// lookup (Worksheet, Job, Community, …), the provider returns the list of
/// (value, display) pairs to populate a select box. Without an implementation,
/// every parameter falls back to a free-text input.
/// </summary>
public interface IReportPickListProvider
{
    /// <summary>
    /// Returns the pick-list options for the given parameter name, or
    /// <c>null</c> if there is no pick-list for that name.
    /// </summary>
    IReadOnlyList<PickListItem>? GetPickList(string parameterName);
}

/// <summary>One entry in a parameter pick-list.</summary>
public sealed record PickListItem(string Value, string Display);
