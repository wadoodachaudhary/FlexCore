using System.Data;
using System.Globalization;

namespace Fx.ControlKit.Reports;

public sealed partial class ReportLayoutSession
{
    private sealed record NavigationGroup(int Level, int Start, int End);
    /// <summary>The main run's first pass, which a drill view does not repeat: read-time formula values by record,
    /// and the variables as that pass left them.</summary>
    private sealed record DrillReadPass(Dictionary<DataRow, Dictionary<string, object?>> Values, VariableSnapshot Variables);
    private readonly Dictionary<FxGroupNode, NavigationGroup> _navigationGroups = new();
    private readonly Dictionary<DataRow, int> _navigationRows = new();
    private readonly string _navigationRoot;
    private readonly int _drillDownGroupLevel;
    private string? _drillDownUnsupportedReason;
    private bool _drillDownSupportChecked;

    /// <summary>Why <see cref="CreateDrillDown"/> cannot open this report's groups, or null when it can. The layout is fixed
    /// for the session, so the answer holds for every group of the main run and of its drill views.</summary>
    public string? DrillDownUnsupportedReason
    {
        get
        {
            if (_drillDownSupportChecked) return _drillDownUnsupportedReason;
            _drillDownUnsupportedReason = UnsupportedDrillDownReason();
            _drillDownSupportChecked = true;
            return _drillDownUnsupportedReason;
        }
    }

    private string? UnsupportedDrillDownReason()
    {
        // Unknown only for a conversion stamped by a converter that did not yet write the option (see the loader).
        if (_layout.ShowAllHeadersOnDrillDown is null)
            return "Native drill-down requires the authored ShowAllHeadersOnDrillDown option, which XML from an earlier converter lacks. Reconvert the original report, or save a designed copy again, to refresh it.";
        // Re-evaluating these operations on a subset can change the selected group's meaning.
        // Do not silently replace snapshot membership with a different Top N, hierarchy or group selection.
        if (_layout.Document.Groups.Any(g => g.IsHierarchical) || _layout.GroupSorts.Count > 0
            || _layout.GroupSelection is not null || _layout.GroupOptions.Values.Any(o => o.SpecifiedOrder))
            return "Native drill-down of hierarchical, specified-order, summary-sorted or group-selected reports is not supported.";
        if (_layout.Document.Groups.Any(g => SpecialName(g.Condition) is "recordnumber" or "groupnumber"))
            return "Native drill-down of a record-context-dependent group is not supported.";
        return OmittedDrillStateReason(1);
    }

    /// <summary>Creates a native drill view of a group from this run's owned records. The main query is never rerun and the
    /// records are not read again: read-time formulas keep this run's values. The view follows the authored
    /// header option, and its summaries keep the main run's totals.</summary>
    public ReportLayoutSession CreateDrillDown(FxGroupNode node)
    {
        if (!_navigationGroups.TryGetValue(node, out var group))
            throw new InvalidOperationException("The selected native group does not belong to this report run.");
        if (DrillDownUnsupportedReason is { } reason) throw new NotSupportedException(reason);

        if (OmittedDrillStateReason(group.Level + 1) is { } stateReason) throw new NotSupportedException(stateReason);

        var selected = _rows.Skip(group.Start).Take(group.End - group.Start).ToArray();
        var table = selected[0].Table.Clone();
        foreach (var row in selected) CopyNavigationRow(table, row);
        // Crystal formats a drill view from the main run's first-pass totals: a report, parent-group or
        // percentage summary printed inside the drilled group is not recomputed over the group's records.
        var summaryRows = (_summaryRows ?? _rows, Enumerable.Range(group.Start, group.End - group.Start).Select(SummaryRowIndex).ToArray());
        // Crystal does not read a drill view's records again: read-time formulas, such as a WhileReadingRecords counter
        // that groups the records, keep this run's values for the drilled records and for the records its summaries read.
        var readValues = new Dictionary<DataRow, Dictionary<string, object?>>();
        for (var index = 0; index < selected.Length; index++)
            if (_readValues.TryGetValue(selected[index], out var values)) readValues[table.Rows[index]] = values;
        foreach (var row in summaryRows.Item1)
            if (_readValues.TryGetValue(row, out var values)) readValues[row] = values;
        // The drill view keeps the run's subreport error tolerance and does not send a query the run saw fail.
        return new ReportLayoutSession(_layout, table, CopyNavigationParameters(_parameters), _executeSubreport,
            new RuntimeState { PrintTime = _printTime, TolerateSubreportQueryError = _state.TolerateSubreportQueryError, FailedQueries = _state.FailedQueries },
            0, drillDownGroupLevel: group.Level + 1,
            navigationRoot: _navigationRoot, navigationRows: selected.Select(row => _navigationRows[row]).ToArray(), summaryRows: summaryRows,
            readPass: new(readValues, _printStart ?? CaptureVariables()));
    }

    private static object CopyNavigationValue(object value, int depth = 0)
    {
        if (value is not Array array) return value;
        if (depth >= 32) throw new InvalidDataException("Report parameter or cell arrays exceed the supported nesting depth.");
        var copy = (Array)array.Clone();
        if (copy.Rank == 1)
            for (var index = copy.GetLowerBound(0); index <= copy.GetUpperBound(0); index++)
                if (copy.GetValue(index) is { } item) copy.SetValue(CopyNavigationValue(item, depth + 1), index);
        return copy;
    }

    private static object CopyNavigationParameterValue(object value, int depth = 0)
    {
        if (value is Array) return CopyNavigationValue(value, depth);
        if (value is string || value is not System.Collections.IEnumerable items) return value;
        if (depth >= 32) throw new InvalidDataException("Report parameter arrays exceed the supported nesting depth.");
        // Formula evaluation already treats enumerable prompt values as arrays. Capture them once,
        // with the same item limit, so a caller's later List mutation cannot change a drill run.
        var copy = new List<object>();
        foreach (var item in items)
        {
            if (copy.Count >= 100_000) throw new InvalidDataException("Array value exceeds the formula engine limit.");
            copy.Add(CopyNavigationParameterValue(item!, depth + 1));
        }
        return copy.ToArray();
    }

    private static Dictionary<string, object> CopyNavigationParameters(IReadOnlyDictionary<string, object>? parameters)
        => (parameters ?? new Dictionary<string, object>()).ToDictionary(pair => pair.Key,
            pair => CopyNavigationParameterValue(pair.Value), StringComparer.OrdinalIgnoreCase);

    private static void CopyNavigationRow(DataTable destination, DataRow source)
    {
        // ImportRow preserves the schema's expressions, read-only columns and nulls.
        destination.ImportRow(source);
        var copy = destination.Rows[^1];
        foreach (DataColumn column in destination.Columns)
        {
            if (column.Expression.Length > 0 || copy[column] is not Array value) continue;
            var readOnly = column.ReadOnly;
            try { column.ReadOnly = false; copy[column] = CopyNavigationValue(value); }
            finally { column.ReadOnly = readOnly; }
        }
    }

    private static DataTable CaptureNavigationData(DataTable source)
    {
        var copy = source.Clone();
        foreach (DataRow row in source.Rows) CopyNavigationRow(copy, row);
        return copy;
    }

    private (List<FxGroupNode> Tree, Dictionary<Placement, List<string>> Anchors) BuildNativeNavigation(List<Page> pages)
    {
        _navigationGroups.Clear();
        var anchors = new Dictionary<Placement, List<string>>();
        if (_rows.Length == 0 || _layout.Document.Groups.Count == 0) return ([], anchors);
        if (_layout.Document.Groups.Any(g => g.IsHierarchical))
        {
            _diagnostics.Add("Native group-tree navigation for hierarchical groups is not supported.");
            return ([], anchors);
        }
        var placements = pages.SelectMany((page, index) => page.Placements.Select(placement => (Placement: placement, Page: index + 1)))
            .Where(item => ReferenceEquals(item.Placement.Band.Owner, this) && item.Placement.Band.Row >= 0
                && !item.Placement.Band.Suppressed && !item.Placement.Band.Blank && item.Placement.Height > 0
                && item.Placement.Band.Section.Kind is "GroupHeader" or "Detail" or "GroupFooter").ToArray();
        var byRow = new List<(Placement Placement, int Page)>?[_rows.Length];
        foreach (var item in placements)
            (byRow[item.Placement.Band.Row] ??= []).Add(item);

        // A drill view's tree starts at the drilled group, as its tab does.
        var rootLevel = Math.Max(0, _drillDownGroupLevel - 1);
        List<FxGroupNode> Level(int level, int start, int end)
        {
            var nodes = new List<FxGroupNode>();
            if (level >= _layout.Document.Groups.Count) return nodes;
            for (var first = start; first < end;)
            {
                var last = first + 1;
                while (last < end && SameGroup(first, last, level)) last++;
                var candidates = Enumerable.Range(first, last - first).SelectMany(row => byRow[row] ?? [])
                    .OrderBy(item => item.Page).ThenBy(item => item.Placement.Top).ToArray();
                var target = candidates.FirstOrDefault(item => item.Placement.Band.Section.Kind == "GroupHeader"
                    && !item.Placement.Band.RepeatedHeader
                    && ReportDesignerEditing.GetGroupNumber(_layout.Document, item.Placement.Band.Section) == level + 1);
                if (target.Placement is null) target = candidates.FirstOrDefault();
                // The id names the group's whole span of records, so part of a group never shares the group's id (and its drill tab).
                var id = $"fx-native-group-{_navigationRoot}-{level}-{_navigationRows[_rows[first]]}-{_navigationRows[_rows[last - 1]]}";
                object? name;
                try
                {
                    if (Options(level)?.NameFormula is { } formula) RequireReadTime(formula, "Group navigation name");
                    name = GroupNameValue(level, first);
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException)
                {
                    _diagnostics.Add("Native group label: " + error.Message);
                    name = GroupKey(level, first);
                }
                var label = Convert.ToString(name, CultureInfo.CurrentCulture) ?? "";
                var node = new FxGroupNode
                {
                    Label = string.IsNullOrWhiteSpace(label) ? "(empty)" : label,
                    Value = label, Field = _layout.Document.Groups[level].Condition, Level = level,
                    PageIndex = target.Page, AnchorId = id, Expanded = false, Children = Level(level + 1, first, last)
                };
                _navigationGroups.Add(node, new(level, first, last));
                if (target.Placement is not null)
                {
                    if (!anchors.TryGetValue(target.Placement, out var ids)) anchors.Add(target.Placement, ids = []);
                    ids.Add(id);
                }
                nodes.Add(node);
                first = last;
            }
            return nodes;
        }
        return (Level(rootLevel, 0, _rows.Length), anchors);
    }

    private string? OmittedDrillStateReason(int groupLevel) => _layout.ShowAllHeadersOnDrillDown == false && _layout.Document.Sections
        .Where(section => DrillSectionDepth(section) < groupLevel)
        .Any(section => section.Elements.Any(element => element.Kind == "Subreport")
            || section.Elements.SelectMany(FieldBindings).Any(NeedsPrintState)
            || section.Elements.Any(element => _layout.ObjectConditions.GetValueOrDefault(element.Id)?.Values.Any(NeedsPrintState) ?? false)
            || (_layout.AreaConditions.GetValueOrDefault(section.Id)?.Values.Any(NeedsPrintState) ?? false)
            || (_layout.SectionConditions.GetValueOrDefault(section.Id)?.Values.Any(NeedsPrintState) ?? false))
        ? "Native drill-down cannot omit ancestor/report sections that participate in persistent print state or subreport execution." : null;

    private int DrillSectionDepth(ReportDesignerSection section) => section.Kind switch
    {
        // Expanded/continuation bands retain the source ID but need not copy its designer GroupId.
        "GroupHeader" or "GroupFooter" => ReportDesignerEditing.GetGroupNumber(_layout.Document,
            _layout.Document.Sections.FirstOrDefault(source => source.Id == section.Id) ?? section),
        "Detail" => _layout.Document.Groups.Count + 1,
        _ => 0
    };

    private bool InDrillScope(ReportDesignerSection section) => _drillDownGroupLevel == 0
        || _layout.ShowAllHeadersOnDrillDown == true || DrillSectionDepth(section) >= _drillDownGroupLevel;
}
