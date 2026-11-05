using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Llm.CodeTools;

/// <summary>
/// Builds a Mermaid classDiagram from C# or Python source without a model.
/// The same source always produces the same diagram. At most 30 types and 24 members each.
/// </summary>
public static class StructuralClassDiagram
{
    public const int MaxTypes = 30;
    public const int MaxMembers = 24;

    public static bool TryCreate(string? language, string? code, out string mermaid)
    {
        mermaid = "";
        if (string.IsNullOrWhiteSpace(code)) return false;
        var types = Normalize(language) switch
        {
            "csharp" => ParseCSharp(code),
            "python" => ParsePython(code),
            _ => null
        };
        if (types is null || types.Count == 0) return false;
        mermaid = Render(types);
        return true;
    }

    private static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "";
        var text = language.Trim().ToLowerInvariant();
        return text switch
        {
            "c#" or "cs" or "csharp" or "c-sharp" => "csharp",
            "py" or "python" or "python3" => "python",
            _ => text
        };
    }

    private static List<TypeModel> ParseCSharp(string code)
    {
        var clean = StripCSharp(code);
        var types = new List<TypeModel>();
        var header = new Regex(@"\b(?:class|struct|interface|record)\s+(?:(?:class|struct)\s+)?(@?[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
        foreach (Match match in header.Matches(clean))
        {
            if (types.Count >= MaxTypes) break;
            var name = match.Groups[1].Value.TrimStart('@');
            if (types.Any(type => type.Name == name) || IsNoise(name)) continue;
            var cursor = match.Index + match.Length;
            var positional = ReadGroup(clean, ref cursor, '(', ')');
            SkipGenerics(clean, ref cursor);
            var bases = ReadBases(clean, ref cursor);
            var body = ReadGroup(clean, ref cursor, '{', '}');
            var model = new TypeModel(name);
            if (!string.IsNullOrEmpty(positional)) AddPositional(model, positional);
            foreach (var baseName in bases) model.Bases.Add(baseName);
            if (!string.IsNullOrEmpty(body)) AddCSharpMembers(model, body);
            types.Add(model);
        }

        Link(types);
        return types;
    }

    private static void AddCSharpMembers(TypeModel model, string body)
    {
        var depths = new int[body.Length];
        var depth = 0;
        for (var i = 0; i < body.Length; i++)
        {
            depths[i] = depth;
            if (body[i] == '{') depth++;
            else if (body[i] == '}') depth = Math.Max(0, depth - 1);
        }

        var member = new Regex(
            @"\b(?:public|private|protected|internal)\b([^;{}()]{0,180}?)\s+(@?[A-Za-z_][A-Za-z0-9_]*)\s*(\(|\{|=|;)",
            RegexOptions.Compiled);
        foreach (Match match in member.Matches(body))
        {
            if (model.Members.Count >= MaxMembers) break;
            if (match.Index >= depths.Length || depths[match.Index] != 0) continue;
            var head = match.Groups[1].Value;
            var name = match.Groups[2].Value.TrimStart('@');
            if (IsNoise(name) || name == model.Name) continue;
            var tokens = Tokens(head);
            while (tokens.Count > 0 && Modifiers.Contains(tokens[0])) tokens.RemoveAt(0);
            if (tokens.Count == 0) continue;
            var typeName = string.Join(" ", tokens);
            var method = match.Groups[3].Value == "(";
            model.Members.Add(new MemberModel(typeName, name, method));
        }
    }

    private static void AddPositional(TypeModel model, string positional)
    {
        foreach (var part in SplitArgs(positional))
        {
            if (model.Members.Count >= MaxMembers) break;
            var tokens = Tokens(part);
            while (tokens.Count > 0 && Modifiers.Contains(tokens[0])) tokens.RemoveAt(0);
            if (tokens.Count < 2) continue;
            model.Members.Add(new MemberModel(string.Join(" ", tokens.Take(tokens.Count - 1)), tokens[^1], false));
        }
    }

    private static List<TypeModel> ParsePython(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n');
        var types = new List<TypeModel>();
        for (var i = 0; i < lines.Length && types.Count < MaxTypes; i++)
        {
            var line = Uncomment(lines[i]);
            var match = Regex.Match(line, @"^(\s*)class\s+([A-Za-z_][A-Za-z0-9_]*)\s*(\(([^)]*)\))?\s*:");
            if (!match.Success) continue;
            var name = match.Groups[2].Value;
            if (types.Any(type => type.Name == name)) continue;
            var indent = match.Groups[1].Value.Length;
            var model = new TypeModel(name);
            if (match.Groups[4].Success)
            {
                foreach (var baseName in match.Groups[4].Value.Split(','))
                {
                    var trimmed = baseName.Trim();
                    if (trimmed.Length > 0 && trimmed != "object" && IsIdentifier(trimmed)) model.Bases.Add(trimmed);
                }
            }

            for (var j = i + 1; j < lines.Length; j++)
            {
                var raw = lines[j];
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var bodyIndent = raw.Length - raw.TrimStart().Length;
                if (bodyIndent <= indent) break;
                var text = Uncomment(raw).Trim();
                var def = Regex.Match(text, @"^def\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(");
                if (def.Success)
                {
                    var method = def.Groups[1].Value;
                    if (method == "__init__" || method.StartsWith('_')) continue;
                    if (model.Members.Count < MaxMembers) model.Members.Add(new MemberModel("", method, true));
                    continue;
                }

                var field = Regex.Match(text, @"^self\.([A-Za-z_][A-Za-z0-9_]*)\s*=");
                if (field.Success && !field.Groups[1].Value.StartsWith('_') && model.Members.Count < MaxMembers
                    && model.Members.All(member => member.Name != field.Groups[1].Value))
                {
                    model.Members.Add(new MemberModel("", field.Groups[1].Value, false));
                }
            }

            types.Add(model);
        }

        Link(types);
        return types;
    }

    private static void Link(List<TypeModel> types)
    {
        var names = new HashSet<string>(types.Select(type => type.Name), StringComparer.Ordinal);
        foreach (var type in types)
        {
            foreach (var member in type.Members)
            {
                foreach (Match id in Regex.Matches(member.Type, @"[A-Za-z_][A-Za-z0-9_]*"))
                {
                    var target = id.Value;
                    if (target == type.Name || !names.Contains(target) || Primitives.Contains(target)) continue;
                    if (type.Relations.All(relation => relation.Target != target))
                        type.Relations.Add((target, member.Name));
                }
            }
        }
    }

    private static string Render(List<TypeModel> types)
    {
        var builder = new StringBuilder();
        builder.AppendLine("classDiagram");
        foreach (var type in types)
        {
            builder.Append("    class ").Append(type.Name).AppendLine(" {");
            foreach (var member in type.Members)
            {
                var typeName = member.Type.Replace('<', '~').Replace('>', '~').Trim();
                builder.Append("        +");
                if (typeName.Length > 0) builder.Append(typeName).Append(' ');
                builder.Append(member.Name);
                if (member.Method) builder.Append("()");
                builder.AppendLine();
            }

            builder.AppendLine("    }");
        }

        foreach (var type in types)
        {
            foreach (var baseName in type.Bases.Where(name => !Primitives.Contains(name)))
                builder.Append("    ").Append(type.Name).Append(" --|> ").AppendLine(baseName);
            foreach (var relation in type.Relations)
                builder.Append("    ").Append(type.Name).Append(" --> ").Append(relation.Target).Append(" : ").AppendLine(relation.Label);
        }

        return builder.ToString().TrimEnd();
    }

    private static string StripCSharp(string code)
    {
        var builder = new StringBuilder(code.Length);
        var mode = 0;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            var next = i + 1 < code.Length ? code[i + 1] : '\0';
            if (mode == 0)
            {
                if (c == '/' && next == '/') { mode = 1; builder.Append(' '); builder.Append(' '); i++; continue; }
                if (c == '/' && next == '*') { mode = 2; builder.Append(' '); builder.Append(' '); i++; continue; }
                if (c == '@' && next == '"') { mode = 4; builder.Append(' '); builder.Append(' '); i++; continue; }
                if (c == '"') { mode = 3; builder.Append(' '); continue; }
                if (c == '\'') { mode = 5; builder.Append(' '); continue; }
                builder.Append(c);
            }
            else if (mode == 1)
            {
                builder.Append(c == '\n' ? '\n' : ' ');
                if (c == '\n') mode = 0;
            }
            else if (mode == 2)
            {
                builder.Append(c == '\n' ? '\n' : ' ');
                if (c == '*' && next == '/') { builder.Append(' '); i++; mode = 0; }
            }
            else if (mode == 3)
            {
                builder.Append(c == '\n' ? '\n' : ' ');
                if (c == '\\') { if (i + 1 < code.Length) { builder.Append(' '); i++; } continue; }
                if (c == '"') mode = 0;
            }
            else if (mode == 4)
            {
                builder.Append(c == '\n' ? '\n' : ' ');
                if (c == '"' && next == '"') { builder.Append(' '); i++; continue; }
                if (c == '"') mode = 0;
            }
            else
            {
                builder.Append(c == '\n' ? '\n' : ' ');
                if (c == '\\') { if (i + 1 < code.Length) { builder.Append(' '); i++; } continue; }
                if (c == '\'') mode = 0;
            }
        }

        return builder.ToString();
    }

    private static string Uncomment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }

            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '#') return line[..i];
        }

        return line;
    }

    private static void SkipGenerics(string text, ref int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
        if (cursor < text.Length && text[cursor] == '<') ReadGroup(text, ref cursor, '<', '>');
    }

    private static List<string> ReadBases(string text, ref int cursor)
    {
        var names = new List<string>();
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
        if (cursor >= text.Length || text[cursor] != ':') return names;
        cursor++;
        var start = cursor;
        while (cursor < text.Length && text[cursor] is not ('{' or ';')) cursor++;
        var clause = text[start..cursor];
        var where = clause.IndexOf(" where ", StringComparison.Ordinal);
        if (where >= 0) clause = clause[..where];
        foreach (var part in clause.Split(','))
        {
            var word = Tokens(part).FirstOrDefault(token => IsIdentifier(token.TrimEnd('?')));
            if (word is null) continue;
            word = word.TrimEnd('?');
            if (!Primitives.Contains(word) && IsIdentifier(word)) names.Add(word);
        }

        return names;
    }

    private static string ReadGroup(string text, ref int cursor, char open, char close)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
        if (cursor >= text.Length || text[cursor] != open) return "";
        var depth = 0;
        var start = cursor + 1;
        for (; cursor < text.Length; cursor++)
        {
            if (text[cursor] == open) depth++;
            else if (text[cursor] == close)
            {
                depth--;
                if (depth == 0)
                {
                    var body = text[start..cursor];
                    cursor++;
                    return body;
                }
            }
        }

        return text[start..];
    }

    private static List<string> Tokens(string text)
    {
        var list = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                if (text[i] == '<')
                {
                    var depth = 0;
                    do
                    {
                        if (text[i] == '<') depth++;
                        else if (text[i] == '>') depth--;
                        i++;
                    }
                    while (i < text.Length && depth > 0);
                }
                else i++;
            }

            list.Add(text[start..i]);
        }

        return list;
    }

    private static IEnumerable<string> SplitArgs(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i == text.Length ? ',' : text[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                var part = text[start..i].Trim();
                if (part.Length > 0) yield return part;
                start = i + 1;
            }
        }
    }

    private static bool IsIdentifier(string value) => Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");

    private static bool IsNoise(string name) =>
        name is "if" or "for" or "while" or "switch" or "catch" or "return" or "new" or "get" or "set" or "init" or "class" or "where";

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "virtual", "override", "abstract",
        "sealed", "async", "required", "new", "partial", "readonly", "unsafe", "extern", "volatile",
        "const", "event", "ref", "out", "in", "params"
    };

    private static readonly HashSet<string> Primitives = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong",
        "short", "ushort", "object", "string", "void", "var", "dynamic", "nint", "nuint",
        "List", "Dictionary", "IList", "IEnumerable", "ICollection", "IReadOnlyList", "Task", "ValueTask",
        "Nullable", "Action", "Func", "Span", "ReadOnlySpan", "str", "int", "float", "bool", "list", "dict"
    };

    private sealed class TypeModel(string name)
    {
        public string Name { get; } = name;
        public List<MemberModel> Members { get; } = [];
        public List<string> Bases { get; } = [];
        public List<(string Target, string Label)> Relations { get; } = [];
    }

    private sealed record MemberModel(string Type, string Name, bool Method);
}
