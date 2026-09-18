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
        bool OwnedBand(Band source) => owners.Contains((source.Owner, source.Section.Id, source.Row));
        var ownedItems = band.Items.Where(Owned).ToArray();
        var neighbors = band.Items.Where(i => !Owned(i) && i.Element.TopTwips < end && i.Element.TopTwips + height(i) > start).ToArray();
        var overlap = neighbors.Any(n => ownedItems.Any(i => n.Element.LeftTwips < i.Element.LeftTwips + i.Element.WidthTwips
            && i.Element.LeftTwips < n.Element.LeftTwips + n.Element.WidthTwips));
        if (overlap) (band.Owner ?? inline.Source.Owner)!._diagnostics.Add($"{inline.Source.Section.Name}: suppressed continuation overlaps other content; its reserved space is retained.");
        // Independent columns keep their occupied space. Only the common, now-empty tail can collapse.
        var collapseStart = overlap ? end : Math.Min(end, neighbors.Select(i => i.Element.TopTwips + height(i)).DefaultIfEmpty(start).Max());
        var items = new List<Item>();
        foreach (var item in band.Items)
        {
            var top = item.Element.TopTwips; var bottom = top + height(item);
            if (Owned(item) && bottom > start) continue;
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
            InlineSections = band.InlineSections!.Select(s => s with { Start = OwnedBand(s.Source) ? Math.Min(s.Start, start) : Move(s.Start),
                End = OwnedBand(s.Source) ? Math.Min(s.End, start) : Move(s.End) }).ToList(),
            InlineHeaders = band.InlineHeaders?.Select(h => h with { Start = OwnedBand(h.Header) ? Math.Min(h.Start, start) : Move(h.Start),
                End = OwnedBand(h.Header) ? Math.Min(h.End, start) : Move(h.End) }).Where(h => h.End > h.Start).ToList()
        };
        int Move(int position) => position <= collapseStart ? position : position < end ? collapseStart : position - (end - collapseStart);
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
            .Any(i => i.ConsumedText is not null || TextMetrics(i)?.Lines.Any(l => l.Top < offset - i.Element.TopTwips && l.Bottom > offset - i.Element.TopTwips) == true
                || fresh.Items.Any(n => SameItem(i, n)
                && (n.Html != i.Html || ReportObjectRenderer.Style(n.Element, false) != ReportObjectRenderer.Style(i.Element, false))));
        if (!changing && previous.Items.All(i => i.ConsumedText is null && !i.OmittedContinuation)) return fresh;
        var added = fresh.Items.Where(n => !previous.Items.Any(i => SameItem(i, n))).ToList();
        foreach (var item in added.Where(n => n.Element.TopTwips < offset || !KnownTail(n)).ToArray())
        {
            _diagnostics.Add($"{item.Element.Name}: newly visible content would enter printed space; it is omitted from this continuation."); added.Remove(item);
        }
        bool KnownTail(Item item) => item.Element.SectionId == fresh.Section.Id && item.Owner == fresh.Owner
            || fresh.InlineSections?.Any(s => s.Source.Owner == item.Owner && s.Source.Row == item.Row
                && s.Source.Section.Id == item.Element.SectionId && s.End >= offset) == true;

        var items = new List<Item>();
        var shifts = new List<(int Bottom, int Delta, Item Item)>();
        foreach (var item in previous.Items.OrderBy(i => i.Element.TopTwips))
        {
            var next = fresh.Items.FirstOrDefault(n => SameItem(item, n));
            if (next is null)
            {
                if (item.Element.TopTwips < offset && item.Element.TopTwips + height(item) > offset)
                    _diagnostics.Add($"{item.Element.Name}: a hidden continuation removes only its unprinted tail.");
                continue;
            }
            var oldBottom = item.Element.TopTwips + height(item);
            if (oldBottom <= offset) { items.Add(item); continue; }
            var element = next.Element.CloneFor(next.Element.SectionId, next.Element.Name); element.Id = next.Element.Id;
            element.LeftTwips = next.Element.LeftTwips;
            element.TopTwips = MoveFor(item.Element.TopTwips, item);
            next = next with { Element = element };
            if (item.OmittedContinuation || next.Element.IsSuppressed && item.Element.TopTwips < offset)
            {
                element.IsSuppressed = true; element.TopTwips = offset;
                items.Add(next with { OmittedContinuation = true });
                shifts.Add((oldBottom, offset - oldBottom, item));
                _diagnostics.Add($"{element.Name}: suppression removes the remaining text occurrence; later page visibility does not replay its printed prefix.");
                continue;
            }
            if (item.Element.TopTwips < offset && item.Element.CanGrow && item.Element.Kind is "Text" or "Field" or "FieldHeading")
            {
                var metric = TextMetrics(item) ?? throw new NotSupportedException("Changing-text continuation requires browser character-offset measurements.");
                var oldText = TextTree(item.Html).Value;
                if (metric.Lines.Any(l => l.Start < 0 || l.End > oldText.Length))
                    throw new InvalidDataException("Text continuation received invalid or missing character offsets.");
                var consumed = metric.Lines.Where(l => l.Bottom <= offset - item.Element.TopTwips).Select(l => l.End).DefaultIfEmpty(0).Max();
                var prefix = (item.ConsumedText ?? "") + oldText[..consumed];
                var tree = TextTree(next.Html);
                var remaining = prefix.Length;
                if (!tree.Value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    _diagnostics.Add($"{item.Element.Name}: a page-dependent value changed an already printed prefix; the previous value is retained for this continuation.");
                    tree = TextTree(item.Html); remaining = consumed;
                }
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
            shifts.Add((oldBottom, element.TopTwips + height(next) - oldBottom, item));
        }
        foreach (var item in added)
        {
            var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name); element.Id = item.Element.Id;
            element.LeftTwips = item.Element.LeftTwips; element.TopTwips = MoveFresh(item.Element.TopTwips, item.Owner);
            if (element.TopTwips < offset)
            { _diagnostics.Add($"{element.Name}: newly visible nested content moved into printed space and is omitted from this continuation."); continue; }
            items.Add(item with { Element = element });
        }
        var omitted = new HashSet<Item>();
        for (var i = 0; i < items.Count; i++)
        for (var j = i + 1; j < items.Count; j++)
        {
            var left = items[i]; var right = items[j];
            if (!Overlaps(left, right)) continue;
            var beforeLeft = previous.Items.FirstOrDefault(p => !p.Element.IsSuppressed && SameItem(p, left));
            var beforeRight = previous.Items.FirstOrDefault(p => !p.Element.IsSuppressed && SameItem(p, right));
            if (beforeLeft is null || beforeRight is null || !Overlaps(beforeLeft, beforeRight))
            {
                if (beforeLeft is null) omitted.Add(left);
                if (beforeRight is null) omitted.Add(right);
                _diagnostics.Add($"{left.Element.Name}/{right.Element.Name}: continuation overlap retains existing content; newly conflicting objects are omitted.");
            }
        }
        items.RemoveAll(omitted.Contains);
        var section = new ReportDesignerSection
        {
            Id = fresh.Section.Id, Name = fresh.Section.Name, Kind = fresh.Section.Kind,
            HeightTwips = Math.Max(offset, Move(previous.Section.HeightTwips)),
            BackgroundColor = fresh.Section.BackgroundColor, KeepTogether = fresh.Section.KeepTogether, NewPageBefore = fresh.Section.NewPageBefore,
            NewPageAfter = fresh.Section.NewPageAfter, PrintAtBottomOfPage = fresh.Section.PrintAtBottomOfPage,
            ResetPageNumberAfter = fresh.Section.ResetPageNumberAfter, RelativePositions = fresh.Section.RelativePositions,
            SuppressIfBlank = fresh.Section.SuppressIfBlank, UnderlayFollowingSections = fresh.Section.UnderlayFollowingSections
        };
        var inlineSections = MergeSections();
        return fresh with
        {
            Items = items, Section = section,
            Breaks = MergeBreaks(previous.Breaks, fresh.Breaks), ForcedBreaks = MergeBreaks(previous.ForcedBreaks, fresh.ForcedBreaks),
            InlineSections = inlineSections,
            InlineHeaders = fresh.InlineHeaders?.Select(next =>
            {
                var old = previous.InlineHeaders?.FirstOrDefault(h => h.Header.Owner == next.Header.Owner
                    && h.Header.Row == next.Header.Row && h.Header.Section.Id == next.Header.Section.Id);
                return next with { Start = old is null ? MoveFresh(next.Start, next.Header.Owner) : Move(old.Start),
                    End = old is null ? MoveFresh(next.End, next.Header.Owner) : Move(old.End) };
            }).ToList()
        };
        List<int> MergeBreaks(List<int>? old, List<int>? current)
        {
            // Fresh nested trees can introduce breaks absent from the printed prefix. Map them through
            // their owning section, whose origin may differ from a consumed text object's origin.
            return (old ?? []).Where(p => p <= offset).Concat((current ?? []).Select(MapBoundary).Where(p => p >= offset)).Distinct().Order().ToList();
        }
        int MapBoundary(int position)
        {
            var source = fresh.InlineSections?.Where(s => s.Start == position || s.End == position)
                .OrderByDescending(s => s.Source.Owner!._depth).ThenBy(s => s.End - s.Start).FirstOrDefault();
            var merged = source is null ? null : inlineSections?.FirstOrDefault(s => s.Source == source.Source);
            return merged is null ? MoveFresh(position, fresh.Owner) : source!.Start == position ? merged.Start : merged.End;
        }
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
        int MoveFor(int position, Item? target) => position + shifts.Where(s => s.Bottom <= position && (target is null || Affects(s.Item, target)))
            .OrderBy(s => s.Bottom).Select(s => s.Delta).DefaultIfEmpty(0).Last();
        bool Affects(Item changed, Item target)
        {
            var sourceOwner = changed.Owner ?? this; var targetOwner = target.Owner ?? this;
            if (sourceOwner == targetOwner)
                return changed.Row == target.Row && changed.Element.SectionId == target.Element.SectionId
                    && targetOwner._layout.Document.Sections.First(s => s.Id == target.Element.SectionId).RelativePositions;
            return sourceOwner.IsDescendantOf(targetOwner) && changed.Element.LeftTwips < target.Element.LeftTwips + target.Element.WidthTwips
                && target.Element.LeftTwips < changed.Element.LeftTwips + changed.Element.WidthTwips;
        }
        bool Overlaps(Item left, Item right) => left.Element.Kind is not ("Line" or "Box") && right.Element.Kind is not ("Line" or "Box")
            && left.Element.TopTwips + height(left) > offset && right.Element.TopTwips + height(right) > offset
            && left.Element.LeftTwips < right.Element.LeftTwips + right.Element.WidthTwips && right.Element.LeftTwips < left.Element.LeftTwips + left.Element.WidthTwips
            && left.Element.TopTwips < right.Element.TopTwips + height(right) && right.Element.TopTwips < left.Element.TopTwips + height(left);
    }

    private static bool SameItem(Item left, Item right) => left.Owner == right.Owner && left.Row == right.Row && left.Element.Id == right.Element.Id;
    private bool IsDescendantOf(ReportLayoutSession ancestor)
    {
        for (var parent = _parent; parent is not null; parent = parent._parent) if (parent == ancestor) return true;
        return false;
    }
}
