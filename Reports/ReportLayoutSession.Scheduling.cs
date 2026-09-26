using System.Data;

namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    private readonly Dictionary<string, CrystalEvaluationTime> _evaluationTimes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, object?> _beforeValues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DataRow, Dictionary<string, object?>> _readValues = new();
    private readonly List<string> _scheduledFormulas = [];
    private readonly Dictionary<string, string> _schedulingErrors = new(StringComparer.OrdinalIgnoreCase);
    private sealed record VariableSnapshot(Dictionary<string, object?> Globals, Dictionary<string, object?> Shared);
    private readonly Dictionary<int, VariableSnapshot> _rowBefore = new();
    private readonly Dictionary<int, VariableSnapshot> _rowAfter = new();
    private bool _preparingFurniture;
    private bool _physicalPageSchedule;
    private bool _replayingPrint;
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

    private static bool VisibilityProperty(string property) => property.ToLowerInvariant() is "enablesuppress" or "enablehidefordrilldown";
    private static bool EndingProperty(string property) => property.ToLowerInvariant() is "enablenewpageafter" or "enableresetpagenumberafter";
    private bool HasScheduledSection(string section) => new[] { _layout.AreaConditions, _layout.SectionConditions }
        .SelectMany(table => table.GetValueOrDefault(section) ?? []).Any(c => NeedsPrintState(c.Value) || _physicalPageSchedule && UsesPage(c.Value));
    private bool MutableCondition(Dictionary<string, Dictionary<string, CrystalFormula>> table, string section, string property) =>
        (table.GetValueOrDefault(section) ?? []).Any(c => c.Key.Equals(property, StringComparison.OrdinalIgnoreCase) && (NeedsPrintState(c.Value) || _physicalPageSchedule && UsesPage(c.Value)));
    private bool HasPrintFields(string section) => _layout.Document.Sections.First(s => s.Id == section).Elements
        .SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(e.Kind == "Field" ? e.Binding : "")).Any(NeedsPrintState);
    private bool HasVisibilityEvent(string section) => HasScheduledSection(section) || _physicalPageSchedule && (HasPrintFields(section)
        || _layout.Document.Sections.First(s => s.Id == section).Elements.Any(e => _layout.Subreports.ContainsKey(e.Id)
            || (_layout.ObjectConditions.GetValueOrDefault(e.Id)?.Values.Any(NeedsPrintState) ?? false)));

    private void PreparePhysicalPageSchedule()
    {
        var furniture = _layout.Document.Sections.Where(s => s.Kind is "PageHeader" or "PageFooter")
            .SelectMany(s => s.Elements).SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(e.Binding));
        var needsReplay = _scheduledFormulas.Any(r => DependsOn(r, true) && NeedsPrintState(r))
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
                return layout.Document.Elements.SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(e.Binding).Append(e.HighlightRule.FieldName)).Any(Reference)
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
            return item with { Element = element, Html = evaluated.Html, TemplateHtml = evaluated.Html, Measurement = -2 };
        }).ToList();
        return source with { Items = items };
    }

    private PrintBandKey FragmentKey(Band band) => new(band.Owner ?? this, band.Section.Id, band.Row, 0);
    private Dictionary<PrintBandKey, int> LastFragmentOffsets(List<Page> pages) => pages.SelectMany(p => p.Placements)
        .GroupBy(p => FragmentKey(p.Band)).ToDictionary(g => g.Key, g => g.Max(p => p.Offset));

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
        var blanks = new Dictionary<(ReportLayoutSession Owner, string Section, int Occurrence), bool>();
        var caches = new Dictionary<PrintBandKey, Dictionary<(string Name, int Row), object?>>();
        var lastOffsets = LastFragmentOffsets(pages);
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
                foreach (var placement in pages[pageIndex].Placements.OrderBy(p => p.Sequence))
                {
                    var occurrence = placement.Band.RepeatedHeader || placement.Band.Section.Kind is "PageHeader" or "PageFooter" ? pageIndex + 1 : 0;
                    var blocked = new HashSet<(ReportLayoutSession? Owner, string Section, int Row)>();
                    PrintBandKey Key(Band band) => new(band.Owner ?? this, band.Section.Id, band.Row, occurrence);
                    bool Section(Band band)
                    {
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
                            var formatted = owner.ApplySectionConditions(owner.ApplySectionConditions(original, band.Row, endingOnly: false), band.Row, true, endingOnly: false);
                            visible = owner.Visible(formatted); formats[key] = PrintSectionFormat.Read(formatted);
                            caches[key] = new(owner._bandFormulaCache);
                            visibility.Add(key, visible); return visible;
                        }
                        finally { owner._replayingPrint = false; }
                    }
                    void Finish(Band band)
                    {
                        var key = Key(band); var owner = key.Owner;
                        if (!owner.HasScheduledSection(band.Section.Id) || !endings.Add(key)) return;
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
                            formats[key] = PrintSectionFormat.Read(formatted); caches[key] = new(owner._bandFormulaCache);
                        }
                        finally { owner._replayingPrint = false; }
                    }
                    // A subreport's links are evaluated when its object prints. Its query ran with the logical-pass values.
                    void VerifyLink(ReportLayoutSession? child)
                    {
                        if (child?._link is not { } link || !linked.Add(child)) return;
                        var owner = link.Owner; var key = new PrintBandKey(owner, link.Section, link.Row, occurrence);
                        owner._formulaPage = pages[pageIndex].Number; owner._formulaPages = counts[pageIndex]; owner._repeatedHeader = placement.Band.RepeatedHeader;
                        owner._bandFormulaCache.Clear();
                        if (caches.TryGetValue(key, out var cache)) foreach (var value in cache) owner._bandFormulaCache.Add(value.Key, value.Value);
                        owner._replayingPrint = true;
                        object?[] values;
                        try { values = link.Links.Select(l => owner.Value(l.MainField, link.Row)).ToArray(); }
                        finally { owner._replayingPrint = false; }
                        caches[key] = new(owner._bandFormulaCache);
                        for (var index = 0; index < values.Length; index++)
                            if (!SameLinkValue(values[index], link.Values[index]))
                                throw new NotSupportedException($"{link.Element}: subreport link '{link.Links[index].MainField}' has a different value where the subreport prints on page {pages[pageIndex].Number} than in the report's logical pass; re-running linked subreports during page replay is not supported.");
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
                        if (original.Elements.Any(e => owner._layout.Subreports.ContainsKey(e.Id)
                            || (owner._layout.ObjectConditions.GetValueOrDefault(e.Id)?.Values.Any(owner.NeedsPrintState) ?? false)))
                            owner._diagnostics.Add($"{original.Name}: hidden subreports and object-format assignments are skipped; named field assignments still run.");
                        owner._formulaPage = pages[pageIndex].Number; owner._formulaPages = counts[pageIndex]; owner._repeatedHeader = placement.Band.RepeatedHeader;
                        owner._bandFormulaCache.Clear();
                        if (caches.TryGetValue(key, out var existing)) foreach (var value in existing) owner._bandFormulaCache.Add(value.Key, value.Value);
                        // Suppression removes presentation, not named formula-field assignments used by later sections.
                        foreach (var reference in original.Elements.OrderBy(e => e.TopTwips)
                            .SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(e.Kind == "Field" ? e.Binding : "")).Where(r => owner._layout.Formulas.ContainsKey(r)))
                            _ = owner.Value(reference, band.Row);
                        caches[key] = new(owner._bandFormulaCache);
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
                        // Cross-tab and table fragments are laid out from their analysis snapshot; they carry no formulas to replay.
                        if (owner._layout.Document.Elements.FirstOrDefault(e => e.Id == item.Element.Id) is not { } original) return;
                        owner._formulaPage = pages[pageIndex].Number; owner._formulaPages = counts[pageIndex]; owner._repeatedHeader = placement.Band.RepeatedHeader;
                        owner._bandFormulaCache.Clear();
                        if (caches.TryGetValue(bandKey, out var cache)) foreach (var value in cache) owner._bandFormulaCache.Add(value.Key, value.Value);
                        owner._replayingPrint = true;
                        ReportDesignerElement element;
                        string html;
                        try
                        {
                            // Resolve field values before their formatting; both share the occurrence's formula cache.
                            // Format() turns a formula fault into a field diagnostic. This prefetch runs outside Format,
                            // so the same faults must not abort the physical replay (while-printing array formulas).
                            foreach (var reference in original.Visual.Runs.Select(r => r.Binding).Prepend(original.Kind == "Field" ? original.Binding : "").Where(r => r.Length > 0))
                            {
                                try { _ = owner.Value(reference, item.Row); }
                                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or FormatException or InvalidCastException or OverflowException or DivideByZeroException or ArgumentException)
                                { /* Format records the diagnostic when it evaluates this reference. */ }
                            }
                            element = owner.ApplyObjectConditions(owner.ApplyObjectConditions(original, item.Row), item.Row, true);
                            html = ReportObjectRenderer.Content(element, reference => owner.Format(reference, item.Row, element.FormatString));
                        }
                        finally { owner._replayingPrint = false; }
                        printed.Add(key, item with { Element = element, Html = html, TemplateHtml = html, Measurement = -2 });
                        caches[bandKey] = new(owner._bandFormulaCache);
                    }
                }
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
            return changed;
        }
        finally
        {
            foreach (var (session, before) in saved)
            {
                session.RestoreVariables(before.Variables); session._bandFormulaCache.Clear();
                session._formulaPage = before.Page; session._formulaPages = before.Count; session._repeatedHeader = before.Repeated;
            }
        }
    }

    private VariableSnapshot CaptureVariables() => new(new(_globals, StringComparer.OrdinalIgnoreCase), new(_state.Shared, StringComparer.OrdinalIgnoreCase));
    private void RestoreVariables(VariableSnapshot snapshot)
    {
        _globals.Clear(); foreach (var value in snapshot.Globals) _globals.Add(value.Key, value.Value);
        _state.Shared.Clear(); foreach (var value in snapshot.Shared) _state.Shared.Add(value.Key, value.Value);
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
        // Schedule only reachable formulas. Unused library definitions must not change report state.
        var roots = _layout.Document.Elements.SelectMany(e => e.Visual.Runs.Select(r => r.Binding)
                .Prepend(e.Kind == "Field" ? e.Binding : "").Append(e.HighlightRule.FieldName))
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
        if (values is null && !pageDependent && _bandFormulaCache.TryGetValue(key, out result)) return result;
        if (_evaluating.Count >= 64 || !_evaluating.Add(key)) throw new InvalidDataException($"Circular/excessively nested formula '{reference}'.");
        try
        {
            var formula = _layout.Formulas[reference];
            // EvaluateAfter is a scheduling edge, even when it occurs after an assignment in the expression.
            foreach (var dependency in formula.EvaluateAfter) _ = Value(dependency, row);
            result = formula.Evaluate(Context(row));
            if (values is not null) values[reference] = result;
            else if (!pageDependent) _bandFormulaCache[key] = result;
            return result;
        }
        catch (Exception failure) when (failure is InvalidDataException or NotSupportedException or ArithmeticException or ArgumentException or FormatException or InvalidCastException)
        { if (values is not null) values[reference] = failure; throw; }
        finally { _evaluating.Remove(key); }
    }
}
