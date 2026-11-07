using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Reports;

// Interpret displayed text and its scoped formatting. RTF destinations such as pictures,
// metadata and font/color definitions must never become text in the printed field.
internal static class ReportRtfField
{
    private readonly record struct Style(bool Skip, bool Bold, bool Italic, bool Underline, int HalfPoints, int Color, int UnicodeFallback);
    internal static bool TryFormat(string? source, out string result)
    {
        result = "";
        if (source is null || !source.StartsWith(@"{\rtf", StringComparison.Ordinal)) return false;
        var colors = new List<string> { "" };
        var table = Regex.Match(source, @"\{\\colortbl\s*;([^{}]*)\}");
        foreach (var entry in table.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var rgb = Regex.Match(entry, @"\\red(\d{1,3})\\green(\d{1,3})\\blue(\d{1,3})");
            colors.Add(rgb.Success ? "#" + string.Concat(rgb.Groups.Cast<Group>().Skip(1)
                .Select(g => Math.Min(255, int.Parse(g.Value, CultureInfo.InvariantCulture)).ToString("x2"))) : "");
        }

        var html = new StringBuilder();
        var run = new StringBuilder();
        var stack = new Stack<Style>();
        var style = new Style(UnicodeFallback: 1, Skip: false, Bold: false, Italic: false, Underline: false, HalfPoints: 0, Color: 0);
        var fallback = 0;
        void Write(char ch)
        {
            if (fallback > 0) fallback--;
            else if (!style.Skip) run.Append(ch);
        }
        void Flush()
        {
            if (run.Length == 0) return;
            html.Append("<span style=\"");
            if (style.Bold) html.Append("font-weight:bold;");
            if (style.Italic) html.Append("font-style:italic;");
            if (style.Underline) html.Append("text-decoration:underline;");
            if (style.Color > 0 && style.Color < colors.Count) html.Append("color:").Append(colors[style.Color]).Append(';');
            if (style.HalfPoints > 0)
            {
                var pt = (style.HalfPoints * .5).ToString(CultureInfo.InvariantCulture);
                html.Append("font-size:").Append(pt).Append("pt;--fx-report-font-points:").Append(pt).Append(';');
            }
            html.Append("\">").Append(WebUtility.HtmlEncode(run.ToString())).Append("</span>");
            run.Clear();
        }
        for (var i = 0; i < source.Length; i++)
        {
            var ch = source[i];
            if (ch == '{') { Flush(); stack.Push(style); continue; }
            if (ch == '}') { Flush(); if (stack.TryPop(out var previous)) style = previous; continue; }
            if (ch == '\r' || ch == '\n') continue;
            if (ch != '\\' || ++i >= source.Length) { Write(ch); continue; }
            ch = source[i];
            if (!char.IsAsciiLetter(ch))
            {
                if (ch is '{' or '}' or '\\') Write(ch);
                else if (ch == '~') Write('\u00a0');
                else if (ch == '*') { Flush(); style = style with { Skip = true }; }
                else if (ch == '\'' && i + 2 < source.Length && byte.TryParse(source.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var ansi))
                {
                    i += 2;
                    const string windows1252 = "€�‚ƒ„…†‡ˆ‰Š‹Œ�Ž��‘’“”•–—˜™š›œ�žŸ";
                    Write(ansi is >= 128 and < 160 ? windows1252[ansi - 128] : (char)ansi);
                }
                continue;
            }
            var start = i++;
            while (i < source.Length && char.IsAsciiLetter(source[i])) i++;
            var word = source[start..i];
            start = i;
            if (i < source.Length && source[i] == '-') i++;
            while (i < source.Length && char.IsAsciiDigit(source[i])) i++;
            var hasArg = int.TryParse(source.AsSpan(start, i - start), out var arg);
            if (i >= source.Length || source[i] != ' ') i--;
            if (style.Skip) continue;
            Flush();
            switch (word)
            {
                case "fonttbl": case "colortbl": case "stylesheet": case "info": case "pict":
                case "object": case "fldinst": case "listtable": case "listoverridetable":
                    style = style with { Skip = true }; break;
                case "b": style = style with { Bold = !hasArg || arg != 0 }; break;
                case "i": style = style with { Italic = !hasArg || arg != 0 }; break;
                case "ul": style = style with { Underline = !hasArg || arg != 0 }; break;
                case "ulnone": style = style with { Underline = false }; break;
                case "plain": style = new Style { UnicodeFallback = style.UnicodeFallback }; break;
                case "fs": style = style with { HalfPoints = Math.Clamp(arg, 0, 600) }; break;
                case "cf": style = style with { Color = arg }; break;
                case "uc": style = style with { UnicodeFallback = Math.Clamp(arg, 0, 32) }; break;
                case "u": run.Append((char)arg); fallback = style.UnicodeFallback; break;
                case "par": case "line": html.Append('\n'); break;
                case "tab": Write('\t'); break;
                case "endash": Write('\u2013'); break;
                case "emdash": Write('\u2014'); break;
            }
        }
        Flush();
        result = html.ToString();
        return true;
    }
}
