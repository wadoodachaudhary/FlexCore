using System.Data;

namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    private readonly HashSet<string> _queryCurrencies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _currencyResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _currencyResolving = new(StringComparer.OrdinalIgnoreCase);

    // A money column prints as currency only where the report saves no type for its field. A field the report
    // types keeps that type, as Crystal prints it: ODBC saves SQL Server's money as decimalField, so Open PO's
    // prints its totals without a currency symbol.
    private void CaptureCurrencySchema(DataTable data)
    {
        var typed = new HashSet<string>(_layout.Document.Fields
            .Where(field => !field.IsFormula && CrystalValueType.FromXml(field.Type) is not null)
            .Select(field => field.Reference), StringComparer.OrdinalIgnoreCase);
        foreach (var (reference, alias) in _layout.Bindings)
            if (!typed.Contains(reference.Trim()) && data.Columns.Contains(alias) && ReportDataSchema.IsCurrency(data.Columns[alias]!))
                _queryCurrencies.Add(reference);
    }

    private bool CurrencyResult(string reference)
    {
        if (_currencyResults.TryGetValue(reference, out var cached)) return cached;
        if (!_currencyResolving.Add(reference)) return false;
        try
        {
            var currency = _queryCurrencies.Contains(reference)
                || ReferenceType(reference)?.BaseType == CrystalBaseType.Currency;
            if (!currency && _layout.Formulas.TryGetValue(reference, out var formula))
                currency = formula.HasCurrencyResult(CurrencyResult);
            if (!currency && (_layout.Summaries.TryGetValue(reference, out var summary) || _layout.RunningTotals.TryGetValue(reference, out summary)))
                currency = summary.Operation.ToLowerInvariant() is "sum" or "average" or "avg" or "minimum" or "maximum"
                    && CurrencyResult(summary.Field);
            return _currencyResults[reference] = currency;
        }
        finally { _currencyResolving.Remove(reference); }
    }
}
