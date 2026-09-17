using System.Text.Json;
using System.Xml.Linq;

namespace Fx.ControlKit.Reports;

public static partial class ReportDesignerEditing
{
    public static bool ResizeSelection(ReportDesignerDocument document, IEnumerable<string> ids, int dx, int dy)
    {
        var elements = Selection(document, ids); RequireEditable(document, elements);
        if (elements.Count == 0) return false;
        if (elements.Select(e => e.SectionId).Distinct().Count() > 1) throw new InvalidOperationException("Resize a selection within one section.");
        var left = elements.Min(e => e.LeftTwips); var top = elements.Min(e => e.TopTwips);
        var width = elements.Max(e => e.LeftTwips + e.WidthTwips) - left; var height = elements.Max(e => e.TopTwips + e.HeightTwips) - top;
        var bounds = new ReportObjectBounds(left, top, width, height);
        var next = bounds.Transform(dx, dy, "se", document.Page.ContentWidthTwips, 0);
        if (next == bounds) return false;
        foreach (var element in elements)
        {
            var sx = next.Width / (double)Math.Max(1, width); var sy = next.Height / (double)Math.Max(1, height);
            new ReportObjectBounds(left + (int)Math.Round((element.LeftTwips - left) * sx), top + (int)Math.Round((element.TopTwips - top) * sy),
                (int)Math.Round(element.WidthTwips * sx), (int)Math.Round(element.HeightTwips * sy)).Apply(element);
        }
        GrowSections(document, elements);
        return true;
    }
    public static List<ReportDesignerElement> Selection(ReportDesignerDocument document, IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        return document.Elements.Where(e => selected.Contains(e.Id)).ToList();
    }
    public static string SectionSuffix(ReportDesignerDocument document, ReportDesignerSection section)
    {
        var siblings = document.Sections.Where(s => SameArea(s, section)).ToList();
        if (siblings.Count <= 1) return "";
        var number = siblings.IndexOf(section) + 1; var suffix = "";
        while (number > 0) { number--; suffix = (char)('a' + number % 26) + suffix; number /= 26; }
        return " " + suffix;
    }

    public static bool MoveSelection(ReportDesignerDocument document, IEnumerable<string> ids, int dx, int dy)
    {
        var elements = Selection(document, ids);
        RequireEditable(document, elements);
        if (elements.Count == 0) return false;
        dx = Math.Clamp(dx, -elements.Min(e => e.LeftTwips), Math.Max(0, document.Page.ContentWidthTwips - elements.Max(e => e.LeftTwips + e.WidthTwips)));
        dy = Math.Max(dy, -elements.Min(e => e.TopTwips));
        if (dx == 0 && dy == 0) return false;
        foreach (var element in elements) { element.LeftTwips += dx; element.TopTwips += dy; }
        GrowSections(document, elements);
        return true;
    }

    public static void Arrange(ReportDesignerDocument document, IEnumerable<string> ids, string anchorId, string command)
    {
        var elements = Selection(document, ids);
        RequireEditable(document, elements);
        if (elements.Count < 2) throw new InvalidOperationException("Select at least two objects.");
        var anchor = elements.FirstOrDefault(e => e.Id == anchorId) ?? elements[0];
        if (elements.Select(e => e.SectionId).Distinct().Count() > 1 && command is not ("left" or "right" or "center" or "width"))
            throw new InvalidOperationException("This arrangement requires objects in the same section.");
        var proposed = elements.ToDictionary(e => e, ReportObjectBounds.Of);
        if (command is "distribute-x" or "distribute-y")
        {
            if (elements.Count < 3) throw new InvalidOperationException("Select at least three objects to distribute.");
            var horizontal = command == "distribute-x";
            var sorted = elements.OrderBy(e => horizontal ? e.LeftTwips : e.TopTwips).ToList();
            int Start(ReportDesignerElement e) => horizontal ? e.LeftTwips : e.TopTwips;
            int Size(ReportDesignerElement e) => horizontal ? e.WidthTwips : e.HeightTwips;
            var gap = (Start(sorted[^1]) + Size(sorted[^1]) - Start(sorted[0]) - sorted.Sum(Size)) / (double)(sorted.Count - 1);
            var position = (double)Start(sorted[0]);
            foreach (var element in sorted)
            {
                var coordinate = (int)Math.Round(position);
                proposed[element] = horizontal ? proposed[element] with { Left = coordinate } : proposed[element] with { Top = coordinate };
                position += Size(element) + gap;
            }
        }
        else foreach (var element in elements)
            proposed[element] = command switch
            {
                "left" => proposed[element] with { Left = anchor.LeftTwips },
                "center" => proposed[element] with { Left = anchor.LeftTwips + (anchor.WidthTwips - element.WidthTwips) / 2 },
                "right" => proposed[element] with { Left = anchor.LeftTwips + anchor.WidthTwips - element.WidthTwips },
                "top" => proposed[element] with { Top = anchor.TopTwips },
                "middle" => proposed[element] with { Top = anchor.TopTwips + (anchor.HeightTwips - element.HeightTwips) / 2 },
                "bottom" => proposed[element] with { Top = anchor.TopTwips + anchor.HeightTwips - element.HeightTwips },
                "width" => proposed[element] with { Width = anchor.WidthTwips },
                "height" => proposed[element] with { Height = anchor.HeightTwips },
                "size" => proposed[element] with { Width = anchor.WidthTwips, Height = anchor.HeightTwips },
                _ => throw new ArgumentException("Unknown object arrangement.", nameof(command))
            };
        if (proposed.Values.Any(b => b.Left < 0 || b.Top < 0 || b.Left + b.Width > document.Page.ContentWidthTwips))
            throw new InvalidOperationException("The arrangement would place an object outside the printable width or above its section.");
        foreach (var entry in proposed) entry.Value.Apply(entry.Key);
        GrowSections(document, elements);
    }

    public static void ChangeLayer(ReportDesignerDocument document, IEnumerable<string> ids, string command)
    {
        var elements = Selection(document, ids); RequireEditable(document, elements);
        var selected = elements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var section in document.Sections.Where(s => s.Elements.Any(e => selected.Contains(e.Id))))
        {
            var all = section.Elements;
            switch (command)
            {
                case "front": section.Elements = all.Where(e => !selected.Contains(e.Id)).Concat(all.Where(e => selected.Contains(e.Id))).ToList(); break;
                case "back": section.Elements = all.Where(e => selected.Contains(e.Id)).Concat(all.Where(e => !selected.Contains(e.Id))).ToList(); break;
                case "forward":
                    for (var i = all.Count - 2; i >= 0; i--) if (selected.Contains(all[i].Id) && !selected.Contains(all[i + 1].Id)) (all[i], all[i + 1]) = (all[i + 1], all[i]);
                    break;
                case "backward":
                    for (var i = 1; i < all.Count; i++) if (selected.Contains(all[i].Id) && !selected.Contains(all[i - 1].Id)) (all[i], all[i - 1]) = (all[i - 1], all[i]);
                    break;
                default: throw new ArgumentException("Unknown layer operation.", nameof(command));
            }
        }
    }

    public static void MoveToSection(ReportDesignerDocument document, IEnumerable<string> ids, ReportDesignerSection destination)
    {
        var elements = Selection(document, ids); RequireEditable(document, elements);
        RequireSection(document, destination);
        foreach (var element in elements.Where(e => e.SectionId != destination.Id))
        {
            document.Sections.First(s => s.Elements.Contains(element)).Elements.Remove(element);
            element.SectionId = destination.Id; destination.Elements.Add(element);
        }
        GrowSections(document, elements);
    }

    public static ReportDesignerSection InsertSection(ReportDesignerDocument document, ReportDesignerSection after)
    {
        RequireSection(document, after);
        var section = CloneSection(after);
        section.Id = Guid.NewGuid().ToString("N");
        section.Elements.Clear(); section.HeightTwips = 360;
        var suffix = 2; var stem = after.Kind + "Section";
        while (document.Sections.Any(s => s.Name.Equals(stem + suffix, StringComparison.OrdinalIgnoreCase))) suffix++;
        section.Name = stem + suffix;
        document.Sections.Insert(document.Sections.IndexOf(after) + 1, section);
        return section;
    }

    public static void MoveSection(ReportDesignerDocument document, ReportDesignerSection section, int offset)
    {
        RequireSection(document, section);
        var index = document.Sections.IndexOf(section); var next = index + Math.Sign(offset);
        if (next < 0 || next >= document.Sections.Count || !SameArea(section, document.Sections[next]))
            throw new InvalidOperationException("Subsections can only move within their own area.");
        RequireSection(document, document.Sections[next]);
        (document.Sections[index], document.Sections[next]) = (document.Sections[next], document.Sections[index]);
    }

    public static void DeleteSection(ReportDesignerDocument document, ReportDesignerSection section)
    {
        RequireSection(document, section);
        if (section.Elements.Count > 0) throw new InvalidOperationException("Move or delete this section's objects before deleting the section.");
        if (document.Sections.Count(s => SameArea(s, section)) <= 1) throw new InvalidOperationException("Keep at least one section in each report area.");
        document.Sections.Remove(section);
    }

    public static ReportDesignerSection SplitSection(ReportDesignerDocument document, ReportDesignerSection section, int atTwips)
    {
        RequireSection(document, section);
        if (atTwips < 15 || atTwips > section.HeightTwips - 15) throw new InvalidOperationException("The split must be inside the section.");
        if (section.Elements.Any(e => e.TopTwips < atTwips && e.TopTwips + e.HeightTwips > atTwips))
            throw new InvalidOperationException("The split crosses an object. Move the split or resize the object first.");
        var moved = section.Elements.Where(e => e.TopTwips >= atTwips).ToList();
        RequireEditable(document, moved);
        var child = InsertSection(document, section);
        child.HeightTwips = section.HeightTwips - atTwips; section.HeightTwips = atTwips;
        foreach (var element in moved) { section.Elements.Remove(element); element.SectionId = child.Id; element.TopTwips -= atTwips; child.Elements.Add(element); }
        return child;
    }

    public static void MergeSection(ReportDesignerDocument document, ReportDesignerSection section)
    {
        RequireSection(document, section);
        var next = document.Sections.ElementAtOrDefault(document.Sections.IndexOf(section) + 1);
        if (next is null || !SameArea(section, next)) throw new InvalidOperationException("There is no following subsection in this area to merge.");
        RequireSection(document, next); RequireEditable(document, next.Elements);
        var left = CloneSection(section); var right = CloneSection(next);
        foreach (var item in new[] { left, right }) { item.Id = item.SourceKey = item.Name = ""; item.HeightTwips = 0; item.Elements.Clear(); }
        if (JsonSerializer.Serialize(left) != JsonSerializer.Serialize(right) || !XNode.DeepEquals(Metadata(section), Metadata(next)))
            throw new InvalidOperationException("These sections have different formatting or conditions. Reconcile them before merging.");
        foreach (var element in next.Elements) { element.TopTwips += section.HeightTwips; element.SectionId = section.Id; section.Elements.Add(element); }
        section.HeightTwips += next.HeightTwips; document.Sections.Remove(next);

        XElement? Metadata(ReportDesignerSection item)
        {
            if (!document.SourceSections.TryGetValue(item.SourceKey, out var source)) return null;
            var copy = new XElement(source); copy.Element("ReportObjects")?.Remove();
            copy.Attribute("Name")?.Remove(); copy.Attribute("Height")?.Remove();
            copy.DescendantNodes().OfType<XText>().Where(text => text.Parent?.HasElements == true && string.IsNullOrWhiteSpace(text.Value)).Remove();
            return copy;
        }
    }

    public static void FitSection(ReportDesignerDocument document, ReportDesignerSection section)
    {
        RequireSection(document, section);
        section.HeightTwips = Math.Max(15, section.Elements.Select(e => e.TopTwips + e.HeightTwips).DefaultIfEmpty(15).Max());
    }

    public static ReportDesignerSection CloneSection(ReportDesignerSection section) => JsonSerializer.Deserialize<ReportDesignerSection>(JsonSerializer.Serialize(section))!;
    private static bool SameArea(ReportDesignerSection left, ReportDesignerSection right) => left.Kind == right.Kind && left.GroupId == right.GroupId &&
        (string.IsNullOrEmpty(left.AreaId) ? left.AreaName == right.AreaName : left.AreaId == right.AreaId);
    private static void RequireSection(ReportDesignerDocument document, ReportDesignerSection section)
    {
        if (!document.Sections.Contains(section)) throw new InvalidOperationException("The section no longer belongs to this document.");
        if (section.ReadOnly) throw new InvalidOperationException("The section is read-only.");
    }
    public static void RequireEditable(ReportDesignerDocument document, IReadOnlyList<ReportDesignerElement> elements)
    {
        if (elements.Any(e => e.LockSizePosition || !document.Sections.Any(s => s.Elements.Contains(e) && !s.ReadOnly)))
            throw new InvalidOperationException("The selection contains a locked object or read-only section.");
    }
    private static void GrowSections(ReportDesignerDocument document, IEnumerable<ReportDesignerElement> elements)
    {
        foreach (var section in document.Sections.Where(s => elements.Any(e => e.SectionId == s.Id)))
            section.HeightTwips = Math.Max(section.HeightTwips, section.Elements.Select(e => e.TopTwips + e.HeightTwips).DefaultIfEmpty(0).Max());
    }
}
