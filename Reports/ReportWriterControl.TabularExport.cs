using System.Data;

namespace Fx.ControlKit.Reports;

public partial class ReportWriterControl
{
    public async Task ShowTabularDataAsync(ReportDefinition definition, DataTable data,
        IReadOnlyDictionary<string, object>? parameters = null)
    {
        ErrorMessage = null;
        ClearPdfPreview();
        Tabs.Clear();
        Tabs.Add(RenderTabularSnapshot(definition, data, parameters));
        ActiveTabIndex = 0;
        IsLoading = false;
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Headless exports use exactly the viewer's tabular grouping, suppression and pagination.</summary>
    public static ReportTab RenderTabularSnapshot(ReportDefinition definition, DataTable data,
        IReadOnlyDictionary<string, object>? parameters = null)
    {
        if (definition.PositionedLayout is not null) throw new ArgumentException("Use the positioned layout session for this report.");
        var columns = definition.Columns.Where(column => !column.Hidden).ToList();
        var writer = new ReportWriterControl();
        writer.ComputeAdaptiveColumnWidths(definition, columns, data);
        return writer.CreateTabularTab(definition.Title, definition, data, columns, parameters, 0);
    }
}
