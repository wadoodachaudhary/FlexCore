namespace Fx.ControlKit.Reports;

public sealed partial class CrystalFormula
{
    // Refreshing a query can promote a saved Number field to Currency. Follow the result expression,
    // not every reference: Count(money), ToNumber(money), and a declared NumberVar remain numbers.
    internal bool HasCurrencyResult(Func<string, bool> currencyReference) => Currency(_root, currencyReference, 0);

    private static bool Currency(Node node, Func<string, bool> reference, int depth)
    {
        if (++depth > MaxNesting) return false;
        bool Result(Node child) => Currency(child, reference, depth);
        return node switch
        {
            Reference r => reference(r.Name),
            Variable v => v.Binding.Type == "currencyvar",
            VarRef v => v.Binding.Type == "currencyvar",
            Assign a => a.Binding.Type == "currencyvar",
            Unary u when u.Op is "+" or "-" => Result(u.Value),
            Binary b when b.Steps.All(s => s.Op is "+" or "-") => Result(b.First) || b.Steps.Any(s => Result(s.Right)),
            Sequence s when s.Nodes.Length > 0 => Result(s.Nodes[^1]),
            Conditional c => Result(c.Yes) || Result(c.No),
            Select s => s.Cases.Any(c => Result(c.Body)) || Result(s.Otherwise),
            Call c => c.Name.ToLowerInvariant() switch
            {
                "ccur" or "currency" or "tocurrency" => true,
                "sum" or "average" or "minimum" or "maximum" or "previous" or "next" or "previousvalue" or "nextvalue"
                    => c.Args.Length > 0 && Result(c.Args[0]),
                "iif" => c.Args.Skip(1).Any(Result),
                _ => false
            },
            _ => false
        };
    }
}
