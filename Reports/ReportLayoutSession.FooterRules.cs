namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    // A vertical line joining two printed page sections belongs to the physical sheet, not to its rows.
    // Resolve both ends after pagination: the space before a bottom-aligned footer has no flow band.
    private Placement? PageFooterRuleLayer(Page sheet, int pageHeight, HashSet<Item> resolved,
        Dictionary<Placement, List<Item>> strokes, Func<Band, int, int> growthAbove)
    {
        var ink = new List<Item>();
        foreach (var header in sheet.Placements.Where(p => p.Band.Section.Kind == "PageHeader" && Visible(p)))
        {
            foreach (var line in header.Band.Items)
            {
                var e = line.Element;
                if (line.Owner != this || e is not { Kind: "Line", UseCrystalScreenMetrics: true, WidthTwips: 0, IsSuppressed: false }
                    || e.LineEndSection.Length == 0 || e.TopTwips < header.Offset || e.TopTwips >= header.Offset + header.Height)
                    continue;
                var footer = sheet.Placements.FirstOrDefault(p => p.Band.Section.Kind == "PageFooter"
                    && p.Band.Section.Name == e.LineEndSection && Visible(p)
                    && e.LineEndTop >= p.Offset && e.LineEndTop <= p.Offset + p.Height);
                if (footer is null) continue;
                var top = header.Top + e.TopTwips - header.Offset;
                var end = footer.Top + Math.Min(footer.Height, e.LineEndTop - footer.Offset + growthAbove(footer.Band, e.LineEndTop));
                if (end <= top) continue;

                resolved.Add(line);
                // Crystal locates the near edge at Left; PDF strokes straddle their center.
                var x = header.Left + e.LeftTwips + Math.Max(0, e.Visual.LineWidthTwips ?? 0) / 2;
                ink.Add(Segment(top, end, x));
                // A section fill covers the sheet ink. Restore just the piece over the fill,
                // before the section's objects so an opaque field can still mask the rule.
                foreach (var p in sheet.Placements.Where(p => Visible(p) &&
                    p.Band.Section.BackgroundColor is not ("" or "none" or "transparent")))
                {
                    var lo = Math.Max(top, p.Top);
                    var hi = Math.Min(end, p.Top + p.Height);
                    if (hi <= lo) continue;
                    if (!strokes.TryGetValue(p, out var parts)) strokes.Add(p, parts = []);
                    parts.Add(Segment(lo - p.Top + p.Offset, hi - p.Top + p.Offset, x - p.Left));
                }

                Item Segment(int from, int to, int left)
                {
                    var seg = e.CloneFor(e.SectionId, e.Name);
                    seg.Id = e.Id; seg.LineEndSection = ""; seg.TopTwips = from; seg.HeightTwips = to - from; seg.LeftTwips = left;
                    if (seg.Visual.LeftLine is not ("" or "NoLine")) seg.Visual.TopLine = seg.Visual.LeftLine;
                    return line with { Element = seg, Html = ReportObjectRenderer.VerticalRuleContent(seg) };
                }
            }
        }
        if (ink.Count == 0) return null;
        var section = new ReportDesignerSection { Name = "Page-spanning rules", HeightTwips = pageHeight, BackgroundColor = "transparent" };
        return new(new Band(section, ink, -1, []), 0, 0, pageHeight);

        static bool Visible(Placement p) => !p.Band.Blank && !p.Band.Suppressed && p.Height > 0;
    }
}
