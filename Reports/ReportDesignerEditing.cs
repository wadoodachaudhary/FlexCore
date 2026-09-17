namespace Fx.ControlKit.Reports;

public static partial class ReportDesignerEditing
{
    public static int GetGroupNumber(ReportDesignerDocument document, ReportDesignerSection section) =>
        document.Groups.FindIndex(group => group.Id == section.GroupId) + 1;

    internal static void AssignSectionGroups(ReportDesignerDocument document)
    {
        Assign("GroupHeader", document.Groups);
        Assign("GroupFooter", document.Groups.AsEnumerable().Reverse().ToList());

        void Assign(string kind, IReadOnlyList<ReportDesignerGroup> groups)
        {
            var areas = document.Sections.Where(section => section.Kind == kind)
                .GroupBy(section => string.IsNullOrEmpty(section.AreaId) ? section.AreaName : section.AreaId).ToList();
            for (var index = 0; index < areas.Count; index++)
                foreach (var section in areas[index])
                    section.GroupId = index < groups.Count ? groups[index].Id : "";
        }
    }

    /// <summary>Reorders whole areas, retaining subsections and refusing destructive group removal.</summary>
    public static void ApplyGroups(ReportDesignerDocument document, List<ReportDesignerGroup> groups)
    {
        var previousGroups = document.Groups.ToList();
        if (document.Groups.Select(group => group.Id).SequenceEqual(groups.Select(group => group.Id)))
        {
            document.Groups = groups;
            SyncGroupReferences();
            return;
        }
        var removed = document.Groups.Where(group => groups.All(next => next.Id != group.Id)).ToList();
        foreach (var group in removed)
        {
            if (document.Sections.Any(section => section.GroupId == group.Id && section.Elements.Count != 0) ||
                document.Summaries.Any(summary => summary.GroupName == group.Condition) ||
                document.RunningTotals.SelectMany(t => new[] { t.Evaluation, t.Reset }).Any(c => c.Type == "OnChangeOfGroup" && c.Group == previousGroups.IndexOf(group) + 1))
                throw new InvalidOperationException($"Group '{group.Condition}' still owns report objects or summaries. Remove those dependencies before deleting the group.");
        }
        if (document.Sections.Any(section => section.Kind is "GroupHeader" or "GroupFooter" && string.IsNullOrEmpty(section.GroupId)))
            throw new InvalidOperationException("Some group areas have no matching group definition. Resolve them before changing the group structure.");

        var headers = Bands("GroupHeader", groups);
        var footers = Bands("GroupFooter", groups.AsEnumerable().Reverse());
        var sections = document.Sections.Where(section => section.Kind is not "GroupHeader" and not "GroupFooter").ToList();
        var detail = sections.FindIndex(section => section.Kind == "Detail");
        sections.InsertRange(detail < 0 ? 0 : detail, headers);
        var afterDetail = sections.FindLastIndex(section => section.Kind == "Detail") + 1;
        sections.InsertRange(afterDetail == 0 ? headers.Count : afterDetail, footers);
        var removedConditions = removed.Select(group => group.Condition).ToHashSet(StringComparer.Ordinal);
        document.Sorts.RemoveAll(sort => sort.SortType == "GroupSortField" && removedConditions.Contains(sort.Field.Reference));
        document.Groups = groups;
        document.Sections = sections;
        SyncGroupReferences();

        void SyncGroupReferences()
        {
            foreach (var group in groups)
                foreach (var sort in document.Sorts.Where(s => s.SortType == "GroupSortField" && s.Field.Reference == group.Condition))
                    sort.Direction = group.SortDirection;
            foreach (var condition in document.RunningTotals.SelectMany(t => new[] { t.Evaluation, t.Reset }))
                if (condition.Type == "OnChangeOfGroup" && condition.Group > 0 && condition.Group <= previousGroups.Count)
                    condition.Group = groups.FindIndex(g => g.Id == previousGroups[condition.Group - 1].Id) + 1;
        }

        List<ReportDesignerSection> Bands(string kind, IEnumerable<ReportDesignerGroup> order) => order.SelectMany(group =>
        {
            var existing = document.Sections.Where(section => section.Kind == kind && section.GroupId == group.Id).ToList();
            if (existing.Count > 0)
                return existing;
            var identity = Guid.NewGuid().ToString("N");
            return new List<ReportDesignerSection>
            {
                new() { Name = $"{kind}Section{identity}", Kind = kind, AreaName = $"{kind}Area{identity}", AreaId = identity, GroupId = group.Id }
            };
        }).ToList();
    }

    public static void SetOrientation(ReportDesignerPage page, string orientation)
    {
        if (page.Orientation == orientation)
            return;
        page.Orientation = orientation;
        (page.ContentWidthTwips, page.ContentHeightTwips) =
            (page.ContentHeightTwips + page.MarginTopTwips + page.MarginBottomTwips - page.MarginLeftTwips - page.MarginRightTwips,
             page.ContentWidthTwips + page.MarginLeftTwips + page.MarginRightTwips - page.MarginTopTwips - page.MarginBottomTwips);
    }

    public static void SetPaperSize(ReportDesignerPage page, string paperSize)
    {
        var (width, height) = paperSize switch
        {
            "PaperLetter" => (12240, 15840),
            "PaperLegal" => (12240, 20160),
            "PaperA4" => (11906, 16838),
            "PaperA3" => (16838, 23811),
            "PaperTabloid" => (15840, 24480),
            _ => throw new ArgumentOutOfRangeException(nameof(paperSize))
        };
        if (page.Orientation == "Landscape")
            (width, height) = (height, width);
        page.PaperSize = paperSize;
        page.ContentWidthTwips = width - page.MarginLeftTwips - page.MarginRightTwips;
        page.ContentHeightTwips = height - page.MarginTopTwips - page.MarginBottomTwips;
    }

    public static string? ValidatePage(ReportDesignerPage page) =>
        page.ContentWidthTwips <= 0 || page.ContentHeightTwips <= 0 ||
        new[] { page.MarginLeftTwips, page.MarginRightTwips, page.MarginTopTwips, page.MarginBottomTwips }.Any(value => value < 0)
            ? "Printable width and height must be positive, and margins cannot be negative." : null;

    public static void SetMargin(ReportDesignerPage page, string side, int twips)
    {
        switch (side)
        {
            case "left":
                page.ContentWidthTwips += page.MarginLeftTwips - twips;
                page.MarginLeftTwips = twips;
                break;
            case "right":
                page.ContentWidthTwips += page.MarginRightTwips - twips;
                page.MarginRightTwips = twips;
                break;
            case "top":
                page.ContentHeightTwips += page.MarginTopTwips - twips;
                page.MarginTopTwips = twips;
                break;
            case "bottom":
                page.ContentHeightTwips += page.MarginBottomTwips - twips;
                page.MarginBottomTwips = twips;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(side));
        }
    }
}
