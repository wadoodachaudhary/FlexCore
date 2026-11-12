namespace Fx.ControlKit.Reports;

public partial class ReportWriterControl
{
    // Only the path-based workflow owns an editable prompt definition. Direct
    // SQL/table/PDF loads must not retain a previous report's toolbar action.
    // Execution may remove multi-value declarations after binding them. Keep
    // authored prompt membership separate from that mutable query definition.
    private sealed record ParameterPromptDefinition(string Sql, IReadOnlyList<ReportParameter> Parameters);
    private ParameterPromptDefinition? _parameterDefinition;
    private bool _parameterPromptOpen;

    private bool CanEditParameters
    {
        get
        {
            // A run that failed after its prompts were read (a query error, the row limit) keeps the action, so the
            // user can try other values; a load that never read them has no definition (ShowReportAsync clears it).
            if (_parameterDefinition is null || IsLoading || _parameterPromptOpen
                || string.IsNullOrEmpty(ReportFilePath)) return false;
            var context = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            AutoInjectSessionParameters(context, _parameterDefinition.Sql, _parameterDefinition.Parameters, authoredDefaults: false);
            // Explicit caller values are editable; configured session context
            // is supplied silently, just as it is in the initial prompt.
            return _parameterDefinition.Parameters.Any(p => p.IsInUse && (p.Required || PromptOptionalParameters)
                && !context.ContainsKey(p.Name));
        }
    }
}
