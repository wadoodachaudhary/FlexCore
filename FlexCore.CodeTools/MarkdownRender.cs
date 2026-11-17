using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.CodeTools;

/// <summary>
/// Renders a safe subset of Markdown to HTML. Raw HTML in the source is escaped.
/// </summary>
public static class MarkdownRender
{
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var html = new StringBuilder();
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                var language = line.Length > 3 ? line[3..].Trim() : "";
                var body = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal))
                {
                    if (body.Length > 0) body.Append('\n');
                    body.Append(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++;
                html.Append("<pre><code");
                if (language.Length > 0) html.Append(" class=\"language-").Append(Escape(language)).Append('"');
                html.Append('>').Append(Escape(body.ToString())).Append("</code></pre>");
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            var heading = Regex.Match(line, @"^(#{1,3})\s+(.*)$");
            if (heading.Success)
            {
                var level = heading.Groups[1].Value.Length;
                html.Append("<h").Append(level).Append('>').Append(Inline(heading.Groups[2].Value)).Append("</h").Append(level).Append('>');
                i++;
                continue;
            }

            if (line.StartsWith("> ", StringComparison.Ordinal) || line == ">")
            {
                var quote = new StringBuilder();
                while (i < lines.Length && (lines[i].StartsWith("> ", StringComparison.Ordinal) || lines[i] == ">"))
                {
                    if (quote.Length > 0) quote.Append(' ');
                    quote.Append(lines[i].Length > 2 ? lines[i][2..] : "");
                    i++;
                }
                html.Append("<blockquote><p>").Append(Inline(quote.ToString())).Append("</p></blockquote>");
                continue;
            }

            if (IsList(line, out var ordered))
            {
                var tag = ordered ? "ol" : "ul";
                html.Append('<').Append(tag).Append('>');
                while (i < lines.Length && IsList(lines[i], out var still) && still == ordered)
                {
                    html.Append("<li>").Append(Inline(ListText(lines[i]))).Append("</li>");
                    i++;
                }
                html.Append("</").Append(tag).Append('>');
                continue;
            }

            var paragraph = new StringBuilder(line);
            i++;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !lines[i].StartsWith('#') && !lines[i].StartsWith("```") && !IsList(lines[i], out _) && !lines[i].StartsWith("> "))
            {
                paragraph.Append(' ').Append(lines[i]);
                i++;
            }
            html.Append("<p>").Append(Inline(paragraph.ToString())).Append("</p>");
        }

        return html.ToString();
    }

    private static bool IsList(string line, out bool ordered)
    {
        ordered = Regex.IsMatch(line, @"^\d+\.\s+");
        return ordered || line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
    }

    private static string ListText(string line)
    {
        if (line.StartsWith("- ") || line.StartsWith("* ")) return line[2..];
        return Regex.Replace(line, @"^\d+\.\s+", "");
    }

    private static string Inline(string text)
    {
        var encoded = Escape(text);
        encoded = Regex.Replace(encoded, @"`([^`]+)`", "<code>$1</code>");
        encoded = Regex.Replace(encoded, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        encoded = Regex.Replace(encoded, @"(?<!\*)\*([^*]+)\*(?!\*)", "<em>$1</em>");
        encoded = Regex.Replace(encoded, @"\[([^\]]+)\]\((https?://[^\s)]+|mailto:[^\s)]+)\)", match =>
            "<a href=\"" + match.Groups[2].Value + "\" rel=\"noopener noreferrer\">" + match.Groups[1].Value + "</a>");
        return encoded;
    }

    private static string Escape(string text) => WebUtility.HtmlEncode(text);
}
