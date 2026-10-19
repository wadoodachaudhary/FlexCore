namespace Fx.ControlKit.Reports;

public sealed partial class CrystalFormula
{
    internal sealed record DesignerSqlValue(string Sql, CrystalValueType Type, bool Predicate = false);

    // This is a bounded, designer-only SQL preview. It does not change native
    // candidate selection or replace Crystal evaluation with SQL semantics.
    internal DesignerSqlValue DesignerSql(Func<string, DesignerSqlValue> reference,
        Func<object, DesignerSqlValue> literal)
    {
        if (IsEmpty) return literal(true);
        return Emit(_root, 0);

        DesignerSqlValue Emit(Node node, int depth)
        {
            if (depth > 64) throw Unsupported();
            switch (node)
            {
                case Reference field: return reference(field.Name);
                case Literal { Value: string or decimal or bool } constant: return literal(constant.Value);
                case Symbol symbol when symbol.Name.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || symbol.Name.Equals("false", StringComparison.OrdinalIgnoreCase):
                    return literal(symbol.Name.Equals("true", StringComparison.OrdinalIgnoreCase));
                case Unary unary:
                {
                    var value = Emit(unary.Value, depth + 1);
                    if (unary.Op == "not") return new("(NOT " + DesignerSqlPredicate(value) + ")", new(CrystalBaseType.Boolean), true);
                    if (unary.Op is "+" or "-" && Numeric(value.Type)) return value with { Sql = "(" + unary.Op + value.Sql + ")" };
                    throw Unsupported();
                }
                case Binary binary:
                {
                    var value = Emit(binary.First, depth + 1);
                    foreach (var step in binary.Steps)
                    {
                        var right = Emit(step.Right, depth + 1);
                        if (step.Op is "and" or "or")
                        {
                            value = new("(" + DesignerSqlPredicate(value) + " " + step.Op.ToUpperInvariant() + " " + DesignerSqlPredicate(right) + ")", new(CrystalBaseType.Boolean), true);
                            continue;
                        }
                        if (step.Op is "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=")
                        {
                            if (value.Predicate || right.Predicate || value.Type.IsRange || right.Type.IsRange
                                || !Compatible(value.Type, right.Type)) throw Unsupported();
                            if (value.Type.IsArray || right.Type.IsArray)
                            {
                                if (step.Op != "=" || value.Type.IsArray && right.Type.IsArray) throw Unsupported();
                                if (value.Type.IsArray) (value, right) = (right, value);
                                if (!right.Sql.StartsWith('@')) throw Unsupported();
                            }
                            // Keep the ordinary placeholder shape used by the report
                            // parameter-list expander; no choices exist in the designer.
                            value = new("(" + value.Sql + " " + (step.Op == "!=" ? "<>" : step.Op) + " " + right.Sql + ")", new(CrystalBaseType.Boolean), true);
                            continue;
                        }
                        if (value.Type.IsArray || right.Type.IsArray || value.Type.IsRange || right.Type.IsRange) throw Unsupported();
                        if (step.Op == "+" && value.Type.BaseType == CrystalBaseType.String && right.Type.BaseType == CrystalBaseType.String
                            || step.Op is "+" or "-" or "*" or "/" && Numeric(value.Type) && Numeric(right.Type))
                        {
                            value = value with { Sql = "(" + value.Sql + " " + step.Op + " " + right.Sql + ")" };
                            continue;
                        }
                        throw Unsupported();
                    }
                    return value;
                }
                default: throw Unsupported();
            }
        }
        static bool Numeric(CrystalValueType type) => !type.IsArray && !type.IsRange && type.BaseType is CrystalBaseType.Number or CrystalBaseType.Currency;
        static bool Compatible(CrystalValueType left, CrystalValueType right) => left.BaseType == right.BaseType
            || left.BaseType is CrystalBaseType.Number or CrystalBaseType.Currency && right.BaseType is CrystalBaseType.Number or CrystalBaseType.Currency;
        static NotSupportedException Unsupported() => new("This expression requires the native report engine; complete SQL generation is unavailable for this formula.");
    }

    internal static string DesignerSqlPredicate(DesignerSqlValue value)
    {
        if (value.Type.BaseType != CrystalBaseType.Boolean || value.Type.IsArray || value.Type.IsRange)
            throw new NotSupportedException("The selection must be Boolean; keep this expression in the native report engine.");
        return value.Predicate ? value.Sql : "(" + value.Sql + " = 1)";
    }
}
