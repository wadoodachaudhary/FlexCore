namespace Fx.ControlKit.Reports;

public sealed partial class CrystalFormula
{
    // Separate from the strict scalar selector. Every AND term must be a
    // proven parameter shape or pure typed literal residual. Unsupported trees
    // reject the entire plan; the complete formula always remains in C#.
    internal bool TryGetBoundedSelections(out IReadOnlyList<(bool Date, string Field, string Parameter)> selections,
        out IReadOnlyList<(string Field, string Operator, object Literal)> residuals)
    {
        var result = new List<(bool, string, string)>();
        var remaining = new List<(string, string, object)>();
        static bool TryLiteral(Node node, out object value)
        {
            if (node is Literal { Value: string or decimal or bool } literal)
            {
                value = literal.Value;
                return true;
            }
            // The parser represents only these Boolean constants as symbols.
            // Other symbols can depend on report state or null-policy directives.
            if (node is Symbol symbol && (symbol.Name.Equals("true", StringComparison.OrdinalIgnoreCase)
                || symbol.Name.Equals("false", StringComparison.OrdinalIgnoreCase)))
            {
                value = symbol.Name.Equals("true", StringComparison.OrdinalIgnoreCase);
                return true;
            }
            value = null!;
            return false;
        }
        var pending = new Stack<Node>();
        pending.Push(_root);
        while (pending.TryPop(out var node))
        {
            if (node is Binary conjunction && conjunction.Steps.All(step => step.Op == "and"))
            {
                foreach (var step in conjunction.Steps.Reverse()) pending.Push(step.Right);
                pending.Push(conjunction.First);
                continue;
            }
            if (node is Binary { First: Call call, Steps: [{ Op: ">=", Right: Reference date }] }
                && call.Name.Equals("Date", StringComparison.OrdinalIgnoreCase)
                && call.Args is [Reference field] && date.Name.StartsWith("{?", StringComparison.Ordinal))
            {
                result.Add((true, field.Name, date.Name));
                continue;
            }
            if (node is Binary { First: Reference left, Steps: [{ Op: "=", Right: Reference right }] })
            {
                var first = left.Name;
                var second = right.Name;
                if (first.StartsWith("{?", StringComparison.Ordinal)) (first, second) = (second, first);
                if (!first.StartsWith("{?", StringComparison.Ordinal) && second.StartsWith("{?", StringComparison.Ordinal))
                {
                    result.Add((false, first, second));
                    continue;
                }
            }
            // Pure literal terms may remain exclusively in the runtime AST.
            // The loader must additionally prove their field identity and type;
            // never admit a call, directive, formula wrapper or arbitrary subtree.
            if (node is Binary { First: Reference residualField, Steps: [{ Op: "=" or ">", Right: var literal }] } residual
                && TryLiteral(literal, out var value))
            {
                remaining.Add((residualField.Name, residual.Steps[0].Op, value));
                continue;
            }
            if (node is Binary { First: var reverseLiteral, Steps: [{ Op: "=" or "<", Right: Reference reverseField }] } reversed
                && TryLiteral(reverseLiteral, out var reverseValue))
            {
                remaining.Add((reverseField.Name, reversed.Steps[0].Op == "<" ? ">" : "=", reverseValue));
                continue;
            }
            selections = [];
            residuals = [];
            return false;
        }
        selections = result;
        residuals = remaining;
        return result.Count > 0;
    }

    internal bool TryGetNullOrEmptyStringReplacement(out string field, out string replacement)
    {
        field = replacement = "";
        if (_root is not Conditional
            {
                Test: Binary
                {
                    First: Call nullTest,
                    Steps: [{ Op: "or", Right: Binary { First: Reference emptyField, Steps: [{ Op: "=", Right: Literal { Value: string empty } }] } }]
                },
                Yes: Literal { Value: string text }, No: Reference value
            }
            || !nullTest.Name.Equals("IsNull", StringComparison.OrdinalIgnoreCase)
            || nullTest.Args is not [Reference nullField] || empty.Length != 0
            || !nullField.Name.Equals(emptyField.Name, StringComparison.OrdinalIgnoreCase)
            || !value.Name.Equals(emptyField.Name, StringComparison.OrdinalIgnoreCase)) return false;
        field = value.Name;
        replacement = text;
        return true;
    }
}
