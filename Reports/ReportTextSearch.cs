using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Fx.ControlKit.Reports;

public sealed record ReportSearchMatch(int Page, int Start, int Length, string Preview);

/// <summary>Searches finalized report output without executing formulas, scripts, or remote requests.</summary>
public sealed class ReportTextSearch
{
    private readonly IReadOnlyList<string> _pages;
    public string Query { get; }
    public IReadOnlyList<ReportSearchMatch> Matches { get; }
    public bool Truncated { get; }
    private sealed record TextPart(IText Node, int Start, int Length);

    public ReportTextSearch(IReadOnlyList<string> pages, string query)
    {
        if (pages.Sum(page => (long)page.Length) > 50_000_000) throw new InvalidDataException("Report search is limited to 50 MB of rendered content.");
        _pages = pages.ToArray(); Query = query.Trim();
        var matches = new List<ReportSearchMatch>(); Matches = matches;
        if (Query.Length == 0) return;
        if (Query.Length > 1000) throw new InvalidDataException("Search text exceeds 1,000 characters.");
        for (var page = 0; page < pages.Count; page++)
        {
            using var document = new HtmlParser().ParseDocument(pages[page]);
            var text = ReadText(document.Body!, out _);
            for (var offset = 0; offset <= text.Length - Query.Length;)
            {
                var found = text.IndexOf(Query, offset, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                var start = Math.Max(0, found - 30);
                matches.Add(new(page + 1, found, Query.Length, text[start..Math.Min(text.Length, found + Query.Length + 40)].Trim()));
                if (matches.Count == 10000) { Truncated = true; return; }
                offset = found + Query.Length;
            }
        }
    }

    public string HighlightPage(int page, int activeMatch)
    {
        if (page < 1 || page > _pages.Count) throw new ArgumentOutOfRangeException(nameof(page));
        using var document = new HtmlParser().ParseDocument(_pages[page - 1]);
        ReadText(document.Body!, out var parts);
        var matches = Matches.Select((match, index) => (match, index)).Where(pair => pair.match.Page == page).ToList();
        var anchored = new HashSet<int>();
        foreach (var part in parts)
        {
            var segments = matches.Where(pair => pair.match.Start < part.Start + part.Length && pair.match.Start + pair.match.Length > part.Start).ToList();
            if (segments.Count == 0) continue;
            if (part.Node.ParentElement?.NamespaceUri == "http://www.w3.org/2000/svg")
            {
                var parent = part.Node.ParentElement;
                var index = segments[0].index;
                parent.SetAttribute("style", (parent.GetAttribute("style") ?? "") + ";fill:#9b3200;font-weight:bold;");
                if (anchored.Add(index)) parent.Id = "fx-report-hit-" + index;
                continue;
            }
            var content = part.Node.TextContent;
            var offset = 0;
            foreach (var (match, index) in segments)
            {
                var start = Math.Max(0, match.Start - part.Start);
                var end = Math.Min(part.Length, match.Start + match.Length - part.Start);
                if (start > offset) part.Node.Parent!.InsertBefore(document.CreateTextNode(content[offset..start]), part.Node);
                var mark = document.CreateElement("mark");
                mark.SetAttribute("style", index == activeMatch ? "background:#ffbd59;color:#111;outline:1px solid #9b3200;" : "background:#fff19a;color:#111;");
                mark.TextContent = content[start..end];
                if (anchored.Add(index)) mark.Id = "fx-report-hit-" + index;
                part.Node.Parent!.InsertBefore(mark, part.Node);
                offset = end;
            }
            if (offset < content.Length) part.Node.Parent!.InsertBefore(document.CreateTextNode(content[offset..]), part.Node);
            part.Node.Parent!.RemoveChild(part.Node);
        }
        return document.Body!.InnerHtml;
    }

    private static string ReadText(INode root, out List<TextPart> parts)
    {
        var collected = new List<TextPart>(); var text = new StringBuilder();
        Visit(root, 0); parts = collected; return text.ToString();
        void Visit(INode node, int depth)
        {
            if (depth > 256) throw new InvalidDataException("Report markup exceeds the search nesting limit.");
            if (node is IText item) { collected.Add(new(item, text.Length, item.Length)); text.Append(item.TextContent); return; }
            if (node is IElement element && (element.LocalName is "script" or "style" or "template" or "title" or "defs" || element.HasAttribute("hidden") || element.GetAttribute("aria-hidden") == "true")) return;
            var block = node is IElement blockElement && blockElement.LocalName is "div" or "section" or "article" or "tr" or "td" or "th" or "p" or "h1" or "h2" or "h3" or "li" or "br" or "text";
            if (block && text.Length > 0 && text[^1] != '\n') text.Append('\n');
            foreach (var child in node.ChildNodes) Visit(child, depth + 1);
            if (block && text.Length > 0 && text[^1] != '\n') text.Append('\n');
        }
    }
}
