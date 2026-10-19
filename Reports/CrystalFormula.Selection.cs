namespace Fx.ControlKit.Reports;

public sealed partial class CrystalFormula
{
    /// <summary>
    /// Recognizes only a complete AND tree of direct reference equalities. This is
    /// shape inspection of the existing AST, not a formula-to-SQL translator.
    /// The caller must additionally prove field/parameter identity and scalar types.
    /// </summary>
    internal bool TryGetParameterEqualities(out IReadOnlyList<(string Field, string Parameter)> equalities)
    {
        var result = new List<(string Field, string Parameter)>();
        var pending = new Stack<Node>();
        pending.Push(_root);
        while (pending.TryPop(out var node))
        {
            if (node is Binary conjunction && conjunction.Steps.All(step => step.Op == "and"))
            {
                for (var index = conjunction.Steps.Length - 1; index >= 0; index--)
                    pending.Push(conjunction.Steps[index].Right);
                pending.Push(conjunction.First);
                continue;
            }
            if (node is Binary { First: Reference left, Steps: [{ Op: "=", Right: Reference right }] })
            {
                var first = left.Name;
                var second = right.Name;
                if (first.StartsWith("{?", StringComparison.Ordinal)) (first, second) = (second, first);
                if (!first.StartsWith("{?", StringComparison.Ordinal) && second.StartsWith("{?", StringComparison.Ordinal))
                {
                    result.Add((first, second));
                    continue;
                }
            }
            // OR/NOT, sequences/directives, coercions, functions, constants, range
            // expressions, variables and formula indirection remain runtime-only.
            equalities = [];
            return false;
        }
        equalities = result;
        return result.Count > 0;
    }
}
