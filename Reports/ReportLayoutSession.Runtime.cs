using System.Data;
using System.Globalization;

namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    private sealed class RuntimeState
    {
        public DateTime PrintTime { get; init; } = DateTime.Now;
        public Dictionary<string, object?> Shared { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(ReportDefinition Definition, Dictionary<string, object> Parameters, List<(string Alias, object? Value)> Filters, DataTable Data, DataTable Filtered)> Queries { get; } = [];
        public int Instances { get; set; }
        public int QueryRows { get; set; }
        public bool HasPageConditions { get; set; }
        public bool HasGrowingText { get; set; }
        public Dictionary<string, Func<int, int, bool, string>> PageValues { get; } = new();
        public bool PhysicalSchedule { get; set; }
        public List<ReportLayoutSession> Sessions { get; } = [];
        public Dictionary<PrintItemKey, Item> PrintedItems { get; set; } = new();
        public Dictionary<PrintBandKey, bool> PrintedVisibility { get; set; } = new();
        public Dictionary<PrintBandKey, PrintSectionFormat> PrintedSections { get; set; } = new();
        public List<HashSet<PrintBandKey>> BlankHistory { get; } = [];
        public Dictionary<(ReportLayoutSession Owner, string Section, int Occurrence), bool> PrintedBlank { get; set; } = new();
    }
    private readonly RuntimeState _state;
    private readonly int _depth;
    private readonly Func<ReportDefinition, IReadOnlyDictionary<string, object>, DataTable>? _executeSubreport;
    private readonly Dictionary<string, object?> _globals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Name, int Row), object?> _bandFormulaCache = new();
    private readonly HashSet<(string Name, int Row)> _evaluating = new();
    private readonly Dictionary<(string Name, bool Page), bool> _dependencies = new();
    private object? _formulaPage;
    private object? _formulaPages;
    private bool _repeatedHeader;
    private readonly Dictionary<(string Id, int Row), (bool Area, bool Section, bool AreaHidden, bool SectionHidden)> _suppression = new();

    private CrystalFormulaContext Context(int row, object? page = null, object? count = null, string? currentField = null) => new()
    {
        Resolve = reference => Value(reference, row), ReferenceType = ReferenceType,
        Aggregate = (op, field, group) => IsParameter(field)
            ? throw new NotSupportedException($"{op} ({field}): a parameter is summarized as an array or range value, not through the record summary context.")
            : Summary(new(SummaryOperation(op), field, group), row),
        ConditionalAggregate = (op, field, group, condition) => Summary(new(SummaryOperation(op), field, group) { GroupLevel = ConditionLevel(group, condition) }, row),
        GroupName = (field, condition) => GroupNameValue(condition is null ? GroupLevel(field) : ConditionLevel(field, condition), row),
        Relative = (field, offset) => row + offset >= 0 && row + offset < _rows.Length ? Value(field, row + offset) : null,
        CurrentFieldValue = () => string.IsNullOrEmpty(currentField) ? null : Value(currentField, row),
        SharedVariables = _state.Shared, GlobalVariables = _globals, Now = _printTime,
        RecordIndex = row, RecordCount = _rows.Length, PageNumber = page ?? _formulaPage ?? 1, TotalPageCount = count ?? _formulaPages ?? 1,
        InRepeatedGroupHeader = _repeatedHeader, GroupNumber = () => GroupNumber(row),
        GroupingLevel = field => _layout.Document.Groups.FindIndex(g => g.Condition.Trim().Equals(field.Trim(), StringComparison.OrdinalIgnoreCase)) + 1,
        HierarchicalChildren = level => HierarchicalChildren(level, row),
        DocumentProperty = name => name switch
        {
            "reporttitle" => ReportTitle, "reportcomments" => _layout.Document.Comments, "fileauthor" => _layout.Document.Author,
            _ => throw new NotSupportedException($"Document property '{name}' is not available to this report run.")
        }
    };

    // Crystal's ReportTitle is the saved summary title, blank when none was saved; the document title falls back to other text for display.
    private string ReportTitle => _layout.Document.SourceDocument?.Root is not { } root ? _layout.Document.Title
        : string.IsNullOrWhiteSpace((string?)root.Element("Summaryinfo")?.Attribute("ReportTitle")) ? "" : _layout.Document.Title;

    // Crystal types every reference statically: fields and formulas by their value type, parameters by their prompt kind (a multi-value
    // parameter reads as an array, a range-capable one as a range) and summaries by their operation.
    private Dictionary<string, CrystalValueType?>? _referenceTypes;
    private CrystalValueType? ReferenceType(string reference)
    {
        if (_referenceTypes is null)
        {
            var types = new Dictionary<string, CrystalValueType?>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in _layout.Document.Fields) types.TryAdd(field.Reference, CrystalValueType.FromXml(field.Type));
            foreach (var parameter in _layout.Document.Parameters)
                types["{?" + parameter.Name + "}"] = CrystalValueType.FromXml(parameter.Type, parameter.AllowMultiple, parameter.AllowRange);
            foreach (var (name, summary) in _layout.Summaries.Concat(_layout.RunningTotals))
                types.TryAdd(name, summary.Operation.ToLowerInvariant() is "count" or "distinctcount" or "sum" or "average" or "avg" ? new(CrystalBaseType.Number)
                    : summary.Operation.ToLowerInvariant() is "minimum" or "maximum" && types.GetValueOrDefault(summary.Field) is { } fieldType ? fieldType with { IsArray = false, IsRange = false } : null);
            foreach (var name in _layout.GroupNames.Keys) types.TryAdd(name, new(CrystalBaseType.String));
            _referenceTypes = types;
        }
        return _referenceTypes.GetValueOrDefault(reference.Trim());
    }

    private static string SummaryOperation(string op) => op.ToLowerInvariant() switch
    {
        "avg" or "average" => "Average", "distinctcount" => "DistinctCount", "minimum" => "Minimum", "maximum" => "Maximum", "count" => "Count", "sum" => "Sum",
        _ => throw new NotSupportedException($"Summary operation '{op}' is not implemented.")
    };

    // Sum ({field}, {group field}, "monthly"): the group on that field whose condition has that name.
    private int ConditionLevel(string group, string condition)
    {
        for (var level = 0; level < _layout.Document.Groups.Count; level++)
            if (_layout.Document.Groups[level].Condition.Trim().Equals(group.Trim(), StringComparison.OrdinalIgnoreCase)
                && ConditionNamed(Options(level)?.ConditionKind ?? 0, condition, ReferenceType(group)?.BaseType)) return level;
        throw new InvalidDataException($"There must be a group on {group} with the condition \"{condition}\" for this summary.");
    }

    // Crystal numbers group instances in print order; the number advances whenever any group changes (DataContext).
    private (DataRow[]? Rows, int[] Numbers) _groupNumbers = (null, []);
    private int GroupNumber(int row)
    {
        if (!ReferenceEquals(_groupNumbers.Rows, _rows))
        {
            var numbers = new int[_rows.Length];
            var innermost = _layout.Document.Groups.Count - 1;
            for (var index = 0; index < numbers.Length; index++)
                numbers[index] = index == 0 ? 1 : numbers[index - 1] + (innermost >= 0 && !SameGroup(index - 1, index, innermost) ? 1 : 0);
            _groupNumbers = (_rows, numbers);
        }
        return row < 0 || _rows.Length == 0 ? 1 : _groupNumbers.Numbers[Math.Min(row, _rows.Length - 1)];
    }

    private bool DependsOn(string reference, bool page, HashSet<string>? stack = null)
    {
        var key = (reference, page);
        if (_dependencies.TryGetValue(key, out var cached)) return cached;
        if (!_layout.Formulas.TryGetValue(reference, out var formula)) return page && SpecialName(reference) is "pagenumber" or "totalpagecount" or "pagenofm";
        stack ??= new(StringComparer.OrdinalIgnoreCase);
        if (!stack.Add(reference)) throw new InvalidDataException($"Circular formula reference '{reference}'.");
        var result = (page ? formula.UsesPageContext : formula.UsesPersistentVariables) || formula.References.Any(r => DependsOn(r, page, stack));
        stack.Remove(reference);
        return _dependencies[key] = result;
    }

    private object? FormulaValue(string reference, int row) => ScheduledValue(reference, row);

    private void PrepareRows()
    {
        if (_layout.RecordSelection is { } selection)
        {
            RequireReadTime(selection, "Record selection");
            _rows = _rows.Where((_, index) => CrystalFormula.Boolean(selection.Evaluate(Context(index)))).ToArray();
            ClearValues();
        }
        if (_linkSelections.Count > 0)
        {
            foreach (var link in _linkSelections) RequireReadTime(_layout.Formulas[link.Field], "Subreport link selection");
            var matches = _linkSelections.Select(link => (link.Field, Match: LinkMatcher(link.Value))).ToArray();
            _rows = _rows.Where((_, index) => matches.All(link => link.Match(Value(link.Field, index)))).ToArray();
            ClearValues();
        }
        var recordSorts = _layout.Document.Sorts.Where(s => s.SortType == "RecordSortField").ToArray();
        foreach (var reference in _layout.Document.Groups.Select(g => g.Condition).Concat(recordSorts.Select(s => s.Field.Reference)))
            if (_layout.Formulas.TryGetValue(reference, out var formula)) RequireReadTime(formula, "Sort/group");
        foreach (var named in _layout.GroupOptions.Values.SelectMany(o => o.SpecifiedGroups)) RequireReadTime(named.Selection, "Specified group");
        DiscardUnspecified();
        var sort = Enumerable.Range(0, _layout.Document.Groups.Count)
            .SelectMany(level => GroupSortKeys(level).Select(key => (Key: key, Descending: _layout.Document.Groups[level].SortDirection.StartsWith("Descending", StringComparison.OrdinalIgnoreCase))))
            .Concat(recordSorts.Select(s => (Key: (Func<int, object?>)(row => Value(s.Field.Reference, row)), Descending: s.Direction.StartsWith("Descending", StringComparison.OrdinalIgnoreCase)))).ToArray();
        var keys = _rows.Select((data, index) => (Data: data, Index: index, Keys: sort.Select(s => s.Key(index)).ToArray())).ToArray();
        Array.Sort(keys, (left, right) =>
        {
            for (var i = 0; i < sort.Length; i++) { var order = CrystalFormula.Compare(left.Keys[i], right.Keys[i]); if (order != 0) return sort[i].Descending ? -order : order; }
            return left.Index.CompareTo(right.Index);
        });
        _rows = keys.Select(k => k.Data).ToArray(); ClearValues();
        OrderHierarchies();
        var discarded = ApplyGroupSorts();
        // Grand and group totals still count the groups a Top/Bottom N sort discards, as group selection's do.
        _summaryRows = _rows;
        if (discarded.Count > 0) { _rows = _rows.Where(data => !discarded.Contains(data)).ToArray(); ClearValues(); }
        if (_layout.GroupSelection is { } groupSelection)
        {
            if (groupSelection.RequiresPrintPass || groupSelection.UsesPersistentVariables || groupSelection.UsesPageContext || groupSelection.References.Any(r => r.StartsWith("{#", StringComparison.Ordinal) || NeedsPrintState(r) || DependsOn(r, true)))
                throw new NotSupportedException("Group selection cannot use print-time state.");
            _rows = _rows.Where((_, index) => CrystalFormula.Boolean(groupSelection.Evaluate(Context(index)))).ToArray();
            ClearValues();
        }
    }
    private void RequireReadTime(CrystalFormula formula, string name)
    {
        if (formula.RequiresPrintPass || RequiredTime(formula) == CrystalEvaluationTime.WhilePrintingRecords)
            throw new NotSupportedException(name + " cannot depend on print-time formulas.");
    }
    private void ClearValues() { _bandFormulaCache.Clear(); _groupRanges.Clear(); _groupStarts.Clear(); _summaryCache.Clear(); _runningValues.Clear(); }

    private string DeferredPageValue(string reference, int row, string format)
    {
        if (NeedsPrintState(reference)) throw new NotSupportedException("Page formulas cannot mutate shared/global variables during pagination.");
        var token = Guid.NewGuid().ToString("N") + "-formula";
        _state.PageValues[token] = (page, count, repeated) =>
        {
            var previousPage = _formulaPage; var previousCount = _formulaPages; var previousRepeat = _repeatedHeader;
            try { _formulaPage = page; _formulaPages = count; _repeatedHeader = repeated; return Format(reference, row, format); }
            finally { _formulaPage = previousPage; _formulaPages = previousCount; _repeatedHeader = previousRepeat; }
        };
        return token;
    }
    private string ResolvePageValues(string html, int page, int count, bool repeated = false)
    {
        foreach (var entry in _state.PageValues)
            if (html.Contains(entry.Key, StringComparison.Ordinal)) html = html.Replace(entry.Key, ReportObjectRenderer.Encode(entry.Value(page, count, repeated)), StringComparison.Ordinal);
        return html;
    }

    private bool UsesPage(CrystalFormula formula) => formula.UsesPageContext || formula.References.Any(r => DependsOn(r, true));
    private IEnumerable<KeyValuePair<string, CrystalFormula>> Conditions(Dictionary<string, Dictionary<string, CrystalFormula>> table, string id, bool pageOnly) =>
        (table.GetValueOrDefault(id) ?? []).Where(c => UsesPage(c.Value) == pageOnly);
    private bool PageSuppresses(Dictionary<string, Dictionary<string, CrystalFormula>> table, string id) =>
        Conditions(table, id, true).Any(c => c.Key.Equals("EnableSuppress", StringComparison.OrdinalIgnoreCase));
    private object? ConditionValue(CrystalFormula formula, int row, bool pageOnly, string? currentField = null)
    {
        if (formula.EvaluationTime is { } requested && requested < RequiredTime(formula))
            throw new InvalidDataException($"Conditional formula {requested} cannot depend on later-pass values.");
        if (formula.UsesPersistentVariables && formula.EvaluationTime is CrystalEvaluationTime.BeforeReadingRecords or CrystalEvaluationTime.WhileReadingRecords)
            throw new NotSupportedException("Early-pass variable assignments in inline formatting formulas are not scheduled. Use a named formula field dependency.");
        if (_preparingFurniture && !_replayingPrint && formula.WritesPersistentVariables)
            throw new NotSupportedException("Page header/footer formatting cannot assign persistent variables during speculative measurement.");
        if (pageOnly && !_replayingPrint && NeedsPrintState(formula))
            throw new NotSupportedException("Page-dependent formatting cannot mutate shared/global variables.");
        return formula.Evaluate(Context(row, currentField: currentField));
    }
    private static ReportDesignerSection CopySection(ReportDesignerSection source) => new()
        {
            Id = source.Id, Name = source.Name, SourceKey = source.SourceKey, Kind = source.Kind, AreaId = source.AreaId, AreaName = source.AreaName, GroupId = source.GroupId,
            HeightTwips = source.HeightTwips, IsSuppressed = source.IsSuppressed, HideForDrillDown = source.HideForDrillDown,
            PrintAtBottomOfPage = source.PrintAtBottomOfPage, SuppressIfBlank = source.SuppressIfBlank, UnderlayFollowingSections = source.UnderlayFollowingSections,
            RelativePositions = source.RelativePositions, NewPageBefore = source.NewPageBefore, NewPageAfter = source.NewPageAfter,
            ResetPageNumberAfter = source.ResetPageNumberAfter,
            KeepTogether = source.KeepTogether, BackgroundColor = source.BackgroundColor, Elements = source.Elements
        };
    private ReportDesignerSection ApplySectionConditions(ReportDesignerSection source, int row, bool pageOnly = false, bool? endingOnly = null)
    {
        var section = CopySection(source);
        var states = pageOnly ? _suppression.GetValueOrDefault((source.Id, row)) :
            (Area: !PageSuppresses(_layout.AreaConditions, source.Id) && !MutableCondition(_layout.AreaConditions, source.Id, "EnableSuppress") && _layout.Areas.GetValueOrDefault(source.Id)?.Suppressed == true,
                Section: !PageSuppresses(_layout.SectionConditions, source.Id) && !MutableCondition(_layout.SectionConditions, source.Id, "EnableSuppress") && source.IsSuppressed,
                AreaHidden: !MutableCondition(_layout.AreaConditions, source.Id, "EnableHideForDrillDown") && _layout.Areas.GetValueOrDefault(source.Id)?.Hidden == true,
                SectionHidden: !MutableCondition(_layout.SectionConditions, source.Id, "EnableHideForDrillDown") && source.HideForDrillDown);
        foreach (var entry in Conditions(_layout.AreaConditions, source.Id, pageOnly).Select(c => (Condition: c, Area: true))
                     .Concat(Conditions(_layout.SectionConditions, source.Id, pageOnly).Select(c => (Condition: c, Area: false))))
        {
            var condition = entry.Condition;
            if (endingOnly is { } ending && EndingProperty(condition.Key) != ending) continue;
            // Until the first physical replay records page formats, page-only conditions use the tentative page number.
            if (_physicalPageSchedule && !_replayingPrint && (NeedsPrintState(condition.Value)
                || UsesPage(condition.Value) && (_state.PrintedVisibility.Count > 0 || _state.PrintedSections.Count > 0))) continue;
            var value = ConditionValue(condition.Value, row, pageOnly);
            switch (condition.Key.ToLowerInvariant())
            {
                case "enablesuppress": if (entry.Area) states.Area = CrystalFormula.Boolean(value); else states.Section = CrystalFormula.Boolean(value); break;
                case "enablehidefordrilldown": if (entry.Area) states.AreaHidden = CrystalFormula.Boolean(value); else states.SectionHidden = CrystalFormula.Boolean(value); break;
                case "enablenewpagebefore": section.NewPageBefore = CrystalFormula.Boolean(value); break;
                case "enablenewpageafter": section.NewPageAfter = CrystalFormula.Boolean(value); break;
                case "enableresetpagenumberafter": section.ResetPageNumberAfter = CrystalFormula.Boolean(value); break;
                case "enablekeeptogether": section.KeepTogether = CrystalFormula.Boolean(value); break;
                case "enablesuppressifblank": section.SuppressIfBlank = CrystalFormula.Boolean(value); break;
                case "enableprintatbottomofpage": section.PrintAtBottomOfPage = CrystalFormula.Boolean(value); break;
                case "enableunderlayfollowingsections" or "enableunderlaysection": section.UnderlayFollowingSections = CrystalFormula.Boolean(value); break;
                case "backgroundcolor": section.BackgroundColor = FormulaColor(value); break;
                default: _diagnostics.Add($"{section.Name}: conditional property '{condition.Key}' is not implemented."); break;
            }
        }
        if (endingOnly != true)
        {
            section.IsSuppressed = states.Area || states.Section;
            section.HideForDrillDown = states.AreaHidden || states.SectionHidden;
            if (!pageOnly) _suppression[(source.Id, row)] = states;
        }
        return section;
    }

    private ReportDesignerElement ApplyObjectConditions(ReportDesignerElement source, int row, bool pageOnly = false)
    {
        var element = source.CloneFor(source.SectionId, source.Name);
        element.Id = source.Id; element.SourceKey = source.SourceKey;
        element.LeftTwips = source.LeftTwips + (pageOnly ? 0 : HierarchicalIndent(source.SectionId, row)); element.TopTwips = source.TopTwips;
        if (!pageOnly && (PageSuppresses(_layout.ObjectConditions, source.Id) || MutableCondition(_layout.ObjectConditions, source.Id, "EnableSuppress"))) element.IsSuppressed = false;
        foreach (var condition in Conditions(_layout.ObjectConditions, source.Id, pageOnly))
        {
            if (_physicalPageSchedule && !_replayingPrint && NeedsPrintState(condition.Value)) continue;
            var value = ConditionValue(condition.Value, row, pageOnly, source.Binding);
            var fontCondition = condition.Key.ToLowerInvariant() is "color" or "fontcolor" or "bold" or "enablebold" or "italic" or "enableitalic" or "underline" or "enableunderline" or "size" or "fontsize" or "name" or "fontname" or "style";
            switch (condition.Key.ToLowerInvariant())
            {
                case "enablesuppress": element.IsSuppressed = CrystalFormula.Boolean(value); break;
                case "enablecangrow": element.CanGrow = CrystalFormula.Boolean(value); break;
                case "color": case "fontcolor": element.TextColor = FormulaColor(value); break;
                case "backgroundcolor": element.BackgroundColor = FormulaColor(value); break;
                case "bold": case "enablebold": element.Bold = CrystalFormula.Boolean(value); break;
                case "italic": case "enableitalic": element.Italic = CrystalFormula.Boolean(value); break;
                case "underline": case "enableunderline": element.Underline = CrystalFormula.Boolean(value); break;
                case "size": case "fontsize": element.FontSize = Math.Clamp(CrystalFormula.Number(value), 1, 300); break;
                case "name": case "fontname": element.FontFamily = value?.ToString() ?? "Arial"; break;
                case "style":
                    var style = value is string text ? text : (int)CrystalFormula.Number(value) switch
                    { 0 => "Regular", 1 => "Bold", 2 => "Italic", 3 => "BoldItalic", _ => throw new NotSupportedException("Unknown conditional font style.") };
                    element.Bold = style.Contains("bold", StringComparison.OrdinalIgnoreCase);
                    element.Italic = style.Contains("italic", StringComparison.OrdinalIgnoreCase);
                    break;
                case "horizontalalignment": element.HorizontalAlignment = value?.ToString() ?? "Left"; break;
                case "bordercolor": element.Visual.BorderColor = FormulaColor(value); break;
                case "leftlinestyle": element.Visual.LeftLine = FormulaLine(value); break;
                case "rightlinestyle": element.Visual.RightLine = FormulaLine(value); break;
                case "toplinestyle": element.Visual.TopLine = FormulaLine(value); break;
                case "bottomlinestyle": element.Visual.BottomLine = FormulaLine(value); break;
                case "displaystring": element.Kind = "Text"; element.Text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? ""; element.Visual.Runs.Clear(); break;
                default: _diagnostics.Add($"{element.Name}: conditional property '{condition.Key}' is not implemented."); break;
            }
            if (fontCondition)
                foreach (var run in element.Visual.Runs)
                    switch (condition.Key.ToLowerInvariant())
                    {
                        case "color": case "fontcolor": run.Color = element.TextColor; break;
                        case "bold": case "enablebold": run.Bold = element.Bold; break;
                        case "italic": case "enableitalic": run.Italic = element.Italic; break;
                        case "underline": case "enableunderline": run.Underline = element.Underline; break;
                        case "size": case "fontsize": run.FontSize = element.FontSize; break;
                        case "name": case "fontname": run.FontFamily = element.FontFamily; break;
                        case "style": run.Bold = element.Bold; run.Italic = element.Italic; break;
                    }
        }
        var rule = element.HighlightRule;
        if (rule.Enabled && !pageOnly)
        {
            var field = string.IsNullOrWhiteSpace(rule.FieldName) ? element.Binding : rule.FieldName;
            if (!field.StartsWith('{')) field = "{" + field + "}";
            if (_physicalPageSchedule && !_replayingPrint && NeedsPrintState(field)) return element;
            var value = Value(field, row);
            object? right = rule.Value;
            if (value is not string && decimal.TryParse(rule.Value, NumberStyles.Any, CultureInfo.CurrentCulture, out var number)) right = number;
            var compare = CrystalFormula.Compare(value, right);
            var matches = rule.Operator.ToLowerInvariant() switch
            {
                "is equal to" or "=" => compare == 0, "is not equal to" or "<>" => compare != 0,
                "is greater than" or ">" => compare > 0, "is less than" or "<" => compare < 0,
                "is greater than or equal to" or ">=" => compare >= 0, "is less than or equal to" or "<=" => compare <= 0,
                "contains" => value?.ToString()?.Contains(rule.Value, StringComparison.OrdinalIgnoreCase) == true,
                _ => throw new NotSupportedException($"Highlight operator '{rule.Operator}' is not implemented.")
            };
            if (matches)
            {
                element.TextColor = rule.TextColor; element.BackgroundColor = rule.BackgroundColor;
                if (rule.FontStyle != "Default") { element.Bold = rule.FontStyle.Contains("Bold", StringComparison.OrdinalIgnoreCase); element.Italic = rule.FontStyle.Contains("Italic", StringComparison.OrdinalIgnoreCase); }
                if (rule.BorderStyle != "Default border style") element.Visual.TopLine = element.Visual.BottomLine = element.Visual.LeftLine = element.Visual.RightLine = rule.BorderStyle;
            }
        }
        return element;
    }

    private Band ForPage(Band source, int page, int count, bool repeated = false, int physicalPage = 0, int? objectPage = null)
    {
        source = ApplyPrintedItems(source, physicalPage, repeated);
        var sectionOwner = source.Owner ?? this;
        var previousPage = sectionOwner._formulaPage; var previousCount = sectionOwner._formulaPages; var previousRepeat = sectionOwner._repeatedHeader;
        try
        {
            sectionOwner._formulaPage = page; sectionOwner._formulaPages = count; sectionOwner._repeatedHeader = repeated;
            var section = sectionOwner.ApplySectionConditions(source.Section, source.Row, true);
            var occurrence = repeated || source.Section.Kind is "PageHeader" or "PageFooter" ? physicalPage : 0;
            if (_state.PrintedSections.TryGetValue(new(sectionOwner, source.Section.Id, source.Row, occurrence), out var format)) format.Apply(section);
            var items = new List<Item>();
            foreach (var item in source.Items)
            {
                var owner = item.Owner ?? this;
                var oldPage = owner._formulaPage; var oldCount = owner._formulaPages; var oldRepeat = owner._repeatedHeader;
                try
                {
                    owner._formulaPage = objectPage ?? page; owner._formulaPages = count; owner._repeatedHeader = repeated;
                    var element = owner.ApplyObjectConditions(item.Element, item.Row, true);
                    var resized = element.CanGrow != item.Element.CanGrow || element.FontSize != item.Element.FontSize || element.FontFamily != item.Element.FontFamily;
                    if (resized && _textMetrics is null) _diagnostics.Add($"{element.Name}: page-dependent font/CanGrow uses approximate font measurement.");
                    var richChanged = element.Visual.Runs.Count > 0 && owner.Conditions(owner._layout.ObjectConditions, element.Id, true).Any();
                    var template = item.TemplateHtml ?? item.Html;
                    var html = richChanged && owner._physicalPageSchedule && element.Visual.Runs.Count > 0
                        ? RestylePrintedRuns(template, element)
                        : richChanged || element.Text != item.Element.Text || element.Kind != item.Element.Kind
                            ? ReportObjectRenderer.Content(element, reference => owner.Format(reference, item.Row, element.FormatString)) : template;
                    html = ResolvePageValues(html.Replace(_pageToken, page.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                        .Replace(_countToken, count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                        .Replace(owner._pageToken, page.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                        .Replace(owner._countToken, count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), page, count, repeated);
                    items.Add(item with { Element = element, Html = html, TemplateHtml = template, Measurement = resized || html != item.Html ? -2 : item.Measurement });
                }
                finally { owner._formulaPage = oldPage; owner._formulaPages = oldCount; owner._repeatedHeader = oldRepeat; }
            }
            var hidden = _state.PrintedVisibility.TryGetValue(new(sectionOwner, source.Section.Id, source.Row, occurrence), out var visible) && !visible;
            return source with { Section = section, Items = items, Suppressed = !Visible(section) || hidden, RepeatedHeader = repeated };
        }
        finally { sectionOwner._formulaPage = previousPage; sectionOwner._formulaPages = previousCount; sectionOwner._repeatedHeader = previousRepeat; }
    }
    private static string FormulaColor(object? value)
    {
        if (value is string color) return ReportObjectRenderer.Color(color, "#000000");
        var rgb = (int)CrystalFormula.Number(value);
        if (rgb == -1) return "transparent";
        return $"#{rgb & 255:x2}{(rgb >> 8) & 255:x2}{(rgb >> 16) & 255:x2}";
    }
    private static string FormulaLine(object? value) => value is string name ? name : (int)CrystalFormula.Number(value) switch
    { 0 => "NoLine", 1 => "Single", 2 => "Double", 3 => "Dash", 4 => "Dot", _ => throw new NotSupportedException("Unknown conditional border line style.") };

    private sealed record SubreportLinkState(ReportLayoutSession Owner, string Section, int Row, string Element, List<ReportLayoutParameterLink> Links, object?[] Values);
    private SubreportLinkState? _link;
    private static object?[]? Multiple(object? value) => value is string or byte[] or null or DBNull || value is not System.Collections.IEnumerable items ? null : items.Cast<object?>().ToArray();
    // Equality under CrystalFormula.Compare; same-kind text, number and date values use a set instead of a scan.
    private static Func<object?, bool> LinkMatcher(object? expected)
    {
        var choices = (Multiple(expected) ?? [expected]).Where(choice => choice is not (null or DBNull)).Cast<object>().ToArray();
        static bool Numeric(object? value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal;
        var texts = choices.All(c => c is string) ? new HashSet<string>(choices.Cast<string>(), StringComparer.OrdinalIgnoreCase) : null;
        var numbers = choices.All(Numeric) ? choices.Select(CrystalFormula.Number).ToHashSet() : null;
        var dates = choices.All(c => c is DateTime) ? choices.Cast<DateTime>().ToHashSet() : null;
        return actual => actual switch
        {
            null or DBNull => false,
            string text when texts is not null => texts.Contains(text),
            DateTime date when dates is not null => dates.Contains(date),
            _ when numbers is not null && Numeric(actual) => numbers.Contains(CrystalFormula.Number(actual)),
            _ => choices.Any(choice => CrystalFormula.Compare(actual, choice) == 0)
        };
    }
    private static bool SameLinkValue(object? left, object? right) =>
        Multiple(left) is { } a ? Multiple(right) is { } b && a.Length == b.Length && a.Zip(b).All(p => SameLinkValue(p.First, p.Second))
            : left is null or DBNull ? right is null or DBNull : right is not (null or DBNull) && Multiple(right) is null && CrystalFormula.Compare(left, right) == 0;
    private bool Resolvable(string reference) => reference.Trim() is var name && (_layout.Formulas.ContainsKey(name) || _layout.FormulaErrors.ContainsKey(name) || _layout.RunningTotals.ContainsKey(name)
        || _layout.GroupNames.ContainsKey(name) || _layout.Bindings.ContainsKey(name) || _layout.Summaries.ContainsKey(name) || IsParameter(name) || IsSpecial(name));

    private ReportLayoutSession? CreateInlineSubreport(ReportDesignerElement element, int row)
    {
        if (!_layout.Subreports.TryGetValue(element.Id, out var subreport))
        { _diagnostics.Add($"{element.Name}: subreport is on-demand or its definition is unavailable."); return null; }
        if (_executeSubreport is null)
        { _diagnostics.Add($"{element.Name}: an inline subreport data executor is required."); return null; }
        if (_depth >= 8 || ++_state.Instances > 10000) throw new InvalidDataException("Inline subreport instance/nesting limit exceeded.");
        var parameters = new Dictionary<string, object>(_parameters, StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in subreport.Definition.Parameters)
            if (!parameters.ContainsKey(parameter.Name) && !string.IsNullOrEmpty(parameter.DefaultValue)) parameters[parameter.Name] = parameter.DefaultValue;
        var filters = new List<(string Alias, object? Value)>();
        var selections = new List<(string Field, object? Value)>();
        var childLayout = subreport.Definition.PositionedLayout!;
        var values = new object?[subreport.Links.Count];
        for (var index = 0; index < subreport.Links.Count; index++)
        {
            // Crystal passes the main field, formula or (multi-value) parameter to {?Pm-...}; the optional
            // subreport field adds "{child} = {?Pm-...}" to the subreport's record selection.
            var link = subreport.Links[index];
            if (!Resolvable(link.MainField))
                throw new InvalidDataException($"{element.Name}: subreport link source '{link.MainField}' is not a field, formula or parameter of the main report.");
            var value = values[index] = Value(link.MainField, row);
            if (link.Parameter.Length > 0) parameters[link.Parameter] = value ?? DBNull.Value;
            if (link.ChildField.Length == 0) continue;
            if (childLayout.Bindings.TryGetValue(link.ChildField, out var alias)) filters.Add((alias, value));
            else if (childLayout.Formulas.ContainsKey(link.ChildField)) selections.Add((link.ChildField, value));
            else throw new InvalidDataException($"{element.Name}: subreport link '{link.MainField}' -> '{{?{link.Parameter}}}' selects on '{link.ChildField}', which is not a field or formula of subreport '{element.SubreportName}'.");
        }
        var cached = _state.Queries.FirstOrDefault(q => ReferenceEquals(q.Definition, subreport.Definition) &&
            q.Filters.SequenceEqual(filters) &&
            q.Parameters.Count == parameters.Count && q.Parameters.All(p => parameters.TryGetValue(p.Key, out var value) && Equals(p.Value, value)));
        var (data, filtered) = (cached.Data, cached.Filtered);
        if (data is null)
        {
            var query = subreport.Definition;
            var bound = new Dictionary<string, object>(parameters, StringComparer.OrdinalIgnoreCase);
            if (filters.Count > 0)
            {
                var predicates = new List<string>();
                if (bound.Keys.Any(k => k.StartsWith("__fxSubreport", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Report parameter conflicts with the reserved subreport parameter prefix.");
                for (var index = 0; index < filters.Count; index++)
                {
                    var key = "__fxSubreport" + index;
                    var column = $"[__fxInline].[{filters[index].Alias.Replace("]", "]]", StringComparison.Ordinal)}]";
                    if (Multiple(filters[index].Value) is { } choices)
                    {
                        if (bound.Count + choices.Length > 2100)
                            throw new InvalidDataException($"{element.Name}: the subreport query would bind more than 2,100 SQL parameters ({bound.Count} before the {choices.Length} values of link '{filters[index].Alias}').");
                        for (var choice = 0; choice < choices.Length; choice++) bound[key + "_" + choice] = choices[choice] ?? DBNull.Value;
                        predicates.Add(choices.Length == 0 ? "1 = 0" : column + " IN (" + string.Join(", ", choices.Select((_, choice) => "@" + key + "_" + choice)) + ")");
                    }
                    else { bound[key] = filters[index].Value ?? DBNull.Value; predicates.Add(column + " = @" + key); }
                }
                if (bound.Count > 2100) throw new InvalidDataException($"{element.Name}: the subreport query would bind {bound.Count} SQL parameters; SQL Server accepts at most 2,100.");
                query = new ReportDefinition { Sql = "SELECT * FROM (" + query.Sql + ") AS [__fxInline] WHERE " + string.Join(" AND ", predicates),
                    Title = query.Title, ReportId = query.ReportId, Parameters = query.Parameters, FixedParameters = query.FixedParameters, PositionedLayout = query.PositionedLayout };
            }
            data = _executeSubreport(query, bound);
            if (data.Rows.Count > 100000) throw new InvalidDataException("Subreport exceeds the 100,000-row limit.");
            _state.QueryRows += data.Rows.Count;
            if (_state.QueryRows > 250000) throw new InvalidDataException("Inline subreport queries exceed the 250,000-row combined limit.");
            // Identical query and link values give identical rows; later instances reuse the filtered table.
            filtered = data.Clone();
            var matches = filters.Select(f => (f.Alias, Match: data.Columns.Contains(f.Alias) || data.Rows.Count == 0 ? LinkMatcher(f.Value)
                : throw new InvalidDataException($"Subreport link column '{f.Alias}' is missing."))).ToArray();
            foreach (DataRow candidate in data.Rows)
                if (matches.All(f => f.Match(candidate[f.Alias]))) filtered.ImportRow(candidate);
            _state.Queries.Add((subreport.Definition, parameters, filters, data, filtered));
        }
        var child = new ReportLayoutSession(subreport.Definition.PositionedLayout!, filtered, parameters, _executeSubreport, _state, _depth + 1, this, selections)
            { _link = new(this, element.SectionId, row, element.Name, subreport.Links, values) };
        if (child._layout.Document.Sections.Any(s => s.ResetPageNumberAfter) || child._layout.Areas.Values.Any(a => a.ResetPageNumberAfter))
            _diagnostics.Add(element.Name + ": inline subreport page-number resets cannot reset the parent report's physical pagination.");
        foreach (var diagnostic in subreport.Definition.RuntimeDiagnostics.Concat(child._diagnostics)) _diagnostics.Add(element.Name + ": " + diagnostic);
        return child;
    }
}
