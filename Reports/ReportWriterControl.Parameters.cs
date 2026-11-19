namespace Fx.ControlKit.Reports;

public partial class ReportWriterControl
{
    // Only the path-based workflow owns an editable prompt definition. Direct
    // SQL/table/PDF loads must not retain a previous report's toolbar action.
    // Execution may remove multi-value declarations after binding them. Keep
    // authored prompt membership separate from that mutable query definition.
    private enum PromptAction { Open, Refresh, Edit }
    private IReadOnlyList<ReportParameter>? _parameterDefinition;
    private bool _parameterPromptOpen;

    private bool CanPrompt(ReportParameter parameter) => parameter.IsInUse && (parameter.Required || PromptOptionalParameters);

    private bool IsSuppliedSessionParameter(string name)
    {
        var key = Options.SessionAutoInjectParameters.FirstOrDefault(key =>
            !string.IsNullOrWhiteSpace(key) && string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        return key is not null && ResolveSessionParameter(key) is not (null or DBNull);
    }

    private bool CanEditParameters
    {
        get
        {
            // A run that failed after its prompts were read (a query error, the row limit) keeps the action, so the
            // user can try other values; a load that never read them has no definition (ShowReportAsync clears it).
            if (_parameterDefinition is null || IsLoading || _parameterPromptOpen
                || string.IsNullOrEmpty(ReportFilePath)) return false;
            return _parameterDefinition.Any(p => CanPrompt(p) && !IsSuppliedSessionParameter(p.Name));
        }
    }
}
