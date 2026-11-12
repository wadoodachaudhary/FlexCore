using System.Data;

namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    // A mutable section may be hidden for thousands of records. Build its child only
    // after the physical print pass has established that this occurrence is visible.
    private readonly Dictionary<(string Element, int Row), (ReportLayoutSession? Child, int Offset)?> _deferredSubreports = new();
    // Page header and footer subreports linked to the page: one for each page number and page count they print with.
    private readonly Dictionary<(string Element, int Row, int Page, int Count), (ReportLayoutSession? Child, int Offset)> _pagedFurniture = new();
    // Page header and footer subreports built again for one physical page, from the link values in effect where they print there.
    private readonly Dictionary<(string Element, int Row, int Physical), (ReportLayoutSession? Child, int Offset)> _replayedFurniture = new();
    // Page header and footer subreports that read no variables, by the records they print: one for each distinct query and parameters.
    private readonly Dictionary<(string Element, DataTable Data), List<(SubreportRun Run, ReportLayoutSession Child, int Offset)>> _sharedFurniture = new();
    private readonly Dictionary<string, bool> _statelessSubreports = new(StringComparer.Ordinal);

    private Item ResolveDeferredSubreport(Item item) => item.Child is null
        && _deferredSubreports.GetValueOrDefault((item.Element.Id, item.Row)) is { } resolved
        ? item with { Child = resolved.Child, ChildMeasurementOffset = resolved.Offset } : item;

    // The item prints a subreport's records: built with its section, or once that section printed.
    private bool HasChild(Item item) => item.Child is not null || _deferredSubreports.GetValueOrDefault((item.Element.Id, item.Row)) is { Child: not null };

    private bool PrepareDeferredSubreport(Item item)
    {
        var key = (item.Element.Id, item.Row);
        if (!_deferredSubreports.TryGetValue(key, out var existing) || existing.HasValue || item.Element.IsSuppressed) return false;
        var saved = CaptureVariables();
        var cache = new Dictionary<(string Name, int Row), object?>(_bandFormulaCache);
        try
        {
            var child = CreateInlineSubreport(item.Element, item.Row, initializePrintContext: true);
            var offset = Measurements.Count;
            if (child is not null)
            {
                Measurements.AddRange(child.Measurements);
                foreach (var native in child._nativeMetrics) _nativeMetrics.TryAdd(native.Key, native.Value);
            }
            _deferredSubreports[key] = (child, offset);
            return true;
        }
        finally
        {
            RestoreVariables(saved);
            _bandFormulaCache.Clear();
            foreach (var value in cache) _bandFormulaCache.Add(value.Key, value.Value);
        }
    }

    private readonly Dictionary<string, CrystalEvaluationTime> _evaluationTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object?> _beforeValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DataRow, Dictionary<string, object?>> _readValues = new();
    private readonly List<string> _scheduledFormulas = [];
    private readonly Dictionary<string, string> _schedulingErrors = new(StringComparer.OrdinalIgnoreCase);
    private sealed record VariableSnapshot(Dictionary<string, object?> Globals, Dictionary<string, object?> Shared);
    private readonly Dictionary<int, VariableSnapshot> _rowBefore = new();
    private readonly Dictionary<int, VariableSnapshot> _rowAfter = new();
    private bool _preparingFurniture;
    // Above zero while one band is evaluated: its layout (CreateBand) or the physical page replay. Only then is a formula's
    // failure kept in the band cache, so a second reader in the band raises it again instead of running the formula (and
    // its assignments) twice; a failure never outlives its band, and in the replay, when its formula reads the page (or a
    // variable the page furniture assigns) and assigns only temporaries, never its page.
    private int _failureScope;
    // While the physical page replay runs: for each failure in a band cache that may run again on a later page, the values
    // the variables its formula reads had when it failed, or the page furniture has left them since (PageReads, ReadValues).
    private Dictionary<((string Name, int Row) Key, Exception Failure), ReadValue[]>? _failureReads;
    // The failures noted in _failureReads since the band cache was last stored (ReplayPhysicalPages, Store).
    private List<((string Name, int Row) Key, Exception Failure)>? _newFailures;
    // On the run's main session (Sessions[0]): how many times a formula or condition that assigns a global or shared variable
    // has failed, or stopped at a null value, in the run's reports. One that fails or stops while the page furniture prints
    // may have set some of its variables and not the rest (Furnish).
    private int _assignmentFailures;
    private bool _physicalPageSchedule;
    private bool _replayingPrint;
    private bool _initializingPrintContext;
    private VariableSnapshot? _printStart;
    private sealed record PrintBandKey(ReportLayoutSession Owner, string Section, int Row, int Occurrence);
    private sealed record PrintItemKey(PrintBandKey Band, string Element);
    private sealed record PrintSectionFormat(bool Suppressed, bool Hidden, bool Before, bool After, bool Keep, bool Bottom, bool Underlay, bool Reset, string Background)
    {
        public static PrintSectionFormat Read(ReportDesignerSection s) => new(s.IsSuppressed, s.HideForDrillDown, s.NewPageBefore, s.NewPageAfter,
            s.KeepTogether, s.PrintAtBottomOfPage, s.UnderlayFollowingSections, s.ResetPageNumberAfter, s.BackgroundColor);
        public void Apply(ReportDesignerSection s)
        {
            s.IsSuppressed = Suppressed; s.HideForDrillDown = Hidden; s.NewPageBefore = Before; s.NewPageAfter = After;
            s.KeepTogether = Keep; s.PrintAtBottomOfPage = Bottom; s.UnderlayFollowingSections = Underlay;
            s.ResetPageNumberAfter = Reset; s.BackgroundColor = Background;
        }
    }

    // A cached first-pass formula is immutable during printing, even if it used variables to compute its value.
    private bool NeedsPrintState(string reference)
    {
        if (!_layout.Formulas.TryGetValue(reference, out var formula)) return false;
        if (EvaluationTime(reference) != CrystalEvaluationTime.WhilePrintingRecords) return false;
        return formula.UsesPersistentVariables || formula.References.Any(NeedsPrintState);
    }

    private bool NeedsPrintState(CrystalFormula formula) => formula.UsesPersistentVariables || formula.References.Any(NeedsPrintState);

    // An object's own binding that carries print state. A binding the schedule refused computes nothing and changes no
    // variable, so a hidden or suppressed object skips it (NeedsPrintState would raise the refusal again); a printed
    // object still shows its formula error.
    private bool BindsPrintState(string reference) => !_schedulingErrors.ContainsKey(reference) && NeedsPrintState(reference);

    private static bool VisibilityProperty(string property) => property.ToLowerInvariant() is "enablesuppress" or "enablehidefordrilldown";
    private static bool EndingProperty(string property) => property.ToLowerInvariant() is "enablenewpageafter" or "enableresetpagenumberafter" or "backgroundcolor";
    private bool HasScheduledSection(string section) => HasScheduledCondition(_layout.AreaConditions, section) || HasScheduledCondition(_layout.SectionConditions, section);
    private bool HasScheduledCondition(Dictionary<string, Dictionary<string, CrystalFormula>> table, string section)
    {
        if (!table.TryGetValue(section, out var conditions)) return false;
        foreach (var condition in conditions.Values)
            if (NeedsPrintState(condition) || _physicalPageSchedule && UsesPage(condition)) return true;
        return false;
    }
    private bool MutableCondition(Dictionary<string, Dictionary<string, CrystalFormula>> table, string section, string property)
    {
        if (!table.TryGetValue(section, out var conditions)) return false;
        foreach (var condition in conditions)
            if (condition.Key.Equals(property, StringComparison.OrdinalIgnoreCase) && (NeedsPrintState(condition.Value) || _physicalPageSchedule && UsesPage(condition.Value))) return true;
        return false;
    }
    private bool HasPrintFields(string section) => _layout.Document.Sections.First(s => s.Id == section).Elements
        .SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(ValueBinding(e))).Any(BindsPrintState);
    private bool HasVisibilityEvent(string section) => HasScheduledSection(section) || _physicalPageSchedule && (HasPrintFields(section)
        || _layout.Document.Sections.First(s => s.Id == section).Elements.Any(e => _layout.Subreports.ContainsKey(e.Id)
            || (_layout.ObjectConditions.GetValueOrDefault(e.Id)?.Values.Any(NeedsPrintState) ?? false)));

    private void PreparePhysicalPageSchedule()
    {
        var furniture = _layout.Document.Sections.Where(s => s.Kind is "PageHeader" or "PageFooter")
            .SelectMany(s => s.Elements).SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(e.Binding).Append(ValueBinding(e)));
        var needsReplay = _layout.Document.Elements.Any(e => e.Analysis?.GridPrintFormulas().Any() == true)
            || _scheduledFormulas.Any(r => DependsOn(r, true) && NeedsPrintState(r))
            || furniture.Any(r => !_schedulingErrors.ContainsKey(r) && NeedsPrintState(r))
            || _layout.ObjectConditions.Values.Concat(_layout.SectionConditions.Values).Concat(_layout.AreaConditions.Values).SelectMany(c => c.Values).Any(NeedsPrintState);
        needsReplay |= _scheduledFormulas.Any(NeedsPrintState) && _layout.SectionConditions.Values.Concat(_layout.AreaConditions.Values)
            .SelectMany(c => c).Any(c => VisibilityProperty(c.Key));
        needsReplay |= _scheduledFormulas.Any(NeedsPrintState) && _layout.Document.Sections.Any(s => s.IsSuppressed || s.HideForDrillDown
            || _layout.Areas.GetValueOrDefault(s.Id) is { } area && (area.Suppressed || area.Hidden));
        if (_depth == 0)
        {
            var layouts = new HashSet<ReportPositionedLayout>();
            void Visit(ReportPositionedLayout layout)
            {
                if (!layouts.Add(layout)) return;
                if (_executeSubreport is not null)
                    foreach (var child in layout.Subreports.Values) if (child.Definition.PositionedLayout is { } nested) Visit(nested);
            }
            Visit(_layout);
            bool UsesState(ReportPositionedLayout layout)
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool Reference(string reference) => visited.Add(reference) && layout.Formulas.TryGetValue(reference, out var f)
                    && (f.UsesPersistentVariables || f.References.Any(Reference));
                var conditions = layout.SectionConditions.Values.Concat(layout.AreaConditions.Values).Concat(layout.ObjectConditions.Values).SelectMany(c => c.Values);
                return layout.Document.Elements.Any(e => e.Analysis?.GridPrintFormulas().Any() == true)
                    || layout.Document.Elements.SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(e.Binding).Append(ValueBinding(e)).Append(e.HighlightRule.FieldName)).Any(Reference)
                    || conditions.Any(f => f.UsesPersistentVariables || f.References.Any(Reference));
            }
            bool PageLinks(ReportPositionedLayout layout)
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool Page(string reference) => visited.Add(reference) && (layout.Formulas.TryGetValue(reference, out var f)
                    ? f.UsesPageContext || f.References.Any(Page) : SpecialName(reference) is "pagenumber" or "totalpagecount" or "pagenofm");
                return layout.Subreports.Values.SelectMany(s => s.Links).Any(l => Page(l.MainField));
            }
            needsReplay |= layouts.Count > 1 && layouts.Any(UsesState) || layouts.Any(PageLinks);
            _state.PhysicalSchedule = needsReplay;
        }
        _physicalPageSchedule = _state.PhysicalSchedule || needsReplay;
        _printStart = CaptureVariables();
        if (!_physicalPageSchedule) return;
        if (_layout.Summaries.Values.Concat(_layout.RunningTotals.Values).Any(s => NeedsPrintState(s.Field))
            || _scheduledFormulas.Any(r => _layout.Formulas[r].AggregateReferences.Any(NeedsPrintState))
            || _layout.RunningTotals.Values.SelectMany(t => new[] { t.Evaluation.Formula, t.Reset.Formula })
                .Any(f => f is not null && (f.UsesPersistentVariables || f.References.Any(NeedsPrintState))))
            throw new NotSupportedException($"{_layout.Document.SourceName}: summaries, running totals and running-total conditions over variable-dependent print-time formulas are not supported.");
    }

    private Band ApplyPrintedItems(Band source, int physicalPage, bool repeated)
    {
        if (!_state.PhysicalSchedule || _state.PrintedItems.Count == 0) return source;
        var items = source.Items.Select(item =>
        {
            var owner = item.Owner ?? source.Owner ?? this;
            var occurrence = repeated || source.Section.Kind is "PageHeader" or "PageFooter" ? physicalPage : 0;
            var key = new PrintItemKey(new(owner, item.Element.SectionId, item.Row, occurrence), item.Element.Id);
            if (!_state.PrintedItems.TryGetValue(key, out var evaluated)) return item;
            var element = evaluated.Element.CloneFor(item.Element.SectionId, item.Element.Name); element.Id = item.Element.Id;
            ReportObjectBounds.Of(item.Element).Apply(element);
            return item with { Element = element, Html = evaluated.Html, TemplateHtml = evaluated.Html, Measurement = -2, DuplicateValue = evaluated.DuplicateValue };
        }).ToList();
        return source with { Items = items };
    }

    private PrintBandKey FragmentKey(Band band) => new(band.Owner ?? this, band.Section.Id, band.Row, 0);
    private Dictionary<PrintBandKey, int> LastFragmentOffsets(List<Page> pages) => pages.SelectMany(p => p.Placements)
        .GroupBy(p => FragmentKey(p.Band)).ToDictionary(g => g.Key, g => g.Max(p => p.Offset));

    // What the next pass is laid out from: what the last replay printed and formatted, the page context and the subreports
    // created so far. A pass that starts from a state seen before repeats the passes after it, so the layout never settles.
    private (long, long, int) ReplayState(int pages, IReadOnlyList<int> counts, IReadOnlyList<int> footerRows, Dictionary<Band, (int Page, int Count)> inline)
    {
        long first = 0, second = 0;
        void Add(int a, int b) { first += a; second += b; }
        foreach (var (key, item) in _state.PrintedItems)
        {
            var style = ReportObjectRenderer.Style(item.Element, false);
            Add(HashCode.Combine(key, item.Html, item.Element.CanGrow, item.Element.IsSuppressed, style), HashCode.Combine(style, item.Html, key, 1));
        }
        foreach (var (key, visible) in _state.PrintedVisibility) Add(HashCode.Combine(key, visible, 2), HashCode.Combine(3, visible, key));
        foreach (var (key, format) in _state.PrintedSections) Add(HashCode.Combine(key, format, 4), HashCode.Combine(5, format, key));
        foreach (var (key, blank) in _state.PrintedBlank) Add(HashCode.Combine(key, blank, 6), HashCode.Combine(7, blank, key));
        foreach (var (band, context) in inline)
            Add(HashCode.Combine(band.Owner, band.Section.Id, band.Row, context, 8), HashCode.Combine(9, context, band.Row, band.Section.Id, band.Owner));
        var sequence = new HashCode();
        sequence.Add(pages);
        foreach (var count in counts) sequence.Add(count);
        foreach (var row in footerRows) sequence.Add(row);
        sequence.Add(_state.Sessions.Sum(session => session._deferredSubreports.Values.Count(child => child.HasValue) + session._pagedFurniture.Count + session._replayedFurniture.Count));
        return (first, second, sequence.ToHashCode());
    }

    private bool ReplayPhysicalPages(List<Page> pages, IReadOnlyList<int> counts)
    {
        if (!_state.PhysicalSchedule) return false;
        var saved = _state.Sessions.ToDictionary(s => s, s => (Variables: s.CaptureVariables(), Page: s._formulaPage, Count: s._formulaPages, Repeated: s._repeatedHeader));
        var printed = new Dictionary<PrintItemKey, Item>();
        var visibility = new Dictionary<PrintBandKey, bool>();
        var formats = new Dictionary<PrintBandKey, PrintSectionFormat>();
        var endings = new HashSet<PrintBandKey>();
        var hiddenValues = new HashSet<PrintBandKey>();
        var linked = new HashSet<ReportLayoutSession>();
        var furnitureLinks = new HashSet<(ReportLayoutSession Child, int Page)>();
        var blanks = new Dictionary<(ReportLayoutSession Owner, string Section, int Occurrence), bool>();
        var caches = new Dictionary<PrintBandKey, Dictionary<(string Name, int Row), object?>>();
        var leaving = new Dictionary<PrintBandKey, bool>();
        var lastOffsets = LastFragmentOffsets(pages);
        // Every band cache is stored through Store, which notes the bands whose cache holds a failure for the next page break,
        // and the failures that may run again on a later page, with what they read (held: one for each formula of a band). A
        // cache is stored for the band it was loaded for, so only the failures noted since then are new to it.
        var failing = new HashSet<PrintBandKey>();
        var held = new Dictionary<(PrintBandKey Band, (string Name, int Row) Key), (ReportLayoutSession Owner, Exception Failure, ReadValue[] Reads)>();
        void Store(PrintBandKey key, ReportLayoutSession owner)
        {
            caches[key] = new(owner._bandFormulaCache);
            foreach (var value in owner._bandFormulaCache.Values) if (value is Exception) { failing.Add(key); break; }
            if (owner._newFailures is not { Count: > 0 } added || owner._failureReads is not { } snapshots) return;
            foreach (var (entry, failure) in added)
            {
                if (!snapshots.TryGetValue((entry, failure), out var reads)) continue;
                if (!owner._bandFormulaCache.TryGetValue(entry, out var value) || !ReferenceEquals(value, failure)) { snapshots.Remove((entry, failure)); continue; }
                if (held.TryGetValue((key, entry), out var old) && !ReferenceEquals(old.Failure, failure)) snapshots.Remove((entry, old.Failure));
                held[(key, entry)] = (owner, failure, reads);
            }
            added.Clear();
        }
        // The band's own formula fields, in its order (a hidden band's too: an assignment a later section reads still runs).
        // A formula that fails here is diagnosed as Format diagnoses a printed one, and the replay goes on. With placed, only
        // the objects placed above part (in the band's own coordinates) run: the rest print, and run, on a later page, except
        // the formulas in read, which run in the band's order.
        static void BandFormulas(ReportLayoutSession owner, ReportDesignerSection original, int row, bool assignmentsOnly = false, Band? placed = null, int part = int.MaxValue,
            HashSet<string>? read = null)
        {
            var later = placed is null || part == int.MaxValue ? null : placed.Items
                .Where(i => ReferenceEquals(i.Owner ?? placed.Owner ?? owner, owner) && i.Element.SectionId == original.Id && i.Element.TopTwips >= part)
                .Select(i => i.Element.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var reference in original.Elements.OrderBy(e => e.TopTwips)
                .SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(ValueBinding(e)).Where(r => later?.Contains(e.Id) != true || read?.Contains(r) == true))
                .Where(r => owner._layout.Formulas.ContainsKey(r) && !owner._schedulingErrors.ContainsKey(r) && (!assignmentsOnly || owner.NeedsPrintState(r))))
            {
                try { _ = owner.Value(reference, row); }
                catch (Exception ex) when (ex is FormatException or InvalidDataException or InvalidCastException or OverflowException or DivideByZeroException or NotSupportedException or ArgumentException)
                { owner._diagnostics.Add($"{reference}: {ex.Message}"); }
            }
        }
        // A failure no band cache holds any more (its formula ran again, or its band finished or is hidden, so its cache is
        // never loaded again) never runs again, and is forgotten: at each page break and before the page furniture.
        void Prune()
        {
            List<(PrintBandKey Band, (string Name, int Row) Key)>? gone = null;
            foreach (var (entry, (_, failure, _)) in held)
                if (endings.Contains(entry.Band) || hiddenValues.Contains(entry.Band) || !Holds(caches.GetValueOrDefault(entry.Band), entry.Key, failure)) (gone ??= []).Add(entry);
            if (gone is null) return;
            foreach (var entry in gone)
                if (held.Remove(entry, out var forgotten)) forgotten.Owner._failureReads?.Remove((entry.Key, forgotten.Failure));
        }
        // The page furniture (a page header or footer, or a repeated group header) sets variables for its page: once it has
        // printed, what a held failure read takes the value the furniture left, unless a band changed it since the failure, or
        // the furniture computes it from a band's variable that changed since then, or a formula that assigns a variable
        // failed while it printed (Rebase, from the values before the furniture, and what the section printed assigns).
        Action Furnish(Band band)
        {
            Prune();
            var furniture = (band.Owner ?? this)._layout;
            var section = furniture.Document.Sections.FirstOrDefault(s => s.Id == band.Section.Id) ?? band.Section;
            var before = held.Values.Select(h => (h.Owner, h.Reads, Before: h.Owner.BeforeFurniture(h.Reads))).ToList();
            var failures = _state.Sessions[0]._assignmentFailures;
            return () =>
            {
                var failed = _state.Sessions[0]._assignmentFailures != failures;
                foreach (var (owner, reads, values) in before) owner.Rebase(reads, values, furniture, section, failed);
            };
        }
        static bool Holds(Dictionary<(string Name, int Row), object?>? cache, (string Name, int Row) key, Exception failure) =>
            cache is not null && cache.TryGetValue(key, out var value) && (ReferenceEquals(value, failure) || value is PageFailure dropped && ReferenceEquals(dropped.Failure, failure));
        foreach (var session in saved.Keys) { session._failureScope++; session._failureReads = new(); session._newFailures = []; }
        var preparedChildren = false;
        try
        {
            foreach (var session in _state.Sessions)
            {
                session._globals.Clear();
                foreach (var value in session._printStart!.Globals) session._globals.Add(value.Key, value.Value);
            }
            _state.Shared.Clear();
            foreach (var value in _printStart!.Shared) _state.Shared.Add(value.Key, value.Value);
            for (var pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                // Crystal decides a section's New Page Before on the page it leaves: with that page's number, and before the next page's
                // header formats (a header that resets a variable must not decide the break that brought it). Only the band that opens
                // the page counts; a band continued from the previous page opened nothing, and an underlay's continuation (placed
                // before the sections it underlays) does not hide the band that did.
                if (pageIndex > 0 && pages[pageIndex].Placements
                        .Where(p => p.Sequence >= 0 && !p.Band.RepeatedHeader && !p.Band.Suppressed && !p.Band.Blank && p.Band.Section.Kind != "PageFooter"
                            && !(p.Offset > 0 && p.Band.Section.UnderlayFollowingSections))
                        .OrderBy(p => p.Sequence).FirstOrDefault() is { Offset: 0 } opener
                    && (opener.Band.Owner ?? this) is var leavingOwner
                    && (leavingOwner.MutableCondition(leavingOwner._layout.SectionConditions, opener.Band.Section.Id, "EnableNewPageBefore")
                        || leavingOwner.MutableCondition(leavingOwner._layout.AreaConditions, opener.Band.Section.Id, "EnableNewPageBefore")))
                {
                    var key = new PrintBandKey(leavingOwner, opener.Band.Section.Id, opener.Band.Row, 0);
                    leavingOwner._formulaPage = pages[pageIndex - 1].Number; leavingOwner._formulaPages = counts[pageIndex - 1]; leavingOwner._repeatedHeader = false;
                    leavingOwner._bandFormulaCache.Clear();
                    if (caches.TryGetValue(key, out var existing)) foreach (var value in existing) leavingOwner._bandFormulaCache.Add(value.Key, value.Value);
                    leavingOwner._replayingPrint = true;
                    try
                    {
                        var original = leavingOwner._layout.Document.Sections.First(s => s.Id == opener.Band.Section.Id);
                        // Crystal formats the band before it decides the break (its formulas run first, and the condition reads their values);
                        // the band formats again on the page it opens, after that page's header, as a band moved there for want of room does.
                        // So neither the values of this pass nor what its formulas assigned are kept; what deciding the condition assigned
                        // stays. What formats is the part the band prints where the break is decided: for a band its own break moved here,
                        // the part the page it leaves would have printed in this layout (Page.BrokenPart); otherwise its first part on this page.
                        // A formula of the band that the condition reads formats here too, wherever it prints, so it counts once.
                        var formatted = new Dictionary<(string Name, int Row), object?>(leavingOwner._bandFormulaCache);
                        var unformatted = leavingOwner.CaptureVariables();
                        BandFormulas(leavingOwner, original, opener.Band.Row, placed: opener.Band,
                            part: pages[pageIndex].BrokenPart is { } broken && broken.Band == key ? broken.Part : opener.Offset + opener.Height,
                            read: leavingOwner.ConditionReads(opener.Band.Section.Id, "EnableNewPageBefore"));
                        var assigned = leavingOwner.CaptureVariables();
                        leaving[key] = leavingOwner.ApplySectionConditions(leavingOwner.ApplySectionConditions(original, opener.Band.Row, only: "EnableNewPageBefore"),
                            opener.Band.Row, true, only: "EnableNewPageBefore").NewPageBefore;
                        leavingOwner.UndoAssignments(unformatted, assigned);
                        leavingOwner._bandFormulaCache.Clear();
                        foreach (var value in formatted) leavingOwner._bandFormulaCache.Add(value.Key, value.Value);
                        Store(key, leavingOwner);
                    }
                    finally { leavingOwner._replayingPrint = false; }
                }
                // A band that spans pages (a detail or group band, occurrence 0) keeps one cache on all of them. Its values carry
                // over as before, and so does a failure, except when the formula reads the page (or a variable the page furniture
                // assigns) and assigns only temporaries: on this page, or a later one, it may succeed, so it runs again there while
                // nothing it reads has changed since it failed (DropPageFailures). Any other failure is raised again, as a value is
                // reused, so a counter does not count twice for one record. A band that finished or is hidden goes on to no page.
                Prune();
                foreach (var key in failing) if (!endings.Contains(key) && !hiddenValues.Contains(key)) key.Owner.DropPageFailures(caches[key]);
                failing.Clear();
                // Set by a furniture placement, run before the next placement (or the next page).
                Action? furnished = null;
                foreach (var placement in pages[pageIndex].Placements.OrderBy(p => p.PrintSequence))
                {
                    furnished?.Invoke(); furnished = null;
                    var occurrence = placement.Band.RepeatedHeader || placement.Band.Section.Kind is "PageHeader" or "PageFooter" ? pageIndex + 1 : 0;
                    if (occurrence > 0) furnished = Furnish(placement.Band);
                    var blocked = new HashSet<(ReportLayoutSession? Owner, string Section, int Row)>();
                    PrintBandKey Key(Band band) => new(band.Owner ?? this, band.Section.Id, band.Row, occurrence);
                    bool Section(Band band)
                    {
                        if (band.EvaluationOnly) return false;
                        var owner = band.Owner ?? this;
                        if (!owner.HasScheduledSection(band.Section.Id)) return !band.Suppressed;
                        var key = Key(band);
                        if (visibility.TryGetValue(key, out var visible)) return visible;
                        owner._formulaPage = pages[pageIndex].Number; owner._formulaPages = counts[pageIndex]; owner._repeatedHeader = placement.Band.RepeatedHeader;
                        owner._bandFormulaCache.Clear();
                        if (caches.TryGetValue(key, out var existing)) foreach (var value in existing) owner._bandFormulaCache.Add(value.Key, value.Value);
                        owner._replayingPrint = true;
                        try
                        {
                            var original = owner._layout.Document.Sections.First(s => s.Id == band.Section.Id);
                            // A New Page Before already decided on the page this band left is not evaluated again (its formula may assign a variable).
                            var decided = leaving.TryGetValue(key, out var before) ? "EnableNewPageBefore" : null;
                            // Crystal decides a band's New Page Before after formatting it: a condition that reads print-time state sees what the
                            // band's own formulas assigned (a page counter the band itself advances breaks on the band that passes the limit).
                            var later = decided is null && (owner.MutableCondition(owner._layout.SectionConditions, band.Section.Id, "EnableNewPageBefore")
                                || owner.MutableCondition(owner._layout.AreaConditions, band.Section.Id, "EnableNewPageBefore")) ? "EnableNewPageBefore" : null;
                            var formatted = owner.ApplySectionConditions(owner.ApplySectionConditions(original, band.Row, endingOnly: false, skip: decided ?? later), band.Row, true, endingOnly: false, skip: decided ?? later);
                            if (later is not null && owner.Visible(formatted))
                            {
                                // The part of the band this page prints formats before the break; what it prints on a later page runs there
                                // (as without a New Page Before). A band that breaks moves whole to the next page, which decides it again
                                // from the same part.
                                var part = ReferenceEquals(band, placement.Band) ? placement.Offset + placement.Height : int.MaxValue;
                                BandFormulas(owner, original, band.Row, placed: band, part: part);
                                formatted.NewPageBefore = owner.ApplySectionConditions(owner.ApplySectionConditions(original, band.Row, only: later), band.Row, true, only: later).NewPageBefore;
                            }
                            visible = owner.Visible(formatted); formats[key] = PrintSectionFormat.Read(formatted);
                            if (decided is not null) formats[key] = formats[key] with { Before = before };
                            Store(key, owner);
                            visibility.Add(key, visible); return visible;
                        }
                        finally { owner._replayingPrint = false; }
                    }
                    void Finish(Band band)
                    {
                        if (band.EvaluationOnly) return;
                        var key = Key(band); var owner = key.Owner;
                        if (!endings.Add(key)) return;
                        if (!owner.HasScheduledSection(band.Section.Id))
                        {
                            owner.CaptureOnDemandVariables(band);
                            return;
                        }
                        owner._formulaPage = pages[pageIndex].Number; owner._formulaPages = counts[pageIndex]; owner._repeatedHeader = placement.Band.RepeatedHeader;
                        owner._bandFormulaCache.Clear();
                        if (caches.TryGetValue(key, out var cache)) foreach (var value in cache) owner._bandFormulaCache.Add(value.Key, value.Value);
                        owner._replayingPrint = true;
                        try
                        {
                            var original = owner._layout.Document.Sections.First(s => s.Id == band.Section.Id);
                            var formatted = CopySection(original);
                            if (formats.TryGetValue(key, out var entry)) entry.Apply(formatted);
                            // Ending formulas observe field assignments made in this section, on its final physical page.
                            formatted = owner.ApplySectionConditions(owner.ApplySectionConditions(formatted, band.Row, endingOnly: true), band.Row, true, endingOnly: true);
                            formats[key] = PrintSectionFormat.Read(formatted); Store(key, owner);
                            owner.CaptureOnDemandVariables(band);
                        }
                        finally { owner._replayingPrint = false; }
                    }
                    // A subreport's links are evaluated when its object prints. Its query ran with the logical-pass values. A page header
                    // or footer subreport's links are evaluated on each page, for the record that page prints it with; where a value
                    // differs, the subreport is built again for that page from the values in effect there (ReplayFurniture).
                    void VerifyLink(ReportLayoutSession? child)
                    {
                        if (child?._link is not { } link) return;
                        var furniture = placement.Band.Section.Kind is "PageHeader" or "PageFooter" && ReferenceEquals(link.Owner, placement.Band.Owner ?? this)
                            && link.Section == placement.Band.Section.Id;
                        if (furniture ? !furnitureLinks.Add((child, pageIndex)) : !linked.Add(child)) return;
                        var owner = link.Owner; var row = furniture ? placement.Band.Row : link.Row;
                        var key = new PrintBandKey(owner, link.Section, row, occurrence);
                        owner._formulaPage = pages[pageIndex].Number; owner._formulaPages = counts[pageIndex]; owner._repeatedHeader = placement.Band.RepeatedHeader;
                        owner._bandFormulaCache.Clear();
                        if (caches.TryGetValue(key, out var cache)) foreach (var value in cache) owner._bandFormulaCache.Add(value.Key, value.Value);
                        owner._replayingPrint = true;
                        object?[] values;
                        try { values = link.Links.Select(l => owner.Value(l.MainField, row)).ToArray(); }
                        finally { owner._replayingPrint = false; }
                        Store(key, owner);
                        for (var index = 0; index < values.Length; index++)
                            if (!SameLinkValue(values[index], link.Values[index]))
                            {
                                if (furniture && owner.ReplayFurniture(link, row, pageIndex + 1)) { preparedChildren = true; return; }
                                throw new NotSupportedException($"{link.Element}: subreport link '{link.Links[index].MainField}' has a different value where the subreport prints on page {pages[pageIndex].Number} than in the report's logical pass; re-running linked subreports during page replay is not supported.");
                            }
                    }
                    void Block(Band band)
                    {
                        blocked.Add((band.Owner, band.Section.Id, band.Row));
                        foreach (var child in band.Items.Select(i => i.Child).OfType<ReportLayoutSession>())
                            foreach (var nested in child._bands) Block(nested);
                    }
                    void HiddenValues(Band band)
                    {
                        var key = Key(band); if (!hiddenValues.Add(key)) return;
                        var owner = key.Owner;
                        var original = owner._layout.Document.Sections.First(s => s.Id == band.Section.Id);
                        // Crystal does not execute a linked subreport inside a suppressed/hidden
                        // section (SAP KBA 1535232). Skipping it is intentional, not a limitation.
                        // Inline format assignments are separate from named formula fields.
                        if (!band.EvaluationOnly && original.Elements.Any(e =>
                            owner._layout.Subreports.TryGetValue(e.Id, out var child) && child.Links.Count == 0))
                            owner._diagnostics.Add($"{original.Name}: hidden unlinked subreport side effects are not scheduled.");
                        if (!band.EvaluationOnly && original.Elements.Any(e =>
                            owner._layout.ObjectConditions.GetValueOrDefault(e.Id)?.Values.Any(owner.NeedsPrintState) == true))
                            owner._diagnostics.Add($"{original.Name}: hidden object-format assignments are skipped; named field assignments still run.");
                        owner._formulaPage = pages[pageIndex].Number; owner._formulaPages = counts[pageIndex]; owner._repeatedHeader = placement.Band.RepeatedHeader;
                        owner._bandFormulaCache.Clear();
                        if (caches.TryGetValue(key, out var existing)) foreach (var value in existing) owner._bandFormulaCache.Add(value.Key, value.Value);
                        // Suppression removes presentation, not named formula-field assignments used by later sections.
                        BandFormulas(owner, original, band.Row, band.EvaluationOnly);
                        Store(key, owner);
                    }
                    if (!Section(placement.Band)) { HiddenValues(placement.Band); Finish(placement.Band); continue; }
                    var events = (placement.Band.InlineSections ?? [])
                        .Where(s => s.Start >= placement.Offset && (s.Start < placement.Offset + placement.Height
                            || s.Start == s.End && s.Start == placement.Offset + placement.Height && placement.Offset == lastOffsets[FragmentKey(placement.Band)]))
                        .Select(s => (Top: s.Start, Order: 1, Depth: s.Source.Owner?._depth ?? 0, Band: (Band?)s.Source, Item: (Item?)null, s.Blank))
                        .Concat((placement.Band.InlineSections ?? []).Where(s => s.End > placement.Offset && s.End <= placement.Offset + placement.Height)
                            .Select(s => (Top: s.End, Order: 0, Depth: -(s.Source.Owner?._depth ?? 0), Band: (Band?)s.Source, Item: (Item?)null, s.Blank)))
                        .Concat(placement.Band.Items.Select(i => (Top: i.Element.TopTwips, Order: 2, Depth: int.MaxValue, Band: (Band?)null, Item: (Item?)i, Blank: false)))
                        .OrderBy(e => e.Top).ThenBy(e => e.Order).ThenBy(e => e.Depth);
                    // Child bands are already positioned inside the parent. Execute only each object's first fragment.
                    foreach (var entry in events)
                    {
                        if (entry.Band is { } childBand)
                        {
                            if (entry.Order == 0) { if (!blocked.Contains((childBand.Owner, childBand.Section.Id, childBand.Row))) Finish(childBand); continue; }
                            if (blocked.Contains((childBand.Owner, childBand.Section.Id, childBand.Row))) { Block(childBand); continue; }
                            VerifyLink(childBand.Owner);
                            if (!Section(childBand)) { HiddenValues(childBand); Finish(childBand); Block(childBand); }
                            // A blank subreport section takes no space, but Crystal formatted its objects to find it blank.
                            else if (entry.Blank) { foreach (var blank in childBand.Items.OrderBy(i => i.Element.TopTwips)) Print(blank); Finish(childBand); }
                            continue;
                        }
                        var item = entry.Item!;
                        if (item.Element.TopTwips < placement.Offset || item.Element.TopTwips >= placement.Offset + placement.Height) continue;
                        Print(item);
                    }
                    if (placement.Offset == lastOffsets[FragmentKey(placement.Band)]) Finish(placement.Band);
                    if (occurrence > 0 && placement.Band.Section.SuppressIfBlank)
                    {
                        var owner = placement.Band.Owner ?? this;
                        blanks[(owner, placement.Band.Section.Id, occurrence)] = IsBlank(placement.Band with { Items = placement.Band.Items
                            .Select(i => printed.GetValueOrDefault(new(new(i.Owner ?? owner, i.Element.SectionId, i.Row, occurrence), i.Element.Id), i)).ToList() });
                    }
                    void Print(Item item)
                    {
                        var owner = item.Owner ?? placement.Band.Owner ?? this;
                        if (blocked.Contains((owner, item.Element.SectionId, item.Row))) return;
                        var bandKey = new PrintBandKey(owner, item.Element.SectionId, item.Row, occurrence);
                        var key = new PrintItemKey(bandKey, item.Element.Id);
                        if (printed.ContainsKey(key)) return;
                        if (item.AccumulatorDelta.HasValue)
                        {
                            owner.ApplyAccumulator(item);
                            printed.Add(key, item);
                            return;
                        }
                        if (item.PrintGrid is { } printGrid)
                        {
                            // Publish a grid's assignments together, once per logical occurrence across all its panes.
                            var variables = new Dictionary<string, object?>(owner._globals, StringComparer.OrdinalIgnoreCase);
                            try
                            {
                                printGrid(variables);
                                foreach (var pair in variables) owner._globals[pair.Key] = pair.Value;
                            }
                            catch (Exception error) when (error is InvalidDataException or NotSupportedException or FormatException or ArithmeticException or InvalidCastException or ArgumentException)
                            { owner._diagnostics.Add($"{item.Element.Name}: {error.Message}"); }
                            printed.Add(key, item);
                            return;
                        }
                        // Ordinary table fragments carry their already evaluated cells.
                        if (owner._layout.Document.Elements.FirstOrDefault(e => e.Id == item.Element.Id) is not { } original) return;
                        owner._formulaPage = pages[pageIndex].Number; owner._formulaPages = counts[pageIndex]; owner._repeatedHeader = placement.Band.RepeatedHeader;
                        owner._bandFormulaCache.Clear();
                        if (caches.TryGetValue(bandKey, out var cache)) foreach (var value in cache) owner._bandFormulaCache.Add(value.Key, value.Value);
                        owner._replayingPrint = true;
                        ReportDesignerElement element;
                        string html;
                        DuplicateFieldValue? duplicateValue;
                        try
                        {
                            // Resolve field values before their formatting; both share the occurrence's formula cache.
                            // Format() turns a formula fault into a field diagnostic. This prefetch runs outside Format,
                            // so the same faults must not abort the physical replay (while-printing array formulas).
                            foreach (var reference in original.Visual.Runs.Select(r => r.Binding).Prepend(ValueBinding(original)).Where(r => r.Length > 0))
                            {
                                try { _ = owner.Value(reference, item.Row); }
                                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FormatException or InvalidCastException or OverflowException or DivideByZeroException or ArgumentException)
                                { /* Format records the diagnostic when it evaluates this reference. */ }
                            }
                            element = owner.ApplyObjectConditions(owner.ApplyObjectConditions(original, item.Row), item.Row, true);
                            (html, duplicateValue) = owner.CaptureFieldContent(element, item.Row);
                        }
                        finally { owner._replayingPrint = false; }
                        preparedChildren |= owner.PrepareDeferredSubreport(item with { Element = element });
                        // An inline subreport whose query failed prints the error in place of its records, as in the layout pass.
                        if (element.Kind == "Subreport" && !owner._layout.OnDemandSubreports.ContainsKey(element.Id) && !owner.HasChild(item)
                            && owner.SubreportErrorHtml(element) is { } failed) html = failed;
                        printed.Add(key, item with { Element = element, Html = html, TemplateHtml = html, Measurement = -2, DuplicateValue = duplicateValue });
                        Store(bandKey, owner);
                    }
                }
                furnished?.Invoke();
            }
            var changed = formats.Count != _state.PrintedSections.Count || formats.Any(p => !_state.PrintedSections.TryGetValue(p.Key, out var old) || p.Value != old)
                || visibility.Count != _state.PrintedVisibility.Count || visibility.Any(p => !_state.PrintedVisibility.TryGetValue(p.Key, out var old) || p.Value != old)
                || printed.Count != _state.PrintedItems.Count || printed.Any(p => !_state.PrintedItems.TryGetValue(p.Key, out var old)
                || p.Value.Html != old.Html || p.Value.Element.CanGrow != old.Element.CanGrow || p.Value.Element.IsSuppressed != old.Element.IsSuppressed
                || ReportObjectRenderer.Style(p.Value.Element, false) != ReportObjectRenderer.Style(old.Element, false));
            _state.PrintedItems = printed;
            _state.PrintedVisibility = visibility;
            _state.PrintedSections = formats;
            _state.PrintedBlank = blanks;
            return changed || preparedChildren;
        }
        finally
        {
            foreach (var (session, before) in saved)
            {
                session.RestoreVariables(before.Variables); session._bandFormulaCache.Clear(); session._failureScope--; session._failureReads = null; session._newFailures = null;
                session._formulaPage = before.Page; session._formulaPages = before.Count; session._repeatedHeader = before.Repeated;
            }
        }
    }

    private VariableSnapshot? _emptyVariables;
    // Snapshots are only read back (RestoreVariables copies out of them), so one empty snapshot serves every row of a report without variables.
    private VariableSnapshot CaptureVariables() => _globals.Count == 0 && _state.Shared.Count == 0
        ? _emptyVariables ??= new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase))
        : new(new(_globals, StringComparer.OrdinalIgnoreCase), new(_state.Shared, StringComparer.OrdinalIgnoreCase));
    private void RestoreVariables(VariableSnapshot snapshot)
    {
        _globals.Clear(); foreach (var value in snapshot.Globals) _globals.Add(value.Key, value.Value);
        _state.Shared.Clear(); foreach (var value in snapshot.Shared) _state.Shared.Add(value.Key, value.Value);
    }
    // The formulas a section's (or its area's) condition for property reads, directly or through other formulas.
    private HashSet<string> ConditionReads(string section, string property)
    {
        var reads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(new[] { _layout.SectionConditions, _layout.AreaConditions }
            .SelectMany(table => table.GetValueOrDefault(section) ?? [])
            .Where(condition => condition.Key.Equals(property, StringComparison.OrdinalIgnoreCase)).SelectMany(condition => condition.Value.References));
        while (pending.TryPop(out var name))
            if (reads.Add(name) && _layout.Formulas.TryGetValue(name, out var formula)) foreach (var next in formula.References) pending.Push(next);
        return reads;
    }
    // Back to the variables of before, except those assigned or created since after (an assigned array is a new array).
    private void UndoAssignments(VariableSnapshot before, VariableSnapshot after)
    {
        static List<KeyValuePair<string, object?>> Since(Dictionary<string, object?> current, Dictionary<string, object?> saved) =>
            current.Where(value => !saved.TryGetValue(value.Key, out var old) || !Equals(old, value.Value)).ToList();
        var globals = Since(_globals, after.Globals); var shared = Since(_state.Shared, after.Shared);
        RestoreVariables(before);
        foreach (var value in globals) _globals[value.Key] = value.Value;
        foreach (var value in shared) _state.Shared[value.Key] = value.Value;
    }

    private Band CreateFurniture(ReportDesignerSection section, int row)
    {
        var saved = CaptureVariables();
        try
        {
            if ((section.Kind == "PageHeader" ? _rowBefore : _rowAfter).TryGetValue(row, out var snapshot)) RestoreVariables(snapshot);
            _preparingFurniture = true;
            return CreateBand(section, row, []);
        }
        finally { _preparingFurniture = false; RestoreVariables(saved); }
    }

    // A page header or footer whose formatting is decided while printing builds its subreports for each page that prints it,
    // as that page is laid out: for the record the page furnishes, with that record's variables (CreateFurniture) and the
    // page's number, so the page reserves the room they print in. A subreport linked to the page number or page count is
    // built for each page number and count it prints with. A subreport the print pass built again for this page (ReplayFurniture)
    // prints that one. A subreport whose query failed prints the error. Without build, only the subreports already built are
    // used (room estimates).
    private Band PrepareFurnitureSubreports(Band band, int number, int count, int physical, bool build = true)
    {
        if (band.Suppressed) return band;
        List<Item>? items = null;
        for (var index = 0; index < band.Items.Count; index++)
        {
            var item = band.Items[index];
            if (item.Element.Kind != "Subreport" || item.Element.IsSuppressed) continue;
            (ReportLayoutSession? Child, int Offset)? built;
            if (_replayedFurniture.TryGetValue((item.Element.Id, item.Row, physical), out var replayed)) built = replayed;
            else
            {
                if (item.Child is not null || !_deferredSubreports.TryGetValue((item.Element.Id, item.Row), out built)) continue;
                var paged = PageLinked(item.Element);
                if (paged) built = _pagedFurniture.TryGetValue((item.Element.Id, item.Row, number, count), out var variant) ? variant : null;
                if (!built.HasValue)
                {
                    if (!build) continue;
                    var saved = CaptureVariables();
                    var cache = new Dictionary<(string Name, int Row), object?>(_bandFormulaCache);
                    var (page, pages, repeated) = (_formulaPage, _formulaPages, _repeatedHeader);
                    try
                    {
                        if ((band.Section.Kind == "PageHeader" ? _rowBefore : _rowAfter).TryGetValue(item.Row, out var snapshot)) RestoreVariables(snapshot);
                        _preparingFurniture = true;
                        (_formulaPage, _formulaPages, _repeatedHeader) = (number, count, false);
                        _bandFormulaCache.Clear();
                        built = _deferredSubreports[(item.Element.Id, item.Row)] = BuildFurnitureSubreport(item.Element, item.Row);
                        if (paged) _pagedFurniture[(item.Element.Id, item.Row, number, count)] = built.Value;
                    }
                    finally
                    {
                        _preparingFurniture = false; RestoreVariables(saved);
                        (_formulaPage, _formulaPages, _repeatedHeader) = (page, pages, repeated);
                        _bandFormulaCache.Clear();
                        foreach (var value in cache) _bandFormulaCache.Add(value.Key, value.Value);
                    }
                }
            }
            var (resolved, resolvedOffset) = built.Value;
            if (resolved is not null) (items ??= [.. band.Items])[index] = item with { Child = resolved, ChildMeasurementOffset = resolvedOffset };
            else if (SubreportErrorHtml(item.Element) is { } error) (items ??= [.. band.Items])[index] = item with { Child = null, Html = error, TemplateHtml = error };
        }
        return items is null ? band : band with { Items = items };
    }

    // A page header or footer subreport whose report reads no variables prints the same for the same records and parameters:
    // the records and pages that pass the same link values share one.
    private (ReportLayoutSession? Child, int Offset) BuildFurnitureSubreport(ReportDesignerElement element, int row)
    {
        SubreportRun? run = null;
        List<(SubreportRun Run, ReportLayoutSession Child, int Offset)>? shared = null;
        if (_executeSubreport is not null && _depth < MaxSubreportDepth && _layout.Subreports.TryGetValue(element.Id, out var subreport)
            && subreport.Definition.PositionedLayout is { } layout
            && StatelessSubreport(element.Id, layout))
        {
            run = ResolveSubreportRun(element, subreport, row);
            if (run is null) return (null, Measurements.Count);
            if (!_sharedFurniture.TryGetValue((element.Id, run.Data), out shared)) _sharedFurniture[(element.Id, run.Data)] = shared = [];
            foreach (var entry in shared) if (SameRun(run, entry.Run)) return (entry.Child, entry.Offset);
        }
        var child = CreateInlineSubreport(element, row, run: run);
        var offset = Measurements.Count;
        if (child is null) return (null, offset);
        Measurements.AddRange(child.Measurements);
        foreach (var native in child._nativeMetrics) _nativeMetrics.TryAdd(native.Key, native.Value);
        shared?.Add((run!, child, offset));
        return (child, offset);
    }

    private static bool SameRun(SubreportRun run, SubreportRun other) => ReferenceEquals(run.Data, other.Data)
        && run.Parameters.Count == other.Parameters.Count && run.Parameters.All(p => other.Parameters.TryGetValue(p.Key, out var value) && Equals(p.Value, value))
        && run.Selections.SequenceEqual(other.Selections) && run.Values.Length == other.Values.Length && run.Values.Zip(other.Values).All(p => Equals(p.First, p.Second));

    private bool StatelessSubreport(string elementId, ReportPositionedLayout layout)
        => _statelessSubreports.TryGetValue(elementId, out var stateless) ? stateless : _statelessSubreports[elementId] = Stateless(layout);

    // The report, and every subreport it holds, reads and sets no variable and keeps no running total, chart or cross-tab.
    private static bool Stateless(ReportPositionedLayout layout)
    {
        // Subreports may nest deeply; they are visited from a stack, not by recursion.
        var seen = new HashSet<ReportPositionedLayout>(ReferenceEqualityComparer.Instance) { layout };
        var pending = new Stack<ReportPositionedLayout>([layout]);
        while (pending.TryPop(out var next))
        {
            var formulas = next.Formulas.Values
                .Concat(next.SectionConditions.Values.Concat(next.AreaConditions.Values).Concat(next.ObjectConditions.Values).SelectMany(c => c.Values))
                .Concat(next.GroupOptions.Values.SelectMany(o => o.SpecifiedGroups.Select(g => g.Selection).Append(o.NameFormula)))
                .Concat(next.GroupSorts.Values.Select(s => s.CountFormula)).Append(next.RecordSelection).Append(next.GroupSelection);
            if (next.RunningTotals.Count > 0 || next.Document.Elements.Any(e => e.Analysis is not null) || formulas.Any(f => f?.UsesPersistentVariables == true))
                return false;
            foreach (var subreport in next.Subreports.Values.Concat(next.OnDemandSubreports.Values))
                if (subreport.Definition.PositionedLayout is { } nested && seen.Add(nested)) pending.Push(nested);
        }
        return true;
    }

    // Builds a page header or footer subreport again for one physical page, with the values its links have where it prints
    // there (the print pass's variables, page and band cache); the next layout prints it on that page.
    private bool ReplayFurniture(SubreportLinkState link, int row, int physical)
    {
        if (_layout.Document.Sections.FirstOrDefault(s => s.Id == link.Section)?.Elements
                .FirstOrDefault(e => e.Kind == "Subreport" && e.Name == link.Element) is not { } element) return false;
        var saved = CaptureVariables();
        var cache = new Dictionary<(string Name, int Row), object?>(_bandFormulaCache);
        try
        {
            var child = CreateInlineSubreport(element, row);
            var offset = Measurements.Count;
            if (child is not null)
            {
                Measurements.AddRange(child.Measurements);
                foreach (var native in child._nativeMetrics) _nativeMetrics.TryAdd(native.Key, native.Value);
            }
            _replayedFurniture[(element.Id, row, physical)] = (child, offset);
            return true;
        }
        finally
        {
            RestoreVariables(saved);
            _bandFormulaCache.Clear();
            foreach (var value in cache) _bandFormulaCache.Add(value.Key, value.Value);
        }
    }

    // The subreport's links pass a page number or page count.
    private bool PageLinked(ReportDesignerElement element) => _layout.Subreports.TryGetValue(element.Id, out var subreport)
        && subreport.Links.Any(link => !_schedulingErrors.ContainsKey(link.MainField) && DependsOn(link.MainField, true));

    private CrystalEvaluationTime EvaluationTime(string reference, HashSet<string>? visiting = null)
    {
        if (_evaluationTimes.TryGetValue(reference, out var time)) return time;
        if (!_layout.Formulas.TryGetValue(reference, out var formula))
        {
            if (_layout.Summaries.ContainsKey(reference) || _layout.RunningTotals.ContainsKey(reference) || _layout.GroupNames.ContainsKey(reference)
                || SpecialName(reference) is "pagenumber" or "totalpagecount" or "pagenofm" or "recordnumber" or "groupnumber")
                return CrystalEvaluationTime.WhilePrintingRecords;
            return _layout.Bindings.ContainsKey(reference) ? CrystalEvaluationTime.WhileReadingRecords : CrystalEvaluationTime.BeforeReadingRecords;
        }
        visiting ??= new(StringComparer.OrdinalIgnoreCase);
        if (!visiting.Add(reference)) throw new InvalidDataException($"Circular formula scheduling dependency '{reference}'.");
        if (visiting.Count > 64) throw new InvalidDataException("Formula scheduling dependency depth exceeds 64.");
        try
        {
            foreach (var after in formula.EvaluateAfter)
                if (!_layout.Formulas.ContainsKey(after)) throw new InvalidDataException($"{reference}: EvaluateAfter target '{after}' is missing.");
            var required = RequiredTime(formula, visiting);
            if (formula.EvaluationTime is { } requested && requested < required)
                throw new InvalidDataException($"{reference}: {requested} cannot depend on {required} values.");
            time = formula.EvaluationTime ?? required;
            _evaluationTimes.Add(reference, time);
            _scheduledFormulas.Add(reference);
            return time;
        }
        finally { visiting.Remove(reference); }
    }

    // Minimum/Maximum/Count/... over a parameter, array or date range are array/range functions, not report summaries.
    private static bool Summarizes(CrystalFormula formula) => formula.UsesAggregates && formula.AggregateReferences.Any(r => !IsParameter(r));
    private static bool IsParameter(string reference) => reference.StartsWith("{?", StringComparison.Ordinal);

    private CrystalEvaluationTime RequiredTime(CrystalFormula formula, HashSet<string>? visiting = null)
    {
        var time = formula.UsesPageContext || Summarizes(formula) || formula.UsesSharedVariables || formula.UsesRecordContext
            ? CrystalEvaluationTime.WhilePrintingRecords : CrystalEvaluationTime.BeforeReadingRecords;
        foreach (var dependency in formula.References)
        {
            var dependencyTime = EvaluationTime(dependency, visiting);
            if (dependencyTime > time) time = dependencyTime;
        }
        return time;
    }

    private void PrepareFormulaSchedule()
    {
        // Schedule only reachable formulas. Unused library definitions must not change report state. A label-only
        // chart's category is scheduled as a field's binding is, so its stamp prints what a field would print.
        var roots = _layout.Document.Elements.SelectMany(e => e.Visual.Runs.Select(r => r.Binding)
                .Prepend(ValueBinding(e)).Append(e.HighlightRule.FieldName))
            .Concat(_layout.Document.Groups.Select(g => g.Condition)).Concat(_layout.Document.Sorts.Select(s => s.Field.Reference))
            .Concat(_layout.Summaries.Values.Concat(_layout.RunningTotals.Values).Select(s => s.Field))
            .Concat(_layout.RunningTotals.Values.SelectMany(t => new[] { t.Evaluation, t.Reset }).SelectMany(c => (c.Formula?.References ?? []).Prepend(c.Field)))
            .Concat(_layout.RecordSelection?.References ?? []).Concat(_layout.GroupSelection?.References ?? [])
            .Concat(_layout.SectionConditions.Values.Concat(_layout.AreaConditions.Values).Concat(_layout.ObjectConditions.Values).SelectMany(c => c.Values).SelectMany(f => f.References))
            .Concat(_layout.Subreports.Values.SelectMany(s => s.Links).Select(l => l.MainField));
        foreach (var reference in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            try { _ = EvaluationTime(reference); }
            catch (InvalidDataException error) { _schedulingErrors[reference] = error.Message; _diagnostics.Add(error.Message); }
        foreach (var formula in _scheduledFormulas.Where(f => _evaluationTimes[f] == CrystalEvaluationTime.BeforeReadingRecords)) Prepare(formula, -1);
        for (var row = 0; row < _rows.Length; row++)
            foreach (var formula in _scheduledFormulas.Where(f => _evaluationTimes[f] == CrystalEvaluationTime.WhileReadingRecords)) Prepare(formula, row);
        void Prepare(string formula, int row)
        {
            try { _ = FormulaValue(formula, row); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException or ArithmeticException or ArgumentException or FormatException or InvalidCastException)
            { _diagnostics.Add(formula + ": " + error.Message); }
        }
    }

    private object? ScheduledValue(string reference, int row)
    {
        if (_schedulingErrors.TryGetValue(reference, out var error)) throw new InvalidDataException(error);
        var time = EvaluationTime(reference);
        if (_preparingFurniture && !_physicalPageSchedule && time == CrystalEvaluationTime.WhilePrintingRecords && _layout.Formulas[reference].WritesPersistentVariables)
            throw new NotSupportedException($"{reference}: page header/footer variable assignments require a physical-page state scheduler; speculative row variants cannot execute them.");
        IDictionary<string, object?>? values = null;
        if (time == CrystalEvaluationTime.BeforeReadingRecords) values = _beforeValues;
        else if (time == CrystalEvaluationTime.WhileReadingRecords)
        {
            if (row < 0 || row >= _rows.Length) return null;
            if (!_readValues.TryGetValue(_rows[row], out var read)) _readValues[_rows[row]] = read = new(StringComparer.OrdinalIgnoreCase);
            values = read;
        }
        if (values?.TryGetValue(reference, out var result) == true)
            return result is Exception failure ? throw (failure is NotSupportedException ? new NotSupportedException(failure.Message, failure) : new InvalidDataException(failure.Message, failure)) : result;
        var key = (reference.ToUpperInvariant(), row);
        var pageDependent = !_physicalPageSchedule && DependsOn(reference, true);
        // A page failure that runs again here (RunsAgain) and fails again keeps what it noted when it first failed.
        ReadValue[]? earlier = null;
        if (values is null && !pageDependent && _bandFormulaCache.TryGetValue(key, out result))
        {
            if (result is PageFailure dropped && RunsAgain(key, dropped)) earlier = dropped.Reads;
            else
            {
                if (result is Exception failed) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed);
                return result;
            }
        }
        if (_evaluating.Count >= 64 || !_evaluating.Add(key)) throw new InvalidDataException($"Circular/excessively nested formula '{reference}'.");
        try
        {
            var formula = _layout.Formulas[reference];
            // EvaluateAfter is a scheduling edge, even when it occurs after an assignment in the expression.
            foreach (var dependency in formula.EvaluateAfter) _ = Value(dependency, row);
            // A named formula with no expression supplies a null to its callers.
            var stopped = false;
            result = formula.IsEmpty ? null : formula.Evaluate(Context(row), out stopped);
            // One that stopped at a null value may have set some of the variables it assigns and not the rest, as one that fails.
            if (stopped && formula.WritesPersistentVariables) _state.Sessions[0]._assignmentFailures++;
            if (values is not null) values[reference] = result;
            else if (!pageDependent) _bandFormulaCache[key] = result;
            return result;
        }
        catch (Exception failure) when (failure is InvalidDataException or NotSupportedException or ArithmeticException or ArgumentException or FormatException or InvalidCastException)
        {
            if (_layout.Formulas.TryGetValue(reference, out var failing) && failing.WritesPersistentVariables) _state.Sessions[0]._assignmentFailures++;
            if (values is not null) values[reference] = failure;
            else if (!pageDependent && _failureScope > 0)
            {
                _bandFormulaCache[key] = failure;
                if (_failureReads is not null && PageReads(reference) is { } reads)
                {
                    _failureReads[(key, failure)] = ReadValues(reads, earlier);
                    _newFailures?.Add((key, failure));
                }
            }
            throw;
        }
        finally { _evaluating.Remove(key); }
    }
}
