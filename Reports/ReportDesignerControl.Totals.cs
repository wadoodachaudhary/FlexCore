using System.Text.Json;

namespace Fx.ControlKit.Reports;

public partial class ReportDesignerControl
{
    private List<ReportDesignerRunningTotal> _expertRunningTotals = [];
    private string _totalError = "";
    private static readonly string[] TotalConditions = ["NoCondition", "OnChangeOfField", "OnChangeOfGroup", "UseFormula"];
    private ReportDesignerRunningTotal? ActiveRunningTotal => GetActiveExpertItem(_expertRunningTotals);
    private static List<ReportDesignerRunningTotal> CloneTotals(List<ReportDesignerRunningTotal> totals) => JsonSerializer.Deserialize<List<ReportDesignerRunningTotal>>(JsonSerializer.Serialize(totals))!;

    private Task AddRunningTotalAsync()
    {
        var index = 1;
        while (_expertRunningTotals.Any(t => t.Name.Equals("RunningTotal" + index, StringComparison.OrdinalIgnoreCase))) index++;
        _expertRunningTotals.Add(new() { Name = "RunningTotal" + index, Field = AllDesignerFields.FirstOrDefault()?.Reference ?? "" });
        _activeExpertItemIndex = _expertRunningTotals.Count - 1;
        return Task.CompletedTask;
    }

    private Task DeleteRunningTotalAsync()
    {
        if (ActiveRunningTotal is not { } total) return Task.CompletedTask;
        if (_document.Elements.Any(e => e.Binding == total.Binding || e.Visual.Runs.Any(r => r.Binding == total.Binding)) ||
            _document.Fields.Any(f => f.Expression.Contains(total.Binding, StringComparison.OrdinalIgnoreCase)))
        { _totalError = "Remove this total's report objects and formula references before deleting it."; return Task.CompletedTask; }
        _expertRunningTotals.Remove(total);
        return Task.CompletedTask;
    }

    private bool ApplyRunningTotals()
    {
        _totalError = "";
        try
        {
            if (_expertRunningTotals.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _expertRunningTotals.Count)
                throw new InvalidOperationException("Running total names must be unique.");
            var functions = CrystalXmlReportLoader.CompileCustomFunctions(_document.SourceDocument?.Root, []);
            foreach (var total in _expertRunningTotals)
            {
                if (string.IsNullOrWhiteSpace(total.Name) || total.Name.IndexOfAny(['{', '}', '#']) >= 0 || string.IsNullOrWhiteSpace(total.Field))
                    throw new InvalidOperationException("Every running total needs a valid name and field.");
                foreach (var condition in new[] { total.Evaluation, total.Reset })
                {
                    if (condition.Type == "OnChangeOfField" && string.IsNullOrWhiteSpace(condition.Field)) throw new InvalidOperationException("Choose a condition field.");
                    if (condition.Type == "OnChangeOfGroup" && (condition.Group < 1 || condition.Group > _document.Groups.Count)) throw new InvalidOperationException("Choose a valid condition group number.");
                    if (condition.Type == "UseFormula")
                    {
                        if (string.IsNullOrWhiteSpace(condition.Formula)) throw new InvalidOperationException("Enter a condition formula.");
                        var formula = CrystalFormula.Compile(condition.Formula, condition.FormulaSyntax, false, functions);
                        // Local variables (and Basic's Formula result) are first-pass; global and shared ones carry print state.
                        if (formula.UsesPersistentVariables || formula.UsesPageContext || formula.UsesAggregates || formula.RequiresPrintPass)
                            throw new InvalidOperationException("Running-total conditions require first-pass formulas.");
                    }
                }
            }
            _document.RunningTotals = CloneTotals(_expertRunningTotals);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or NotSupportedException)
        { _totalError = ex.Message; return false; }
    }
}
