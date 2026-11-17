namespace Fx.ControlKit.Migration;

internal enum AspxKind { Element, Text, Expression, Comment }

internal sealed class AspxDirective
{
    public string Name { get; init; } = "";
    public List<(string Name, string Value)> Attributes { get; init; } = [];
    public string? Attribute(string name) => Attributes.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}

internal sealed class AspxNode
{
    public AspxKind Kind { get; init; }
    public string Name { get; init; } = "";
    public bool SelfClosing { get; init; }
    public List<(string Name, string Value)> Attributes { get; init; } = [];
    public string Text { get; init; } = "";
    public List<AspxNode> Children { get; init; } = [];
    public string? Attribute(string name) => Attributes.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    public bool HasAttribute(string name) => Attributes.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

internal sealed class AspxDocument
{
    public required IReadOnlyList<AspxDirective> Directives { get; init; }
    public required AspxNode Root { get; init; }
    public AspxDirective? Directive(string name) => Directives.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Parses ASPX, ASCX, and master markup, including data-binding expressions that are not well-formed XML.</summary>
internal static class AspxSyntax
{
    public static AspxDocument Parse(string text)
    {
        text = StripComments(text);
        var directives = new List<AspxDirective>();
        text = TakeDirectives(text, directives);
        var nodes = ParseChildren(text, 0, null, out _);
        return new AspxDocument
        {
            Directives = directives,
            Root = new AspxNode { Kind = AspxKind.Element, Name = "#root", Children = nodes }
        };
    }

    private static string StripComments(string text)
    {
        var output = new System.Text.StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf("<%--", index, StringComparison.Ordinal);
            if (start < 0)
            {
                output.Append(text, index, text.Length - index);
                break;
            }
            output.Append(text, index, start - index);
            var end = text.IndexOf("--%>", start + 4, StringComparison.Ordinal);
            index = end < 0 ? text.Length : end + 4;
        }
        return output.ToString();
    }

    private static string TakeDirectives(string text, List<AspxDirective> directives)
    {
        var output = new System.Text.StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            var start = text.IndexOf("<%@", index, StringComparison.Ordinal);
            if (start < 0)
            {
                output.Append(text, index, text.Length - index);
                break;
            }
            output.Append(text, index, start - index);
            var end = text.IndexOf("%>", start + 3, StringComparison.Ordinal);
            if (end < 0) break;
            directives.Add(ParseDirective(text[(start + 3)..end]));
            index = end + 2;
        }
        return output.ToString();
    }

    private static AspxDirective ParseDirective(string body)
    {
        var index = 0;
        Skip(body, ref index);
        var name = ReadName(body, ref index);
        return new AspxDirective { Name = name, Attributes = ReadAttributes(body, ref index, '@') };
    }

    private static List<AspxNode> ParseChildren(string text, int start, string? untilName, out int index)
    {
        var nodes = new List<AspxNode>();
        index = start;
        while (index < text.Length)
        {
            if (untilName is not null && LookingAtClose(text, index, untilName))
                return nodes;
            if (StartsWith(text, index, "<%"))
            {
                var end = text.IndexOf("%>", index + 2, StringComparison.Ordinal);
                if (end < 0) end = text.Length - 2;
                nodes.Add(new AspxNode { Kind = AspxKind.Expression, Text = text[(index + 2)..end].Trim() });
                index = Math.Min(text.Length, end + 2);
                continue;
            }
            if (text[index] == '<' && IsTagStart(text, index))
            {
                if (StartsWith(text, index, "<!--"))
                {
                    var end = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
                    var comment = end < 0 ? text[index..] : text[index..(end + 3)];
                    nodes.Add(new AspxNode { Kind = AspxKind.Comment, Text = comment });
                    index = end < 0 ? text.Length : end + 3;
                    continue;
                }
                if (StartsWith(text, index, "<!"))
                {
                    var end = text.IndexOf('>', index);
                    nodes.Add(new AspxNode { Kind = AspxKind.Text, Text = end < 0 ? text[index..] : text[index..(end + 1)] });
                    index = end < 0 ? text.Length : end + 1;
                    continue;
                }
                var element = ParseElement(text, ref index);
                if (element is null) continue;
                if (element.Name.StartsWith('/'))
                {
                    if (untilName is not null && element.Name[1..].Equals(untilName, StringComparison.OrdinalIgnoreCase))
                        return nodes;
                    continue;
                }
                nodes.Add(element);
                continue;
            }
            var next = NextSpecial(text, index);
            nodes.Add(new AspxNode { Kind = AspxKind.Text, Text = text[index..next] });
            index = next;
        }
        return nodes;
    }

    private static AspxNode? ParseElement(string text, ref int index)
    {
        var cursor = index + 1;
        if (cursor < text.Length && text[cursor] == '/')
        {
            cursor++;
            var closing = ReadName(text, ref cursor);
            var end = text.IndexOf('>', cursor);
            index = end < 0 ? text.Length : end + 1;
            return new AspxNode { Kind = AspxKind.Element, Name = "/" + closing, SelfClosing = true };
        }
        var name = ReadName(text, ref cursor);
        if (name.Length == 0)
        {
            index++;
            return new AspxNode { Kind = AspxKind.Text, Text = "<" };
        }
        var attributes = ReadAttributes(text, ref cursor, '>');
        var selfClosing = false;
        if (cursor < text.Length && text[cursor] == '/')
        {
            selfClosing = true;
            cursor++;
        }
        if (cursor < text.Length && text[cursor] == '>') cursor++;
        index = cursor;
        var children = new List<AspxNode>();
        if (!selfClosing && !IsVoid(name))
            children = ParseChildren(text, index, name, out index);
        return new AspxNode { Kind = AspxKind.Element, Name = name, SelfClosing = selfClosing, Attributes = attributes, Children = children };
    }

    private static List<(string Name, string Value)> ReadAttributes(string text, ref int index, char terminator)
    {
        var attributes = new List<(string, string)>();
        while (index < text.Length)
        {
            Skip(text, ref index);
            if (index >= text.Length) break;
            if (text[index] == terminator || text[index] == '/' || text[index] == '>') break;
            if (text[index] == '%') break;
            var name = ReadName(text, ref index);
            if (name.Length == 0)
            {
                index++;
                continue;
            }
            Skip(text, ref index);
            if (index < text.Length && text[index] == '=')
            {
                index++;
                Skip(text, ref index);
                attributes.Add((name, ReadValue(text, ref index)));
            }
            else attributes.Add((name, ""));
        }
        return attributes;
    }

    private static string ReadValue(string text, ref int index)
    {
        if (index >= text.Length) return "";
        var quote = text[index];
        if (quote is '"' or '\'')
        {
            index++;
            var start = index;
            var output = new System.Text.StringBuilder();
            while (index < text.Length)
            {
                if (StartsWith(text, index, "<%"))
                {
                    var end = text.IndexOf("%>", index + 2, StringComparison.Ordinal);
                    if (end < 0) break;
                    index = end + 2;
                    continue;
                }
                if (text[index] == quote)
                {
                    output.Append(text, start, index - start);
                    index++;
                    return output.ToString();
                }
                index++;
            }
            return text[start..];
        }
        var plain = index;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] is not '>' and not '/')
            index++;
        return text[plain..index];
    }

    private static bool LookingAtClose(string text, int index, string name)
    {
        if (!StartsWith(text, index, "</")) return false;
        var cursor = index + 2;
        var closing = ReadName(text, ref cursor);
        return closing.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTagStart(string text, int index)
    {
        if (index + 1 >= text.Length) return false;
        var next = text[index + 1];
        return next is '/' or '!' or '?' || char.IsLetter(next);
    }

    private static int NextSpecial(string text, int index)
    {
        var cursor = index + 1;
        while (cursor < text.Length && text[cursor] != '<') cursor++;
        return cursor;
    }

    private static string ReadName(string text, ref int index)
    {
        var start = index;
        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is ':' or '_' or '-' or '.'))
            index++;
        return text[start..index];
    }

    private static void Skip(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
    }

    private static bool StartsWith(string text, int index, string value) =>
        index + value.Length <= text.Length && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static bool IsVoid(string name) => name.Equals("br", StringComparison.OrdinalIgnoreCase)
        || name.Equals("hr", StringComparison.OrdinalIgnoreCase)
        || name.Equals("img", StringComparison.OrdinalIgnoreCase)
        || name.Equals("image", StringComparison.OrdinalIgnoreCase)
        || name.Equals("meta", StringComparison.OrdinalIgnoreCase)
        || name.Equals("link", StringComparison.OrdinalIgnoreCase)
        || name.Equals("input", StringComparison.OrdinalIgnoreCase);
}
