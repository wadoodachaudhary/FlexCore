using System.Text;
using System.Text.Json;

namespace Fx.ControlKit.Llm;

/// <summary>
/// Recovers a JSON object from model output that is not quite JSON: strips
/// code fences and prose around the object, then repairs truncation (an
/// unterminated string, a dangling key, unclosed arrays and objects) so a
/// response cut off at the token cap still parses. For output that cannot be
/// repaired, <see cref="ExtractLooseString"/> and <see cref="ExtractLooseArray"/>
/// pull named properties out of the raw text.
/// </summary>
public static class StructuredJson
{
    /// <summary>The JSON object text inside <paramref name="raw"/>: the body of a code fence, or the first balanced <c>{…}</c>; the tail from the first <c>{</c> when unbalanced.</summary>
    public static string ExtractObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = json.IndexOf('\n');
            if (firstNewline >= 0) json = json[(firstNewline + 1)..];
            var fenceEnd = json.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) json = json[..fenceEnd];
            json = json.Trim();
            if (!json.StartsWith('{'))
            {
                var inner = json.IndexOf('{');
                if (inner >= 0) json = json[inner..];
            }
        }
        else
        {
            var start = json.IndexOf('{');
            if (start >= 0)
            {
                var end = FindObjectEnd(json, start);
                json = end > start ? json[start..(end + 1)] : json[start..];
            }
        }

        return json;
    }

    /// <summary>Extracts, parses, and on failure repairs and parses again.</summary>
    public static bool TryParse(string raw, out JsonDocument? document)
    {
        document = null;
        var json = ExtractObject(raw);
        if (TryParseCore(json, out document)) return true;

        var repaired = Repair(json);
        return !string.IsNullOrWhiteSpace(repaired)
               && !string.Equals(repaired, json, StringComparison.Ordinal)
               && TryParseCore(repaired, out document);
    }

    /// <summary>Deserialises through <see cref="TryParse"/>; false when nothing parseable can be recovered.</summary>
    public static bool TryDeserialize<T>(string raw, out T? value, JsonSerializerOptions? options = null)
    {
        value = default;
        if (!TryParse(raw, out var document) || document is null) return false;
        using (document)
        {
            try
            {
                value = document.RootElement.Deserialize<T>(options ?? Http.Json.Options);
                return value is not null;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    private static bool TryParseCore(string json, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            document = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int FindObjectEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var index = start; index < text.Length; index++)
        {
            var ch = text[index];
            if (inString)
            {
                if (escape) escape = false;
                else if (ch == '\\') escape = true;
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0) return index;
            }
        }

        return -1;
    }

    private enum ContainerKind { Object, Array }

    private enum Expectation { KeyOrEnd, Colon, Value, ValueOrEnd, CommaOrEnd }

    private enum StringRole { PropertyName, Value }

    private sealed class ContainerState
    {
        public ContainerState(ContainerKind kind, Expectation expectation)
        {
            Kind = kind;
            Expectation = expectation;
        }

        public ContainerKind Kind { get; }
        public Expectation Expectation { get; set; }
    }

    /// <summary>
    /// Rebuilds a truncated object: keeps every complete value, drops a
    /// half-written key or value, closes an open string, then closes every
    /// open array and object. Returns the input unchanged when it holds no <c>{</c>.
    /// </summary>
    public static string Repair(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return json;

        var start = json.IndexOf('{');
        if (start < 0) return json.Trim();

        var builder = new StringBuilder(json.Length + 32);
        var stack = new Stack<ContainerState>();
        var lastSafeLength = 0;
        var inString = false;
        var escape = false;
        var stringRole = StringRole.Value;
        var primitiveStart = -1;

        void MarkValueComplete()
        {
            if (stack.Count > 0) stack.Peek().Expectation = Expectation.CommaOrEnd;
            lastSafeLength = builder.Length;
        }

        void OpenContainer(char ch)
        {
            builder.Append(ch);
            stack.Push(new ContainerState(
                ch == '{' ? ContainerKind.Object : ContainerKind.Array,
                ch == '{' ? Expectation.KeyOrEnd : Expectation.ValueOrEnd));
            lastSafeLength = builder.Length;
        }

        void CloseContainer(char ch)
        {
            if (stack.Count == 0) return;
            builder.Append(ch);
            stack.Pop();
            MarkValueComplete();
        }

        bool StartValue(char ch)
        {
            switch (ch)
            {
                case '"':
                    inString = true;
                    escape = false;
                    stringRole = StringRole.Value;
                    builder.Append(ch);
                    return true;
                case '{':
                case '[':
                    OpenContainer(ch);
                    return true;
                default:
                    if (IsPrimitiveStart(ch))
                    {
                        primitiveStart = builder.Length;
                        builder.Append(ch);
                        return true;
                    }

                    return false;
            }
        }

        for (var index = start; index < json.Length; index++)
        {
            var ch = json[index];

            if (primitiveStart >= 0)
            {
                if (IsPrimitiveBodyChar(ch))
                {
                    builder.Append(ch);
                    continue;
                }

                var token = builder.ToString(primitiveStart, builder.Length - primitiveStart);
                if (IsPrimitiveToken(token))
                {
                    primitiveStart = -1;
                    MarkValueComplete();
                    index--;
                    continue;
                }

                builder.Length = lastSafeLength;
                primitiveStart = -1;
                break;
            }

            if (inString)
            {
                builder.Append(ch);
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escape = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = false;
                    if (stack.Count > 0 &&
                        stack.Peek().Kind == ContainerKind.Object &&
                        stack.Peek().Expectation == Expectation.KeyOrEnd &&
                        stringRole == StringRole.PropertyName)
                    {
                        stack.Peek().Expectation = Expectation.Colon;
                    }
                    else
                    {
                        MarkValueComplete();
                    }
                }

                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
                continue;
            }

            if (stack.Count == 0)
            {
                if (ch == '{') OpenContainer(ch);
                continue;
            }

            var current = stack.Peek();
            if (current.Kind == ContainerKind.Object)
            {
                switch (current.Expectation)
                {
                    case Expectation.KeyOrEnd:
                        if (ch == '"')
                        {
                            inString = true;
                            escape = false;
                            stringRole = StringRole.PropertyName;
                            builder.Append(ch);
                        }
                        else if (ch == '}')
                        {
                            CloseContainer('}');
                        }
                        break;

                    case Expectation.Colon:
                        if (ch == ':')
                        {
                            builder.Append(ch);
                            current.Expectation = Expectation.Value;
                        }
                        break;

                    case Expectation.Value:
                        StartValue(ch);
                        break;

                    case Expectation.CommaOrEnd:
                        if (ch == ',')
                        {
                            builder.Append(ch);
                            current.Expectation = Expectation.KeyOrEnd;
                        }
                        else if (ch == '}')
                        {
                            CloseContainer('}');
                        }
                        break;
                }

                continue;
            }

            switch (current.Expectation)
            {
                case Expectation.ValueOrEnd:
                    if (ch == ']') CloseContainer(']');
                    else StartValue(ch);
                    break;

                case Expectation.CommaOrEnd:
                    if (ch == ',')
                    {
                        builder.Append(ch);
                        current.Expectation = Expectation.ValueOrEnd;
                    }
                    else if (ch == ']')
                    {
                        CloseContainer(']');
                    }
                    break;
            }
        }

        if (primitiveStart >= 0)
        {
            var token = builder.ToString(primitiveStart, builder.Length - primitiveStart);
            if (IsPrimitiveToken(token)) MarkValueComplete();
            else builder.Length = lastSafeLength;
        }
        else if (inString)
        {
            if (stringRole == StringRole.Value)
            {
                builder.Append('"');
                MarkValueComplete();
            }
            else
            {
                builder.Length = lastSafeLength;
            }
        }

        if (builder.Length > lastSafeLength &&
            stack.Count > 0 &&
            stack.Peek().Expectation is Expectation.Colon or Expectation.Value)
        {
            builder.Length = lastSafeLength;
        }

        TrimTrailingDelimiters(builder);

        while (stack.Count > 0)
        {
            if (builder.Length > lastSafeLength &&
                stack.Peek().Expectation is Expectation.Colon or Expectation.Value)
            {
                builder.Length = lastSafeLength;
                TrimTrailingDelimiters(builder);
            }

            builder.Append(stack.Pop().Kind == ContainerKind.Object ? '}' : ']');
            lastSafeLength = builder.Length;
        }

        return builder.ToString().Trim();
    }

    private static bool IsPrimitiveStart(char ch) => ch is '-' or 't' or 'f' or 'n' || char.IsDigit(ch);

    private static bool IsPrimitiveBodyChar(char ch) => ch is '-' or '+' or '.' or 'e' or 'E' || char.IsLetterOrDigit(ch);

    private static bool IsPrimitiveToken(string token)
    {
        token = token.Trim();
        if (token is "true" or "false" or "null") return true;
        return token.Length > 0 && token.All(ch => char.IsDigit(ch) || ch is '-' or '+' or '.' or 'e' or 'E');
    }

    private static void TrimTrailingDelimiters(StringBuilder builder)
    {
        TrimTrailingWhitespace(builder);
        while (builder.Length > 0 && builder[^1] is ',' or ':')
        {
            builder.Length--;
            TrimTrailingWhitespace(builder);
        }
    }

    private static void TrimTrailingWhitespace(StringBuilder builder)
    {
        while (builder.Length > 0 && char.IsWhiteSpace(builder[^1])) builder.Length--;
    }

    /// <summary>The first of <paramref name="propertyNames"/> that has a non-empty value in the raw text; null when none does.</summary>
    public static string? ExtractLooseString(string raw, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryExtractLooseString(raw, propertyName, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Everything that is not JSON punctuation or a code fence, on one line — the last resort when a response is prose.</summary>
    public static string LooseText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var cleaned = raw
            .Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("```", string.Empty, StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return cleaned.Trim(' ', '{', '}', '[', ']', '"');
    }

    /// <summary>The items of a named array property; a scalar value is split on commas/semicolons/newlines. Distinct, trimmed, at most <paramref name="limit"/>.</summary>
    public static IReadOnlyList<string> ExtractLooseArray(string raw, string propertyName, int limit = int.MaxValue)
    {
        if (!TryFindLoosePropertyValue(raw, propertyName, out var index) || index >= raw.Length)
        {
            return Array.Empty<string>();
        }

        if (raw[index] == '"')
        {
            var value = ReadLooseQuotedString(raw, ref index);
            return Distinct(new[] { value }, limit);
        }

        if (raw[index] != '[')
        {
            var boundary = FindLooseValueBoundary(raw, index);
            return Distinct(raw[index..boundary].Split(new[] { '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), limit);
        }

        index++;
        var values = new List<string>();
        while (index < raw.Length)
        {
            while (index < raw.Length && char.IsWhiteSpace(raw[index])) index++;
            if (index >= raw.Length || raw[index] == ']') break;

            if (raw[index] == '"')
            {
                var item = ReadLooseQuotedString(raw, ref index);
                if (!string.IsNullOrWhiteSpace(item)) values.Add(item);
            }
            else
            {
                var boundary = index;
                while (boundary < raw.Length && raw[boundary] is not ',' and not ']') boundary++;
                var item = raw[index..boundary].Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(item)) values.Add(item);
                index = boundary;
            }

            while (index < raw.Length && char.IsWhiteSpace(raw[index])) index++;
            if (index < raw.Length && raw[index] == ',') index++;
        }

        return Distinct(values, limit);
    }

    private static bool TryExtractLooseString(string raw, string propertyName, out string value)
    {
        value = string.Empty;
        if (!TryFindLoosePropertyValue(raw, propertyName, out var index) || index >= raw.Length) return false;

        if (raw[index] == '"')
        {
            value = ReadLooseQuotedString(raw, ref index);
            return !string.IsNullOrWhiteSpace(value);
        }

        var boundary = FindLooseValueBoundary(raw, index);
        value = raw[index..boundary].Trim().Trim('"');
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryFindLoosePropertyValue(string raw, string propertyName, out int valueIndex)
    {
        valueIndex = -1;
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(propertyName)) return false;

        foreach (var pattern in new[] { $"\"{propertyName}\"", propertyName })
        {
            var propertyIndex = raw.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (propertyIndex < 0) continue;

            var colonIndex = raw.IndexOf(':', propertyIndex + pattern.Length);
            if (colonIndex < 0) continue;

            valueIndex = colonIndex + 1;
            while (valueIndex < raw.Length && char.IsWhiteSpace(raw[valueIndex])) valueIndex++;
            return valueIndex < raw.Length;
        }

        return false;
    }

    private static string ReadLooseQuotedString(string raw, ref int index)
    {
        if (index >= raw.Length || raw[index] != '"') return string.Empty;

        var builder = new StringBuilder();
        index++;
        var escape = false;
        while (index < raw.Length)
        {
            var ch = raw[index++];
            if (escape)
            {
                builder.Append(ch switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => ch,
                });
                escape = false;
                continue;
            }

            if (ch == '\\')
            {
                escape = true;
                continue;
            }

            if (ch == '"') break;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    private static int FindLooseValueBoundary(string raw, int start)
    {
        var index = start;
        while (index < raw.Length && raw[index] is not ',' and not '}' and not ']') index++;
        return index;
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values, int limit)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in values)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !seen.Add(trimmed)) continue;
            result.Add(trimmed);
            if (result.Count >= limit) break;
        }

        return result;
    }
}

public static class ChatResultJsonExtensions
{
    /// <summary>Parses <see cref="ChatResult.Text"/> as a JSON object, repairing fences and truncation.</summary>
    public static bool TryParseJson(this ChatResult result, out JsonDocument? document)
        => StructuredJson.TryParse(result.Text, out document);

    public static bool TryParseJson<T>(this ChatResult result, out T? value, JsonSerializerOptions? options = null)
        => StructuredJson.TryDeserialize(result.Text, out value, options);
}
