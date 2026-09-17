using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    private Band SuppressInlineContinuations(Band band, int offset, int page, int count, Func<Item, int> height)
    {
        foreach (var source in band.InlineSections?.Where(s => s.Start < offset && s.End > offset).OrderByDescending(s => s.End - s.Start).Select(s => s.Source).ToArray() ?? [])
        {
            var inline = band.InlineSections!.First(s => s.Source == source);
            if (inline.End <= offset || !ForPage(source, page, count).Suppressed) continue;
            var cut = new InlineCut(source, offset - inline.Start);
            band = CutInline(band, inline, offset, height) with { InlineCuts = [.. band.InlineCuts ?? [], cut] };
        }
        return band;
    }

    private static Band ApplyInlineCuts(Band band, List<InlineCut>? cuts, Func<Item, int> height)
    {
        foreach (var cut in cuts ?? [])
        {
            var inline = band.InlineSections?.FirstOrDefault(s => s.Source == cut.Source)
                ?? throw new NotSupportedException("A previously continued subreport section disappeared during page reflow.");
            var start = inline.Start + cut.RetainedHeight;
            if (start < inline.End) band = CutInline(band, inline, start, height);
        }
        return band with { InlineCuts = cuts };
    }

    private static Band CutInline(Band band, InlineSection inline, int start, Func<Item, int> height)
    {
        var end = inline.End;
        var owners = new HashSet<(ReportLayoutSession? Owner, string Section, int Row)>();
        Collect(inline.Source);
        void Collect(Band source)
        {
            owners.Add((source.Owner, source.Section.Id, source.Row));
            foreach (var item in source.Items)
                if (item.Child is { } child) foreach (var nested in child._bands) Collect(nested);
        }
        bool Owned(Item item) => owners.Contains((item.Owner ?? band.Owner, item.Element.SectionId, item.Row));
        var items = new List<Item>();
        foreach (var item in band.Items)
        {
            var top = item.Element.TopTwips; var bottom = top + height(item);
            if (Owned(item) && bottom > start) continue;
            if (!Owned(item) && top < end && bottom > start)
                throw new NotSupportedException("Suppressing an inline continuation cannot remove space shared with an overlapping neighboring object.");
            var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name); element.Id = item.Element.Id;
            element.LeftTwips = item.Element.LeftTwips; element.TopTwips = Move(top);
            items.Add(item with { Element = element });
        }
        var section = new ReportDesignerSection
        {
            Id = band.Section.Id, Name = band.Section.Name, Kind = band.Section.Kind, HeightTwips = Move(band.Section.HeightTwips),
            BackgroundColor = band.Section.BackgroundColor, KeepTogether = band.Section.KeepTogether,
            NewPageBefore = band.Section.NewPageBefore, NewPageAfter = band.Section.NewPageAfter,
            PrintAtBottomOfPage = band.Section.PrintAtBottomOfPage, ResetPageNumberAfter = band.Section.ResetPageNumberAfter,
            RelativePositions = band.Section.RelativePositions, SuppressIfBlank = band.Section.SuppressIfBlank,
            UnderlayFollowingSections = band.Section.UnderlayFollowingSections
        };
        return band with
        {
            Section = section, Items = items,
            Breaks = band.Breaks?.Select(Move).Distinct().ToList(), ForcedBreaks = band.ForcedBreaks?.Select(Move).Distinct().ToList(),
            InlineSections = band.InlineSections!.Select(s => s with { Start = Move(s.Start), End = Move(s.End) }).ToList(),
            InlineHeaders = band.InlineHeaders?.Select(h => h with { Start = Move(h.Start), End = Move(h.End) }).Where(h => h.End > h.Start).ToList()
        };
        int Move(int position) => position <= start ? position : position < end ? start : position - (end - start);
    }

    private static XElement TextTree(string html) => XElement.Parse("<text>" + html + "</text>", LoadOptions.PreserveWhitespace);

    private static string RestylePrintedRuns(string html, ReportDesignerElement element)
    {
        var tree = TextTree(html);
        foreach (var (span, run) in tree.Elements().Zip(element.Visual.Runs))
            span.SetAttributeValue("style", ReportObjectRenderer.FontStyle(run.FontFamily, run.FontSize, run.Bold, run.Italic, run.Underline, run.Color));
        return string.Concat(tree.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)));
    }

    private Band ContinueText(Band previous, Band fresh, int offset, Func<Item, int> height)
    {
        var changing = previous.Items.Where(i => i.Element.CanGrow && i.Element.TopTwips < offset && i.Element.TopTwips + height(i) > offset)
            .Any(i => i.ConsumedText is not null || fresh.Items.Any(n => SameItem(i, n)
                && (n.Html != i.Html || ReportObjectRenderer.Style(n.Element, false) != ReportObjectRenderer.Style(i.Element, false))));
        if (!changing && previous.Items.All(i => i.ConsumedText is null)) return fresh;
        var added = fresh.Items.Where(n => !previous.Items.Any(i => SameItem(i, n))).ToList();
        if (added.Any(n => n.Element.TopTwips < offset || !KnownTail(n)))
            throw new NotSupportedException("Changing-text continuation cannot introduce newly visible objects into already printed space or an unresolved inline section.");
        bool KnownTail(Item item) => item.Element.SectionId == fresh.Section.Id && item.Owner == fresh.Owner
            || fresh.InlineSections?.Any(s => s.Source.Owner == item.Owner && s.Source.Row == item.Row
                && s.Source.Section.Id == item.Element.SectionId && s.End >= offset) == true;

        var items = new List<Item>();
        var shifts = new List<(int Bottom, int Delta, ReportLayoutSession Owner)>();
        foreach (var item in previous.Items.OrderBy(i => i.Element.TopTwips))
        {
            var next = fresh.Items.FirstOrDefault(n => SameItem(item, n));
            if (next is null)
            {
                if (item.Element.TopTwips < offset && item.Element.TopTwips + height(item) > offset)
                    throw new NotSupportedException("A continuing text object cannot be conditionally removed after its first fragment.");
                continue;
            }
            var oldBottom = item.Element.TopTwips + height(item);
            if (oldBottom <= offset) { items.Add(item); continue; }
            var element = next.Element.CloneFor(next.Element.SectionId, next.Element.Name); element.Id = next.Element.Id;
            element.LeftTwips = next.Element.LeftTwips;
            var owner = item.Owner ?? this;
            var owningSection = owner._layout.Document.Sections.First(s => s.Id == item.Element.SectionId);
            element.TopTwips = MoveFor(item.Element.TopTwips, owningSection.RelativePositions ? null : owner);
            next = next with { Element = element };
            if (item.Element.TopTwips < offset && item.Element.CanGrow && item.Element.Kind is "Text" or "Field" or "FieldHeading")
            {
                var metric = TextMetrics(item) ?? throw new NotSupportedException("Changing-text continuation requires browser character-offset measurements.");
                var oldText = TextTree(item.Html).Value;
                if (metric.Lines.Any(l => l.Start < 0 || l.End > oldText.Length))
                    throw new InvalidDataException("Text continuation received invalid or missing character offsets.");
                var consumed = metric.Lines.Where(l => l.Bottom <= offset - item.Element.TopTwips).Select(l => l.End).DefaultIfEmpty(0).Max();
                var prefix = (item.ConsumedText ?? "") + oldText[..consumed];
                var tree = TextTree(next.Html);
                if (!tree.Value.StartsWith(prefix, StringComparison.Ordinal))
                    throw new NotSupportedException($"{item.Element.Name}: a page-dependent value changed text that was already printed.");
                var remaining = prefix.Length;
                foreach (var node in tree.DescendantNodes().OfType<XText>().ToArray())
                {
                    var take = Math.Min(remaining, node.Value.Length); node.Value = node.Value[take..]; remaining -= take;
                }
                // Empty rich runs otherwise retain their old line box/font height.
                foreach (var span in tree.Descendants().Where(e => e.Value.Length == 0).ToArray()) span.Remove();
                var html = string.Concat(tree.Nodes().Select(n => n.ToString(SaveOptions.DisableFormatting)));
                element.TopTwips = offset; element.HeightTwips = Math.Max(0, item.Element.HeightTwips - (offset - item.Element.TopTwips));
                next = next with { Html = html, TemplateHtml = html, Measurement = -2, ConsumedText = prefix };
            }
            items.Add(next);
            shifts.Add((oldBottom, element.TopTwips + height(next) - oldBottom, owner));
        }
        foreach (var item in added)
        {
            var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name); element.Id = item.Element.Id;
            element.LeftTwips = item.Element.LeftTwips; element.TopTwips = MoveFresh(item.Element.TopTwips, item.Owner);
            if (element.TopTwips < offset) throw new NotSupportedException("A newly visible nested object moved into already printed space.");
            items.Add(item with { Element = element });
        }
        for (var i = 0; i < items.Count; i++)
        for (var j = i + 1; j < items.Count; j++)
        {
            var left = items[i]; var right = items[j];
            if (!Overlaps(left, right)) continue;
            var beforeLeft = previous.Items.FirstOrDefault(p => SameItem(p, left));
            var beforeRight = previous.Items.FirstOrDefault(p => SameItem(p, right));
            if (beforeLeft is null || beforeRight is null || !Overlaps(beforeLeft, beforeRight))
                throw new NotSupportedException("Changing-text continuation would overlap an independent neighboring object.");
        }
        var section = new ReportDesignerSection
        {
            Id = fresh.Section.Id, Name = fresh.Section.Name, Kind = fresh.Section.Kind,
            HeightTwips = Math.Max(offset, Move(previous.Section.HeightTwips)),
            BackgroundColor = fresh.Section.BackgroundColor, KeepTogether = fresh.Section.KeepTogether, NewPageBefore = fresh.Section.NewPageBefore,
            NewPageAfter = fresh.Section.NewPageAfter, PrintAtBottomOfPage = fresh.Section.PrintAtBottomOfPage,
            ResetPageNumberAfter = fresh.Section.ResetPageNumberAfter, RelativePositions = fresh.Section.RelativePositions,
            SuppressIfBlank = fresh.Section.SuppressIfBlank, UnderlayFollowingSections = fresh.Section.UnderlayFollowingSections
        };
        return fresh with
        {
            Items = items, Section = section,
            Breaks = previous.Breaks?.Select(Move).ToList(), ForcedBreaks = previous.ForcedBreaks?.Select(Move).ToList(),
            InlineSections = MergeSections(),
            InlineHeaders = fresh.InlineHeaders?.Select(next =>
            {
                var old = previous.InlineHeaders?.FirstOrDefault(h => h.Header.Owner == next.Header.Owner
                    && h.Header.Row == next.Header.Row && h.Header.Section.Id == next.Header.Section.Id);
                return next with { Start = old is null ? MoveFresh(next.Start, next.Header.Owner) : Move(old.Start),
                    End = old is null ? MoveFresh(next.End, next.Header.Owner) : Move(old.End) };
            }).ToList()
        };
        List<InlineSection>? MergeSections()
        {
            if (fresh.InlineSections is null) return null;
            var result = new List<InlineSection>();
            foreach (var next in fresh.InlineSections)
            {
                var old = previous.InlineSections?.FirstOrDefault(s => s.Source == next.Source);
                result.Add(old is null
                    ? next with { Start = MoveFresh(next.Start, next.Source.Owner), End = MoveFresh(next.End, next.Source.Owner) }
                    : next with { Start = Move(old.Start), End = Math.Max(Move(old.End), items.Where(i => i.Owner == next.Source.Owner
                        && i.Row == next.Source.Row && i.Element.SectionId == next.Source.Section.Id).Select(i => i.Element.TopTwips + height(i)).DefaultIfEmpty(0).Max()) });
            }
            return result;
        }
        int MoveFresh(int position, ReportLayoutSession? owner)
        {
            // Match a surviving future item, not a consumed text tail whose origin moved to the page edge.
            var anchor = fresh.Items.Where(n => n.Owner == owner && n.Element.TopTwips <= position
                && previous.Items.Any(p => SameItem(p, n) && p.Element.TopTwips >= offset))
                .OrderByDescending(n => n.Element.TopTwips).FirstOrDefault();
            if (anchor is null) return position;
            var moved = items.FirstOrDefault(i => SameItem(i, anchor));
            return moved is null ? position : position + moved.Element.TopTwips - anchor.Element.TopTwips;
        }
        int Move(int position) => MoveFor(position, null);
        int MoveFor(int position, ReportLayoutSession? fixedOwner) => position + shifts.Where(s => s.Bottom <= position && (fixedOwner is null || s.Owner._depth > fixedOwner._depth))
            .OrderBy(s => s.Bottom).Select(s => s.Delta).DefaultIfEmpty(0).Last();
        bool Overlaps(Item left, Item right) => left.Element.Kind is not ("Line" or "Box") && right.Element.Kind is not ("Line" or "Box")
            && left.Element.TopTwips + height(left) > offset && right.Element.TopTwips + height(right) > offset
            && left.Element.LeftTwips < right.Element.LeftTwips + right.Element.WidthTwips && right.Element.LeftTwips < left.Element.LeftTwips + left.Element.WidthTwips
            && left.Element.TopTwips < right.Element.TopTwips + height(right) && right.Element.TopTwips < left.Element.TopTwips + height(left);
    }

    private static bool SameItem(Item left, Item right) => left.Owner == right.Owner && left.Row == right.Row && left.Element.Id == right.Element.Id;
}
