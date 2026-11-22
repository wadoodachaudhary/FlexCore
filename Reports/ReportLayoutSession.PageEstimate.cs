namespace Fx.ControlKit.Reports;

/// <summary>One section of the report as the page estimator sees it: its designed height and the formatting that
/// decides where pages break.</summary>
public sealed record ReportPageEstimateSection(string Name, string Kind, int HeightTwips, int GroupLevel)
{
    public bool NewPageBefore { get; init; }
    public bool NewPageAfter { get; init; }
    public bool KeepTogether { get; init; }
    public bool KeepGroupTogether { get; init; }
    public bool PrintAtBottomOfPage { get; init; }
    public bool Underlay { get; init; }
    public bool SuppressIfBlank { get; init; }
    public bool RepeatHeader { get; init; }
    public bool ResetPageNumberAfter { get; init; }
    public bool InColumns { get; init; }
    /// <summary>The names of the section's and its area's conditional formats (EnableNewPageAfter, EnableSuppress, ...).</summary>
    public IReadOnlyList<string> Conditions { get; init; } = [];
    /// <summary>Whether an instance of this section prints is decided while printing: its suppress formula reads the
    /// page or what an earlier section or a subreport left in a variable. A listed instance may then not print.</summary>
    public bool SuppressAtPrintTime { get; init; }
    public int GrowingObjects { get; init; }
    /// <summary>Object kinds in the section with their counts, for example "Field:6,Subreport:1".</summary>
    public string Objects { get; init; } = "";
}

/// <summary>What is known about a report's length before any page is laid out: the page, the sections, and every
/// section instance in print order.</summary>
public sealed record ReportPageEstimateInput(int ContentHeightTwips, int ContentWidthTwips, int PageHeaderTwips, int PageFooterTwips,
    IReadOnlyList<ReportPageEstimateSection> Sections, int[] Order, int[] Rows, int Records)
{
    /// <summary>A new page starts before this instance.</summary>
    public const byte NewPageBefore = 1;
    /// <summary>A new page starts after this instance.</summary>
    public const byte NewPageAfter = 2;
    /// <summary>This instance prints at the bottom of its page.</summary>
    public const byte PrintAtBottom = 4;
    /// <summary>This instance lies under the sections that follow it and takes no room.</summary>
    public const byte Underlay = 8;
    /// <summary>This instance is listed for its formulas only and prints nothing.</summary>
    public const byte Suppressed = 16;
    /// <summary>This instance's section is suppressed when blank and every object in it is empty for its record: it
    /// prints nothing.</summary>
    public const byte Blank = 32;

    /// <summary>For each instance of <see cref="Order"/>, the formats that hold for that record: a format set by a
    /// formula can differ from one record to the next. Empty when only the sections' own formats are known.</summary>
    public byte[] Flags { get; init; } = [];
    /// <summary>For each instance of <see cref="Order"/>, the height in twips it is expected to print at: the designed
    /// height, or more where a growing text object needs more lines for its record (measured as Crystal breaks the
    /// lines, without the browser). Empty when not measured; then every instance has its section's designed height.</summary>
    public int[] Heights { get; init; } = [];
    /// <summary>How many instances had their growing objects measured for <see cref="Heights"/>. Past the measuring
    /// budget, an unmeasured instance takes the last measured height of its section.</summary>
    public int MeasuredInstances { get; init; }
    /// <summary>Columns across the page for sections printed in columns; 1 without a multiple-column layout.</summary>
    public int ColumnsAcross { get; init; } = 1;
    public int ColumnDetailHeightTwips { get; init; }
    public bool ColumnsAcrossThenDown { get; init; }
    public int VisibleRecordsPerPage { get; init; }
    public int Subreports { get; init; }
    /// <summary>Names of the page header and page footer sections that take room on the page (an underlay does not):
    /// they print on every page and are not in the order.</summary>
    public IReadOnlyList<string> FurnitureSections { get; init; } = [];
}

/// <summary>How a report viewer uses the page estimator. Every report lays out its first pages; one that is then
/// expected to be long, or has many records, goes on in batches and shows the estimate until its last page is laid
/// out.</summary>
public sealed record ReportPageEstimateOptions
{
    /// <summary>False lays every report out in full before its page count shows, as without the estimator.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Pages every report lays out before the first estimate is used.</summary>
    public int FirstBatchPages { get; init; } = 25;
    /// <summary>Pages laid out per batch after the first.</summary>
    public int BatchPages { get; init; } = 25;
    /// <summary>Pages a report that runs queries per record (its subreports) lays out before its first page shows. One that
    /// ends within them opens with its exact count.</summary>
    public int OpeningPages { get; init; } = 10;
    /// <summary>Printed lines such a report lays out before its first page shows, so its estimate has enough to go on: it
    /// goes past <see cref="OpeningPages"/> for them, up to <see cref="FirstBatchPages"/>.</summary>
    public int OpeningPrintedLines { get; init; } = 500;
    /// <summary>Pages laid out at a time after the opening for a report that runs queries per record, since each page costs queries.</summary>
    public int SubreportBatchPages { get; init; } = 1;
    /// <summary>A report estimated at more pages than this goes on in batches.</summary>
    public int EstimatedPagesThreshold { get; init; } = 50;
    /// <summary>A report with more records than this goes on in batches.</summary>
    public int RecordsThreshold { get; init; } = 2500;
    /// <summary>Growing text objects measured at most for one estimate input; past it, instances are sampled.
    /// Measuring takes about a microsecond an object, and a sampled report's estimate drifts by a few pages.</summary>
    public int MeasureBudget { get; init; } = 1_000_000;
    /// <summary>The two estimates agree when within this share of each other.</summary>
    public double AgreeWithin { get; init; } = 0.03;

    public static ReportPageEstimateOptions Default { get; } = new();

    /// <summary>True when a report with this estimate and this many records goes on in batches after its first pages.</summary>
    public bool UsesBatches(int estimatedPages, int records) =>
        Enabled && (estimatedPages > EstimatedPagesThreshold || records > RecordsThreshold);
}

public sealed partial class ReportLayoutSession
{
    /// <summary>For each page of the last pagination pass, the index (into the estimate input's order) of the section
    /// instance being placed when the page was opened.</summary>
    public IReadOnlyList<int> PageStartBands => _pageStartBands;
    private readonly List<int> _pageStartBands = [];


    /// <summary>The estimator's view of this run before layout: no section is formatted or measured for it.</summary>
    public ReportPageEstimateInput CreatePageEstimateInput(ReportPageEstimateOptions? options = null)
    {
        var measureBudget = Math.Max(1, (options ?? ReportPageEstimateOptions.Default).MeasureBudget);
        var document = _layout.Document;
        var page = document.Page;
        var columns = _layout.Columns;
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var sections = new List<ReportPageEstimateSection>();
        // A listed instance's section is that record's copy, with the record's formula results applied; the section's
        // own formats are read from the design.
        var design = new Dictionary<string, ReportDesignerSection>(StringComparer.Ordinal);
        foreach (var authored in document.Sections) design.TryAdd(authored.Id, authored);
        int Index(ReportDesignerSection instance)
        {
            if (index.TryGetValue(instance.Id, out var known)) return known;
            var section = design.GetValueOrDefault(instance.Id, instance);
            var area = _layout.Areas.GetValueOrDefault(section.Id);
            var conditions = new List<string>();
            if (_layout.SectionConditions.TryGetValue(section.Id, out var own)) conditions.AddRange(own.Keys);
            if (_layout.AreaConditions.TryGetValue(section.Id, out var areas)) conditions.AddRange(areas.Keys.Select(key => "Area." + key));
            sections.Add(new(section.Name, section.Kind, section.HeightTwips, ReportDesignerEditing.GetGroupNumber(document, section))
            {
                NewPageBefore = section.NewPageBefore || area?.NewPageBefore == true,
                NewPageAfter = section.NewPageAfter || area?.NewPageAfter == true,
                KeepTogether = section.KeepTogether || area?.KeepTogether == true,
                KeepGroupTogether = area?.KeepGroupTogether == true,
                PrintAtBottomOfPage = section.PrintAtBottomOfPage || area?.PrintAtBottomOfPage == true,
                Underlay = section.UnderlayFollowingSections,
                SuppressIfBlank = section.SuppressIfBlank,
                RepeatHeader = area?.RepeatHeader == true,
                ResetPageNumberAfter = section.ResetPageNumberAfter || area?.ResetPageNumberAfter == true,
                InColumns = columns is not null && (section.Kind == "Detail" || columns.FormatGroups && section.Kind is "GroupHeader" or "GroupFooter"),
                Conditions = conditions,
                SuppressAtPrintTime = PageSuppresses(_layout.AreaConditions, section.Id) || PageSuppresses(_layout.SectionConditions, section.Id)
                    || MutableCondition(_layout.AreaConditions, section.Id, "EnableSuppress") || MutableCondition(_layout.SectionConditions, section.Id, "EnableSuppress")
                    || MutableCondition(_layout.AreaConditions, section.Id, "EnableHideForDrillDown") || MutableCondition(_layout.SectionConditions, section.Id, "EnableHideForDrillDown"),
                GrowingObjects = section.Elements.Count(element => element.CanGrow),
                Objects = string.Join(",", section.Elements.GroupBy(element => element.Kind).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => group.Key + ":" + group.Count()))
            });
            return index[section.Id] = sections.Count - 1;
        }
        var furniture = document.Sections.Where(InDrillScope).ToList();
        var order = new int[_bands.Count];
        var rows = new int[_bands.Count];
        var flags = new byte[_bands.Count];
        var heights = new int[_bands.Count];
        // Measuring every growing object of a long report would cost as much as laying it out: past the budget, every
        // stride-th instance of a section is measured and the others take the last measured height of that section.
        var growingWork = 0L;
        var blank = new bool[_bands.Count];
        for (var band = 0; band < _bands.Count; band++)
        {
            var listed = _bands[band];
            if (listed.Suppressed) continue;
            // A blank instance prints nothing: it is neither measured nor counted toward the budget.
            if (listed.Section.SuppressIfBlank && IsBlank(listed)) { blank[band] = true; continue; }
            growingWork += listed.Items.Count(item => item.Element.CanGrow);
        }
        var stride = (int)Math.Max(1, (growingWork + measureBudget - 1) / measureBudget);
        var seenOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var measured = 0;
        for (var band = 0; band < _bands.Count; band++)
        {
            var instance = _bands[band].Section;
            order[band] = Index(instance);
            rows[band] = _bands[band].Row;
            var area = _layout.Areas.GetValueOrDefault(instance.Id);
            flags[band] = (byte)((instance.NewPageBefore || area?.NewPageBefore == true ? ReportPageEstimateInput.NewPageBefore : 0)
                | (instance.NewPageAfter || area?.NewPageAfter == true ? ReportPageEstimateInput.NewPageAfter : 0)
                | (instance.PrintAtBottomOfPage || area?.PrintAtBottomOfPage == true ? ReportPageEstimateInput.PrintAtBottom : 0)
                | (instance.UnderlayFollowingSections ? ReportPageEstimateInput.Underlay : 0)
                | (_bands[band].Suppressed ? ReportPageEstimateInput.Suppressed : 0));
            var listed = _bands[band];
            if (listed.Suppressed) continue;
            if (blank[band]) { flags[band] |= ReportPageEstimateInput.Blank; continue; }
            if (!listed.Items.Any(item => item.Element.CanGrow)) { heights[band] = instance.HeightTwips; continue; }
            var seen = seenOf.GetValueOrDefault(instance.Id);
            seenOf[instance.Id] = seen + 1;
            if (seen % stride != 0 && lastOf.TryGetValue(instance.Id, out var last)) { heights[band] = last; continue; }
            heights[band] = lastOf[instance.Id] = EstimatedBandHeight(listed);
            measured++;
        }
        var across = 1;
        if (columns is { DetailWidth: > 0 })
            across = Math.Max(1, (page.ContentWidthTwips + columns.HorizontalGap) / (columns.DetailWidth + columns.HorizontalGap));
        return new(page.ContentHeightTwips, page.ContentWidthTwips,
            furniture.Where(section => section.Kind == "PageHeader" && !section.IsSuppressed && !section.UnderlayFollowingSections).Sum(section => section.HeightTwips),
            furniture.Where(section => section.Kind == "PageFooter" && !section.IsSuppressed && !section.UnderlayFollowingSections).Sum(section => section.HeightTwips),
            sections, order, rows, _rows.Length)
        {
            Flags = flags, Heights = heights, MeasuredInstances = measured, ColumnsAcross = across, ColumnDetailHeightTwips = columns?.DetailHeight ?? 0, ColumnsAcrossThenDown = columns?.AcrossThenDown ?? false,
            VisibleRecordsPerPage = _layout.VisibleRecordsPerPage, Subreports = _layout.Subreports.Count,
            FurnitureSections = furniture.Where(section => (section.Kind is "PageHeader" or "PageFooter") && !section.UnderlayFollowingSections).Select(section => section.Name).Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    // A band's expected height for its record: as pagination's band height (the designed height, or the bottom of the
    // lowest object), with each growing object measured and the objects below it pushed down by its growth.
    private static int EstimatedBandHeight(Band band)
    {
        var items = band.Items;
        var grown = new int[items.Count];
        for (var index = 0; index < items.Count; index++)
            grown[index] = items[index].Element is { CanGrow: true, IsSuppressed: false } ? EstimatedItemHeight(items[index]) : items[index].Element.HeightTwips;
        var bottom = band.Section.HeightTwips;
        for (var index = 0; index < items.Count; index++)
        {
            var element = items[index].Element;
            if (element.IsSuppressed) continue;
            var push = 0;
            for (var other = 0; other < items.Count; other++)
            {
                var e = items[other].Element;
                if (other == index || !e.CanGrow || e.IsSuppressed || e.TopTwips + e.HeightTwips > element.TopTwips) continue;
                push = Math.Max(push, grown[other] - e.HeightTwips);
            }
            bottom = Math.Max(bottom, element.TopTwips + push + grown[index]);
        }
        return bottom;
    }

    // A growing object's height for its record without the browser: Crystal's printer line breaks where its font is
    // measurable, else pagination's own approximate font rule.
    private static int EstimatedItemHeight(Item item)
    {
        var element = item.Element;
        if (!element.CanGrow || element.HasRotatedText || element.Kind is not ("Text" or "Field" or "FieldHeading")) return element.HeightTwips;
        if (CrystalDeviceText.Measure(element, item.Html) is { } native) return Math.Max(element.HeightTwips, native.Height);
        var text = System.Text.RegularExpressions.Regex.Replace(item.Html, "<[^>]+>", "");
        var font = Math.Max(element.FontSize, element.Visual.Runs.Select(run => run.FontSize).DefaultIfEmpty(1).Max());
        var capacity = Math.Max(1, (int)(element.WidthTwips / Math.Max(1, font * 10)));
        var lines = text.Split('\n').Sum(line => Math.Max(1, Math.Ceiling(line.Length / (double)capacity)));
        if (ReportObjectRenderer.TextLineLimit(element) is > 0 and var limit) lines = Math.Min(lines, limit);
        return Math.Max(element.HeightTwips, (int)Math.Ceiling(lines * (double)font * 23));
    }
}
