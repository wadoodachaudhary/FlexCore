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

    // A cached first-pass formula is immutable during printing, even if it used variables to compute its value.
    private bool NeedsPrintState(string reference)
    {
        if (!_layout.Formulas.TryGetValue(reference, out var formula)) return false;
        if (EvaluationTime(reference) != CrystalEvaluationTime.WhilePrintingRecords) return false;
        return formula.UsesPersistentVariables || formula.References.Any(NeedsPrintState);
    }

    private bool NeedsPrintState(CrystalFormula formula) => formula.UsesPersistentVariables || formula.References.Any(NeedsPrintState);

    private static bool VisibilityProperty(string property) => property.ToLowerInvariant() is "enablesuppress" or "enablehidefordrilldown";
    private bool HasMutableVisibility(string section) => new[] { _layout.AreaConditions, _layout.SectionConditions }
        .SelectMany(table => table.GetValueOrDefault(section) ?? []).Any(c => VisibilityProperty(c.Key) && (NeedsPrintState(c.Value) || _physicalPageSchedule && UsesPage(c.Value)));
    private bool MutableCondition(Dictionary<string, Dictionary<string, CrystalFormula>> table, string section, string property) =>
        (table.GetValueOrDefault(section) ?? []).Any(c => c.Key.Equals(property, StringComparison.OrdinalIgnoreCase) && (NeedsPrintState(c.Value) || _physicalPageSchedule && UsesPage(c.Value)));
    private bool HasPrintFields(string section) => _layout.Document.Sections.First(s => s.Id == section).Elements
        .SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(e.Kind == "Field" ? e.Binding : "")).Any(NeedsPrintState);
    private bool HasVisibilityEvent(string section) => HasMutableVisibility(section) || _physicalPageSchedule && (HasPrintFields(section)
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
            needsReplay |= layouts.Count > 1 && layouts.Any(UsesState);
            _state.PhysicalSchedule = needsReplay;
        }
        _physicalPageSchedule = _state.PhysicalSchedule || needsReplay;
        _printStart = CaptureVariables();
        if (!_physicalPageSchedule) return;
        var conditions = _layout.SectionConditions.Values.Concat(_layout.AreaConditions.Values).SelectMany(c => c);
        if (_layout.Document.Sections.Any(s => s.SuppressIfBlank)
            || conditions.Any(c => !VisibilityProperty(c.Key) && NeedsPrintState(c.Value))
            || _layout.ObjectConditions.Values.SelectMany(c => c).Any(c => c.Key.Equals("EnableSuppress", StringComparison.OrdinalIgnoreCase) && NeedsPrintState(c.Value))
            || _layout.Summaries.Values.Concat(_layout.RunningTotals.Values).Any(s => NeedsPrintState(s.Field))
            || _scheduledFormulas.Any(r => _layout.Formulas[r].AggregateReferences.Any(NeedsPrintState))
            || _layout.RunningTotals.Values.SelectMany(t => new[] { t.Evaluation.Formula, t.Reset.Formula })
                .Any(f => f is not null && (f.UsesPersistentVariables || f.References.Any(NeedsPrintState))))
            throw new NotSupportedException($"{_layout.Document.SourceName}: mutable page formulas do not yet support non-visibility section formatting/object suppression, blank-section suppression, or state-dependent summaries.");
        if (_layout.Subreports.Values.SelectMany(s => s.Links).Any(l => NeedsPrintState(l.MainField) || DependsOn(l.MainField, true)))
            throw new NotSupportedException("Inline subreport query links cannot depend on mutable or page-dependent print state.");
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
        var hiddenValues = new HashSet<PrintBandKey>();
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
                foreach (var placement in pages[pageIndex].Placements)
                {
                    var occurrence = placement.Band.RepeatedHeader || placement.Band.Section.Kind is "PageHeader" or "PageFooter" ? pageIndex + 1 : 0;
                    var blocked = new HashSet<(ReportLayoutSession? Owner, string Section, int Row)>();
                    PrintBandKey Key(Band band) => new(band.Owner ?? this, band.Section.Id, band.Row, occurrence);
                    bool Section(Band band)
                    {
                        var owner = band.Owner ?? this;
                        if (!owner.HasMutableVisibility(band.Section.Id)) return !band.Suppressed;
                        var key = Key(band);
                        if (visibility.TryGetValue(key, out var visible)) return visible;
                        owner._formulaPage = pages[pageIndex].Number; owner._formulaPages = counts[pageIndex]; owner._repeatedHeader = placement.Band.RepeatedHeader;
                        owner._bandFormulaCache.Clear();
                        if (caches.TryGetValue(key, out var existing)) foreach (var value in existing) owner._bandFormulaCache.Add(value.Key, value.Value);
                        owner._replayingPrint = true;
                        try
                        {
                            var original = owner._layout.Document.Sections.First(s => s.Id == band.Section.Id);
                            visible = owner.Visible(owner.ApplySectionConditions(owner.ApplySectionConditions(original, band.Row), band.Row, true));
                            caches[key] = new(owner._bandFormulaCache);
                            visibility.Add(key, visible); return visible;
                        }
                        finally { owner._replayingPrint = false; }
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
                            throw new NotSupportedException("Suppressed sections containing inline subreports or mutable object formatting require an explicit Crystal engine compatibility policy.");
                        owner._formulaPage = pages[pageIndex].Number; owner._formulaPages = counts[pageIndex]; owner._repeatedHeader = placement.Band.RepeatedHeader;
                        owner._bandFormulaCache.Clear();
                        if (caches.TryGetValue(key, out var existing)) foreach (var value in existing) owner._bandFormulaCache.Add(value.Key, value.Value);
                        // Suppression removes presentation, not named formula-field assignments used by later sections.
                        foreach (var reference in original.Elements.OrderBy(e => e.TopTwips)
                            .SelectMany(e => e.Visual.Runs.Select(r => r.Binding).Prepend(e.Kind == "Field" ? e.Binding : "")).Where(r => owner._layout.Formulas.ContainsKey(r)))
                            _ = owner.Value(reference, band.Row);
                        caches[key] = new(owner._bandFormulaCache);
                    }
                    if (!Section(placement.Band)) { HiddenValues(placement.Band); continue; }
                    var events = (placement.Band.InlineSections ?? [])
                        .Where(s => s.Start >= placement.Offset && (s.Start < placement.Offset + placement.Height
                            || s.Start == s.End && s.Start == placement.Offset + placement.Height && placement.Offset == lastOffsets[FragmentKey(placement.Band)]))
                        .Select(s => (Top: s.Start, Depth: s.Source.Owner?._depth ?? 0, Band: (Band?)s.Source, Item: (Item?)null))
                        .Concat(placement.Band.Items.Select(i => (Top: i.Element.TopTwips, Depth: int.MaxValue, Band: (Band?)null, Item: (Item?)i)))
                        .OrderBy(e => e.Top).ThenBy(e => e.Depth);
                    // Child bands are already positioned inside the parent. Execute only each object's first fragment.
                    foreach (var entry in events)
                    {
                        if (entry.Band is { } childBand)
                        {
                            if (blocked.Contains((childBand.Owner, childBand.Section.Id, childBand.Row))) Block(childBand);
                            else if (!Section(childBand)) { HiddenValues(childBand); Block(childBand); }
                            continue;
                        }
                        var item = entry.Item!;
                        if (item.Element.TopTwips < placement.Offset || item.Element.TopTwips >= placement.Offset + placement.Height) continue;
                        var owner = item.Owner ?? placement.Band.Owner ?? this;
                        if (blocked.Contains((owner, item.Element.SectionId, item.Row))) continue;
                        var bandKey = new PrintBandKey(owner, item.Element.SectionId, item.Row, occurrence);
                        var key = new PrintItemKey(bandKey, item.Element.Id);
                        if (printed.ContainsKey(key)) continue;
                        owner._formulaPage = pages[pageIndex].Number; owner._formulaPages = counts[pageIndex]; owner._repeatedHeader = placement.Band.RepeatedHeader;
                        owner._bandFormulaCache.Clear();
                        if (caches.TryGetValue(bandKey, out var cache)) foreach (var value in cache) owner._bandFormulaCache.Add(value.Key, value.Value);
                        var original = owner._layout.Document.Elements.First(e => e.Id == item.Element.Id);
                        owner._replayingPrint = true;
                        ReportDesignerElement element;
                        string html;
                        try
                        {
                            // Resolve field values before their formatting; both share the occurrence's formula cache.
                            foreach (var reference in original.Visual.Runs.Select(r => r.Binding).Prepend(original.Kind == "Field" ? original.Binding : "").Where(r => r.Length > 0))
                                _ = owner.Value(reference, item.Row);
                            element = owner.ApplyObjectConditions(owner.ApplyObjectConditions(original, item.Row), item.Row, true);
                            html = ReportObjectRenderer.Content(element, reference => owner.Format(reference, item.Row, element.FormatString));
                        }
                        finally { owner._replayingPrint = false; }
                        printed.Add(key, item with { Element = element, Html = html, TemplateHtml = html, Measurement = -2 });
                        caches[bandKey] = new(owner._bandFormulaCache);
                    }
                }
            }
            var changed = visibility.Count != _state.PrintedVisibility.Count || visibility.Any(p => !_state.PrintedVisibility.TryGetValue(p.Key, out var old) || p.Value != old)
                || printed.Count != _state.PrintedItems.Count || printed.Any(p => !_state.PrintedItems.TryGetValue(p.Key, out var old)
                || p.Value.Html != old.Html || p.Value.Element.CanGrow != old.Element.CanGrow
                || ReportObjectRenderer.Style(p.Value.Element, false) != ReportObjectRenderer.Style(old.Element, false));
            _state.PrintedItems = printed;
            _state.PrintedVisibility = visibility;
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
                || SpecialName(reference) is "pagenumber" or "totalpagecount" or "pagenofm" or "recordnumber")
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

    private CrystalEvaluationTime RequiredTime(CrystalFormula formula, HashSet<string>? visiting = null)
    {
        var time = formula.UsesPageContext || formula.UsesAggregates || formula.UsesSharedVariables || formula.UsesRecordContext
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
            return result is Exception failure ? throw new InvalidDataException(failure.Message, failure) : result;
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
