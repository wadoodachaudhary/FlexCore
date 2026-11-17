using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Fx.ControlKit.Reports;

public sealed record ReportSearchMatch(int Page, int Start, int Length, string Preview);

/// <summary>Searches finalized report output one page at a time, without executing formulas, scripts, or remote requests.
/// A page is searched when the viewer reaches it, so a long report is searched as far as the first match and no rendered
/// page is kept: the searched pages keep only their match positions.</summary>
public sealed class ReportTextSearch
{
    private readonly SortedDictionary<int, ReportSearchMatch[]> _pages = new();
    public string Query { get; }
    /// <summary>Every match on the pages searched so far, in page order.</summary>
    public IReadOnlyList<ReportSearchMatch> Matches => _pages.Values.SelectMany(matches => matches).ToArray();
    public int MatchCount { get; private set; }
    /// <summary>A page stopped at its 10,000th match.</summary>
    public bool Truncated { get; private set; }
    /// <summary>How many pages the search has covered.</summary>
    public int PageCount => _pages.Count;
    public IEnumerable<int> SearchedPages => _pages.Keys;
    private sealed record TextPart(IText Node, int Start, int Length);

    public ReportTextSearch(string query)
    {
        Query = query.Trim();
        if (Query.Length > 1000) throw new InvalidDataException("Search text exceeds 1,000 characters.");
    }

    public bool IsSearched(int page) => _pages.ContainsKey(page);

    public IReadOnlyList<ReportSearchMatch> MatchesOn(int page) => _pages.TryGetValue(page, out var matches) ? matches : [];

    /// <summary>Matches on the searched pages before <paramref name="page"/>: the ordinal of a match once every page is searched.</summary>
    public int MatchesBefore(int page) => _pages.TakeWhile(pair => pair.Key < page).Sum(pair => pair.Value.Length);

    /// <summary>Searches one rendered page; searching it again replaces its result.</summary>
    public IReadOnlyList<ReportSearchMatch> SearchPage(int page, string html)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        var matches = new List<ReportSearchMatch>();
        if (Query.Length > 0)
        {
            using var document = new HtmlParser().ParseDocument(html);
            var text = ReadText(document.Body!, out _);
            for (var offset = 0; offset <= text.Length - Query.Length;)
            {
                var found = text.IndexOf(Query, offset, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                var start = Math.Max(0, found - 30);
                matches.Add(new(page, found, Query.Length, text[start..Math.Min(text.Length, found + Query.Length + 40)].Trim()));
                if (matches.Count == 10000) { Truncated = true; break; }
                offset = found + Query.Length;
            }
        }
        if (_pages.TryGetValue(page, out var previous)) MatchCount -= previous.Length;
        var result = matches.ToArray();
        _pages[page] = result;
        MatchCount += result.Length;
        return result;
    }

    /// <summary>Marks the page's matches in its rendered HTML; the active match (an index into <see cref="MatchesOn"/>) gets the
    /// stronger mark. Each mark carries the anchor <c>fx-report-hit-{index}</c>.</summary>
    public string HighlightPage(int page, string html, int activeMatch)
    {
        var matches = MatchesOn(page).Select((match, index) => (match, index)).ToList();
        if (matches.Count == 0) return html;
        using var document = new HtmlParser().ParseDocument(html);
        ReadText(document.Body!, out var parts);
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

    private static string ReadText(IElement root, out List<TextPart> parts)
    {
        var collected = new List<TextPart>(); var text = new StringBuilder();
        var tableFragments = root.QuerySelectorAll("table.fx-report-table-fragment[data-region]")
            .Where(table => !string.IsNullOrEmpty(table.GetAttribute("data-region"))).ToArray();
        var visitedFragments = new HashSet<INode>();
        Visit(root, 0); parts = collected; return text.ToString();
        void Visit(INode node, int depth)
        {
            if (depth > 256) throw new InvalidDataException("Report markup exceeds the search nesting limit.");
            if (visitedFragments.Contains(node)) return;
            if (node is IElement table && tableFragments.Contains(table))
            {
                // Line fragments are positioned row-wise, but search follows each logical cell so
                // a word wrapped within the same page remains searchable and highlightable.
                var region = tableFragments.Where(fragment => fragment.GetAttribute("data-region") == table.GetAttribute("data-region")
                    && fragment.GetAttribute("data-pane") == table.GetAttribute("data-pane")).ToArray();
                foreach (var fragment in region) visitedFragments.Add(fragment);
                var logicalCells = region.OrderBy(fragment => int.TryParse(fragment.GetAttribute("data-row"), out var row) ? row : -1)
                    .ThenBy(fragment => int.TryParse(fragment.GetAttribute("data-line"), out var line) ? line : 0)
                    .SelectMany(fragment => fragment.QuerySelectorAll("th,td").Where(cell => cell.Closest("table") == fragment)
                        .Select((cell, column) => (Cell: cell, Row: cell.GetAttribute("data-cell-row") ?? fragment.GetAttribute("data-row"),
                            Column: cell.GetAttribute("data-cell-column") ?? column.ToString(System.Globalization.CultureInfo.InvariantCulture))))
                    .GroupBy(cell => (cell.Row, cell.Column));
                foreach (var cell in logicalCells)
                {
                    text.Append('\n');
                    foreach (var line in cell)
                        foreach (var child in line.Cell.ChildNodes) Visit(child, depth + 1);
                    text.Append('\n');
                }
                return;
            }
            if (node is IText item) { collected.Add(new(item, text.Length, item.Length)); text.Append(item.TextContent); return; }
            if (node is IElement element && (element.LocalName is "script" or "style" or "template" or "title" or "defs" || element.HasAttribute("hidden") || element.GetAttribute("aria-hidden") == "true")) return;
            var block = node is IElement blockElement && blockElement.LocalName is "div" or "section" or "article" or "tr" or "td" or "th" or "p" or "h1" or "h2" or "h3" or "li" or "br" or "text";
            if (block && text.Length > 0 && text[^1] != '\n') text.Append('\n');
            foreach (var child in node.ChildNodes) Visit(child, depth + 1);
            if (block && text.Length > 0 && text[^1] != '\n') text.Append('\n');
        }
    }
}
