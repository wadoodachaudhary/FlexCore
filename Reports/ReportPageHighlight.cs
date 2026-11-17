using AngleSharp.Html.Parser;

namespace Fx.ControlKit.Reports;

/// <summary>Find's marks for a page that <see cref="ReportPageControl"/> renders: the content of each object that holds a
/// match, as <see cref="ReportTextSearch.HighlightPage"/> marked it. The page keeps its own objects, so their drill-down,
/// focus and keys stay; only a marked object's content is replaced.</summary>
public sealed class ReportPageHighlight
{
    private readonly Dictionary<(int Band, int Item), string> _content;
    private ReportPageHighlight(Dictionary<(int Band, int Item), string> content) => _content = content;

    /// <summary>How many objects of the page carry a mark.</summary>
    public int Count => _content.Count;

    /// <summary>The marked content of the page's <paramref name="item"/>th object of its <paramref name="band"/>th band; null
    /// when that object holds no match.</summary>
    public string? ContentOf(int band, int item) => _content.TryGetValue((band, item), out var html) ? html : null;

    /// <summary>Reads the marked objects from <paramref name="marked"/>, which is <paramref name="html"/> (the page rendered by
    /// ReportPageControl) after ReportTextSearch.HighlightPage. Bands and objects are matched by position: the page renders
    /// one section per band and one div per object, in the snapshot's order.</summary>
    public static ReportPageHighlight FromMarkedPage(ReportPageSnapshot page, string html, string marked)
    {
        var parser = new HtmlParser();
        using var plainDocument = parser.ParseDocument(html);
        using var markedDocument = parser.ParseDocument(marked);
        var plain = Objects(plainDocument.Body!, page);
        var marks = Objects(markedDocument.Body!, page);
        var content = new Dictionary<(int Band, int Item), string>();
        for (var band = 0; band < marks.Count; band++)
            for (var item = 0; item < marks[band].Count; item++)
                if (marks[band][item] != plain[band][item]) content[(band, item)] = marks[band][item];
        return new(content);
    }

    private static List<List<string>> Objects(AngleSharp.Dom.IElement body, ReportPageSnapshot page)
    {
        var sections = body.QuerySelector("article.fx-report-positioned-page")?.FirstElementChild?.Children
            .Where(element => element.LocalName == "section").ToList() ?? [];
        if (sections.Count != page.Bands.Count) throw new InvalidDataException("The searched page does not match the page shown.");
        return sections.Select((section, band) =>
        {
            var objects = section.Children.Where(element => element.LocalName == "div").Select(element => element.InnerHtml).ToList();
            if (objects.Count != page.Bands[band].Objects.Count) throw new InvalidDataException("The searched page does not match the page shown.");
            return objects;
        }).ToList();
    }
}
