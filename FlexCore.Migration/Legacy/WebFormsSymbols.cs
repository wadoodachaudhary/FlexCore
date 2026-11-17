namespace Fx.ControlKit.Migration;

internal sealed class ControlSymbol
{
    public required string Id { get; init; }
    public required string Field { get; init; }
    public required string Kind { get; init; }
    public string? ItemType { get; init; }
    public string? DataTextField { get; init; }
    public string? DataValueField { get; init; }
    public string? SelectMethod { get; init; }
    public string? CssClass { get; init; }
    public string? TextExpression { get; init; }
    public string? LiteralText { get; init; }
    public string? OnClick { get; init; }
    public bool ReadOnly { get; init; }
    public string? ToolTip { get; init; }
    public string? NavigateExpression { get; init; }
    public bool ClassMutated { get; set; }
    public bool HrefMutated { get; set; }
    public bool ItemsMutated { get; set; }
    public List<ValidatorSymbol> Validators { get; } = [];
    public string ItemsField => Field + "Items";
    public string ClassField => Field + "Class";
    public string HrefField => Field + "Href";
}

internal sealed class ValidatorSymbol
{
    public required string Kind { get; init; }
    public string? Message { get; init; }
    public string? Type { get; init; }
    public string? Minimum { get; init; }
    public string? Maximum { get; init; }
    public string? Pattern { get; init; }
    public string? Control { get; init; }
}

internal sealed class TranslatedPage
{
    public string Code { get; init; } = "";
    public List<MigrationTodo> Todos { get; init; } = [];
    public List<(string Type, string Name)> Services { get; init; } = [];
    public List<(string Name, string ClrType, bool Optional)> Parameters { get; init; } = [];
    public bool NeedsNavigation { get; init; }
    public bool NeedsValidation { get; init; }
}

internal static class CSharpIds
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "virtual", "void", "volatile", "while", "add", "alias", "async", "await", "dynamic",
        "from", "get", "global", "group", "into", "join", "let", "nameof", "on", "orderby", "partial", "record", "remove",
        "select", "set", "value", "var", "when", "where", "yield", "required", "init", "file", "nint", "nuint"
    };

    public static string Field(string id)
    {
        if (string.IsNullOrEmpty(id)) return "control";
        var name = char.IsDigit(id[0]) ? "_" + id : id;
        return Keywords.Contains(name) ? name + "Value" : name;
    }

    public static string Quote(string value) => "\"" + value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
