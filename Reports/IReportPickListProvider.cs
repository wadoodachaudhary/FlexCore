namespace Fx.ControlKit.Reports;

public interface IReportPickListProvider
{
    IReadOnlyList<PickListItem>? GetPickList(ReportParameter parameter);

    IReadOnlyList<PickListItem>? GetPickList(string parameterName);

    bool HasPickList(string parameterName);
}

public sealed record PickListItem(string Value, string Display);
