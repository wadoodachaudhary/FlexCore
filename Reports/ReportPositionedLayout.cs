using System.Data;
using System.Globalization;
using System.Text;

namespace Fx.ControlKit.Reports;

public sealed class ReportPositionedLayout
{
    public ReportDesignerDocument Document { get; init; } = ReportDesignerDocument.CreateBlank();
    /// <summary>Authored native report option; null means older XML did not preserve this drill contract.</summary>
    public bool? ShowAllHeadersOnDrillDown { get; init; }
    /// <summary>Convert database NULLs to their field type's default before formulas, grouping and formatting.</summary>
    public bool ConvertDatabaseNullToDefault { get; init; }
    /// <summary>An authored fixed report date; null uses the supplied run date. The run's time of day is retained.</summary>
    public DateTime? FixedPrintDate { get; init; }
    public Dictionary<string, string> Bindings { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> GroupNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ReportLayoutSummary> Summaries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ReportLayoutArea> Areas { get; } = new();
    public Dictionary<string, CrystalFormula> Formulas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FormulaErrors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ReportLayoutSummary> RunningTotals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, CrystalFormula>> SectionConditions { get; } = new();
    public Dictionary<string, Dictionary<string, CrystalFormula>> AreaConditions { get; } = new();
    public Dictionary<string, Dictionary<string, CrystalFormula>> ObjectConditions { get; } = new();
    public Dictionary<string, ReportLayoutSubreport> Subreports { get; } = new();
    /// <summary>On-demand subreports by their object's element id: the object prints the caption; <see cref="ReportLayoutSession.CreateOnDemandSubreport"/> runs one.</summary>
    public Dictionary<string, ReportLayoutSubreport> OnDemandSubreports { get; } = new();
    public List<string> Diagnostics { get; } = [];
    public CrystalFormula? RecordSelection { get; set; }
    public CrystalFormula? GroupSelection { get; set; }
    /// <summary>Crystal group options by group level (index into <see cref="ReportDesignerDocument.Groups"/>).</summary>
    public Dictionary<int, ReportLayoutGroupOptions> GroupOptions { get; } = new();
    /// <summary>Group sorts by summary (Crystal "Group Sort Expert"), by the level of the groups they order.</summary>
    public Dictionary<int, ReportLayoutGroupSort> GroupSorts { get; } = new();
    /// <summary>Crystal "Format with Multiple Columns" on the details area; null prints every section across the page.</summary>
    public ReportLayoutColumns? Columns { get; set; }
    /// <summary>The details area's limit counts a visible source row once across all its sections.</summary>
    public int VisibleRecordsPerPage { get; set; }
    internal List<string> Projections { get; } = [];
    // Stays with the layout when subreport query definitions are cloned with link filters.
    internal IReadOnlyList<string> SqlStringParameters { get; set; } = [];
}

/// <summary>Crystal GroupOptions: <see cref="ConditionKind"/> is the date/date-time period (0 daily ... 7 annually; 8-11 by second, minute, hour, AM/PM
/// for date-times), the time period (0-3) or the Boolean change (1-6); 0 on any other type groups on every change. A specified-order group lists its
/// named groups in print order; <see cref="UnspecifiedValues"/> ("mergeValues", "discardValues" or "separateValues") places the other records.
/// <see cref="NameFormula"/> names every group of the level except a Top/Bottom N Others group.</summary>
public sealed record ReportLayoutGroupOptions(int ConditionKind = 0, bool ShowLastDateInPeriod = false)
{
    public IReadOnlyList<(string Name, CrystalFormula Selection)> SpecifiedGroups { get; init; } = [];
    public string UnspecifiedValues { get; init; } = "";
    public string OthersName { get; init; } = "Others";
    public bool SpecifiedOrder => UnspecifiedValues.Length > 0;
    public CrystalFormula? NameFormula { get; init; }
    /// <summary>Why the group-name formula could not be compiled; reading the group's name then fails with it.</summary>
    public string NameFormulaError { get; init; } = "";
}

/// <summary>A group sort by a summary; <see cref="TopBottomN"/> ("TopN" or "BottomN") keeps the first <see cref="Count"/> groups
/// (<see cref="CountFormula"/> when set) and discards or merges the rest.</summary>
public sealed record ReportLayoutGroupSort(ReportLayoutSummary Summary, bool Descending)
{
    public string TopBottomN { get; init; } = "";
    public int Count { get; init; }
    public double Percentage { get; init; }
    public CrystalFormula? CountFormula { get; init; }
    public bool DiscardOthers { get; init; }
    public bool WithTies { get; init; }
    public string OthersName { get; init; } = "Others";
}

public sealed record ReportLayoutSummary(string Operation, string Field, string Group)
{
    public string WeightField { get; init; } = "";
    /// <summary>A percent summary uses zero for the grand total and a one-based group number for a parent subtotal.</summary>
    public int? PercentOfGroup { get; init; }
    /// <summary>The group level the summary totals; -1 takes the first group on <see cref="Group"/>.</summary>
    public int GroupLevel { get; init; } = -1;
    /// <summary>Crystal "summarize across hierarchy": a hierarchical group's total includes its descendant groups.</summary>
    public bool AcrossHierarchy { get; init; }
    public ReportRunningTotalCondition Evaluation { get; init; } = new("NoCondition");
    public ReportRunningTotalCondition Reset { get; init; } = new("NoCondition");
}
public sealed record ReportRunningTotalCondition(string Type, string Field = "", int Group = 0, CrystalFormula? Formula = null);
/// <summary>Area-level formatting; <see cref="KeepTogether"/> keeps the area's sections for one record on one page and <see cref="PrintAtBottomOfPage"/>
/// prints them at the bottom of their page.</summary>
public sealed record ReportLayoutArea(bool Suppressed, bool RepeatHeader, bool NewPageBefore, bool NewPageAfter, bool Hidden = false, bool ResetPageNumberAfter = false,
    bool KeepGroupTogether = false, bool KeepTogether = false, bool PrintAtBottomOfPage = false)
{
    public bool ReserveFinalFooter { get; init; }
    public int VisibleInstanceLimit { get; init; }
}
/// <summary>Crystal MultiColumnInfo: detail areas, and group areas when <see cref="FormatGroups"/>, print in columns <see cref="DetailWidth"/> twips wide, as many
/// as fit across the page with <see cref="HorizontalGap"/> between them and <see cref="VerticalGap"/> below each area. A nonzero <see cref="DetailHeight"/> fixes the
/// height of each detail (mailing labels) when the groups print across the page. Down then across fills a column before the next; across then down fills a row.</summary>
public sealed record ReportLayoutColumns(int DetailWidth, int DetailHeight = 0, int HorizontalGap = 0, int VerticalGap = 0, bool AcrossThenDown = false, bool FormatGroups = false);
public sealed record ReportTextMeasurement(string Style, string Html, int MaxNumberOfLines = 0, bool MeasureNumericWidth = false);
public sealed record ReportLayoutResult(List<string> Pages, List<string> Diagnostics)
{
    /// <summary>False when only a stable prefix was requested; Pages.Count is then not the report total.</summary>
    public bool IsComplete { get; init; } = true;
    public List<ReportPageSnapshot> Snapshots { get; init; } = [];
    /// <summary>Group navigation from this native run and its final physical page placements.</summary>
    public List<FxGroupNode> Tree { get; init; } = [];
    /// <summary>Inline subreports that printed an error in place of their records. Only a run that accepts subreport query
    /// failures (the session's <c>tolerateSubreportQueryError</c>) has any; without it a failure ends the run.</summary>
    public List<ReportSubreportError> SubreportErrors { get; init; } = [];
}
/// <summary>An inline subreport object whose query failed with an error the run accepted: the element, the subreport it
/// shows and the error's message, which the object prints as "[Subreport error: ...]".</summary>
public sealed record ReportSubreportError(string ElementId, string ElementName, string SubreportName, string Message);
public sealed record ReportLayoutParameterLink(string Parameter, string MainField, string ChildField);
public sealed record ReportLayoutSubreport(ReportDefinition Definition, List<ReportLayoutParameterLink> Links);

/// <summary>Detail rows -> measured bands -> fixed-size pages. No browser, SQL, or Crystal dependency.
/// The session owns copies of input records and parameter arrays. Its layout is shared and must remain
/// unchanged for the lifetime of the main and drill-down sessions.</summary>
public sealed partial class ReportLayoutSession
{
    private readonly ReportPositionedLayout _layout;
    private readonly ReportLayoutSession? _parent;
    private readonly IReadOnlyList<(string Field, object? Value)> _linkSelections;
    private DataRow[] _rows;
    private DataRow[]? _summaryRows;
    private Dictionary<DataRow, int>? _summaryRowIndexes;
    private readonly IReadOnlyDictionary<string, object> _parameters;
    private readonly List<Band> _bands = [];
    private readonly Dictionary<string, int> _measurementKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _diagnostics = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (int Start, int End)[]> _groupRanges = new();
    private readonly Dictionary<(ReportLayoutSummary Summary, int Start, int End), object?> _summaryCache = new();
    private readonly Dictionary<(string Element, int ScopeStart), ReportAnalysisSnapshot> _analysisSnapshots = new();
    private readonly string _pageToken = Guid.NewGuid().ToString("N") + "-page";
    private readonly string _countToken = Guid.NewGuid().ToString("N") + "-count";
    private readonly DateTime _printTime;
    /// <summary>The effective run clock after applying the main report's authored fixed date.</summary>
    public DateTime PrintTime => _printTime;
    public List<ReportTextMeasurement> Measurements { get; } = [];
    /// <summary>Measurements already answered by <see cref="CrystalDeviceText"/>; never sent to the browser.</summary>
    private readonly Dictionary<string, ReportTextMetrics> _nativeMetrics = new(StringComparer.Ordinal);
    private sealed record Item(ReportDesignerElement Element, string Html, int Measurement, ReportLayoutSession? Child = null, int ChildMeasurementOffset = 0, ReportLayoutSession? Owner = null, int Row = 0, string? TemplateHtml = null, string? ConsumedText = null, bool OmittedContinuation = false, ReportAnalysisSnapshot? Analysis = null, ReportTableFragment? Table = null, DuplicateFieldValue? DuplicateValue = null, decimal? AccumulatorDelta = null, Action<IDictionary<string, object?>>? PrintGrid = null, int HorizontalPane = 0, bool NarrowedByFrame = false);
    private sealed record InlineHeader(int Start, int End, Band Header, int KeepThrough = 0, bool Repeat = true, object? HorizontalPeers = null);
    private sealed record InlineSection(int Start, int End, Band Source, ReportDesignerSection? Format = null, bool Blank = false, bool FirstVisible = false);
    private sealed record InlineCut(Band Source, int RetainedHeight);
    private sealed record Band(ReportDesignerSection Section, List<Item> Items, int Row, List<Band> Repeats, bool Suppressed = false,
        List<int>? Breaks = null, List<int>? ForcedBreaks = null, List<InlineHeader>? InlineHeaders = null, bool RepeatedHeader = false,
        ReportLayoutSession? Owner = null, List<InlineSection>? InlineSections = null, List<InlineCut>? InlineCuts = null, bool Blank = false,
        bool EvaluationOnly = false, List<Item>? PageStartInk = null, int? KeptStartHeight = null);
    // Sequence is the print order (page header, repeated headers, bands by index, page footer); list order is drawing order.
    // A section in a multiple-column block has its column's Left and Width; Width 0 spans the page.
    private sealed record Placement(Band Band, int Top, int Offset, int Height, int Sequence = 0, int Left = 0, int Width = 0)
    {
        public int PrintSequence { get; init; } = Sequence;
    }
    private sealed class Page
    {
        public int Number { get; init; }
        public List<Placement> Placements { get; } = [];
        public int Cursor { get; set; }
        public int BodyStart { get; set; }
        public int Bottom { get; set; }
        public bool HasBody { get; set; }
        public bool HasHeaders { get; set; }
        public bool EndsWithReportHeaderBreak { get; set; }
    }

    /// <summary>Lays out <paramref name="layout"/> over <paramref name="data"/>. <c>executeSubreport</c> runs inline subreport
    /// queries. <c>tolerateSubreportQueryError</c> decides which failures of those queries the run accepts: an accepted
    /// failure prints "[Subreport error: ...]" in the subreport object and is listed in <see cref="ReportLayoutResult.SubreportErrors"/>,
    /// and the same query is not sent again in this run or its drill views. Null (the default) accepts none: the failure
    /// ends the run. Cancellation is never accepted.</summary>
    public ReportLayoutSession(ReportPositionedLayout layout, DataTable data, IReadOnlyDictionary<string, object>? parameters = null,
        Func<ReportDefinition, IReadOnlyDictionary<string, object>, DataTable>? executeSubreport = null, DateTime? printTime = null,
        Func<Exception, bool>? tolerateSubreportQueryError = null)
        : this(layout, CaptureNavigationData(data), parameters, executeSubreport,
            new RuntimeState { PrintTime = ApplyPrintDate(layout, printTime ?? DateTime.Now), TolerateSubreportQueryError = tolerateSubreportQueryError }, 0) { }

    private static DateTime ApplyPrintDate(ReportPositionedLayout layout, DateTime clock) => layout.FixedPrintDate is { } date
        ? new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, clock.Kind).AddTicks(clock.TimeOfDay.Ticks) : clock;

    private ReportLayoutSession(ReportPositionedLayout layout, DataTable data, IReadOnlyDictionary<string, object>? parameters,
        Func<ReportDefinition, IReadOnlyDictionary<string, object>, DataTable>? executeSubreport, RuntimeState state, int depth, ReportLayoutSession? parent = null,
        IReadOnlyList<(string Field, object? Value)>? linkSelections = null, int drillDownGroupLevel = 0,
        string? navigationRoot = null, int[]? navigationRows = null, (DataRow[] Rows, int[] Indexes)? summaryRows = null,
        DrillReadPass? readPass = null, bool initializePrintContext = false)
    {
        _layout = layout;
        CaptureCurrencySchema(data);
        _navigationRoot = navigationRoot ?? Guid.NewGuid().ToString("N");
        _drillDownGroupLevel = drillDownGroupLevel;
        _parent = parent;
        _linkSelections = linkSelections ?? [];
        _printTime = state.PrintTime;
        _diagnostics.UnionWith(layout.Diagnostics);
        if (layout.Document.Sections.SelectMany(s => s.Elements)
            .Any(e => e.FontFamily.Equals("Calibri", StringComparison.OrdinalIgnoreCase)
                || e.Visual.Runs.Any(r => r.FontFamily.Equals("Calibri", StringComparison.OrdinalIgnoreCase))))
            _diagnostics.Add(ReportFontCatalog.CalibriNotice);
        // An inline subreport's sections print inside the section that holds it.
        if (parent is not null && layout.Columns is not null)
            _diagnostics.Add($"{layout.Document.Title}: the subreport's multiple-column details area prints in one column inside its subreport object.");
        // Authored layouts can bypass the XML loader. Report capabilities even for hidden/empty bands.
        var reportName = layout.Document.SourceDocument?.Root is { } root ? (string?)root.Attribute("Name") ?? "Report" : layout.Document.Title;
        foreach (var section in layout.Document.Sections)
        foreach (var element in section.Elements)
        {
            if (ReportObjectCapabilities.UnsupportedCrystalKind(element.Kind) is { } kind && element.Analysis is null && element.ChartMarker is null)
                _diagnostics.Add(ReportObjectCapabilities.Diagnostic(reportName, section.Name, element.Name, kind, true,
                    layout.Document.SourceObjects.GetValueOrDefault(element.SourceKey)));
            if (string.IsNullOrEmpty(element.FormatString) && element.NativeDateTimeFormat?.UnsupportedReason is { } reason)
                _diagnostics.Add($"{element.Name}: native date/time format: {reason}.");
            if (string.IsNullOrEmpty(element.FormatString) && element.NativeNumericFormat is null && element.NativeNumberFormat?.UnsupportedReason is { } numberReason)
                _diagnostics.Add($"{element.Name}: native numeric format: {numberReason}; the value prints with the default format.");
            if (string.IsNullOrEmpty(element.FormatString) && element.NativeNumericFormat?.UnsupportedReason is { } numericReason)
                _diagnostics.Add($"{element.Name}: native numeric format: {numericReason}; the value prints with the default format.");
        }
        _rows = data.Rows.Cast<DataRow>().ToArray();
        for (var index = 0; index < _rows.Length; index++) _navigationRows.Add(_rows[index], navigationRows?[index] ?? index);
        _parameters = CopyNavigationParameters(parameters);
        _executeSubreport = executeSubreport; _state = state; _depth = depth;
        _state.Sessions.Add(this);
        _state.HasGrowingText |= layout.Document.Elements.Any(e => e.CanGrow);
        _state.HasPageConditions |= layout.SectionConditions.Values.Concat(layout.AreaConditions.Values).Concat(layout.ObjectConditions.Values).SelectMany(c => c.Values).Any(UsesPage);
        _state.HasPageConditions |= layout.Formulas.Values.Any(f => f.UsesPageContext || f.References.Any(IsSpecial))
            || layout.Document.Elements.Any(e => IsSpecial(e.Binding) || e.Visual.Runs.Any(r => IsSpecial(r.Binding)));
        if (ReportDesignerEditing.ValidatePage(layout.Document.Page) is { } error) throw new InvalidDataException(error);
        // A drill run's records keep the parent run's read-time values, so its schedule reads none of them again.
        if (readPass is not null)
            foreach (var (row, values) in readPass.Values) _readValues.Add(row, new(values, StringComparer.OrdinalIgnoreCase));
        PrepareFormulaSchedule();
        if (readPass is not null) RestoreVariables(readPass.Variables);
        // A drill run already owns the selected, ordered group from the parent run.
        // Applying selection or sorting again must not change that membership.
        if (summaryRows is { } source)
        {
            // Its summaries read the main run's records (Summary), each drill record at its place there.
            _summaryRows = source.Rows;
            _summaryRowIndexes = new(_rows.Length);
            for (var index = 0; index < _rows.Length; index++) _summaryRowIndexes.Add(_rows[index], source.Indexes[index]);
        }
        else PrepareRows();
        PreparePhysicalPageSchedule();
        if (_physicalPageSchedule) { _formulaPage = 1; _formulaPages = 1; }
        _initializingPrintContext = _replayingPrint = initializePrintContext;
        try { BuildBands(); }
        finally { _formulaPage = null; _formulaPages = null; _initializingPrintContext = _replayingPrint = false; }
    }

    // The date/time behind a special field, for a saved date/time format that applies to it.
    private DateTime? SpecialTime(string reference) => SpecialName(reference) switch
    {
        "printdate" or "printtime" or "datadate" or "datatime" => _printTime,
        "filecreationdate" => FileCreationTime, "modificationdate" or "modificationtime" => ModificationTime,
        _ => null
    };

    public static bool IsSpecial(string reference) => SpecialName(reference) is "pagenumber" or "totalpagecount" or "pagenofm" or "reporttitle" or "reportfilename" or "printdate" or "printtime" or "recordnumber"
        or "groupnumber" or "datadate" or "datatime" or "reportcomments" or "fileauthor" or "filepathandname" or "filecreationdate" or "modificationdate" or "modificationtime";
    private static string SpecialName(string reference) => reference.Trim('{', '}').Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();

    // Unsummarized chart formulas may read stable summaries, but never mutable printing or page state.
    private void RequireChartFormulaValue(string reference, HashSet<string> path)
    {
        if (path.Count >= 32 || !path.Add(reference)) throw new NotSupportedException("Cyclic or deeply nested chart formula dependency.");
        try
        {
            if (_layout.RunningTotals.ContainsKey(reference) || _layout.Summaries.ContainsKey(reference) || IsSpecial(reference))
                throw new NotSupportedException("Unsummarized chart formulas cannot read running totals, summary fields or page values.");
            if (!_layout.Formulas.TryGetValue(reference, out var formula)) return;
            if (formula.RequiresPrintPass || formula.UsesPersistentVariables || formula.UsesPageContext
                || formula.UsesRecordContext || formula.UsesGridContext || formula.UnknownSymbols.Count > 0
                || formula.CustomFunctions.Count > 0 || formula.EvaluateAfter.Count > 0)
                throw new NotSupportedException("Unsummarized chart formulas require stable record or aggregate values.");
            foreach (var dependency in formula.References) RequireChartFormulaValue(dependency, path);
        }
        finally { path.Remove(reference); }
    }

    private int ReportChartGroupLevel(ReportAnalysisDefinition definition, string reference) =>
        definition.ReportGroupChart && (reference.Equals(definition.CategoryField, StringComparison.OrdinalIgnoreCase)
            || reference.Equals(definition.SeriesField, StringComparison.OrdinalIgnoreCase)) ? GroupLevel(reference) : -1;

    private void ValidateReportChartOthers(ReportAnalysisDefinition definition, IReadOnlyList<int> indexes)
    {
        foreach (var field in new[] { definition.CategoryField, definition.SeriesField }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var level = ReportChartGroupLevel(definition, field);
            if (level < 0 || definition.Measures.Any(measure => measure.Field.Equals(field, StringComparison.OrdinalIgnoreCase))
                || !_others.TryGetValue(level, out var others)) continue;
            var merged = indexes.FirstOrDefault(index => others.ContainsKey(_rows[index]), -1);
            if (merged < 0) continue;
            var name = GroupNameValue(level, merged);
            if (indexes.Any(index => !others.ContainsKey(_rows[index]) && Equals(Value(field, index), name)))
                throw new InvalidDataException("The report-group chart's Others name matches a retained group value.");
        }
    }

    private object AxisValue(ReportAnalysisDefinition definition, string reference, int index)
    {
        var level = ReportChartGroupLevel(definition, reference);
        // Keep measure inputs raw when an authored chart also summarizes its axis field.
        if (level >= 0 && index >= 0 && index < _rows.Length
            && !definition.Measures.Any(measure => measure.Field.Equals(reference, StringComparison.OrdinalIgnoreCase))
            && _others.TryGetValue(level, out var others) && others.ContainsKey(_rows[index]))
            return GroupNameValue(level, index) ?? DBNull.Value;
        var value = Value(reference, index);
        var axis = reference.Equals(definition.CategoryField, StringComparison.OrdinalIgnoreCase)
            || reference.Equals(definition.SeriesField, StringComparison.OrdinalIgnoreCase)
            || definition.RowFields.Contains(reference, StringComparer.OrdinalIgnoreCase)
            || definition.ColumnFields.Contains(reference, StringComparer.OrdinalIgnoreCase);
        if (axis && definition.GroupKinds.TryGetValue(reference, out var kind) && kind != 0 && value is not null and not DBNull)
        {
            try { value = AnalyticalPeriod(value, kind); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            { throw new InvalidDataException($"{reference}: {error.Message}", error); }
        }
        return value ?? DBNull.Value;
    }

    private object? Value(string reference, int row)
    {
        reference = reference.Trim();
        if (_layout.Formulas.ContainsKey(reference)) return FormulaValue(reference, row);
        if (_layout.FormulaErrors.TryGetValue(reference, out var error)) throw new InvalidDataException($"{reference}: {error}");
        if (_layout.RunningTotals.TryGetValue(reference, out var running)) return RunningTotal(reference, running, row);
        if (_layout.GroupNames.TryGetValue(reference, out var condition)) return GroupNameValue(GroupLevel(condition), row);
        if (reference.StartsWith("{?", StringComparison.Ordinal))
        {
            var name = reference.Trim('{', '}').TrimStart('?');
            // A subreport link may carry its parameter name with trailing blanks ("Pm-@kDisplayValuesList ").
            if (_parameters.TryGetValue(name, out var parameter) || _parameters.TryGetValue(name.Trim(), out parameter)) return parameter;
            var sqlName = new string(name.Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray());
            if (_parameters.TryGetValue(sqlName, out parameter)) return parameter;
            _diagnostics.Add($"Parameter '{name}' has no value.");
            return "[?" + name + "]";
        }
        if (IsSpecial(reference)) return SpecialName(reference) switch
        {
            "pagenumber" => _formulaPage ?? _pageToken, "totalpagecount" => _formulaPages ?? _countToken, "pagenofm" => "Page " + (_formulaPage ?? _pageToken) + " of " + (_formulaPages ?? _countToken),
            "reporttitle" => ReportTitle, "reportfilename" => _layout.Document.SourceName,
            "printdate" => _printTime.Date, "printtime" => _printTime.ToShortTimeString(), "recordnumber" => row + 1,
            // The session reads its records when it is created, so the data date/time is the print time.
            "datadate" => _printTime.ToShortDateString(), "datatime" => _printTime.ToShortTimeString(), "groupnumber" => GroupNumber(row),
            "reportcomments" => _layout.Document.Comments, "fileauthor" => _layout.Document.Author, "filepathandname" => FilePathAndName,
            "filecreationdate" => FileCreationTime.Date, "modificationdate" => ModificationTime.Date, "modificationtime" => ModificationTime.ToShortTimeString(),
            _ => throw new InvalidOperationException($"Special field '{reference}' has no value.")
        };
        if (_layout.Bindings.TryGetValue(reference, out var alias))
        {
            if (row < 0 || row >= _rows.Length) return null;
            var data = _rows[row];
            if (ResultColumn(data.Table, alias) is { } column)
            {
                var value = data[column];
                if (value is not DBNull) return value;
                return _layout.ConvertDatabaseNullToDefault ? DatabaseNullDefault(reference) : null;
            }
            _diagnostics.Add($"Result column '{alias}' for '{reference}' is missing.");
        }
        else if (_layout.Summaries.TryGetValue(reference, out var summary)) return PrintedSummary(summary, row);
        _diagnostics.Add($"Field '{reference}' could not be resolved.");
        return "[" + reference + "]";
    }

    // DataRow[name] looks the column up by name on every read (twice, with the Contains check); the table's columns do not change.
    private DataTable? _resultColumnTable;
    private readonly Dictionary<string, DataColumn?> _resultColumns = new(StringComparer.Ordinal);
    private DataColumn? ResultColumn(DataTable table, string alias)
    {
        if (!ReferenceEquals(table, _resultColumnTable)) { _resultColumnTable = table; _resultColumns.Clear(); }
        if (!_resultColumns.TryGetValue(alias, out var column))
            _resultColumns[alias] = column = table.Columns.Contains(alias) ? table.Columns[alias] : null;
        return column;
    }

    private object DatabaseNullDefault(string reference) => ReferenceType(reference)?.BaseType switch
    {
        CrystalBaseType.String => "",
        CrystalBaseType.Number or CrystalBaseType.Currency => 0m,
        CrystalBaseType.Boolean => false,
        CrystalBaseType.Date or CrystalBaseType.DateTime => DateTime.MinValue,
        CrystalBaseType.Time => TimeSpan.Zero,
        _ => throw new InvalidDataException($"Cannot convert database NULL for '{reference}': its Crystal field type is unknown.")
    };

    // A summary field printed over no records is blank, not zero: Crystal prints an empty report's "Grand Total:" without
    // a value, and a report whose group selection eliminated every group has no current record to print one from.
    // Formulas and conditions keep the summary's own value (Count is 0) through Summary.
    private object? PrintedSummary(ReportLayoutSummary summary, int row)
        => _rows.Length == 0 && (_summaryRows is not { Length: > 0 } || row < 0 && _layout.GroupSelection is not null) ? null : Summary(summary, row);

    private object? Summary(ReportLayoutSummary summary, int row, int? stop = null)
    {
        if (_summaryRows is null || ReferenceEquals(_rows, _summaryRows)) return SummaryCore(summary, row, stop);
        // Group selection is a print-pass filter. First-pass summaries still include the excluded groups,
        // and a drill view's summaries are the main run's.
        var displayed = _rows;
        var sourceRow = SummaryRowIndex(row);
        try { _rows = _summaryRows; return SummaryCore(summary, sourceRow, stop); }
        finally { _rows = displayed; }
    }

    private int SummaryRowIndex(int row)
    {
        if (_summaryRows is null || ReferenceEquals(_rows, _summaryRows) || row < 0 || row >= _rows.Length) return row;
        _summaryRowIndexes ??= _summaryRows.Select((data, index) => (data, index)).ToDictionary(pair => pair.data, pair => pair.index);
        return _summaryRowIndexes[_rows[row]];
    }

    private object? SummaryCore(ReportLayoutSummary summary, int row, int? stop)
    {
        if (summary.PercentOfGroup is int scope)
        {
            if (scope < 0 || scope > _layout.Document.Groups.Count)
            {
                _diagnostics.Add($"Unknown percentage summary group {scope}.");
                return null;
            }
            var ordinary = summary with { PercentOfGroup = null };
            var denominator = ordinary with { Group = scope == 0 ? "" : _layout.Document.Groups[scope - 1].Condition, GroupLevel = scope - 1 };
            var part = SummaryCore(ordinary, row, stop);
            var whole = SummaryCore(denominator, row, stop);
            if (part is null || whole is null || part is string || whole is string) return null;
            var total = Convert.ToDecimal(whole, CultureInfo.InvariantCulture);
            return total == 0 ? null : Convert.ToDecimal(part, CultureInfo.InvariantCulture) / total * 100m;
        }
        // With no group instance, its summary fields are blank. Report-level
        // summaries below retain their operation's no-record semantics.
        if (row < 0 && summary.Group.Length > 0) return null;
        if (_layout.Formulas.TryGetValue(summary.Field, out var formula)) RequireReadTime(formula, "Summary");
        if (_layout.Summaries.ContainsKey(summary.Field)) { _diagnostics.Add("Nested summaries are not supported."); return "[Nested summary]"; }
        var start = 0;
        var end = Math.Clamp(stop ?? _rows.Length, 0, _rows.Length);
        if (summary.Group.Length > 0 && row >= 0 && row < _rows.Length)
        {
            var level = summary.GroupLevel >= 0 ? summary.GroupLevel : _layout.Document.Groups.FindIndex(group => group.Condition.Equals(summary.Group, StringComparison.OrdinalIgnoreCase));
            if (level < 0) { _diagnostics.Add($"Unknown summary group '{summary.Group}'."); return "[Unknown summary group]"; }
            if (!_groupRanges.TryGetValue(level, out var ranges))
            {
                ranges = new (int Start, int End)[_rows.Length];
                for (var first = 0; first < _rows.Length;)
                {
                    var last = first + 1;
                    while (last < _rows.Length && SameGroup(last, first, level)) last++;
                    for (var index = first; index < last; index++) ranges[index] = (first, last);
                    first = last;
                }
                _groupRanges[level] = ranges;
            }
            (start, end) = ranges[row];
            if (summary.AcrossHierarchy && HierarchyAt(level) is { } tree) end = tree.Nodes[tree.NodeOfRow[row]].SubtreeEnd;
        }
        var key = (summary, start, end);
        if (_summaryCache.TryGetValue(key, out var cached)) return cached;
        var values = Enumerable.Range(start, end - start).Select(index => Value(summary.Field, index)).Where(value => value is not null).ToList();
        if (summary.Operation is "Count") return _summaryCache[key] = values.Count;
        if (summary.Operation is "DistinctCount") return _summaryCache[key] = values.Distinct().Count();
        // A record summary has no numeric value when its range is empty or
        // contains only nulls. Keep it null so dependent formulas do not invent
        // zero totals; counts and array aggregates retain their own identities.
        if (values.Count == 0) return _summaryCache[key] = null;
        if (summary.Operation is "Minimum" or "Maximum")
            return _summaryCache[key] = values.Aggregate((left, right) => (CrystalFormula.Compare(left, right) < 0) == (summary.Operation == "Minimum") ? left : right);
        try
        {
            if (summary.Operation == "Weighted Average")
            {
                if (summary.WeightField.Length == 0) return Unsupported();
                var pairs = Enumerable.Range(start, end-start)
                    .Select(index => (Value: Value(summary.Field,index), Weight: Value(summary.WeightField,index)))
                    .Where(pair => pair.Value is not null and not DBNull && pair.Weight is not null and not DBNull)
                    .Select(pair => (Value: Convert.ToDecimal(pair.Value,CultureInfo.InvariantCulture), Weight: Convert.ToDecimal(pair.Weight,CultureInfo.InvariantCulture))).ToArray();
                var weight = pairs.Sum(pair => pair.Weight);
                return _summaryCache[key] = weight == 0 ? null : pairs.Sum(pair => pair.Value*pair.Weight)/weight;
            }
            var numbers = values.Select(value => Convert.ToDecimal(value, CultureInfo.InvariantCulture)).ToArray();
            if (summary.Operation is "Standard Deviation" or "Population Standard Deviation" or "Variance" or "Population Variance")
            {
                var population = summary.Operation.StartsWith("Population",StringComparison.Ordinal);
                var divisor = numbers.Length-(population?0:1);
                if(divisor<=0)return _summaryCache[key]=null;
                var mean = numbers.Average();
                var variance = numbers.Sum(number=>(number-mean)*(number-mean))/divisor;
                return _summaryCache[key] = summary.Operation.Contains("Deviation",StringComparison.Ordinal) ? (decimal)Math.Sqrt((double)variance) : variance;
            }
            return _summaryCache[key] = summary.Operation switch { "Sum" => numbers.Sum(), "Average" => numbers.Average(), "Minimum" => numbers.Min(), "Maximum" => numbers.Max(), _ => Unsupported() };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        { _diagnostics.Add($"Summary '{summary.Operation} {summary.Field}' has nonnumeric values."); return "[Invalid summary]"; }
        object Unsupported() { _diagnostics.Add($"Summary operation '{summary.Operation}' is not implemented."); return "[Unsupported summary]"; }
    }

    private string Format(string reference, int row, string format, ReportNativeDateTimeFormat? nativeDateTime = null,
        ReportNativeNumberFormat? nativeNumber = null, ReportNativeNumericFormat? nativeNumeric = null, Action<object?>? capture = null)
    {
        try
        {
            if (_formulaPage is null && DependsOn(reference, true)) return DeferredPageValue(reference, row, format, nativeDateTime, nativeNumber, nativeNumeric);
            // A native date/time format this renderer cannot honour (reported once per object in the
            // constructor) falls back to the special field's default text, never to a formula error.
            object? value;
            if (nativeDateTime is { UnsupportedReason: null } && SpecialTime(reference) is { } special) value = special;
            else if (nativeDateTime is { UnsupportedReason: null, SystemDefaultFormatting: false, WindowsDateDefault: 2, DateComponents: { } components }
                && GroupPeriodDate(reference, row) is var (period, kind) && period is { } periodDate)
            {
                // A date group's name field with its own date components prints the group's period in them, without the day
                // for a month, quarter or half-year group and without the month as well for a year group ("1/03", "2003").
                value = periodDate;
                if (kind >= 4) nativeDateTime = nativeDateTime with { DateComponents = components with { DayType = 2, MonthType = kind == 7 ? 4 : components.MonthType } };
            }
            else value = Value(reference, row);
            capture?.Invoke(value);
            // A saved format this renderer cannot honour is reported once per object (see the constructor);
            // the value still prints, with the default format, rather than as a formula error.
            if (string.IsNullOrEmpty(format) && nativeNumeric is null && nativeNumber is { UnsupportedReason: null } && value is not (null or DBNull or string))
                return nativeNumber.Format(value, CultureInfo.CurrentCulture);
            if (string.IsNullOrEmpty(format) && value is DateTime dateTime && nativeDateTime is { UnsupportedReason: null })
                return nativeDateTime.Format(dateTime, CultureInfo.CurrentCulture);
            if (string.IsNullOrEmpty(format) && nativeNumeric is { UnsupportedReason: null }
                && value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                // An embedded field without a format of its own prints whole numbers (counts, integer fields) without
                // decimals and other numbers with the regional decimals, never with a currency symbol.
                var effective = ReferenceEquals(nativeNumeric, EmbeddedNumericDefault)
                    ? value is sbyte or byte or short or ushort or int or uint or long or ulong ? EmbeddedIntegerDefault : nativeNumeric
                    : nativeNumeric.SystemDefaultFormatting && CurrencyResult(reference) ? nativeNumeric with { CurrencySymbolType = 2 } : nativeNumeric;
                return effective.Format(value);
            }
            // Without a field format a Crystal Date prints as a short date, without the time of day.
            if (value is DateTime date && string.IsNullOrEmpty(format) && (ReferenceType(reference)?.BaseType == CrystalBaseType.Date || SpecialName(reference) is "printdate" or "filecreationdate" or "modificationdate"))
                return date.ToString("d", CultureInfo.CurrentCulture);
            return value is IFormattable formattable ? formattable.ToString(string.IsNullOrEmpty(format) ? null : format, CultureInfo.CurrentCulture) ?? "" : value?.ToString() ?? "";
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or InvalidCastException or OverflowException or DivideByZeroException or NotSupportedException or ArgumentException)
        { _diagnostics.Add($"{reference}: {ex.Message}"); return "[Formula error: " + reference + "]"; }
    }

    // Hide (Drill-Down OK) hides main output but permits it inside the selected drill scope.
    // Suppress and its conditional formulas remain effective in both views.
    private bool Visible(ReportDesignerSection section) => InDrillScope(section) && !section.IsSuppressed
        && (!section.HideForDrillDown || _drillDownGroupLevel > 0 && DrillSectionDepth(section) <= _drillDownGroupLevel + 1);

    private static string OnDemandCaptionHtml(ReportDesignerElement element, int row)
    {
        var caption = string.IsNullOrWhiteSpace(element.Text) ? element.SubreportName : element.Text;
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            title = string.IsNullOrWhiteSpace(element.SubreportTabText) ? caption : element.SubreportTabText,
            onDemand = true, elementId = element.Id, row
        });
        return "<a class=\"fx-rpt-subreport-link\" href=\"#\" data-subreport=\"" + System.Net.WebUtility.HtmlEncode(payload)
            + "\" style=\"color:inherit;text-decoration:inherit\">" + System.Net.WebUtility.HtmlEncode(caption) + "</a>";
    }

    private void ApplyAccumulator(Item item)
    {
        var variable = item.Element.Analysis!.AccumulatorVariable;
        _globals[variable] = Convert.ToDecimal(_globals.GetValueOrDefault(variable), CultureInfo.InvariantCulture) + item.AccumulatorDelta!.Value;
    }

    // A band evaluates each formula once. A failure stays in the band cache only while the band is laid out: the cache's
    // values outlive the band (the last page-furniture variant is laid out at a tentative page 1 of 1, and a subreport's last
    // band keeps its entries into pagination), and a stale failure would be raised again at a real page.
    private Band CreateBand(ReportDesignerSection section, int row, List<Band> repeats)
    {
        _failureScope++;
        try { return LayOutBand(section, row, repeats); }
        finally { _failureScope--; DropBandFailures(); }
    }

    private void DropBandFailures()
    {
        if (_failureScope == 0) DropFailures(_bandFormulaCache);
    }

    // Before a band goes on to its next physical page: marks in the band's replay cache the failures of formulas that may
    // succeed on a later page and are safe to run again (PageReads), so they run again where they are next evaluated, on any
    // later page, while each variable they read holds the value it had when they failed (PageFailure). Such a formula reads
    // the page, or a variable the page furniture assigns (RunVariables.Furniture), and assigns only temporaries
    // (CrystalFormula.AssignsOnlyTemporaries) that no other formula, condition or subreport of the run reads
    // (RunVariables.SharesAssignments), so its second run changes nothing another object prints. A counter keeps its failure,
    // so its record counts once. When a band has changed one of those variables since the failure (the band's own later
    // objects, or a later record's band printed over an underlay before the underlay's next page), the failure is raised
    // again, so a record never prints a later record's value; the page furniture's own writes count as the value it had
    // (ReplayPhysicalPages, Rebase).
    private void DropPageFailures(Dictionary<(string Name, int Row), object?> cache)
    {
        if (_failureReads is not { } reads) return;
        List<((string Name, int Row) Key, PageFailure Failure)>? dropped = null;
        foreach (var entry in cache)
            if (entry.Value is Exception failure && reads.TryGetValue((entry.Key, failure), out var values)) (dropped ??= []).Add((entry.Key, new(failure, values)));
        if (dropped is not null) foreach (var (key, failure) in dropped) cache[key] = failure;
    }

    // A failure a page break lets run again (DropPageFailures), with what its formula read when it failed (Rebase moves it on
    // with the page furniture).
    private sealed record PageFailure(Exception Failure, ReadValue[] Reads);
    // A variable as "scope:name" (a global of the run's main report read from a subreport as "main:global:name"), whether it
    // was set, and its value when the formula failed, or as the page furniture has left it since (a Crystal array is never
    // changed in place, so the value itself is kept); for one the page furniture sets, the variables a band assigns that the
    // furniture computes it from, and those only the furniture assigns that it computes it through (RunVariables.Sources), as
    // they were when the formula first failed, or as the page furniture has left them since (Rebase). Kept: the furniture
    // may have set it from a band's change since then, or a formula that sets it failed partway, so whatever its value it
    // never matches (Rebase).
    private sealed record ReadValue(string Variable, bool Present, object? Value, ReadValue[] Sources, bool Kept = false);

    // Where a page failure is evaluated again: it runs (true, the cache forgets it) while each variable it read holds the value
    // it had when it failed, or the page furniture has left in it since (Rebase); otherwise the band keeps the failure, raised
    // again here.
    private bool RunsAgain((string Name, int Row) key, PageFailure dropped)
    {
        if (dropped.Reads.All(Unchanged))
        {
            _bandFormulaCache.Remove(key);
            _failureReads?.Remove((key, dropped.Failure));
            return true;
        }
        _bandFormulaCache[key] = dropped.Failure;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(dropped.Failure);
        return false;
    }

    // The variables a failure compares before it runs again on a later page: what the formula, or one it reads, reads before
    // it sets it (ReadsVariables), less those only the page furniture assigns, which change with the page, unless the page
    // furniture placed on the page computes one from variables a band assigns; each with those band variables (Sources). null
    // when the failure never runs again (it reads neither the page nor a variable the page furniture assigns, assigns more
    // than temporaries, or assigns one another object reads). A circular reference runs again. The same for every failure of
    // the formula (_pageReads).
    private (string Variable, string[] Sources)[]? PageReads(string name) =>
        _pageReads.TryGetValue(name, out var known) ? known : _pageReads[name] = FindPageReads(name);

    private readonly Dictionary<string, (string Variable, string[] Sources)[]?> _pageReads = new(StringComparer.OrdinalIgnoreCase);

    private (string Variable, string[] Sources)[]? FindPageReads(string name)
    {
        var run = RunVariableMap();
        if (_layout.Formulas.TryGetValue(name, out var formula) && formula.WritesPersistentVariables
            && (!formula.AssignsOnlyTemporaries || run.SharesAssignments(_layout, formula))) return null;
        try
        {
            var reads = ReadsVariables(name);
            var furniture = run.Furniture(_layout);
            var bands = run.Bands(_layout);
            bool Furnished(string variable) => furniture.Contains(variable) || run.SharedFurniture.Contains(variable);
            if (!DependsOn(name, true) && !reads.Any(Furnished)) return null;
            return reads.Select(v => (Variable: v, Sources: Furnished(v) ? run.Sources(_layout, v) : []))
                .Where(r => !Furnished(r.Variable) || bands.Contains(r.Variable) || run.SharedBands.Contains(r.Variable) || r.Sources.Length > 0 && run.Placed(_layout, r.Variable))
                .ToArray();
        }
        catch (InvalidDataException) { return []; }
    }

    // A formula that ran again on a later page and failed again (earlier: what its failure noted) keeps the band variables'
    // values from when it first failed, as the page furniture has left them since: a band may have changed them since, and
    // what the furniture computes from them then may come from a later record. earlier lists the same reads in the same
    // order, as PageReads is the same for every failure of a formula.
    private ReadValue[] ReadValues((string Variable, string[] Sources)[] reads, ReadValue[]? earlier = null) =>
        reads.Select((read, index) => Current(read.Variable) with
        {
            Sources = (earlier is null ? null : index < earlier.Length && earlier[index].Variable == read.Variable ? earlier[index]
                : Array.Find(earlier, e => e.Variable == read.Variable))?.Sources ?? read.Sources.Select(Current).ToArray()
        }).ToArray();

    private ReadValue Current(string variable)
    {
        var (present, value) = Read(variable);
        return new(variable, present, value, []);
    }

    private bool Unchanged(ReadValue read) => Matches(read, Current(read.Variable));

    // A variable unset when the formula failed reads as its type's default, as it does once a declaration sets it. A kept
    // variable never matches.
    private static bool Matches(ReadValue read, ReadValue current) =>
        !read.Kept && (read.Present ? current.Present && SameValue(current.Value, read.Value) : !current.Present || Declared(current.Value));

    // Before the page furniture prints: what each read, and each variable its Sources name, holds.
    private (ReadValue Value, ReadValue[] Sources)[] BeforeFurniture(ReadValue[] reads) =>
        reads.Select(read => (Current(read.Variable), Array.ConvertAll(read.Sources, source => Current(source.Variable)))).ToArray();

    // After the page furniture (section, of the report furniture) prints. A read the furniture computes from no band's
    // variable takes the value the furniture left when it held its value up to the furniture (before), or the furniture
    // changed it and sets it without reading it (RunVariables.Resets). For any other, each variable the section assigns among
    // the read and its Sources is noted again, from what the section computes it from (RunVariables.Placing). One it may
    // compute from a variable that changed since it was noted (a band's change, or a kept one) is kept, whatever its value,
    // so the failure is raised again rather than run with what the furniture computed from a band's later change; so is
    // each one when a formula that assigns a variable failed while the section printed (failed), as that formula may have
    // set some of its variables and not the rest. One a band changed that the section left as it was stays as noted. Any
    // other takes the value the section left, which then counts as unchanged: only the furniture printed since it was
    // compared, so a change it makes itself (a page count it resets) is no band's change. A variable the section does not
    // assign stays as noted, so a band's change to it stays a change.
    private void Rebase(ReadValue[] reads, (ReadValue Value, ReadValue[] Sources)[] before, ReportPositionedLayout furniture, ReportDesignerSection section,
        bool failed)
    {
        var run = RunVariableMap();
        for (var index = 0; index < reads.Length; index++)
        {
            var read = reads[index];
            var (held, heldSources) = before[index];
            var after = Current(read.Variable);
            if (read.Sources.Length == 0)
            {
                if (Matches(read, held) || !Matches(held, after) && run.Resets(_layout, read.Variable)) reads[index] = after;
                continue;
            }
            var placing = run.Placing(_layout, furniture, section);
            var noted = read.Sources;
            bool Changed(string variable)
            {
                if (variable == read.Variable) return !Matches(read, held);
                var source = Array.FindIndex(noted, s => s.Variable == variable);
                return source >= 0 && !Matches(noted[source], heldSources[source]);
            }
            ReadValue Next(ReadValue value, ReadValue was, ReadValue now) =>
                !placing.TryGetValue(value.Variable, out var from) ? value
                : failed || from.Any(Changed) ? value with { Kept = true }
                : !Matches(value, was) && Matches(was, now) ? value
                : now;
            var sources = new ReadValue[noted.Length];
            for (var source = 0; source < noted.Length; source++) sources[source] = Next(noted[source], heldSources[source], Current(noted[source].Variable));
            reads[index] = Next(read, held, after) with { Sources = sources };
        }
    }

    // The value a declaration gives a variable it finds unset (the type's default).
    private static bool Declared(object? value) => value switch
    {
        null or "" or false => true,
        decimal number => number == 0,
        DateTime date => date == DateTime.MinValue,
        TimeSpan time => time == TimeSpan.Zero,
        object?[] items => items.Length == 0,
        CrystalRange range => range is { IncludeStart: true, IncludeEnd: true } && Declared(range.Start) && Equals(range.Start, range.End),
        _ => false
    };

    // A global variable of this report (or, as "main:global:name", of the run's main report), or a shared one of the run, by
    // its "scope:name".
    private (bool Present, object? Value) Read(string variable)
    {
        if (variable.StartsWith("main:", StringComparison.Ordinal)) return _state.Sessions[0].Read(variable["main:".Length..]);
        var store = variable.StartsWith("shared:", StringComparison.Ordinal) ? _state.Shared : _globals;
        return store.TryGetValue(variable[(variable.IndexOf(':') + 1)..], out var value) ? (true, value) : (false, null);
    }

    // Arrays compare element by element, the same array at once; an array may hold arrays to any depth, so without recursion,
    // and may hold one array many times, so each pair of arrays is compared once (the work grows with the arrays, never with
    // the paths through them). A short array (8 elements or fewer) that holds no array is compared again each time it is
    // reached, at most 8 comparisons each time, and is not recorded, so a value of many small arrays keeps no record of them.
    private static bool SameValue(object? a, object? b)
    {
        var pending = new Stack<(object? Left, object? Right)>();
        HashSet<(object?[] Left, object?[] Right)>? compared = null;
        pending.Push((a, b));
        while (pending.TryPop(out var pair))
        {
            if (ReferenceEquals(pair.Left, pair.Right)) continue;
            if (pair.Left is object?[] left)
            {
                if (pair.Right is not object?[] right || left.Length != right.Length) return false;
                if ((left.Length > 8 || Array.Exists(left, item => item is object?[])) && !(compared ??= new(new ArrayPairs())).Add((left, right))) continue;
                for (var index = 0; index < left.Length; index++) pending.Push((left[index], right[index]));
            }
            else if (pair.Right is object?[] || !Equals(pair.Left, pair.Right)) return false;
        }
        return true;
    }

    // A pair of arrays by reference.
    private sealed class ArrayPairs : IEqualityComparer<(object?[] Left, object?[] Right)>
    {
        public bool Equals((object?[] Left, object?[] Right) x, (object?[] Left, object?[] Right) y) => ReferenceEquals(x.Left, y.Left) && ReferenceEquals(x.Right, y.Right);
        public int GetHashCode((object?[] Left, object?[] Right) pair) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Left), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Right));
    }

    private RunVariables RunVariableMap() => _state.Variables ??= new(_state.Sessions[0]._layout);

    // What the formula, or one it reads, reads before it sets it (CrystalFormula.ReadVariables), as "scope:name".
    private HashSet<string> ReadsVariables(string reference, HashSet<string>? stack = null)
    {
        if (!_layout.Formulas.TryGetValue(reference, out var formula) || formula.ReadVariables.Count == 0 && formula.References.Count == 0) return [];
        var readers = RunVariableMap().Readers(_layout);
        if (readers.TryGetValue(reference, out var cached)) return cached;
        stack ??= new(StringComparer.OrdinalIgnoreCase);
        if (!stack.Add(reference)) throw new InvalidDataException($"Circular formula reference '{reference}'.");
        var reads = new HashSet<string>(formula.ReadVariables, StringComparer.Ordinal);
        foreach (var other in formula.References) reads.UnionWith(ReadsVariables(other, stack));
        stack.Remove(reference);
        return readers[reference] = reads;
    }

    // What a page break needs to know about the variables of the run's reports (its main report and the inline subreports
    // below it), worked out once a run, however many subreport instances print: the variables each report's page furniture
    // assigns and those its bands assign, the shared ones the run's furniture assigns and those its bands assign, and the
    // formulas and conditions that declare each variable.
    // An on-demand subreport, and every subreport below it, declares the shared variables its formulas and conditions do (it
    // reads the run's shared variables when it opens); it prints no page furniture in the run.
    private sealed class RunVariables
    {
        private readonly ReportPositionedLayout _main;
        private readonly Dictionary<ReportPositionedLayout, Furnishing> _furniture = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _sharedRereads = new(StringComparer.Ordinal);
        // What the page furniture of the run's printed reports computes each variable it assigns from (FurnitureVariables), and
        // the variables the furniture of a subreport assigns (a subreport's repeated group header prints inside its band, so
        // it never rebases a failure): a global keyed by its report, a shared variable by none.
        private readonly Dictionary<(ReportPositionedLayout? Layout, string Variable), HashSet<(ReportPositionedLayout? Layout, string Variable)>> _sources = new();
        private readonly HashSet<(ReportPositionedLayout? Layout, string Variable)> _unplaced = new();
        private readonly Dictionary<(ReportPositionedLayout Layout, string Variable), string[]> _bandSources = new();
        private readonly Dictionary<(ReportPositionedLayout Layout, ReportPositionedLayout Furniture, string Section), Dictionary<string, HashSet<string>>> _placing = new();
        private readonly Dictionary<ReportPositionedLayout, HashSet<string>> _bands = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ReportPositionedLayout, Dictionary<string, HashSet<string>>> _readers = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, List<(ReportPositionedLayout Layout, CrystalFormula Formula)>> _declarers = new(StringComparer.Ordinal);

        public RunVariables(ReportPositionedLayout main)
        {
            _main = main;
            SharedBands.UnionWith(Bands(main).Where(IsShared));
            var printed = new HashSet<ReportPositionedLayout>(ReferenceEqualityComparer.Instance);
            var opened = new HashSet<ReportPositionedLayout>(ReferenceEqualityComparer.Instance);
            var pending = new Stack<(ReportPositionedLayout Layout, bool OnDemand)>();
            pending.Push((main, false));
            while (pending.TryPop(out var next))
            {
                var (layout, onDemand) = next;
                if (!(onDemand ? opened : printed).Add(layout)) continue;
                foreach (var child in layout.Subreports.Values) if (child.Definition.PositionedLayout is { } nested) pending.Push((nested, onDemand));
                foreach (var child in layout.OnDemandSubreports.Values) if (child.Definition.PositionedLayout is { } nested) pending.Push((nested, true));
                if (!onDemand)
                {
                    var furnishing = Furnished(layout);
                    SharedFurniture.UnionWith(furnishing.Assigned.Where(IsShared));
                    _sharedRereads.UnionWith(furnishing.Rereads.Where(IsShared));
                    foreach (var (variable, from) in furnishing.Sources)
                    {
                        (_sources.TryGetValue(variable, out var set) ? set : _sources[variable] = []).UnionWith(from);
                        if (!ReferenceEquals(layout, main)) _unplaced.Add(variable);
                    }
                }
                foreach (var formula in layout.Formulas.Values.Concat(layout.SectionConditions.Values.Concat(layout.AreaConditions.Values)
                             .Concat(layout.ObjectConditions.Values).SelectMany(c => c.Values)))
                    foreach (var variable in formula.PersistentVariables)
                        if (!onDemand || IsShared(variable))
                            (_declarers.TryGetValue(variable, out var list) ? list : (_declarers[variable] = [])).Add((layout, formula));
            }
        }

        /// <summary>The shared variables the page furniture of any report of the run assigns.</summary>
        public HashSet<string> SharedFurniture { get; } = new(StringComparer.Ordinal);

        /// <summary>The shared variables the bands of the run assign: the main report's, and those of the subreports they print.</summary>
        public HashSet<string> SharedBands { get; } = new(StringComparer.Ordinal);

        /// <summary>The variables the report's page furniture assigns (FurnitureVariables).</summary>
        public HashSet<string> Furniture(ReportPositionedLayout layout) => Furnished(layout).Assigned;

        /// <summary>True when the page furniture of the report (for a global) or of the run (for a shared variable) assigns the
        /// variable and none of its formulas that assign a variable reads it: what the furniture sets in it never carries
        /// what was in it.</summary>
        public bool Resets(ReportPositionedLayout layout, string variable) => IsShared(variable)
            ? SharedFurniture.Contains(variable) && !_sharedRereads.Contains(variable)
            : Furnished(layout) is var (assigned, rereads, _) && assigned.Contains(variable) && !rereads.Contains(variable);

        /// <summary>For each variable the section of a report's furniture assigns (SectionVariables), as the report (layout)
        /// names it, the variables whose values when the section began the value it leaves there may come from: what each
        /// formula there assigns from what (CrystalFormula.AssignmentReads), followed back to formulas that may run before it
        /// (FurnitureStep); a formula that runs once there never feeds itself.</summary>
        public Dictionary<string, HashSet<string>> Placing(ReportPositionedLayout layout, ReportPositionedLayout furniture, ReportDesignerSection section)
        {
            if (_placing.TryGetValue((layout, furniture, section.Id), out var cached)) return cached;
            var steps = new Dictionary<(ReportPositionedLayout? Layout, string Variable), List<FurnitureStep>>();
            SectionVariables(furniture, [section], steps: steps);
            var placing = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (variable, into) in steps)
            {
                if (Named(layout, variable) is not { } name) continue;
                var from = placing.TryGetValue(name, out var known) ? known : placing[name] = new(StringComparer.Ordinal);
                // A variable as it was where a formula ran (before Before), and that formula.
                var seen = new HashSet<((ReportPositionedLayout? Layout, string Variable) Node, int Before, CrystalFormula? Formula)>();
                var pending = new Stack<((ReportPositionedLayout? Layout, string Variable) Node, int Before, CrystalFormula? Formula)>();
                foreach (var step in into) pending.Push((step.From, step.Hi, step.Formula));
                while (pending.TryPop(out var state))
                {
                    if (!seen.Add(state)) continue;
                    if (Named(layout, state.Node) is { } source) from.Add(source);
                    if (steps.TryGetValue(state.Node, out var next))
                        foreach (var step in next)
                            if (step.Lo <= state.Before && (state.Formula is null || !ReferenceEquals(step.Formula, state.Formula)))
                                pending.Push((step.From, Math.Min(step.Hi, state.Before), step.Formula));
                }
            }
            return _placing[(layout, furniture, section.Id)] = placing;
        }

        // A variable of the run as the report (layout) names it; null for a subreport's own global another report prints.
        private string? Named(ReportPositionedLayout layout, (ReportPositionedLayout? Layout, string Variable) node) =>
            node.Layout is null || ReferenceEquals(node.Layout, layout) ? node.Variable : ReferenceEquals(node.Layout, _main) ? "main:" + node.Variable : null;

        /// <summary>The variables a band assigns that the page furniture computes the variable from, through the variables the
        /// furniture assigns in between (the furniture formulas that assign one read them, or a formula they refer to):
        /// globals of the report, those of the run's main report (as "main:global:name"), and shared variables; then, when
        /// there is one, the other variables in between. A subreport's own globals that its furniture prints are followed,
        /// not compared; the variable itself never counts.</summary>
        public string[] Sources(ReportPositionedLayout layout, string variable)
        {
            if (_bandSources.TryGetValue((layout, variable), out var cached)) return cached;
            var found = new List<string>();
            var through = new List<string>();
            var start = Node(layout, variable);
            var seen = new HashSet<(ReportPositionedLayout? Layout, string Variable)> { start };
            var pending = new Stack<(ReportPositionedLayout? Layout, string Variable)>();
            pending.Push(start);
            while (pending.TryPop(out var node))
            {
                if (!_sources.TryGetValue(node, out var from)) continue;
                foreach (var source in from)
                {
                    if (!seen.Add(source)) continue;
                    pending.Push(source);
                    if (source.Layout is null) (SharedBands.Contains(source.Variable) ? found : through).Add(source.Variable);
                    else if (ReferenceEquals(source.Layout, layout)) (Bands(layout).Contains(source.Variable) ? found : through).Add(source.Variable);
                    else if (ReferenceEquals(source.Layout, _main)) (Bands(_main).Contains(source.Variable) ? found : through).Add("main:" + source.Variable);
                }
            }
            return _bandSources[(layout, variable)] = found.Count == 0 ? [] : [.. found, .. through];
        }

        /// <summary>True when only furniture placed on the page itself assigns the variable: the run's main report's page
        /// header and footer and repeated group headers (and what they print), whose writes rebase a failure (Rebase).</summary>
        public bool Placed(ReportPositionedLayout layout, string variable) => !_unplaced.Contains(Node(layout, variable));

        private static (ReportPositionedLayout? Layout, string Variable) Node(ReportPositionedLayout layout, string variable) =>
            IsShared(variable) ? (null, variable) : (layout, variable);

        private Furnishing Furnished(ReportPositionedLayout layout) =>
            _furniture.TryGetValue(layout, out var sets) ? sets : _furniture[layout] = FurnitureVariables(layout, ReferenceEquals(layout, _main));

        /// <summary>The variables the report's bands assign (BandVariables).</summary>
        public HashSet<string> Bands(ReportPositionedLayout layout) =>
            _bands.TryGetValue(layout, out var set) ? set : _bands[layout] = BandVariables(layout);

        /// <summary>For the report, the variables each of its formulas reads (ReadsVariables).</summary>
        public Dictionary<string, HashSet<string>> Readers(ReportPositionedLayout layout) =>
            _readers.TryGetValue(layout, out var readers) ? readers : _readers[layout] = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>True when another formula or condition of the run declares a variable the formula assigns, a shared one in
        /// any of the run's reports, a global one in the formula's own, and may read it before it sets it
        /// (CrystalFormula.ReadVariables): one that only declares it, or sets it before it uses it, never reads what the
        /// formula left.</summary>
        public bool SharesAssignments(ReportPositionedLayout layout, CrystalFormula formula) =>
            formula.AssignedVariables.Any(variable => _declarers.TryGetValue(variable, out var declarers)
                && declarers.Any(d => !ReferenceEquals(d.Formula, formula) && (IsShared(variable) || ReferenceEquals(d.Layout, layout))
                    && d.Formula.ReadVariables.Contains(variable)));

        private static bool IsShared(string variable) => variable.StartsWith("shared:", StringComparison.Ordinal);
    }

    // The variables a report's page furniture assigns: through the formulas its repeated group headers print and, for the run's
    // main report, its page header and page footer (a subreport prints neither), their objects' and sections' conditions, the
    // link formulas of the subreports printed there and the formulas those read, the print-time cell formulas of a cross-tab
    // printed there, and the global a ranked cross-tab printed there adds to; and the shared variables of a subreport printed
    // there, through what its own sections print. With them, the variables its formulas that assign a variable read, or the
    // formulas those refer to (Rereads), and for each variable they assign (a subreport's own globals too), what the formulas
    // that assign it read (Sources).
    private sealed record Furnishing(HashSet<string> Assigned, HashSet<string> Rereads,
        Dictionary<(ReportPositionedLayout? Layout, string Variable), HashSet<(ReportPositionedLayout? Layout, string Variable)>> Sources);

    private static Furnishing FurnitureVariables(ReportPositionedLayout layout, bool main)
    {
        var furnishing = new Furnishing(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new());
        furnishing.Assigned.UnionWith(SectionVariables(layout, layout.Document.Sections.Where(s => (main && s.Kind is "PageHeader" or "PageFooter")
            || s.Kind == "GroupHeader" && (layout.Areas.GetValueOrDefault(s.Id)?.RepeatHeader ?? false)), furnishing));
        return furnishing;
    }

    // The variables a report's bands assign in the same way: through every section but its page header and page footer (a
    // repeated group header prints as a band too).
    private static HashSet<string> BandVariables(ReportPositionedLayout layout) =>
        SectionVariables(layout, layout.Document.Sections.Where(s => s.Kind is not ("PageHeader" or "PageFooter")));

    // A variable a furniture formula (Formula; null for one that may run again there) assigns takes its value from From, as
    // it was where the formula ran: between the tops Lo and Hi of the section printed.
    private sealed record FurnitureStep((ReportPositionedLayout? Layout, string Variable) From, CrystalFormula? Formula, int Lo, int Hi);

    // steps: for each variable the sections assign, where its value comes from (RunVariables.Placing). A formula runs where
    // the objects that print it are (their tops), where a formula that refers to it runs, and anywhere for a condition; a
    // subreport's sections run from its top on, and run again. The objects are followed in the order they print, so a
    // subreport the section prints twice runs from the upper one's top on. The walk recurses only into a subreport's
    // sections, as deep as inline subreports nest; the formulas a formula refers to are followed from a stack.
    private static HashSet<string> SectionVariables(ReportPositionedLayout layout, IEnumerable<ReportDesignerSection> printed, Furnishing? furnishing = null,
        Dictionary<(ReportPositionedLayout? Layout, string Variable), List<FurnitureStep>>? steps = null)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var nested = new HashSet<ReportPositionedLayout>(ReferenceEqualityComparer.Instance);
        var rereads = furnishing?.Rereads;
        var spans = new Dictionary<CrystalFormula, (int Lo, int Hi)>(ReferenceEqualityComparer.Instance);
        Sections(layout, printed, false, int.MinValue, 0);
        if (steps is not null)
            foreach (var into in steps.Values)
                for (var index = 0; index < into.Count; index++)
                    if (into[index].Formula is { } formula) into[index] = into[index] with { Lo = spans[formula].Lo, Hi = spans[formula].Hi };
        return set;

        // level: how many subreports deep owner is below layout.
        void Sections(ReportPositionedLayout owner, IEnumerable<ReportDesignerSection> sections, bool sharedOnly, int at, int level)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var following = new Stack<(string? Reference, int Lo, int Hi, bool Again)>();
            (ReportPositionedLayout? Layout, string Variable) Node(string variable) => variable.StartsWith("shared:", StringComparison.Ordinal) ? (null, variable) : (owner, variable);
            // again: the formula may run more than once there (a cross-tab's cells).
            void Assigns(CrystalFormula formula, int lo, int hi, bool again = false)
            {
                if (sharedOnly) (lo, hi) = (at, int.MaxValue);
                Span(formula, lo, hi);
                set.UnionWith(sharedOnly ? formula.AssignedVariables.Where(v => v.StartsWith("shared:", StringComparison.Ordinal)) : formula.AssignedVariables);
                if (furnishing is not null && formula.AssignedVariables.Count > 0)
                {
                    var reads = Rereads(formula);
                    rereads!.UnionWith(reads);
                    foreach (var variable in formula.AssignedVariables)
                    {
                        var sources = furnishing.Sources.TryGetValue(Node(variable), out var found) ? found : furnishing.Sources[Node(variable)] = [];
                        foreach (var read in reads) if (read != variable) sources.Add(Node(read));
                    }
                }
                if (steps is not null)
                    foreach (var (variable, from) in formula.AssignmentReads)
                    {
                        var into = steps.TryGetValue(Node(variable), out var known) ? known : steps[Node(variable)] = [];
                        foreach (var read in from)
                            if (!read.StartsWith("{@", StringComparison.Ordinal)) into.Add(new(Node(read), sharedOnly || again ? null : formula, lo, hi));
                            // What a formula it refers to reads, wherever that one ran.
                            else if (owner.Formulas.TryGetValue(read, out var referred)) foreach (var inner in Rereads(referred)) into.Add(new(Node(inner), null, int.MinValue, int.MaxValue));
                    }
                Refers(formula, lo, hi, again);
            }
            HashSet<string> Rereads(CrystalFormula formula)
            {
                var reads = new HashSet<string>(StringComparer.Ordinal);
                var seen = new HashSet<CrystalFormula>(ReferenceEqualityComparer.Instance);
                var pending = new Stack<CrystalFormula>([formula]);
                while (pending.TryPop(out var next))
                {
                    if (!seen.Add(next)) continue;
                    reads.UnionWith(next.ReadVariables);
                    foreach (var reference in next.References) if (owner.Formulas.TryGetValue(reference, out var f)) pending.Push(f);
                }
                return reads;
            }
            void Span(CrystalFormula formula, int lo, int hi)
            {
                if (steps is not null) spans[formula] = spans.TryGetValue(formula, out var span) ? (Math.Min(span.Lo, lo), Math.Max(span.Hi, hi)) : (lo, hi);
            }
            // The formulas a formula refers to are followed from a stack (following), in the order a recursive walk would follow
            // them, so a long chain of references never runs out of the thread's stack.
            void Refers(CrystalFormula formula, int lo, int hi, bool again)
            {
                for (var index = formula.References.Count - 1; index >= 0; index--) following.Push((formula.References[index], lo, hi, again));
            }
            void Follow() { while (following.TryPop(out var next)) Visit(next.Reference, next.Lo, next.Hi, next.Again); }
            void Read(string? reference, int lo, int hi) { Visit(reference, lo, hi, false); Follow(); }
            void Visit(string? reference, int lo, int hi, bool again)
            {
                if (string.IsNullOrEmpty(reference) || !owner.Formulas.TryGetValue(reference, out var f)) return;
                if (visited.Add(again ? "*" + reference : reference)) { Assigns(f, lo, hi, again); return; }
                if (sharedOnly) (lo, hi) = (at, int.MaxValue);
                // Reached again, from another place: it may run there, and so may the formulas it refers to.
                if (steps is null || !spans.TryGetValue(f, out var span) || lo >= span.Lo && hi <= span.Hi) return;
                Span(f, lo, hi);
                Refers(f, lo, hi, again);
            }
            void Conditions(Dictionary<string, Dictionary<string, CrystalFormula>> table, string id) { if (table.TryGetValue(id, out var c)) foreach (var f in c.Values) { Assigns(f, int.MinValue, int.MaxValue); Follow(); } }
            foreach (var section in sections)
            {
                Conditions(owner.SectionConditions, section.Id);
                Conditions(owner.AreaConditions, section.Id);
                foreach (var element in section.Elements.OrderBy(e => e.TopTwips))
                {
                    foreach (var binding in element.Visual.Runs.Select(r => r.Binding).Prepend(element.Binding).Append(ValueBinding(element)).Append(element.HighlightRule.FieldName))
                        Read(binding, element.TopTwips, element.TopTwips);
                    Conditions(owner.ObjectConditions, element.Id);
                    if (owner.Subreports.TryGetValue(element.Id, out var sub))
                    {
                        // The links are evaluated where the subreport prints.
                        foreach (var link in sub.Links) Read(link.MainField, element.TopTwips, element.TopTwips);
                        // A subreport nested deeper than inline subreports print (MaxSubreportDepth) never runs, so the walk stops there.
                        if (sub.Definition.PositionedLayout is { } inner && level < MaxSubreportDepth && nested.Add(inner))
                            Sections(inner, inner.Document.Sections.Where(s => s.Kind is not ("PageHeader" or "PageFooter")), true, sharedOnly ? at : element.TopTwips, level + 1);
                    }
                    // A cross-tab's print-time cell formulas assign the globals of the report that prints it, where it prints.
                    if (element.Analysis is { } grid) foreach (var (_, formula) in grid.GridPrintFormulas()) { Assigns(formula, element.TopTwips, element.TopTwips, true); Follow(); }
                    if (!sharedOnly && element.Analysis is { AccumulatorVariable.Length: > 0 } analysis)
                    {
                        set.Add("global:" + analysis.AccumulatorVariable.ToLowerInvariant());
                        rereads?.Add("global:" + analysis.AccumulatorVariable.ToLowerInvariant());
                        var total = Node("global:" + analysis.AccumulatorVariable.ToLowerInvariant());
                        if (steps is not null) (steps.TryGetValue(total, out var known) ? known : steps[total] = []).Add(new(total, null, element.TopTwips, element.TopTwips));
                    }
                }
            }
        }
    }

    private static void DropFailures(Dictionary<(string Name, int Row), object?> cache)
    {
        List<(string Name, int Row)>? failed = null;
        foreach (var entry in cache) if (entry.Value is Exception) (failed ??= []).Add(entry.Key);
        if (failed is not null) foreach (var key in failed) cache.Remove(key);
    }

    private Band LayOutBand(ReportDesignerSection section, int row, List<Band> repeats)
    {
        _bandFormulaCache.Clear();
        section = ApplySectionConditions(section, row);
        var sectionHidden = !Visible(section);
        if (sectionHidden && !(_initializingPrintContext && HasScheduledSection(section.Id)))
        {
            // Suppression hides output; Crystal still evaluates the section's formula fields (variable assignments).
            foreach (var reference in section.Elements.OrderBy(e => e.TopTwips).SelectMany(FieldBindings).Where(BindsPrintState)) _ = Format(reference, row, "");
            return new(section, [], row, repeats.ToList(), true, Owner: this);
        }
        var items = new List<Item>();
        foreach (var original in section.Elements)
        {
            var element = ApplyObjectConditions(original, row);
            if (element.IsSuppressed)
            {
                if (element.Kind is not "Subreport" && FieldBindings(element).Any(BindsPrintState))
                    items.Add(new(element, element.Kind == "Chart" && element.ChartMarker is { } stamp ? MarkerHtml(element, stamp, row) : ReportObjectRenderer.Content(element, reference => Format(reference, row, element.FormatString, element.NativeDateTimeFormat, element.NativeNumberFormat, element.NativeNumericFormat),
                        resolveRun: run => Format(run.Binding, row, element.FormatString, run.NativeDateTimeFormat ?? element.NativeDateTimeFormat, element.NativeNumberFormat, RunNumericFormat(element))), -1, Owner: this, Row: row));
                continue;
            }
            if (element.Kind == "Picture" && element.Visual.VectorImage is null && !ReportObjectVisual.IsEmbeddedImage(element.Visual.ImageDataUrl))
                _diagnostics.Add($"{element.Name}: embedded image bytes are missing. Replace the picture in the designer.");
            var onDemand = element.Kind == "Subreport" && _layout.OnDemandSubreports.ContainsKey(element.Id);
            var deferChild = element.Kind == "Subreport" && !onDemand && _physicalPageSchedule && HasScheduledSection(section.Id);
            if (deferChild) _deferredSubreports.TryAdd((element.Id, row), null);
            var child = element.Kind == "Subreport" && !onDemand && !deferChild ? CreateInlineSubreport(element, row) : null;
            var childOffset = Measurements.Count;
            if (child is not null)
            {
                Measurements.AddRange(child.Measurements);
                foreach (var native in child._nativeMetrics) _nativeMetrics.TryAdd(native.Key, native.Value);
            }
            if (ReportObjectCapabilities.UnsupportedCrystalKind(element.Kind) is null
                && element.Kind is not ("Text" or "Field" or "FieldHeading" or "Picture" or "Line" or "Box" or "Subreport" or "Table"))
                _diagnostics.Add($"{element.Name}: {element.Kind} objects are not implemented in positioned rendering.");
            var (html, duplicateValue) = CaptureFieldContent(element, row);
            // Crystal prints an on-demand subreport as its caption (the subreport's name unless the Format Editor's caption formula
            // says otherwise) in the object's font; the reader opens the subreport from it, for the record this band printed.
            if (onDemand) html = OnDemandCaptionHtml(element, row);
            if (element.Kind == "Subreport" && child is null && _subreportErrors.TryGetValue(element.Id, out var subreportError))
                html = "<span class=\"fx-report-subreport-error\" style=\"color:#b00020;font-style:italic\">[Subreport error: " + System.Net.WebUtility.HtmlEncode(subreportError) + "]</span>";
            var measure = -1;
            if (element.CanGrow && !element.HasRotatedText && element.Kind is "Text" or "Field" or "FieldHeading")
            {
                var style = ReportObjectRenderer.Style(element, false);
                var measuredHtml = ResolvePageValues(html.Replace(_pageToken, "88888", StringComparison.Ordinal).Replace(_countToken, "88888", StringComparison.Ordinal), 88888, 88888);
                var measurement = new ReportTextMeasurement(style, measuredHtml, ReportObjectRenderer.TextLineLimit(element));
                var key = MeasurementKey(measurement);
                if (!_measurementKeys.TryGetValue(key, out measure))
                {
                    measure = Measurements.Count;
                    _measurementKeys[key] = measure;
                    Measurements.Add(measurement);
                    // Crystal's printer-device line breaks replace the browser's measurement for native text in a measurable font.
                    if (CrystalDeviceText.Measure(element, measuredHtml) is { } native) _nativeMetrics[key] = native;
                }
            }
            ReportAnalysisSnapshot? analysis = null;
            if (element.Analysis is { } definition && element.Kind is "Chart" or "CrossTab" or "Table")
            {
                try
                {
                    if (definition.Validate(element.Kind) is { } invalid) throw new InvalidDataException(invalid);
                    foreach (var reference in definition.References)
                    {
                        var directMeasure = element.Kind == "Chart" && definition.Measures.Any(measure => measure.DoNotSummarize
                            && measure.Field.Equals(reference, StringComparison.OrdinalIgnoreCase));
                        var recordRole = definition.Measures.Any(measure => !measure.DoNotSummarize && measure.Field.Equals(reference, StringComparison.OrdinalIgnoreCase))
                            || definition.RowFields.Concat(definition.ColumnFields).Concat(definition.TableColumns.Select(column => column.Field))
                                .Append(definition.CategoryField).Append(definition.SeriesField).Append(definition.GroupScope)
                                .Contains(reference, StringComparer.OrdinalIgnoreCase);
                        var totalMeasure = definition.SummariesAsCategories && definition.Measures.Any(measure =>
                            measure.Aggregate is ReportAggregateType.Max or ReportAggregateType.Min
                            && measure.Field.Equals(reference, StringComparison.OrdinalIgnoreCase))
                            && _layout.Formulas.TryGetValue(reference, out var totalFormula) && totalFormula.UsesAggregates;
                        // A cross-tab groups by a formula over a group or report summary (SummaryBound, sample "Group on Subtotals"); a
                        // chart axis or a summarized measure keeps the read-time gate, or Sum of {@Total} would plot the total once per record.
                        var gridAxis = element.Kind == "CrossTab" && definition.RowFields.Concat(definition.ColumnFields).Contains(reference, StringComparer.OrdinalIgnoreCase);
                        if (totalMeasure || directMeasure && !recordRole) RequireChartFormulaValue(reference, new(StringComparer.OrdinalIgnoreCase));
                        else if (_layout.Formulas.TryGetValue(reference, out var formula) && !(gridAxis && SummaryBound(formula))) RequireReadTime(formula, "Analytical item");
                        if (_layout.RunningTotals.ContainsKey(reference) || _layout.Summaries.ContainsKey(reference) || IsSpecial(reference))
                            throw new InvalidDataException("Analytical fields must be record-time values, not page values or accumulated summaries.");
                    }
                    var indexes = Enumerable.Range(0, _rows.Length);
                    if (definition.GroupScope.Length > 0)
                    {
                        var level = _layout.Document.Groups.FindIndex(group => group.Condition.Equals(definition.GroupScope, StringComparison.OrdinalIgnoreCase));
                        if (level < 0 || row < 0 || row >= _rows.Length) throw new InvalidDataException("The analytical group scope is unavailable.");
                        indexes = indexes.Where(index => SameGroup(index, row, level));
                    }
                    var scopeStart = definition.GroupScope.Length == 0 ? -1 : indexes.FirstOrDefault(-1);
                    // A grid whose captions embed fields prints the values current where it prints, so each band instance gets its own snapshot.
                    var snapshotKey = (original.Id, definition.NativeCrossTab?.Appearance is { CaptionFields.Count: > 0 } ? row : scopeStart);
                    if (!_analysisSnapshots.TryGetValue(snapshotKey, out analysis))
                    {
                        var scopeIndexes = indexes.ToArray();
                        ValidateReportChartOthers(definition, scopeIndexes);
                        var data = scopeIndexes.Select(index => definition.References.ToDictionary(reference => reference,
                            reference => AxisValue(definition, reference, index), StringComparer.OrdinalIgnoreCase)).ToList();
                        var snapshotDefinition = definition.Clone();
                        // A field embedded in a total caption prints the value current where the grid prints, in its own format
                        // (sample "Field in Crosstab Text object": the corner reads "Fedex:" over the group's percentage).
                        if (snapshotDefinition.NativeCrossTab is { Appearance: { CaptionFields.Count: > 0 } captionAppearance } captionGrid)
                        {
                            string SubstituteCaptionFields(string caption) => captionAppearance.CaptionFields.Aggregate(caption, (current, embedded) =>
                                current.Replace(embedded.Binding, Format(embedded.Binding, row, "", nativeNumeric: embedded.NumericFormat ?? EmbeddedNumericDefault)));
                            snapshotDefinition.NativeCrossTab = captionGrid with { Appearance = captionAppearance with {
                                RowTotalCaption = SubstituteCaptionFields(captionAppearance.RowTotalCaption),
                                ColumnTotalCaption = SubstituteCaptionFields(captionAppearance.ColumnTotalCaption), CaptionFields = [] } };
                        }
                        foreach (var field in new[] { definition.CategoryField, definition.SeriesField })
                        {
                            var level = ReportChartGroupLevel(definition, field);
                            if (level < 0 || !_layout.GroupSorts.ContainsKey(level)) continue;
                            // The report already ranked its groups and placed Others last.
                            snapshotDefinition.SortCategories = false;
                            snapshotDefinition.DescendingFields.RemoveAll(axis => axis.Equals(field, StringComparison.OrdinalIgnoreCase));
                        }
                        analysis = new(snapshotDefinition, data) { PrintTime = _printTime };
                        if (definition.AccumulatorVariable.Length > 0)
                        {
                            var rowField = definition.RowFields.Single();
                            var measureField = definition.Measures.Single().Field;
                            var totals = data.GroupBy(record => record[rowField])
                                .Select(group => group.Where(record => record[measureField] is not DBNull)
                                    .Sum(record => Convert.ToDecimal(record[measureField], CultureInfo.InvariantCulture)))
                                .OrderDescending().ToArray();
                            var count = Math.Min(definition.AccumulatorCount, totals.Length);
                            var delta = count == 0 ? 0 : totals.TakeWhile((value, i) => i < count ||
                                definition.AccumulatorWithTies && value == totals[count - 1]).Sum();
                            var hidden = element.CloneFor(element.SectionId, element.Name);
                            hidden.Id = element.Id;
                            hidden.IsSuppressed = true;
                            var calculation = new Item(hidden, "", -1, Owner: this, Row: row, AccumulatorDelta: delta);
                            ApplyAccumulator(calculation);
                            items.Add(calculation);
                            continue;
                        }
                        if (element.Kind is "CrossTab" or "Table") analysis = analysis with { Table = ReportTabularData.Create(element, analysis) };
                        _analysisSnapshots[snapshotKey] = analysis;
                    }
                    html = "[Analytical report item: use the component viewer or asynchronous HTML export]";
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or FormatException)
                { _diagnostics.Add(element.Name + ": " + error.Message); html = ReportObjectRenderer.Encode("[" + element.Name + ": " + error.Message + "]"); }
            }
            items.Add(new(element, html, measure, child, childOffset, this, row, Analysis: analysis, DuplicateValue: duplicateValue));
        }
        var band = new Band(section, items, row, repeats.ToList(), Suppressed: sectionHidden, Owner: this);
        CaptureOnDemandVariables(band);
        return band;
    }

    private static bool IsBlank(Band band) => band.Items.All(item => item.Element.IsSuppressed || item.Child is null && item.Element.Kind is not ("Line" or "Box" or "Picture" or "Map") &&
        string.IsNullOrWhiteSpace(System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(item.Html, "<[^>]+>", ""))));

    private static IEnumerable<string> FieldBindings(ReportDesignerElement element) =>
        element.Visual.Runs.Select(r => r.Binding).Prepend(ValueBinding(element)).Where(r => r.Length > 0);

    /// <summary>The value an object prints by itself: a field's binding, or the category a label-only chart stamps
    /// (a cheque's VOID); "" for every other object. The schedule and the print-state replay read both alike.</summary>
    private static string ValueBinding(ReportDesignerElement element) => element.Kind switch
    {
        "Field" => element.Binding,
        "Chart" when element.ChartMarker is { } marker => marker.Binding,
        _ => ""
    };

    private void BuildBands()
    {
        var sections = _layout.Document.Sections.Where(InDrillScope).ToList();
        var repeats = new List<Band>();
        void Add(string kind, int row, int? group = null)
        {
            foreach (var section in sections.Where(section => section.Kind == kind && (group is null || ReportDesignerEditing.GetGroupNumber(_layout.Document, section) == group + 1)))
            {
                var band = CreateBand(section, row, repeats);
                if (band.Suppressed && !HasVisibilityEvent(section.Id)) continue;
                _bands.Add(band);
                if (kind == "GroupHeader" && (_layout.Areas.GetValueOrDefault(section.Id)?.RepeatHeader ?? false)) repeats.Add(band);
            }
        }
        Add("ReportHeader", _rows.Length > 0 ? 0 : -1);
        _rowBefore[-1] = _rowAfter[-1] = CaptureVariables();
        if (_rows.Length == 0)
        {
            // Empty reports retain template furniture, subreports included: Crystal formats the group and detail bands once
            // without a record, under their own conditions, with blank summaries (see PrintedSummary) and zero running totals.
            for (var group = 0; group < _layout.Document.Groups.Count; group++) Add("GroupHeader", -1, group);
            Add("Detail", -1);
            for (var group = _layout.Document.Groups.Count - 1; group >= 0; group--)
            {
                Add("GroupFooter", -1, group);
                repeats.RemoveAll(band => ReportDesignerEditing.GetGroupNumber(_layout.Document, band.Section) == group + 1);
            }
        }
        for (var row = 0; row < _rows.Length; row++)
        {
            _rowBefore[row] = CaptureVariables();
            for (var group = 0; group < _layout.Document.Groups.Count; group++)
                if (row == 0 || !SameGroup(row - 1, row, group)) Add("GroupHeader", row, group);
            Add("Detail", row);
            for (var group = _layout.Document.Groups.Count - 1; group >= 0; group--)
                if (row + 1 == _rows.Length || !SameGroup(row, row + 1, group))
                {
                    if (HierarchyAt(group) is { } tree)
                    {
                        // A hierarchical group closes after its descendants, then each ancestor whose subtree ends here.
                        for (var node = tree.NodeOfRow[row]; node >= 0 && tree.Nodes[node].SubtreeEnd == row + 1; node = tree.Nodes[node].Parent)
                        {
                            var closing = tree.Nodes[node];
                            Add("GroupFooter", closing.End - 1, group);
                            repeats.RemoveAll(band => band.Row == closing.Start && ReportDesignerEditing.GetGroupNumber(_layout.Document, band.Section) == group + 1);
                        }
                        continue;
                    }
                    Add("GroupFooter", row, group);
                    repeats.RemoveAll(band => ReportDesignerEditing.GetGroupNumber(_layout.Document, band.Section) == group + 1);
                }
            _rowAfter[row] = CaptureVariables();
        }
        Add("ReportFooter", _rows.Length - 1);
        _rowAfter[_rows.Length - 1] = CaptureVariables();
        // Page furniture can bind to the first/last row on each page. Prepare all variants before DOM measurement.
        foreach (var section in sections.Where(section => section.Kind is "PageHeader" or "PageFooter"))
            for (var row = -1; row < _rows.Length; row++) _furniture[(section.Id, row)] = CreateFurniture(section, row);
    }

    private readonly Dictionary<(string Id, int Row), Band> _furniture = new();

    private sealed record PaginationRetry(int Pages, int Attempt, IReadOnlyList<int>? Counts,
        Dictionary<Band, (int Page, int Count)>? Inline, IReadOnlyList<int>? Footers, int ScheduleAttempt);

    public ReportLayoutResult Paginate(IReadOnlyList<int>? measuredHeights = null)
    {
        var next = new PaginationRetry(1, 0, null, null, null, 0);
        while (true)
        {
            var result = PaginatePass(measuredHeights, next.Pages, next.Attempt, next.Counts,
                next.Inline, next.Footers, next.ScheduleAttempt, out var retry);
            if (retry is null) return result;
            next = retry;
        }
    }

    private ReportLayoutResult PaginatePass(IReadOnlyList<int>? measuredHeights, int expectedPages, int attempt, IReadOnlyList<int>? expectedCounts,
        Dictionary<Band, (int Page, int Count)>? inlineContexts, IReadOnlyList<int>? footerRows, int scheduleAttempt, out PaginationRetry? retry)
    {
        retry = null;
        if (_textMetrics is null && Measurements.Count > 0 && _state.HasGrowingText && (measuredHeights is null || measuredHeights.Count != Measurements.Count))
            _diagnostics.Add("Browser text measurement is unavailable; CanGrow pagination uses an approximate font metric.");
        var size = _layout.Document.Page;
        var columns = _layout.Columns;
        IReadOnlyList<Band> bands = _paginationPageLimit > 0
            ? new DeferredBands(_bands, band => Expand(band, 0))
            : _bands.Select(band => Expand(band, 0)).ToList();
        // Empty trailing footer templates are spacing, not another physical page.
        // Do not discard painted sections, objects, or section formula events.
        var frameEnds = _layout.Document.Elements.Where(e => e.LineEndSection.Length > 0 && !e.IsSuppressed)
            .Select(e => e.LineEndSection).ToHashSet(StringComparer.Ordinal);
        var trailingSpacer = bands.Count;
        while (trailingSpacer > 0 && bands[trailingSpacer - 1] is var tail
            && !frameEnds.Contains(tail.Section.Name) && IsFooterSpacer(tail)) trailingSpacer--;
        var pages = new List<Page>();
        var headers = _layout.Document.Sections.Where(section => section.Kind == "PageHeader" && InDrillScope(section)).ToList();
        var footers = _layout.Document.Sections.Where(section => section.Kind == "PageFooter" && InDrillScope(section)).ToList();
        var pendingHidden = new List<(Band Band, int Height, int Sequence)>();
        var sequence = -1;
        var underlays = new List<(int Index, int Page, int Top)>();
        (Page Page, int Row, int End)? detailUnderlay = null;
        var scopedUnderlays = new List<(Band Band, Page Page, int End)>();
        var limitedInstances = new Dictionary<string, (Page Page, int Count)>();
        Page? countedPage = null;
        var lastDetailRow = -1;
        var visibleDetailRows = 0;
        void CountVisibleDetail(Page target, Band band)
        {
            if (_layout.VisibleRecordsPerPage <= 0 || band.Section.Kind != "Detail") return;
            if (target != countedPage)
            {
                countedPage = target;
                lastDetailRow = -1;
                visibleDetailRows = 0;
            }
            if (band.Row == lastDetailRow) return;
            lastDetailRow = band.Row;
            visibleDetailRows++;
        }
        var resetPageNumber = false;
        var areaInstance = (Area: "", Row: -1);
        var bottomArea = false;
        // Crystal "Format with Multiple Columns" (ReportColumnFormatter): a column cell is an area instance DetailWidth wide. Down then across, sections
        // flow down a column to the bottom of the page and go on in the next; across then down, each cell takes the next place in a row of columns.
        // Printing at the bottom (the whole area, or one section while the area's others keep their place), or a page break after it, ends the cell's
        // column (row); a page break before a cell starts a new one. The block of columns ends below its tallest column at the first section that
        // spans the page.
        var columnCount = columns is null ? 0 : Math.Max(1, (size.ContentWidthTwips + columns.HorizontalGap) / (columns.DetailWidth + columns.HorizontalGap));
        Page? block = null;
        int blockTop = 0, blockBottom = 0, column = 0, rowHeight = 0, cellLeft = 0, cellCursor = 0, cellLimit = 0;
        bool columnUsed = false, gapPending = false, bottomCell = false, fixedCell = false, breakAfterCell = false, breakNow = false;
        var areaOf = new Dictionary<string, string>();
        foreach (var section in _layout.Document.Sections) areaOf.TryAdd(section.Id, section.AreaId);
        (int Index, int Page, int Count, Band Band)? lastPageBand = null;
        var page = NewPage(bands.FirstOrDefault()?.Row ?? -1, [], bands.FirstOrDefault()?.Section.Kind != "ReportHeader");
        var prefixOnly = false;
        try
        {
        for (var bandIndex = 0; bandIndex < bands.Count; bandIndex++)
        {
            _paginationCancellationToken.ThrowIfCancellationRequested();
            sequence = bandIndex;
            var band = PageBand(bandIndex);
            // A detail underlay shares the following subsections of this record.
            // Finish the taller of the overlay and foreground before starting the next record or footer.
            if (detailUnderlay is { } previous && (band.Section.Kind != "Detail" || band.Row != previous.Row))
            {
                if (previous.Page == page) page.Cursor = Math.Max(page.Cursor, Math.Min(page.Bottom, previous.End));
                detailUnderlay = null;
            }
            if (bandIndex >= trailingSpacer && !(band.Section.Kind == "ReportFooter" && bandIndex > 0
                && bands[bandIndex - 1] is var preceding && preceding.Section.Kind == "GroupFooter"
                && (preceding.Section.NewPageAfter || _layout.Areas.GetValueOrDefault(preceding.Section.Id)?.NewPageAfter == true))) continue;
            if (band.Suppressed) { HiddenEvent(band); continue; }
            if (band.Section.SuppressIfBlank && IsBlank(band)) { BlankEvent(band); continue; }
            // A header may underlay the body of its group, but its matching footer follows its full extent.
            if (band.Section.Kind is "GroupFooter" or "ReportFooter")
            {
                for (var u = scopedUnderlays.Count - 1; u >= 0; u--)
                {
                    var pending = scopedUnderlays[u];
                    if (band.Section.Kind != "ReportFooter" && (pending.Band.Section.Kind != "GroupHeader"
                        || pending.Band.Section.GroupId != band.Section.GroupId)) continue;
                    if (pending.Page == page) page.Cursor = Math.Max(page.Cursor, Math.Min(page.Bottom, pending.End));
                    scopedUnderlays.RemoveAt(u);
                }
            }
            var height = Height(band);
            var area = _layout.Areas.GetValueOrDefault(band.Section.Id);
            var areaId = areaOf.GetValueOrDefault(band.Section.Id) ?? "";
            int FreshBodyRoom() => page.Bottom - (band.Section.Kind == "ReportHeader" ? 0 :
                headers.Sum(s => Space(Blankness(Furnish(_furniture[(s.Id, band.Row)], page.Number)))))
                - band.Repeats.Sum(r => Space(Blankness(r)));
            // Close the preceding record (including its group footers) before the next
            // record's group headings or details. Repeated headings do not consume a record.
            if (countedPage == page && visibleDetailRows >= _layout.VisibleRecordsPerPage
                && band.Row != lastDetailRow && band.Section.Kind is "GroupHeader" or "Detail")
            {
                CloseColumns();
                page = NewPage(band.Row, band.Repeats, withHeaders: true);
            }
            if (area is { VisibleInstanceLimit: > 0 } && limitedInstances.TryGetValue(band.Section.Id, out var prior)
                && prior.Page == page && prior.Count >= area.VisibleInstanceLimit)
            {
                CloseColumns(); page = NewPage(band.Row, band.Repeats, band.Section.Kind != "ReportHeader");
            }
            if (bottomArea && (areaId, band.Row) != areaInstance) { page.Cursor = page.Bottom; bottomArea = false; }
            var withHeaders = band.Section.Kind != "ReportHeader";
            if (!InColumns(band.Section) && (band.Section.NewPageBefore || area?.NewPageBefore == true) && page.HasBody) { CloseColumns(); page = NewPage(band.Row, band.Repeats, withHeaders); }
            if (withHeaders && page.EndsWithReportHeaderBreak) page = NewPage(band.Row, band.Repeats, withHeaders);
            if (withHeaders && !page.HasHeaders)
            {
                var headerHeight = headers.Sum(section => Space(Blankness(Furnish(_furniture[(section.Id, band.Row)], page.Number))));
                if (page.Cursor + headerHeight + height > page.Bottom && page.HasBody) { CloseColumns(); page = NewPage(band.Row, band.Repeats); }
                else AddHeaders(page, band.Row);
            }
            if (InColumns(band.Section))
            {
                PlaceInColumns(bandIndex, band, area, areaId);
                CountVisibleDetail(page, band);
                continue;
            }
            CloseColumns();
            if (band.Section.Kind == "GroupHeader" && page.HasBody)
            {
                // An overheight kept group may share its first page with an introduction.
                // After a previous group's details it starts a fresh page, then flows normally.
                var freshRoom = page.Bottom - headers.Sum(s => Space(Blankness(Furnish(_furniture[(s.Id, band.Row)], page.Number)))) - band.Repeats.Sum(r => Space(Blankness(r)));
                var keepHeight = KeepHeight(bandIndex, band, area, freshRoom, afterPriorDetails: true);
                if (keepHeight > page.Bottom - page.Cursor && keepHeight <= freshRoom) page = NewPage(band.Row, band.Repeats);
            }
            // Area formatting applies to the area's sections for one record (FormattedArea): kept together, they start a new page when they do not fit;
            // printed at the bottom, they are placed so their extent (an underlay section covers what follows it) ends at the bottom of the page.
            if ((area?.KeepTogether == true || area?.PrintAtBottomOfPage == true) && (areaId, band.Row) != areaInstance)
            {
                int extent = 0, flow = 0;
                for (var next = bandIndex; next < bands.Count && areaOf.GetValueOrDefault(bands[next].Section.Id) == areaId && bands[next].Row == band.Row; next++)
                {
                    if (bands[next].Suppressed || bands[next].Section.SuppressIfBlank && IsBlank(bands[next])) continue;
                    extent = Math.Max(extent, flow + Height(bands[next]));
                    if (!bands[next].Section.UnderlayFollowingSections) flow += Height(bands[next]);
                }
                if (extent > page.Bottom - page.Cursor && page.HasBody && extent <= FreshBodyRoom()) page = NewPage(band.Row, band.Repeats, withHeaders);
                if (area.PrintAtBottomOfPage && extent <= page.Bottom - page.Cursor) { page.Cursor = page.Bottom - extent; bottomArea = true; }
            }
            areaInstance = (areaId, band.Row);
            if (band.Section.UnderlayFollowingSections)
            {
                if (band.Section.Kind is "GroupHeader" or "ReportHeader")
                {
                    if (band.Section.KeepTogether && page.HasBody && UnderlayInkHeight(band) > page.Bottom - page.Cursor && height <= FreshBodyRoom())
                        page = NewPage(band.Row, band.Repeats, withHeaders);
                    scopedUnderlays.Add((band, page, page.Cursor + height));
                }
                if (band.Section.Kind == "Detail")
                {
                    if (band.Section.KeepTogether && page.HasBody && height > page.Bottom - page.Cursor && height <= FreshBodyRoom())
                        page = NewPage(band.Row, band.Repeats, withHeaders);
                    var end = page.Cursor + height;
                    if (detailUnderlay is { } earlier && earlier.Page == page) end = Math.Max(end, earlier.End);
                    detailUnderlay = (page, band.Row, end);
                }
                underlays.Add((bandIndex, pages.Count - 1, page.Cursor)); page.HasBody = true;
                CountVisibleDetail(page, band);
                if (band.Section.PrintAtBottomOfPage || band.Section.NewPageAfter || area?.NewPageAfter == true
                    || band.Section.ResetPageNumberAfter || area?.ResetPageNumberAfter == true)
                    _diagnostics.Add($"{band.Section.Name}: underlay placement ignores bottom alignment and ending page-break/reset flags; foreground flow is retained.");
                continue;
            }
            // A kept subreport starts on a fresh page even when its complete output must span pages.
            // Expansion replaces the container, so retain its authored keep request from the source band.
            var sourceItems = _bands[bandIndex].Items;
            var keptSubreport = sourceItems.Count == 1 && sourceItems[0].Child is not null
                && sourceItems[0].Element.TopTwips == 0
                && _layout.Document.SourceObjects.TryGetValue(sourceItems[0].Element.SourceKey, out var subreportSource)
                && (bool?)subreportSource.Element("ObjectFormat")?.Attribute("EnableKeepTogether") == true;
            var sectionKeep = band.KeptStartHeight ?? height;
            if (page.HasBody && ((sectionKeep > page.Bottom - page.Cursor && band.Section.KeepTogether && (sectionKeep <= FreshBodyRoom() || band.Section.Kind == "GroupFooter" && page.Cursor > page.BodyStart))
                || (height > page.Bottom - page.Cursor && keptSubreport)))
                page = NewPage(band.Row, band.Repeats, withHeaders);
            band = PageBand(bandIndex);
            if (band.Suppressed) { HiddenEvent(band); continue; }
            height = Height(band);
            var offset = 0;
            do
            {
                if (page.Bottom <= page.Cursor) page = NewPage(band.Row, band.Repeats, withHeaders);
                if (page.HasBody && band.InlineSections?.Any(s => !s.FirstVisible && s.Start == offset && s.End > s.Start
                    && ((s.Format ?? s.Source.Section).NewPageBefore || s.Source.Owner!._layout.Areas.GetValueOrDefault(s.Source.Section.Id)?.NewPageBefore == true)) == true)
                {
                    page = NewPage(band.Row, band.Repeats, withHeaders);
                    if (offset == 0) { band = PageBand(bandIndex); height = Height(band); }
                    AddInlineHeaders(page, band, offset);
                }
                var available = page.Bottom - page.Cursor;
                if (available <= 0) throw new InvalidDataException("Page headers and footers leave no room for report content.");
                var slice = Math.Min(height - offset, available);
                var forced = band.ForcedBreaks?.Where(point => point > offset && point <= offset + slice).DefaultIfEmpty(0).Min() ?? 0;
                foreach (var ending in band.InlineSections?.Where(s => s.End > offset && s.End <= offset + slice) ?? [])
                {
                    var owner = ending.Source.Owner!;
                    if (owner.Conditions(owner._layout.SectionConditions, ending.Source.Section.Id, true)
                        .Concat(owner.Conditions(owner._layout.AreaConditions, ending.Source.Section.Id, true))
                        .Any(c => c.Key.Equals("EnableNewPageAfter", StringComparison.OrdinalIgnoreCase))
                        && ForPage(ending.Source, page.Number, ExpectedCount(pages.Count - 1)).Section.NewPageAfter)
                        forced = forced == 0 ? ending.End : Math.Min(forced, ending.End);
                }
                var kept = band.InlineSections?.Where(s => (s.Format ?? s.Source.Section).KeepTogether && s.Start >= offset
                    && s.Start < offset + slice && s.End > offset + slice).OrderBy(s => s.Start).FirstOrDefault();
                if (kept is not null && (forced == 0 || forced >= kept.End))
                {
                    // Honor the child's section boundary, not just the outer subreport object's KeepTogether.
                    var freshRoom = page.Bottom - headers.Sum(s => Space(Blankness(Furnish(_furniture[(s.Id, band.Row)], page.Number)))) - band.Repeats.Sum(r => Space(Blankness(r)))
                        - (band.InlineHeaders?.Where(h => h.Repeat && h.Start < kept.Start && h.End > kept.Start).Sum(h => Height(h.Header)) ?? 0);
                    if (kept.End - kept.Start <= freshRoom)
                    {
                        if (kept.Start > offset && SafeBreaks(band).Contains(kept.Start)) slice = kept.Start - offset;
                        else if (kept.Start == offset && page.HasBody)
                        { page = NewPage(band.Row, band.Repeats, withHeaders); AddInlineHeaders(page, band, offset); continue; }
                    }
                }
                if (forced > offset + slice) forced = 0;
                if (forced > 0) slice = forced - offset;
                else if (slice < height - offset && band.Breaks is { Count: > 0 })
                {
                    var boundary = SafeAt(band, offset + slice) ? offset + slice
                        : SafeBreaks(band).Where(point => point > offset && point <= offset + slice).DefaultIfEmpty(0).Max();
                    if (boundary > 0) slice = boundary - offset;
                    else if (page.HasBody && band.Breaks.FirstOrDefault(point => point > offset) - offset <= page.Bottom)
                    { page = NewPage(band.Row, band.Repeats, withHeaders); AddInlineHeaders(page, band, offset); continue; }
                }
                if ((offset > 0 || slice < height) && !SafeAt(band, offset + slice) && !CanContinueAt(band, offset + slice))
                    _diagnostics.Add($"{band.Section.Name}: a band spans pages; overflowing objects are clipped at the page boundary.");
                foreach (var inline in band.InlineSections?.Where(s => s.Start < offset + slice && s.End > offset + slice) ?? [])
                {
                    var owner = inline.Source.Owner!;
                    if (owner.Conditions(owner._layout.SectionConditions, inline.Source.Section.Id, true)
                        .Concat(owner.Conditions(owner._layout.AreaConditions, inline.Source.Section.Id, true))
                        .Any(c => c.Key.ToLowerInvariant() is not ("enablesuppress" or "enablehidefordrilldown" or "enablekeeptogether" or "enablenewpagebefore" or "enablenewpageafter")))
                        _diagnostics.Add($"{inline.Source.Section.Name}: continuation retains its section-entry layout settings for unsupported mid-section format changes.");
                }
                var atBottom = band.Section.PrintAtBottomOfPage && offset + slice == height;
                foreach (var inline in band.InlineSections?.Where(s => (s.Format ?? s.Source.Section).PrintAtBottomOfPage && s.End > offset && s.End <= offset + slice) ?? [])
                {
                    if (inline.Start <= offset && inline.End == offset + slice && band.Items.Where(i => i.Element.TopTwips < offset + slice && i.Element.TopTwips + ItemHeight(i) > offset)
                        .All(i => i.Owner == inline.Source.Owner && i.Row == inline.Source.Row && i.Element.SectionId == inline.Source.Section.Id
                            || i.Owner?.IsDescendantOf(inline.Source.Owner!) == true)) atBottom = true;
                    else _diagnostics.Add($"{inline.Source.Section.Name}: inline bottom alignment shares space with other content; normal flow is retained.");
                }
                var top = atBottom ? page.Bottom - slice : page.Cursor;
                page.Placements.Add(new(band, top, offset, slice, sequence));
                CountVisibleDetail(page, band);
                page.Cursor = top + slice;
                if (forced > 0 && forced == offset + slice) page.Cursor = page.Bottom;
                page.HasBody = true;
                offset += slice;
                if (offset < height)
                {
                    var resetBeforeContinuation = resetPageNumber;
                    page = NewPage(band.Row, band.Repeats, withHeaders);
                    var continued = PageBand(bandIndex);
                    continued = ApplyInlineCuts(continued, band.InlineCuts, ItemHeight);
                    band = ContinueText(band, continued, offset, ItemHeight);
                    band = SuppressInlineContinuations(band, offset, page.Number, ExpectedCount(pages.Count - 1), ItemHeight);
                    band = band with { Breaks = SafeBreaks(band) };
                    height = Height(band);
                    if (band.Suppressed || offset >= height)
                    {
                        pages.RemoveAt(pages.Count - 1); page = pages[^1]; resetPageNumber = resetBeforeContinuation;
                        break;
                    }
                    AddInlineHeaders(page, band, offset);
                }
            } while (offset < height);
            if (area is { VisibleInstanceLimit: > 0 })
            {
                var last = limitedInstances.GetValueOrDefault(band.Section.Id);
                limitedInstances[band.Section.Id] = (page, last.Page == page ? last.Count + 1 : 1);
            }
            var breakAfter = band.Section.NewPageAfter || area?.NewPageAfter == true;
            if (band.Section.Kind == "ReportHeader")
            {
                // Crystal omits page furniture on a title sheet closed by New Page After.
                if (breakAfter) page.EndsWithReportHeaderBreak = true;
                else if (!page.HasHeaders && _bands.Skip(bandIndex + 1).All(next => next.Section.Kind != "ReportHeader")) EnsureHeaders(band.Row);
            }
            if (breakAfter) page.Cursor = page.Bottom;
            resetPageNumber |= band.Section.ResetPageNumberAfter || area?.ResetPageNumberAfter == true;
        }
        CloseColumns();
        }
        catch (PagePrefixComplete) { prefixOnly = true; }
        // A report whose body prints nothing still has page headers on its page.
        if (!page.HasHeaders && !page.EndsWithReportHeaderBreak) EnsureHeaders(bands.LastOrDefault()?.Row ?? -1);
        void EnsureHeaders(int row)
        {
            if (headers.Count == 0) return;
            var headerHeight = headers.Sum(section => Space(Blankness(Furnish(_furniture[(section.Id, row)], page.Number))));
            if (page.Cursor + headerHeight <= page.Bottom) AddHeaders(page, row);
        }

        // Underlays do not consume foreground flow. Place their fragments behind the completed flow,
        // extending the page sequence only when the underlay itself still has unprinted content.
        foreach (var underlay in underlays)
        {
            sequence = underlay.Index;
            var offset = 0; Band? previous = null;
            for (var index = underlay.Page; ; index++)
            {
                var created = index == pages.Count;
                var resetBeforeUnderlay = resetPageNumber;
                page = !created ? pages[index] : NewPage(_bands[underlay.Index].Row, _bands[underlay.Index].Repeats);
                var band = PageBand(underlay.Index, index);
                if (previous is not null)
                {
                    band = ApplyInlineCuts(band, previous.InlineCuts, ItemHeight);
                    band = ContinueText(previous, band, offset, ItemHeight);
                    band = SuppressInlineContinuations(band, offset, page.Number, ExpectedCount(index), ItemHeight);
                }
                var height = UnderlayInkHeight(band);
                if (band.Suppressed || offset >= height)
                {
                    if (created) { pages.RemoveAt(index); resetPageNumber = resetBeforeUnderlay; }
                    break;
                }
                var top = index == underlay.Page ? underlay.Top : page.BodyStart;
                var room = page.Bottom - top;
                if (room <= 0) { previous = band; continue; }
                var slice = Math.Min(height - offset, room);
                if (slice < height - offset && !SafeAt(band, offset + slice))
                {
                    var safe = SafeBreaks(band).Where(p => p > offset && p <= offset + slice).DefaultIfEmpty(0).Max();
                    if (safe > offset) slice = safe - offset;
                    else if (!CanContinueAt(band, offset + slice)) _diagnostics.Add($"{band.Section.Name}: oversized underlay objects are clipped at the page boundary.");
                }
                var insertion = page.Placements.TakeWhile(p => p.Band.Section.Kind == "PageHeader" || p.Band.Section.UnderlayFollowingSections).Count();
                page.Placements.Insert(insertion, new(band, top, offset, slice, sequence)); page.HasBody = true;
                offset += slice; if (offset >= height) break;
                previous = band;
            }
        }
        page = pages[^1];
        foreach (var hidden in pendingHidden) page.Placements.Add(new(hidden.Band, page.Cursor, 0, hidden.Height, hidden.Sequence));
        pendingHidden.Clear();
        var counts = new int[pages.Count];
        for (var start = 0; start < pages.Count;)
        {
            var end = start + 1;
            while (end < pages.Count && pages[end].Number != 1) end++;
            for (var index = start; index < end; index++) counts[index] = end - start;
            start = end;
        }
        var nextInlineContexts = new Dictionary<Band, (int Page, int Count)>();
        var lastOffsets = LastFragmentOffsets(pages);
        for (var index = 0; index < pages.Count; index++)
        foreach (var placement in pages[index].Placements)
        foreach (var inline in placement.Band.InlineSections ?? [])
            if (inline.Start >= placement.Offset && (inline.Start < placement.Offset + placement.Height
                || inline.Start == inline.End && inline.Start == placement.Offset + placement.Height && placement.Offset == lastOffsets[FragmentKey(placement.Band)]))
                nextInlineContexts.TryAdd(inline.Source, (pages[index].Number, counts[index]));
        var inlineChanged = nextInlineContexts.Any(pair => inlineContexts is null || !inlineContexts.TryGetValue(pair.Key, out var context) || context != pair.Value);
        var nextFooterRows = pages.Select(p => p.Placements.LastOrDefault(p => p.Band.Section.Kind != "PageHeader")?.Band.Row ?? -1).ToArray();
        var footerChanged = footers.Count > 0 && (footerRows is null || !nextFooterRows.SequenceEqual(footerRows));
        if (footerChanged || _state.HasPageConditions && (inlineChanged || pages.Count != expectedPages || expectedCounts is null || !counts.SequenceEqual(expectedCounts)))
        {
            if (attempt >= 7) _diagnostics.Add("Page-dependent formatting did not stabilize within eight passes; the last computed layout is retained.");
            else { retry = new(pages.Count, attempt + 1, counts, nextInlineContexts, nextFooterRows, scheduleAttempt); return new([], []); }
        }
        for (var index = 0; index < pages.Count; index++)
        {
            var current = pages[index];
            var lastRow = current.Placements.LastOrDefault(placement => placement.Band.Section.Kind != "PageHeader")?.Band.Row ?? -1;
            if (current.EndsWithReportHeaderBreak) continue;
            var footerTop = current.Bottom;
            foreach (var section in footers)
            {
                var footer = Blankness(FurnitureBand(section.Id, lastRow, current.Number, counts[index], index + 1), index + 1);
                if (footer.Blank && !_state.PhysicalSchedule) continue;
                current.Placements.Add(new(footer, footerTop, 0, Height(footer), int.MaxValue)); footerTop += Space(footer);
            }
        }
        if (_state.PhysicalSchedule)
        {
            if (scheduleAttempt == 0) _state.BlankHistory.Clear();
            _state.BlankHistory.Add(pages.SelectMany((current, index) => current.Placements.SelectMany(placement => (placement.Band.InlineSections ?? []).Where(s => s.Blank)
                .Select(s => new PrintBandKey(s.Source.Owner ?? this, s.Source.Section.Id, s.Source.Row, 0))
                .Concat(placement.Band.Blank ? [new PrintBandKey(placement.Band.Owner ?? this, placement.Band.Section.Id, placement.Band.Row,
                    placement.Band.RepeatedHeader || placement.Band.Section.Kind is "PageHeader" or "PageFooter" ? index + 1 : 0)] : []))).ToHashSet());
        }
        if (ReplayPhysicalPages(pages, counts))
        {
            // Blankness is decided from the previous replay. A section that is blank where it was placed but not blank
            // on the page it moves to (or the reverse) flips on every replay.
            var history = _state.BlankHistory;
            if (history.Count >= 2 && !history[^1].SetEquals(history[^2])
                && (scheduleAttempt >= 15 || history.Count >= 4 && history[^1].SetEquals(history[^3]) && history[^2].SetEquals(history[^4])))
            {
                var names = history[^1].Except(history[^2]).Concat(history[^2].Except(history[^1]))
                    .Select(key => key.Owner._layout.Document.Sections.First(s => s.Id == key.Section).Name).Distinct(StringComparer.Ordinal);
                throw new NotSupportedException($"{string.Join(", ", names)}: 'Suppress Blank Section' does not settle, because whether the section is blank depends on the page it prints on. "
                    + "Crystal formats such a section again on the page it moves to; that re-formatting is not supported.");
            }
            // A page-counter reset can advance the settled boundary one page per replay.
            // Long reports need room to converge; iterative retries release each old layout.
            var replayLimit = Math.Min(256, Math.Max(16, pages.Count + 16));
            if (scheduleAttempt + 1 >= replayLimit) _diagnostics.Add($"Mutable page formula layout did not stabilize within {replayLimit} replays; the last computed layout is retained.");
            else { retry = new(pages.Count, 0, counts, nextInlineContexts, nextFooterRows, scheduleAttempt + 1); return new([], []); }
        }
        if (_textMetrics is not null)
        {
            // While measurements are still being gathered, the pages built below are thrown away by the measurement loop:
            // only the text measurements the paint would request matter. Request them without building the output.
            DiscoverPaintMeasurements();
            if (_measurementRequests.Count > 0)
            {
                return new([], []) { IsComplete = !prefixOnly };
            }
        }
        // Cross-section lines follow the printed bands, including repeated detail rows.
        // Split them at band boundaries so the HTML and PDF clips agree.
        var strokes = new Dictionary<Placement, List<Item>>(ReferenceEqualityComparer.Instance);
        var borderTails = new Dictionary<Placement, Placement>(ReferenceEqualityComparer.Instance);
        if (_layout.Document.Sections.Any(section => section.Elements.Any(element => element.LineEndSection.Length > 0)))
        {
            var detailNames = _layout.Document.Sections.Where(s => s.Kind == "Detail").Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            var pageDetailSpans = _layout.Document.Sections.Where(s => s.Kind == "PageHeader").SelectMany(s => s.Elements)
                .Where(e => detailNames.Contains(e.LineEndSection)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var activeLines = new List<Item>();
            foreach (var sheet in pages)
            {
                var lastBody = sheet.Placements.LastOrDefault(p => !p.Band.Suppressed && !p.Band.Blank
                    && p.Band.Section.Kind is not ("PageHeader" or "PageFooter"));
                // A page-header frame ending in details closes at the last printed record on that sheet.
                var lastDetailRows = new Dictionary<string, int>(StringComparer.Ordinal);
                if (pageDetailSpans.Count != 0)
                    foreach (var p in sheet.Placements.Where(p => p.Band.Section.Kind == "Detail" && !p.Band.Suppressed
                        && !p.Band.Blank && !p.Band.Section.UnderlayFollowingSections))
                        lastDetailRows[p.Band.Section.Name] = p.Band.Row;
                var drawnBoxes = new HashSet<Item>(ReferenceEqualityComparer.Instance);
                foreach (var placement in sheet.Placements)
                {
                    if (placement.Band.Suppressed || placement.Band.Blank || placement.Band.Section.UnderlayFollowingSections) continue;
                    var section = placement.Band.Section;
                    bool furniture = section.Kind is "PageHeader" or "PageFooter";
                    var starts = placement.Band.Items.Where(i => SpanningLine(i) && !i.Element.IsSuppressed
                        && i.Element.TopTwips >= placement.Offset && i.Element.TopTwips < placement.Offset + placement.Height).ToList();
                    foreach (var line in starts)
                    {
                        activeLines.RemoveAll(previous => previous.Element.Id == line.Element.Id);
                        activeLines.Add(line);
                    }
                    foreach (var line in activeLines.ToArray())
                    {
                        var ends = line.Element.LineEndSection == section.Name
                            && (!pageDetailSpans.Contains(line.Element.Id) || lastDetailRows[section.Name] == placement.Band.Row);
                        if (furniture && !ends && !starts.Contains(line)) continue;
                        var top = starts.Contains(line) ? line.Element.TopTwips : placement.Offset;
                        var end = placement.Offset + placement.Height;
                        if (ends && line.Element.LineEndTop <= end)
                        {
                            end = line.Element.LineEndTop;
                            activeLines.Remove(line);
                        }
                        if (end <= top) continue;
                        var segment = line.Element.CloneFor(section.Id, line.Element.Name);
                        if (segment.Kind == "Box")
                        {
                            var firstOnSheet = drawnBoxes.Add(line);
                            if (!starts.Contains(line) && !(segment.Visual.CloseAtPageBreak && firstOnSheet)) segment.Visual.TopLine = "NoLine";
                            var closesPage = segment.Visual.CloseAtPageBreak && ReferenceEquals(placement, lastBody);
                            if (!ends && closesPage && !ReferenceEquals(sheet, pages[^1]) && placement.Width == 0
                                && sheet.Bottom > placement.Top + placement.Height)
                            {
                                // The side strokes continue through the unused space below the last
                                // detail. Keep this paint outside that band's clip and out of pagination.
                                if (!borderTails.TryGetValue(placement, out var tail))
                                {
                                    var y = placement.Top + placement.Height;
                                    var sectionInk = new ReportDesignerSection { Name = "Continuation borders", HeightTwips = sheet.Bottom - y, BackgroundColor = "transparent" };
                                    tail = new(new Band(sectionInk, [], placement.Band.Row, []), y, 0, sheet.Bottom - y);
                                    borderTails.Add(placement, tail);
                                }
                                var edge = segment.CloneFor(section.Id, segment.Name);
                                edge.LeftTwips = line.Element.LeftTwips;
                                edge.TopTwips = 0;
                                edge.HeightTwips = tail.Height;
                                edge.LineEndSection = "";
                                edge.Visual.TopLine = "NoLine";
                                tail.Band.Items.Add(line with { Element = edge });
                                segment.Visual.BottomLine = "NoLine";
                            }
                            else if (!ends && !closesPage) segment.Visual.BottomLine = "NoLine";
                        }
                        segment.Id = line.Element.Id; segment.LeftTwips = line.Element.LeftTwips;
                        segment.TopTwips = top; segment.HeightTwips = end - top; segment.LineEndSection = "";
                        if (!strokes.TryGetValue(placement, out var parts)) strokes[placement] = parts = [];
                        parts.Add(line with { Element = segment });
                    }
                }
                activeLines.RemoveAll(line => pageDetailSpans.Contains(line.Element.Id));
            }
        }
        IEnumerable<Placement> PaintPlacements(Page sheet)
        {
            foreach (var placement in sheet.Placements)
            {
                yield return placement;
                if (borderTails.TryGetValue(placement, out var tail)) yield return tail;
            }
        }
        var navigation = BuildNativeNavigation(pages);
        // The measurements this loop asks for are also requested by DiscoverPaintMeasurements below; keep the two in step.
        var output = new List<string>();
        var snapshots = new List<ReportPageSnapshot>();
        var duplicates = new DuplicateFields();
        // Crystal tiles oversized main-report pictures across printable sheets. Other objects
        // intersecting those tiles keep the logical page's formulas and their original alignment.
        var sheets = pages.SelectMany((current, index) =>
        {
            var lastPane = current.Placements.Where(p => !p.Band.Suppressed)
                .SelectMany(p => p.Band.Items.Where(i => i.Owner == this && i.Element.TopTwips >= p.Offset
                    && i.Element.TopTwips < p.Offset + p.Height))
                .Select(i => i.HorizontalPane).DefaultIfEmpty().Max();
            var picturePane = 0;
            if (_rows.Length > 0 && size.ContentWidthTwips > 0)
            {
                var pictureRight = current.Placements.Where(p => p.Width == 0 && !p.Band.Suppressed && !p.Band.Blank)
                    .SelectMany(p => p.Band.Items.Where(i => i.Owner == this && !i.Element.IsSuppressed
                        && i.Element is { Kind: "Picture", UseCrystalScreenMetrics: true }
                        && i.Element.TopTwips < p.Offset + p.Height && i.Element.TopTwips + ItemHeight(i) > p.Offset))
                    .Select(i => (long)i.Element.LeftTwips + i.Element.WidthTwips).DefaultIfEmpty().Max();
                var panes = Math.Max(0, pictureRight - 1) / size.ContentWidthTwips;
                if (panes > 5000) throw new InvalidDataException("Report exceeds the horizontal page limit.");
                picturePane = (int)panes;
            }
            var gridSheets = Enumerable.Range(0, Math.Max(lastPane, picturePane) + 1)
                .Select(pane => (current, index, pane, pictureTile: pane > 0 && pane <= picturePane, footerContinuation: false, rulesOnly: false));
            if (_rows.Length > 0 || size.ContentWidthTwips <= 0) return gridSheets;

            // On a no-records print, an oversized report-footer picture tiles the closing band horizontally.
            // Each additional tile has a rules-only sheet followed by its repeated furniture.
            var lastFooterPane = current.Placements.Where(p => p.Band.Section.Kind == "ReportFooter" && !p.Band.Suppressed)
                .SelectMany(p => p.Band.Items)
                .Where(i => i.Element is { Kind: "Picture", UseCrystalScreenMetrics: true })
                .Select(i => (int)Math.Max(0, ((long)i.Element.LeftTwips + i.Element.WidthTwips - 1) / size.ContentWidthTwips))
                .DefaultIfEmpty().Max();
            if (lastFooterPane > 5000) throw new InvalidDataException("Report exceeds the horizontal page limit.");
            return gridSheets.Concat(Enumerable.Range(1, lastFooterPane).SelectMany(pane => new[]
            {
                (current, index, pane, pictureTile: false, footerContinuation: true, rulesOnly: true),
                (current, index, pane, pictureTile: false, footerContinuation: true, rulesOnly: false)
            }));
        });
        var duplicateDecisions = new Dictionary<(Placement, Item), bool>();
        var firstSheet = new int[pages.Count];
        foreach (var (current, index, paneIndex, pictureTile, footerContinuation, rulesOnly) in sheets)
        {
            if (paneIndex == 0)
            {
                duplicateDecisions.Clear();
                firstSheet[index] = snapshots.Count + 1;
            }
            var pageBands = new List<ReportPageBand>();
            var html = new StringBuilder();
            var paper = ReportDesignerEditing.PhysicalPaperSize(size);
            html.Append("<article class=\"fx-report-positioned-page\" aria-label=\"").Append(ReportObjectRenderer.Encode(_layout.Document.Title)).Append("\" data-page=\"").Append(snapshots.Count + 1).Append("\" style=\"position:relative;box-sizing:content-box;overflow:hidden;background:white;color:black;letter-spacing:0;width:")
                .Append(Px(paper.Width)).Append("px;height:")
                .Append(Px(paper.Height)).Append("px;break-after:page;\">");
            html.Append("<div style=\"position:absolute;left:").Append(Px(size.MarginLeftTwips)).Append("px;top:").Append(Px(size.MarginTopTwips)).Append("px;width:").Append(Px(size.ContentWidthTwips)).Append("px;height:").Append(Px(size.ContentHeightTwips)).Append("px;overflow:hidden;\">");
            foreach (var placement in PaintPlacements(current))
            {
                if (placement.Band.Suppressed || placement.Band.Blank) continue;
                var objects = new List<ReportPageObject>();
                html.Append("<section data-section=\"").Append(ReportObjectRenderer.Encode(placement.Band.Section.Name)).Append("\" style=\"position:absolute;overflow:hidden;")
                    .Append(placement.Width > 0 ? "left:" + Px(placement.Left) + "px;width:" + Px(placement.Width) + "px" : "left:0;width:100%").Append(";top:").Append(Px(placement.Top))
                    .Append("px;height:").Append(Px(placement.Height)).Append("px;background:").Append(ReportObjectRenderer.Color(placement.Band.Section.BackgroundColor)).Append(";\">");
                var anchors = paneIndex == 0 ? navigation.Anchors.GetValueOrDefault(placement) ?? [] : [];
                foreach (var anchor in anchors)
                    html.Append("<span id=\"").Append(anchor).Append("\" class=\"fx-rpt-native-anchor\" style=\"position:absolute;inset:0;pointer-events:none;\"></span>");
                IEnumerable<Item> paintItems = placement.Band.Items;
                // Expansion orders items vertically for pagination. Opaque chart drawings
                // must cover earlier objects in the section's authored stacking order.
                if (placement.Band.Items.Any(i => i.Element.Visual.TightHorizontal || i.Element.Analysis?.ChartType is Charts.ChartType.Area or Charts.ChartType.Pie or Charts.ChartType.Donut or Charts.ChartType.Radar or Charts.ChartType.Funnel or Charts.ChartType.Gauge or Charts.ChartType.Scatter or Charts.ChartType.Bubble))
                {
                    var sources = _layout.Document.SourceObjects;
                    if (placement.Band.Items.All(i => sources.ContainsKey(i.Element.SourceKey) && i.Child is null))
                        paintItems = paintItems.OrderBy(i => sources[i.Element.SourceKey].ElementsBeforeSelf().Count());
                }
                if (strokes.TryGetValue(placement, out var segments)) paintItems = segments.Concat(paintItems);
                if (index > 0 && placement.Offset == 0 && placement.Top == current.BodyStart
                    && !placement.Band.RepeatedHeader && placement.Band.PageStartInk is { } ink)
                    paintItems = paintItems.Concat(ink);
                foreach (var item in paintItems)
                {
                    var tileLeft = (long)paneIndex * size.ContentWidthTwips;
                    var right = (long)item.Element.LeftTwips + Math.Max(1, item.Element.WidthTwips);
                    var intersectsTile = paneIndex > 0 && right > tileLeft && item.Element.LeftTwips < tileLeft + size.ContentWidthTwips;
                    var translate = !footerContinuation && intersectsTile && (item.Element.Kind == "Box"
                        || pictureTile && placement.Width == 0 && item.HorizontalPane == 0 && item.Table is null);
                    // A legacy map without rendered artwork occupies space but paints no frame.
                    if (SpanningLine(item) || item.Element.IsSuppressed || item.Element.Kind == "Map") continue;
                    if (footerContinuation)
                    {
                        if (item.Element.Kind != "Line" && (rulesOnly || placement.Band.Section.Kind != "ReportFooter"
                            && !(placement.Band.Section.Kind == "ReportHeader" && item.Element.Kind == "Picture"))) continue;
                    }
                    else if (item.HorizontalPane != paneIndex && !translate) continue;
                    var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name);
                    // Crystal leaves a NULL field unpainted, including its border and fill. An empty string,
                    // suppressed zero, or defaulted database NULL still owns its authored decoration.
                    var nullNativeField = element.Kind == "Field" && element.UseCrystalScreenMetrics
                        && item.DuplicateValue is { Value: null or DBNull };
                    if (nullNativeField)
                    {
                        element.BackgroundColor = "transparent";
                        element.Visual.TopLine = element.Visual.BottomLine = element.Visual.LeftLine = element.Visual.RightLine = "NoLine";
                    }
                    element.LeftTwips = checked((int)(item.Element.LeftTwips - (translate ? tileLeft : 0))); element.TopTwips = item.Element.TopTwips - placement.Offset;
                    element.HeightTwips = ItemHeight(item);
                    if (element.CanGrow && TextMetrics(item) is { } metric
                        && metric.Lines.Any(l => l.Top < placement.Height - element.TopTwips && l.Bottom > placement.Height - element.TopTwips))
                    {
                        // A mandatory nested break may intersect a neighboring text line. Render only
                        // complete lines here; ContinueText carries the unprinted characters to the next page.
                        element.HeightTwips = metric.Lines.Where(l => l.Bottom <= placement.Height - element.TopTwips)
                            .Select(l => l.Bottom).DefaultIfEmpty(0).Max();
                    }
                    // A horizontal line has zero geometric height but a visible
                    // stroke, including when it lies exactly at the section top.
                    var visibleHeight = element.Kind == "Line" && element.HeightTwips == 0 ? 1 : element.HeightTwips;
                    if (element.TopTwips >= placement.Height || element.TopTwips + visibleHeight <= 0) continue;
                    if (element.Kind == "Box")
                    {
                        var end = element.TopTwips + element.HeightTwips;
                        if (!element.Visual.CloseAtPageBreak)
                        {
                            if (element.TopTwips < 0) element.Visual.TopLine = "NoLine";
                            if (end > placement.Height) element.Visual.BottomLine = "NoLine";
                        }
                        // Each fragment owns its borders; clipping the original box loses page-edge closure.
                        var childTop = element.TopTwips;
                        element.TopTwips = Math.Max(0, element.TopTwips);
                        element.HeightTwips = Math.Min(placement.Height, end) - element.TopTwips;
                        if (element.UseCrystalScreenMetrics && element.SubreportName.Length > 0)
                        {
                            // The child's coordinates are content edges. Crystal paints its frame outside them on all four
                            // sides, including the one-point gutter; a fragment cut by a page break keeps the cut edge in place.
                            int FrameInset(string line)
                            {
                                var pixels = ReportObjectRenderer.BorderPixels(line, element.Visual.LineWidthTwips);
                                return pixels > 0 ? 20 + (int)Math.Ceiling(pixels * 15) : 0;
                            }
                            var leftInset = FrameInset(element.Visual.LeftLine);
                            element.LeftTwips -= leftInset;
                            element.WidthTwips += leftInset + FrameInset(element.Visual.RightLine);
                            var topInset = childTop >= 0 ? FrameInset(element.Visual.TopLine) : 0;
                            var bottomInset = end <= placement.Height ? FrameInset(element.Visual.BottomLine) : 0;
                            element.TopTwips -= topInset;
                            element.HeightTwips += topInset + bottomInset;
                        }
                    }
                    var content = nullNativeField ? "" : ResolvePageValues(item.Html.Replace(_pageToken, current.Number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace(_countToken, counts[index].ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), current.Number, counts[index], placement.Band.RepeatedHeader);
                    var numericContent = content;
                    content = MaskNumericOverflow(item with { Element = element, Html = content });
                    numericContent = numericContent != content ? numericContent : null;
                    // Repainting a duplicate field's right-hand fragment must not advance its logical row.
                    var suppressedDuplicate = false;
                    if (paneIndex == 0 || !duplicateDecisions.TryGetValue((placement, item), out suppressedDuplicate))
                    {
                        suppressedDuplicate = duplicates.Suppress(item, placement.Band.Owner ?? this, index + 1, placement.Band.RepeatedHeader);
                        if (item.HorizontalPane == 0 && right > size.ContentWidthTwips && SuppressesDuplicates(item.Element))
                            duplicateDecisions[(placement, item)] = suppressedDuplicate;
                    }
                    if (suppressedDuplicate)
                    {
                        content = "";
                        numericContent = null;
                    }
                    var textFrame = element.TextLayoutFrame();
                    var textMetric = element.UseCrystalScreenMetrics && content.Length > 0
                        ? element.CanGrow && !element.HasRotatedText ? TextMetrics(item with { Html = content }) : CrystalDeviceText.Measure(textFrame, content) : null;
                    var textLines = textMetric is not null && (element.CanGrow || textMetric.Native) ? textMetric.Lines.ToArray() : null;
                    if (textMetric is { Native: true } && textLines is not null)
                    {
                        // Crystal's device-metric lines are explicit in the page markup, so the viewer's own
                        // wrapping (fractional advances) can neither join nor split them; a line GDI keeps that
                        // the browser would clip by a fraction of a pixel widens the painted box instead.
                        if (!element.HasRotatedText) CrystalDeviceText.WidenForOvershoot(element, content, textLines);
                        content = CrystalDeviceText.ExplicitLines(content, textLines);
                    }
                    else if (textMetric is null && element.UseCrystalScreenMetrics && !element.HasRotatedText && content.Length > 0) CrystalDeviceText.WidenSingleLine(element, content);
                    if (item.NarrowedByFrame && !element.HasRotatedText) content = CrystalDeviceText.PreviewInFixedFrame(element, content);
                    if (element.Visual.TightHorizontal && !element.HasRotatedText && element.Kind is "Field" or "Text" or "FieldHeading")
                        CrystalDeviceText.FitTightFrame(element, content, textLines);
                    var painted = new ReportPageObject(element, content, item.Analysis)
                    { Table = item.Table, TextLines = textLines, UnmaskedNumericContent = numericContent };
                    var overprint = ReportObjectRenderer.IsDuplicateNativeTextPaint(objects.LastOrDefault(), painted,
                        size.MarginLeftTwips + placement.Left, size.MarginTopTwips + placement.Top);
                    objects.Add(painted);
                    html.Append("<div data-object=\"").Append(ReportObjectRenderer.Encode(element.Name)).Append("\" style=\"")
                        .Append(ReportObjectRenderer.Style(element, true, size.MarginLeftTwips + placement.Left, size.MarginTopTwips + placement.Top))
                        .Append(overprint ? "visibility:hidden;\" data-fx-native-overprint=\"true\" aria-hidden=\"true\">" : "\">")
                        .Append(content).Append("</div>");
                }
                pageBands.Add(new(placement.Band.Section.Name, placement.Top, placement.Height, placement.Band.Section.BackgroundColor, objects)
                    { LeftTwips = placement.Left, WidthTwips = placement.Width, NavigationAnchors = anchors,
                        DrillTarget = navigation.Targets.GetValueOrDefault(placement) });
                html.Append("</section>");
            }
            snapshots.Add(new(_layout.Document.Title, snapshots.Count + 1, new ReportDesignerPage
            {
                Orientation = size.Orientation, PaperSize = size.PaperSize, ContentWidthTwips = size.ContentWidthTwips, ContentHeightTwips = size.ContentHeightTwips,
                MarginLeftTwips = size.MarginLeftTwips, MarginRightTwips = size.MarginRightTwips, MarginTopTwips = size.MarginTopTwips, MarginBottomTwips = size.MarginBottomTwips
            }, pageBands));
            html.Append("</div></article>"); output.Add(html.ToString());
        }
        // The formula page number stays vertical; navigation indexes the physical sheets.
        foreach (var node in _navigationGroups.Keys)
            if (node.PageIndex > 0) node.PageIndex = firstSheet[node.PageIndex - 1];
        // Nested sessions' own diagnostics reach the result, except those a parent already copied
        // with its "<element>: " prefix (ReportLayoutSession.Runtime), which would show twice.
        var diagnostics = new List<string>(_diagnostics);
        foreach (var session in _state.Sessions.Where(session => !ReferenceEquals(session, this)))
            foreach (var diagnostic in session._diagnostics)
                if (!diagnostics.Any(d => d == diagnostic || d.EndsWith(": " + diagnostic, StringComparison.Ordinal)))
                    diagnostics.Add(diagnostic);
        return new(output, diagnostics.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList())
            { Snapshots = snapshots, Tree = navigation.Tree, SubreportErrors = _state.SubreportErrors.ToList(), IsComplete = !prefixOnly };

        int ExpectedCount(int index) => expectedCounts is not null && index < expectedCounts.Count ? expectedCounts[index] : expectedPages;
        // The measurement requests of the paint loop below, in its order and under its conditions, without its output:
        // the growing text's page-resolved content and the numeric overflow probes (MaskNumericOverflow), each after the
        // duplicate suppression that decides whether the object prints at all. Anything missed here is requested by the
        // paint itself, which then costs one more pass, never a different page.
        void DiscoverPaintMeasurements()
        {
            var duplicates = new DuplicateFields();
            for (var index = 0; index < pages.Count; index++)
            {
                var current = pages[index];
                foreach (var placement in current.Placements)
                {
                    if (placement.Band.Suppressed || placement.Band.Blank) continue;
                    foreach (var item in placement.Band.Items)
                    {
                        if (item.Element.IsSuppressed) continue;
                        var source = item.Element;
                        var nullNativeField = source.Kind == "Field" && source.UseCrystalScreenMetrics && item.DuplicateValue is { Value: null or DBNull };
                        var top = source.TopTwips - placement.Offset;
                        var height = ItemHeight(item);
                        if (source.CanGrow && TextMetrics(item) is { } metric
                            && metric.Lines.Any(l => l.Top < placement.Height - top && l.Bottom > placement.Height - top))
                            height = metric.Lines.Where(l => l.Bottom <= placement.Height - top).Select(l => l.Bottom).DefaultIfEmpty(0).Max();
                        var visibleHeight = source.Kind == "Line" && height == 0 ? 1 : height;
                        if (top >= placement.Height || top + visibleHeight <= 0) continue;
                        var content = nullNativeField ? "" : ResolvePageValues(item.Html.Replace(_pageToken, current.Number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace(_countToken, counts[index].ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), current.Number, counts[index], placement.Band.RepeatedHeader);
                        if (content.Length > 0 && NeedsNumericMask(source))
                        {
                            // The probe's key carries the painted box, so the box is built as the paint builds it.
                            var element = source.CloneFor(source.SectionId, source.Name);
                            element.LeftTwips = source.LeftTwips; element.TopTwips = top; element.HeightTwips = height;
                            content = MaskNumericOverflow(item with { Element = element, Html = content });
                        }
                        if (duplicates.Suppress(item, placement.Band.Owner ?? this, index + 1, placement.Band.RepeatedHeader)) content = "";
                        if (source.UseCrystalScreenMetrics && content.Length > 0 && source.CanGrow) TextMetrics(item with { Html = content });
                    }
                }
            }
        }
        void HiddenEvent(Band hidden)
        {
            if (!HasVisibilityEvent(hidden.Section.Id)) return;
            if (page.Cursor >= page.Bottom) pendingHidden.Add((hidden, 0, sequence));
            else page.Placements.Add(new(hidden, page.Cursor, 0, 0, sequence));
        }
        // Crystal formats a blank section (formulas, subreports) before discarding it; the physical replay must see that work.
        void BlankEvent(Band blank)
        {
            if (!_state.PhysicalSchedule) return;
            blank = blank with { Blank = true };
            if (page.Cursor >= page.Bottom) pendingHidden.Add((blank, Height(blank), sequence));
            else page.Placements.Add(new(blank, page.Cursor, 0, Height(blank), sequence));
        }
        // Native areas honor their explicit group-keep flag; Crystal can leave a heading on the preceding page.
        // Designer-only layouts without area metadata retain the implicit header/detail keep.
        int KeepHeight(int bandIndex, Band band, ReportLayoutArea? area, int room, bool afterPriorDetails = false)
        {
            int Flow(int index)
            {
                var next = index == bandIndex ? band : PageBand(index);
                return next.Suppressed || next.Section.UnderlayFollowingSections || next.Section.SuppressIfBlank && IsBlank(next) ? 0 : Height(next);
            }
            var keepHeight = band.KeptStartHeight ?? Flow(bandIndex);
            if (area is { KeepGroupTogether: false }) return keepHeight;
            for (var next = bandIndex + 1; next < bands.Count && bands[next].Row == band.Row; next++)
            {
                keepHeight += Flow(next);
                if (bands[next].Section.Kind != "GroupHeader") break;
            }
            if (area?.KeepGroupTogether != true) return keepHeight;
            var wholeGroup = Flow(bandIndex);
            var groupNumber = DrillSectionDepth(band.Section);
            if (groupNumber < 1 || band.Row < 0) return keepHeight;
            var level = groupNumber - 1;
            var hierarchy = HierarchyAt(level);
            var groupEnd = hierarchy is null ? -1 : hierarchy.Nodes[hierarchy.NodeOfRow[band.Row]].SubtreeEnd;
            for (var next = bandIndex + 1; next < bands.Count; next++)
            {
                var candidate = bands[next];
                if (candidate.Section.Kind == "ReportFooter" ||
                    candidate.Section.Kind == "GroupFooter" && DrillSectionDepth(candidate.Section) < groupNumber) break;
                if (groupEnd >= 0 ? candidate.Row >= groupEnd : GroupStart(level, candidate.Row) != GroupStart(level, band.Row)) break;
                wholeGroup += Flow(next);
                if (wholeGroup > room)
                {
                    if (afterPriorDetails && page.Placements.Any(p => p.Band.Section.Kind == "Detail"
                        && !p.Band.Suppressed && !p.Band.Blank && p.Band.Row < band.Row))
                        return room;
                    return keepHeight;
                }
            }
            return wholeGroup;
        }
        bool InColumns(ReportDesignerSection section) => columns is not null && (section.Kind == "Detail" || columns.FormatGroups && section.Kind is "GroupHeader" or "GroupFooter");
        int ColumnLeft(int index) => index * (columns!.DetailWidth + columns.HorizontalGap);
        void OpenColumns()
        {
            block = page; blockTop = blockBottom = page.Cursor; column = rowHeight = 0;
            columnUsed = gapPending = bottomCell = fixedCell = breakAfterCell = breakNow = false;
        }
        // Down then across: the end of the current column, with the gap below its last area.
        int ColumnEnd() => gapPending ? Math.Min(page.Bottom, page.Cursor + columns!.VerticalGap) : page.Cursor;
        void CloseColumns()
        {
            if (block is null) return;
            if (block == page) page.Cursor = !columns!.AcrossThenDown ? Math.Max(blockBottom, ColumnEnd())
                : column > 0 ? Math.Min(page.Bottom, page.Cursor + rowHeight + columns.VerticalGap) : page.Cursor;
            block = null;
        }
        void NextColumn(Band band)
        {
            if (column + 1 >= columnCount) { ColumnPage(band); return; }
            blockBottom = Math.Max(blockBottom, ColumnEnd());
            column++; page.Cursor = blockTop; columnUsed = gapPending = false;
        }
        void NextRow() { page.Cursor = Math.Min(page.Bottom, page.Cursor + rowHeight + columns!.VerticalGap); column = rowHeight = 0; }
        // The block goes on at the top of a new page; repeated group headers print across the page, or in the columns when the groups are,
        // where each takes a place (and the vertical gap after it) like any area; a blank one takes none.
        void ColumnPage(Band band)
        {
            page = NewPage(band.Row, columns!.FormatGroups ? [] : band.Repeats);
            OpenColumns();
            if (!columns.FormatGroups) return;
            foreach (var repeat in band.Repeats)
            {
                var header = Blankness(ForPage(repeat, page.Number, ExpectedCount(pages.Count - 1), true, pages.Count), pages.Count);
                if (header.Blank && !_state.PhysicalSchedule) continue;
                if (columns.AcrossThenDown && column >= columnCount) NextRow();
                page.Placements.Add(new(header, page.Cursor, 0, Math.Min(Height(header), page.Bottom - page.Cursor), -1, ColumnLeft(columns.AcrossThenDown ? column : 0), columns.DetailWidth));
                if (header.Blank) continue;
                if (columns.AcrossThenDown) { column++; rowHeight = Math.Max(rowHeight, Height(header)); continue; }
                if (!header.Section.UnderlayFollowingSections) page.Cursor = Math.Min(page.Bottom, page.Cursor + Height(header));
                page.Cursor = Math.Min(page.Bottom, page.Cursor + columns.VerticalGap); columnUsed = true;
            }
        }
        // Down then across: room for `need` twips in the next column when this one has content (or a fresh column is as tall as a fresh page),
        // otherwise on a new page when content above leaves less than a fresh page.
        void Need(int need, Band band)
        {
            if (need <= page.Bottom - page.Cursor) return;
            if (columnUsed && column + 1 < columnCount && (need <= page.Bottom - blockTop || blockTop <= page.BodyStart)) NextColumn(band);
            else if (page.Cursor > page.BodyStart) ColumnPage(band);
        }
        // An area instance's extent (an underlay covers what follows it). Printed at the bottom as an area, the whole instance ends its column.
        int Cell(int from, string areaId, int row)
        {
            int extent = 0, flow = 0;
            for (var next = from; next < bands.Count && areaOf.GetValueOrDefault(bands[next].Section.Id) == areaId && bands[next].Row == row; next++)
            {
                if (bands[next].Suppressed || bands[next].Section.SuppressIfBlank && IsBlank(bands[next])) continue;
                extent = Math.Max(extent, flow + Height(bands[next]));
                if (!bands[next].Section.UnderlayFollowingSections) flow += Height(bands[next]);
            }
            return extent;
        }
        void PlaceInColumns(int bandIndex, Band band, ReportLayoutArea? area, string areaId)
        {
            var across = columns!.AcrossThenDown;
            if (block != page) OpenColumns();
            if ((areaId, band.Row) != areaInstance)
            {
                areaInstance = (areaId, band.Row);
                var extent = Cell(bandIndex, areaId, band.Row);
                var bottom = area?.PrintAtBottomOfPage == true;
                // A fixed detail height (mailing labels) applies when the groups print across the page (Crystal's fixed-height detail formatter).
                var fixedHeight = !columns.FormatGroups && band.Section.Kind == "Detail" ? columns.DetailHeight : 0;
                if (fixedHeight > 0) extent = fixedHeight;
                var breakBefore = band.Section.NewPageBefore || area?.NewPageBefore == true;
                if (across)
                {
                    // Each cell takes the next place in the row; a cell that does not fit starts the next row, or the next page.
                    if (breakNow || breakBefore) { if (column > 0) NextRow(); breakNow = false; }
                    if (column >= columnCount || extent > page.Bottom - page.Cursor && column > 0) NextRow();
                    // Repeated group headers may fill the new page's first row; the cell then starts the next.
                    if (extent > page.Bottom - page.Cursor && page.Cursor > page.BodyStart) { ColumnPage(band); if (column >= columnCount) NextRow(); }
                    cellLeft = ColumnLeft(column++);
                    cellCursor = bottom && extent <= page.Bottom - page.Cursor ? page.Bottom - extent : page.Cursor;
                    rowHeight = Math.Max(rowHeight, bottom ? page.Bottom - page.Cursor : extent);
                }
                else
                {
                    if (breakNow || breakAfterCell || breakBefore) { if (columnUsed) NextColumn(band); breakNow = breakAfterCell = false; }
                    if (gapPending) { page.Cursor = ColumnEnd(); gapPending = false; }
                    var keep = fixedHeight > 0 || bottom || area?.KeepTogether == true ? extent
                        : band.Section.Kind == "GroupHeader" ? KeepHeight(bandIndex, band, area, page.Bottom - blockTop) : Height(band);
                    Need(keep <= page.Bottom - blockTop ? keep : Height(band), band);
                    if (bottom && extent <= page.Bottom - page.Cursor) { page.Cursor = page.Bottom - extent; blockBottom = page.Bottom; }
                    cellLeft = ColumnLeft(column); cellCursor = page.Cursor;
                    if (fixedHeight > 0) page.Cursor = Math.Min(page.Bottom, page.Cursor + fixedHeight);
                    breakAfterCell = bottom;
                }
                bottomCell = bottom; fixedCell = fixedHeight > 0;
                cellLimit = fixedCell ? Math.Min(page.Bottom, cellCursor + fixedHeight) : page.Bottom;
            }
            else if (!across && !fixedCell && !bottomCell)
            {
                // A later section of the area goes on in the next column when it does not fit or follows a break.
                if ((breakNow || breakAfterCell || band.Section.NewPageBefore) && columnUsed) { NextColumn(band); breakNow = breakAfterCell = false; }
                else Need(Height(band), band);
                cellLeft = ColumnLeft(column); cellCursor = page.Cursor; cellLimit = page.Bottom;
            }
            band = PageBand(bandIndex);
            if (band.Suppressed) { HiddenEvent(band); return; }
            var height = Height(band);
            // A section printed at the bottom goes to the bottom of its label, or of the page; its area then fills the column (row), so what follows
            // starts the next column (row).
            if (band.Section.PrintAtBottomOfPage && !bottomCell && !band.Section.UnderlayFollowingSections)
            {
                cellCursor = Math.Max(cellCursor, cellLimit - height);
                if (!fixedCell && across) rowHeight = Math.Max(rowHeight, page.Bottom - page.Cursor);
                else if (!fixedCell) { blockBottom = page.Bottom; breakAfterCell = true; }
            }
            var slice = Math.Max(0, Math.Min(height, cellLimit - cellCursor));
            if (slice < height) _diagnostics.Add(fixedCell ? $"{band.Section.Name}: the part of the details below their fixed height (mailing labels) is clipped."
                : $"{band.Section.Name}: a section taller than the room left in its column is clipped; Crystal continues it in the next column.");
            var placement = new Placement(band, cellCursor, 0, slice, sequence, cellLeft, columns.DetailWidth);
            if (band.Section.UnderlayFollowingSections)
                page.Placements.Insert(page.Placements.TakeWhile(p => p.Band.Section.Kind == "PageHeader" || p.Band.Section.UnderlayFollowingSections).Count(), placement);
            else { page.Placements.Add(placement); cellCursor += slice; }
            if (!across && !fixedCell) page.Cursor = breakAfterCell ? page.Bottom : cellCursor;
            page.HasBody = columnUsed = gapPending = true;
            breakNow |= band.Section.NewPageAfter || area?.NewPageAfter == true;
            resetPageNumber |= band.Section.ResetPageNumberAfter || area?.ResetPageNumberAfter == true;
        }
        // Page furniture and repeated headers: a blank "Suppress Blank Section" band takes no space, but the physical replay still formats it.
        Band Blankness(Band band, int physicalPage = 0)
        {
            if (!band.Section.SuppressIfBlank || band.Suppressed) return band;
            var blank = IsBlank(band);
            // This page's furniture may use a record variant the last replay did not format; that page's printed blankness still applies.
            if (physicalPage > 0 && _state.PhysicalSchedule && band.Items.Count > 0
                && !band.Items.Any(i => _state.PrintedItems.ContainsKey(new(new(i.Owner ?? band.Owner ?? this, i.Element.SectionId, i.Row, physicalPage), i.Element.Id)))
                && _state.PrintedBlank.TryGetValue((band.Owner ?? this, band.Section.Id, physicalPage), out var printed)) blank = printed;
            return blank ? band with { Blank = true } : band;
        }
        bool IsFooterSpacer(Band band) => band.Section.Kind is "GroupFooter" or "ReportFooter"
            && !band.Section.NewPageAfter && _layout.Areas.GetValueOrDefault(band.Section.Id) is not ({ NewPageAfter: true } or { ReserveFinalFooter: true })
            && band.Items.Count == 0 && band.Section.Elements.Count == 0
            && band.Section.BackgroundColor.ToLowerInvariant() is "transparent" or "#ffffff"
            && !band.Section.UnderlayFollowingSections
            && !HasVisibilityEvent(band.Section.Id)
            && !(_layout.SectionConditions.GetValueOrDefault(band.Section.Id)?.Count > 0)
            && !(_layout.AreaConditions.GetValueOrDefault(band.Section.Id)?.Count > 0);
        int Space(Band band) => band.Blank || band.Section.UnderlayFollowingSections ? 0 : Height(band);
        // Page headers and footers print their subreports on every page, laid out like a body section's.
        Band Furnish(Band band, int number) => band.Items.Any(i => i.Child is not null) ? Expand(band, 0, number) : band;
        Band FurnitureBand(string section, int row, int number, int count, int physical)
        {
            var band = ForPage(_furniture[(section, row)], number, count, physicalPage: physical);
            return band.Items.Any(i => i.Child is not null) ? ForPage(Expand(band, 0, number), number, count, physicalPage: physical) : band;
        }
        // The band loop asks for the same page band twice (before and after the keep/new-page decisions); when neither the
        // page number nor the expected count changed in between, the second answer is the first one.
        Band PageBand(int index, int? physicalIndex = null)
        {
            var count = ExpectedCount(physicalIndex ?? pages.Count - 1);
            if (lastPageBand is { } last && last.Index == index && last.Page == page.Number && last.Count == count) return last.Band;
            var band = ForPage(Expand(ForPage(_bands[index], page.Number, count), 0, page.Number), page.Number, count);
            lastPageBand = (index, page.Number, count, band);
            return band;
        }
        Page NewPage(int row, List<Band> repeats, bool withHeaders = true)
        {
            if (_paginationPageLimit > 0 && pages.Count >= _paginationPageLimit) throw new PagePrefixComplete();
            if (pages.Count >= 10000) throw new InvalidDataException("Report exceeds the 10,000-page layout limit.");
            var number = resetPageNumber || pages.Count == 0 ? 1 : pages[^1].Number + 1;
            var footerRow = footerRows is not null && pages.Count < footerRows.Count ? footerRows[pages.Count] : row;
            var reserve = footers.Sum(section => Space(Blankness(FurnitureBand(section.Id, footerRow, number, ExpectedCount(pages.Count), pages.Count + 1), pages.Count + 1)));
            var next = new Page { Number = number, Bottom = size.ContentHeightTwips - reserve };
            resetPageNumber = false;
            if (withHeaders) AddHeaders(next, row);
            if (next.Cursor >= next.Bottom)
            {
                // Designed header and footer sections that already fill the page are Crystal's
                // "page area too large" error. Growth past a section that fits (an inline
                // subreport in the page header is the usual case) is clipped to that section
                // so the body keeps the room the design left for it.
                var staticSpan = headers.Concat(footers).Where(section => !section.IsSuppressed).Sum(section => section.HeightTwips);
                if (staticSpan >= size.ContentHeightTwips)
                    throw new InvalidDataException("Page headers plus footers leave no space for content (Crystal: page area too large).");
                _diagnostics.Add("Page header or footer content that grows past its section is clipped to the designed section height. The overflow is usually an inline subreport.");
                var cursor = 0;
                for (var i = 0; i < next.Placements.Count; i++)
                {
                    var placement = next.Placements[i];
                    // Expand() copies the section and adds growth onto HeightTwips. The document
                    // section still has the designed height, which is what the page must reserve.
                    var designed = _layout.Document.Sections.First(section => section.Id == placement.Band.Section.Id).HeightTwips;
                    next.Placements[i] = placement with { Top = cursor, Height = designed };
                    cursor += placement.Band.Section.UnderlayFollowingSections ? 0 : designed;
                }
                next.Cursor = cursor;
            }
            var (headerEnd, headerCount) = (next.Cursor, next.Placements.Count);
            // A carried footer closes its group; it does not continue the detail
            // body. Repeat ancestor headers, not the finished group's heading.
            var closingFooter = sequence >= 0 && sequence < bands.Count
                && bands[sequence].Section.Kind is "GroupFooter" or "ReportFooter"
                && !bands[sequence].Section.NewPageBefore && !bands[sequence].Section.NewPageAfter
                && _layout.Areas.GetValueOrDefault(bands[sequence].Section.Id)?.NewPageBefore != true
                && _layout.Areas.GetValueOrDefault(bands[sequence].Section.Id)?.NewPageAfter != true
                ? _bands[sequence] : null;
            var closingGroup = closingFooter is null ? int.MaxValue : closingFooter.Section.Kind == "ReportFooter"
                ? 0 : ReportDesignerEditing.GetGroupNumber(_layout.Document, closingFooter.Section);
            foreach (var repeat in repeats)
            {
                if (ReportDesignerEditing.GetGroupNumber(_layout.Document, repeat.Section) >= closingGroup) continue;
                var header = Blankness(ForPage(repeat, next.Number, ExpectedCount(pages.Count), true, pages.Count + 1), pages.Count + 1);
                if (header.Blank && !_state.PhysicalSchedule) continue;
                // An underlaid repeated header prints behind the sections that follow it.
                next.Placements.Add(new(header, next.Cursor, 0, Height(header), -1)); next.Cursor += header.Section.UnderlayFollowingSections ? 0 : Space(header);
            }
            // Crystal drops repeated group headers that do not fit and formats the page without them (ai: repeated header not fit).
            if (next.Cursor >= next.Bottom)
            {
                _diagnostics.Add("Repeated group headers that do not fit on a page are omitted from that page, as Crystal does.");
                next.Placements.RemoveRange(headerCount, next.Placements.Count - headerCount); next.Cursor = headerEnd;
            }
            next.BodyStart = next.Cursor;
            foreach (var hidden in pendingHidden) next.Placements.Add(new(hidden.Band, next.Cursor, 0, hidden.Height, hidden.Sequence));
            pendingHidden.Clear();
            pages.Add(next); return next;
        }
        void AddHeaders(Page target, int row)
        {
            foreach (var section in headers)
            {
                var physical = pages.Contains(target) ? pages.Count : pages.Count + 1;
                var band = Blankness(FurnitureBand(section.Id, row, target.Number, ExpectedCount(physical - 1), physical), physical);
                if (band.Blank && !_state.PhysicalSchedule) continue;
                // A first-page header printed below a report header observes its variable assignments; on a later page the header
                // prints first, also above a band continued from the page before.
                target.Placements.Add(new(band, target.Cursor, 0, Height(band), -2) { PrintSequence = target.HasBody && physical == 1 ? sequence : -2 });
                target.Cursor += band.Section.UnderlayFollowingSections ? 0 : Space(band);
            }
            target.HasHeaders = true;
            if (!target.HasBody) target.BodyStart = target.Cursor;
        }
        void AddInlineHeaders(Page target, Band band, int offset)
        {
            var (start, count) = (target.Cursor, target.Placements.Count);
            // Horizontal panes are printed on separate sheets at the same vertical position.
            var headersToRepeat = (band.InlineHeaders ?? []).Where(h => h.Repeat && h.Start < offset && h.End > offset)
                .OrderBy(h => h.Start).GroupBy(h => h.HorizontalPeers ?? h, ReferenceEqualityComparer.Instance);
            foreach (var peers in headersToRepeat)
            {
                var headers = peers.Select(repeat => Blankness(ForPage(repeat.Header, target.Number,
                        ExpectedCount(pages.Count - 1), true, pages.Count), pages.Count))
                    .Where(header => !header.Blank || _state.PhysicalSchedule).ToList();
                if (headers.Count == 0) continue;
                var reserved = headers.Max(Space);
                if (target.Cursor + reserved >= target.Bottom)
                {
                    _diagnostics.Add("Repeated subreport group headers that do not fit on a page are omitted from this page, as Crystal does.");
                    target.Placements.RemoveRange(count, target.Placements.Count - count); target.Cursor = start;
                    return;
                }
                var header = headers.FirstOrDefault(h => !h.Blank) ?? headers[0];
                if (headers.Count > 1)
                {
                    // Paint one band; a second opaque section background would cover
                    // the labels on the other pane even when it has no cells on this sheet.
                    header = header with { Items = headers.Where(h => !h.Blank).SelectMany(h => h.Items).ToList() };
                }
                target.Placements.Add(new(header, target.Cursor, 0, headers.Max(Height), sequence));
                target.Cursor += reserved;
            }
        }
        Band Expand(Band source, int measurementOffset, int? objectPage = null, int? printableWidth = null)
        {
            _paginationCancellationToken.ThrowIfCancellationRequested();
            source = ApplyPrintedItems(source, 0, false);
            if (source.Suppressed) return source;
            var items = new List<Item>();
            var breaks = new List<int>();
            var forcedBreaks = new List<int>();
            var inlineHeaders = new List<InlineHeader>();
            var inlineSections = new List<InlineSection>();
            List<Item>? pageStartInk = null;
            HashSet<string>? splittableGrids = null;
            var growth = new List<(int Bottom, int Amount, int Left, int Right, bool NativeGrid)>();
            var ordered = true;
            for (var index = 1; index < source.Items.Count && ordered; index++) ordered = source.Items[index - 1].Element.TopTwips <= source.Items[index].Element.TopTwips;
            foreach (var sourceItem in ordered ? source.Items : (IEnumerable<Item>)source.Items.OrderBy(i => i.Element.TopTwips))
            {
                var item = (sourceItem.Owner ?? source.Owner ?? this).ResolveDeferredSubreport(sourceItem);
                if (item.Element.IsSuppressed) { items.Add(item); continue; }
                var shift = 0;
                foreach (var g in growth)
                    if (g.Bottom <= item.Element.TopTwips && (source.Section.RelativePositions || g.Left < item.Element.LeftTwips + item.Element.WidthTwips && item.Element.LeftTwips < g.Right))
                        shift = Math.Max(shift, g.Amount);
                var top = item.Element.TopTwips + shift;
                if (item.Analysis?.Table is { } table)
                {
                    if (item.Element.Kind == "CrossTab" && table.NativeCrossTab is not null && item.Analysis.Rows.Count == 0)
                        continue;
                    // Crystal clips a cross-tab that runs past the printable edge; never ask for panes
                    // narrower than the pager's minimum, which would fail the whole report.
                    var pageRoom = (printableWidth ?? size.ContentWidthTwips) - item.Element.LeftTwips;
                    var available = item.Analysis.Definition.NativeCrossTab is not null ? pageRoom : Math.Min(item.Element.WidthTwips, pageRoom);
                    var width = Math.Max(ReportTabularData.MinimumPaneWidthTwips, available);
                    if (width > available)
                        _diagnostics.Add($"{item.Element.Name}: table is narrower than the 0.25-inch minimum pane; content past its edge is clipped.");
                    var panes = table.PaginateColumns(width, item.Element.FontSize, item.Analysis.Definition.RowHeightTwips);
                    var regionCursor = top;
                    var horizontal = table.NativeCrossTab is not null && table.RepeatedColumns == 0;
                    object? headerPeers = horizontal && panes.Count > 1 ? item : null;
                    var gridDocument = (item.Owner ?? source.Owner ?? this)._layout.Document;
                    if (table.NativeCrossTab is not null && item.Element.CanGrow && !InColumns(source.Section)
                        && gridDocument.SourceObjects.TryGetValue(item.Element.SourceKey, out var gridSource)
                        && (bool?)gridSource.Element("ObjectFormat")?.Attribute("EnableKeepTogether") == false)
                        (splittableGrids ??= []).Add(item.Element.SourceKey);
                    foreach (var pane in panes)
                    {
                        if (horizontal) regionCursor = top;
                        if (regionCursor > top) forcedBreaks.Add(regionCursor);
                        var start = regionCursor;
                        var headerItems = new List<Item>();
                        int? nestedHeaderStart = null;
                        var fragments = pane.Headers.Concat(pane.Rows).ToArray();
                        if (fragments.FirstOrDefault()?.NativeCrossTab is { Templates.Count: > 0 }
                            && new[] { item.Element.Visual.TopLine, item.Element.Visual.BottomLine, item.Element.Visual.LeftLine, item.Element.Visual.RightLine }
                                .Any(line => ReportObjectRenderer.BorderPixels(line, item.Element.Visual.LineWidthTwips) > 0))
                        {
                            // Crystal's native grid frame surrounds the cell area with a two-point inset.
                            const int frameInset = 40;
                            var frame = item.Element.CloneFor(item.Element.SectionId, item.Element.Name);
                            frame.Id = item.Element.Id + "-pane" + fragments[0].Pane + "-grid-frame";
                            frame.Kind = "Box"; frame.CanGrow = false;
                            frame.LeftTwips = item.Element.LeftTwips - frameInset;
                            frame.TopTwips = start - frameInset;
                            frame.WidthTwips = fragments[0].Columns.Sum(column => column.WidthTwips) + 2 * frameInset + (frame.Visual.LineWidthTwips ?? 20);
                            frame.HeightTwips = fragments.Sum(fragment => fragment.HeightTwips) + 2 * frameInset + (frame.Visual.LineWidthTwips ?? 20);
                            items.Add(item with { Element = frame, Analysis = null, Table = null, Measurement = -1, Html = "", TemplateHtml = "" });
                        }
                        for (var fragmentIndex = 0; fragmentIndex < fragments.Length; fragmentIndex++)
                        {
                            var fragment = fragments[fragmentIndex];
                            if (fragment.KeepWithNext) nestedHeaderStart ??= regionCursor;
                            else if (nestedHeaderStart is { } keepStart)
                            {
                                inlineHeaders.Add(new(keepStart, regionCursor + fragment.HeightTwips,
                                    new(source.Section, [], item.Row, [], Owner: item.Owner), regionCursor + fragment.HeightTwips, false));
                                nestedHeaderStart = null;
                            }
                            var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name);
                            element.Id = item.Element.Id + $"-pane{fragment.Pane}-row{fragment.Row}-line{fragment.Line}";
                            // A continuation begins at the three-point print gutter.
                            element.LeftTwips = horizontal && fragment.Pane > 0 ? 60 : item.Element.LeftTwips;
                            element.TopTwips = regionCursor;
                            element.WidthTwips = fragment.Columns.Sum(column => column.WidthTwips);
                            element.HeightTwips = fragment.HeightTwips; element.CanGrow = false;
                            if (fragment.NativeCrossTab is { } nativeCell)
                            {
                                element.LeftTwips += nativeCell.LeftInset;
                                if (fragment.Pane == 0 && nativeCell.ColumnSumFormat.Length > 0 && nativeCell.RowTotalLabelStyle?.Suppressed == true)
                                    element.LeftTwips += nativeCell.PaddingX;
                                element.FontFamily = nativeCell.FontFamily; element.FontSize = nativeCell.FontSize;
                                if (nativeCell.Templates.Count > 0)
                                {
                                    // The native grid strokes own its frame, including the unpainted corner.
                                    element.Visual.TopLine = element.Visual.BottomLine = element.Visual.LeftLine = element.Visual.RightLine = "NoLine";
                                }
                            }
                            var rowItem = item with { Element = element, Analysis = null, PrintGrid = fragmentIndex == 0 && fragment.Pane == 0 ? table.PrintCellFormulas : null, HorizontalPane = horizontal ? fragment.Pane : 0, Table = fragment with { RegionId = item.Element.Id + "-" + item.Row }, Measurement = -1,
                                Html = ReportObjectRenderer.Encode(string.Join(" ", fragment.Cells)) };
                            items.Add(rowItem);
                            if (fragment.ContinuationHeaders.Count > 0)
                            {
                                var nestedHeaders = fragment.ContinuationHeaders.Select((header, index) =>
                                {
                                    var headerElement = element.CloneFor(element.SectionId, element.Name);
                                    headerElement.Id = element.Id + "-nested-header-" + index;
                                    headerElement.LeftTwips = element.LeftTwips;
                                    headerElement.TopTwips = index * fragment.HeightTwips;
                                    return rowItem with { Element = headerElement, Table = header with { RegionId = rowItem.Table!.RegionId } };
                                }).ToList();
                                var nestedSection = new ReportDesignerSection { Id = source.Section.Id, Name = item.Element.Name + " nested headers",
                                    Kind = source.Section.Kind, HeightTwips = nestedHeaders.Count * fragment.HeightTwips };
                                inlineHeaders.Add(new(regionCursor - 1, regionCursor + fragment.HeightTwips,
                                    new(nestedSection, nestedHeaders, item.Row, [], Owner: item.Owner)));
                            }
                            if (fragment.IsHeader) headerItems.Add(Shift(rowItem, 0, -start, element.WidthTwips + element.LeftTwips));
                            regionCursor += fragment.HeightTwips;
                            breaks.Add(regionCursor);
                        }
                        if (headerItems.Count > 0 && pane.Rows.Count > 0)
                        {
                            var headerSection = new ReportDesignerSection { Id = source.Section.Id, Name = item.Element.Name + " headers",
                                Kind = source.Section.Kind, HeightTwips = pane.Headers.Sum(header => header.HeightTwips) };
                            inlineHeaders.Add(new(start, regionCursor, new(headerSection, headerItems, item.Row, [], Owner: item.Owner),
                                start + headerSection.HeightTwips + pane.Rows[0].HeightTwips, item.Analysis.Definition.RepeatHeaders, headerPeers));
                        }
                    }
                    // Crystal's stacked native grid includes a point below its closing hairline
                    // in the flow extent. The cells and their border stay at their authored coordinates.
                    if (table.NativeCrossTab is { StackedRowGroups: true, Appearance: { } appearance }
                        && appearance.Stroke(14).Width == 0 && regionCursor > top)
                        regionCursor += 20;
                    growth.Add((item.Element.TopTwips + item.Element.HeightTwips,
                        Math.Max(0, regionCursor - item.Element.TopTwips - item.Element.HeightTwips), item.Element.LeftTwips, item.Element.LeftTwips + width, table.NativeCrossTab is not null));
                    continue;
                }
                if (item.Child is not { } child)
                {
                    var measurement = item.Measurement < 0 ? item.Measurement : item.Measurement + measurementOffset;
                    Item positioned;
                    if (top == item.Element.TopTwips)
                        // Nothing above grew: the copy would equal the item (same id, position and content), so the item serves.
                        positioned = measurement == item.Measurement ? item : item with { Measurement = measurement };
                    else
                    {
                        var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name); element.Id = item.Element.Id; element.TopTwips = top; element.LeftTwips = item.Element.LeftTwips;
                        positioned = item with { Element = element, Measurement = measurement };
                    }
                    items.Add(positioned);
                    if (source.Section.RelativePositions)
                        growth.Add((item.Element.TopTwips + item.Element.HeightTwips, top - item.Element.TopTwips + Math.Max(0, ItemHeight(positioned) - item.Element.HeightTwips),
                            item.Element.LeftTwips, item.Element.LeftTwips + item.Element.WidthTwips, false));
                    continue;
                }
                // Expanding a subreport replaces its container with child bands. Keep its
                // authored frame behind those children and use normal Box fragmentation at
                // page breaks; otherwise every border/background on the container is lost.
                ReportDesignerElement? childFrame = null;
                var visual = item.Element.Visual;
                if (new[] { visual.TopLine, visual.BottomLine, visual.LeftLine, visual.RightLine }
                    .Any(line => line is "Single" or "Double" or "Dash" or "Dot")
                    || ReportObjectRenderer.Color(item.Element.BackgroundColor) != "transparent")
                {
                    childFrame = item.Element.CloneFor(item.Element.SectionId, item.Element.Name);
                    // The frame must not reuse the scheduled original subreport item:
                    // that item carries placeholder HTML rather than expanded child text.
                    childFrame.Id = item.Element.Id + "-subreport-frame";
                    childFrame.Kind = "Box";
                    childFrame.TopTwips = top;
                    childFrame.LeftTwips = item.Element.LeftTwips;
                    childFrame.CanGrow = false;
                    items.Add(item with { Element = childFrame, Html = "", TemplateHtml = "", Measurement = -1, Child = null, Analysis = null, Table = null });
                }
                var cursor = top;
                var extent = top;
                var fixedBottom = item.Element.CanGrow ? int.MaxValue : top + item.Element.HeightTwips;
                var clipped = false;
                var active = new Dictionary<Band, (int Start, Band Header)>();
                foreach (var original in child._bands)
                {
                    var context = inlineContexts?.GetValueOrDefault(original) ?? (Page: 1, Count: ExpectedCount(0));
                    if (context.Page == 0) context = (1, ExpectedCount(0));
                    var childWidth = Math.Min(item.Element.WidthTwips, (printableWidth ?? size.ContentWidthTwips) - item.Element.LeftTwips);
                    var nested = Expand(ForPage(original, context.Page, context.Count, objectPage: objectPage), measurementOffset + item.ChildMeasurementOffset, objectPage, childWidth);
                    if (nested.Suppressed) { inlineSections.Add(new(cursor, cursor, original, nested.Section)); continue; }
                    if (nested.Section.SuppressIfBlank && IsBlank(nested)) { inlineSections.Add(new(cursor, cursor, original, nested.Section, true)); continue; }
                    // A no-grow subreport only prints child bands that fit its authored box.
                    // Omitted calculation fields must still update shared variables for the parent.
                    if (clipped || cursor + Height(nested) > fixedBottom)
                    {
                        // On a fresh page Crystal can paint the start of the first overheight child
                        // band inside its fixed frame. It does not grow the parent or run formulas twice.
                        if (item.Element.UseCrystalScreenMetrics && !clipped && cursor < fixedBottom && nested.Section.KeepTogether)
                            foreach (var field in nested.Items.Where(i => !i.Element.IsSuppressed && i.Element.Kind is "Text" or "Field" or "FieldHeading"
                                && !i.Element.CanGrow && i.Element.TopTwips >= 0 && cursor + i.Element.TopTwips + ItemHeight(i) <= fixedBottom))
                                (pageStartInk ??= []).Add(Shift(field with { Html = field.Html.Replace(child._pageToken, _pageToken, StringComparison.Ordinal)
                                    .Replace(child._countToken, _countToken, StringComparison.Ordinal) }, item.Element.LeftTwips, cursor, item.Element.WidthTwips));
                        clipped = true;
                        inlineSections.Add(new(cursor, cursor, original with { EvaluationOnly = true }, nested.Section));
                        continue;
                    }
                    foreach (var previous in active.Keys.Where(k => !original.Repeats.Contains(k)).ToArray())
                    { var header = active[previous]; inlineHeaders.Add(new(header.Start, cursor, header.Header)); active.Remove(previous); }
                    foreach (var repeat in original.Repeats.Where(r => !active.ContainsKey(r)))
                    {
                        var header = Expand(repeat, measurementOffset + item.ChildMeasurementOffset, objectPage, childWidth);
                        active[repeat] = (cursor, header with { Items = header.Items.Select(i => Shift(i, item.Element.LeftTwips, 0, item.Element.WidthTwips)).ToList() });
                    }
                    var nestedArea = child._layout.Areas.GetValueOrDefault(original.Section.Id);
                    // A child starts a fresh report even when its object is inset in the parent.
                    // New Page Before on its first visible band must not split that inset off.
                    if ((nested.Section.NewPageBefore || nestedArea?.NewPageBefore == true) && cursor > top) forcedBreaks.Add(cursor);
                    breaks.Add(cursor);
                    items.AddRange(nested.Items.Select(i => Shift(i with { Html = i.Html.Replace(child._pageToken, _pageToken, StringComparison.Ordinal).Replace(child._countToken, _countToken, StringComparison.Ordinal) }, item.Element.LeftTwips, cursor, item.Element.WidthTwips)));
                    breaks.AddRange(nested.Breaks?.Select(b => cursor + b) ?? []);
                    forcedBreaks.AddRange(nested.ForcedBreaks?.Select(b => cursor + b) ?? []);
                    inlineHeaders.AddRange(nested.InlineHeaders?.Select(h => new InlineHeader(cursor + h.Start, cursor + h.End,
                        h.Header with { Items = h.Header.Items.Select(i => Shift(i, item.Element.LeftTwips, 0, item.Element.WidthTwips)).ToList() },
                        h.KeepThrough == 0 ? 0 : cursor + h.KeepThrough, h.Repeat, h.HorizontalPeers)) ?? []);
                    inlineSections.AddRange(nested.InlineSections?.Select(s => s with { Start = cursor + s.Start, End = cursor + s.End }) ?? []);
                    var end = cursor + Height(nested);
                    inlineSections.Add(new(cursor, end, original, nested.Section, FirstVisible: cursor == top));
                    extent = Math.Max(extent, end);
                    if (!nested.Section.UnderlayFollowingSections) cursor = end;
                    var conditionalAfter = child.Conditions(child._layout.SectionConditions, original.Section.Id, true)
                        .Concat(child.Conditions(child._layout.AreaConditions, original.Section.Id, true))
                        .Any(c => c.Key.Equals("EnableNewPageAfter", StringComparison.OrdinalIgnoreCase));
                    if (!conditionalAfter && (nested.Section.NewPageAfter || nestedArea?.NewPageAfter == true)) forcedBreaks.Add(end);
                    breaks.Add(end);
                }
                foreach (var header in active.Values) inlineHeaders.Add(new(header.Start, cursor, header.Header));
                if (childFrame is not null)
                    childFrame.HeightTwips = Math.Max(item.Element.HeightTwips, extent - top);
                growth.Add((item.Element.TopTwips + item.Element.HeightTwips, Math.Max(0, extent - item.Element.TopTwips - item.Element.HeightTwips),
                    item.Element.LeftTwips, item.Element.LeftTwips + item.Element.WidthTwips, item.Element.UseCrystalScreenMetrics));
            }
            var grown = 0;
            foreach (var g in growth) grown = Math.Max(grown, g.NativeGrid ? Math.Max(0, g.Bottom + g.Amount - source.Section.HeightTwips) : g.Amount);
            var section = new ReportDesignerSection
            {
                Id = source.Section.Id, Name = source.Section.Name, Kind = source.Section.Kind, HeightTwips = source.Section.HeightTwips + grown,
                BackgroundColor = source.Section.BackgroundColor, KeepTogether = source.Section.KeepTogether, NewPageBefore = source.Section.NewPageBefore,
                NewPageAfter = source.Section.NewPageAfter, PrintAtBottomOfPage = source.Section.PrintAtBottomOfPage,
                ResetPageNumberAfter = source.Section.ResetPageNumberAfter, RelativePositions = source.Section.RelativePositions,
                SuppressIfBlank = source.Section.SuppressIfBlank, UnderlayFollowingSections = source.Section.UnderlayFollowingSections
            };
            var expanded = source with { Section = section, Items = items, Breaks = SortedDistinct(breaks), ForcedBreaks = SortedDistinct(forcedBreaks), InlineHeaders = inlineHeaders, InlineSections = inlineSections, PageStartInk = pageStartInk };
            // Only break between child bands when no neighboring object crosses that boundary.
            var safeBreaks = SafeBreaks(expanded);
            int? keptStart = null;
            if (splittableGrids is not null && section.HeightTwips > source.Section.HeightTwips)
            {
                // Keep the authored section and its other objects together. A native grid with
                // Keep Object Together off may continue at a complete row on the next page.
                var minimum = source.Section.HeightTwips;
                foreach (var other in items.Where(i => !i.Element.IsSuppressed && !SpanningLine(i)
                    && !splittableGrids.Contains(i.Element.SourceKey)))
                    minimum = Math.Max(minimum, other.Element.TopTwips + ItemHeight(other));
                var totalHeight = Height(expanded);
                keptStart = safeBreaks.Where(point => point >= minimum && point < totalHeight)
                    .Select(point => (int?)point).FirstOrDefault();
            }
            return expanded with { Breaks = safeBreaks, KeptStartHeight = keptStart };
        }
        static Item Shift(Item item, int left, int top, int width)
        {
            var element = item.Element.CloneFor(item.Element.SectionId, item.Element.Name);
            element.Id = item.Element.Id;
            element.LeftTwips = item.Element.LeftTwips; element.TopTwips = item.Element.TopTwips;
            element.WidthTwips = Math.Min(element.WidthTwips, Math.Max(0, width - element.LeftTwips));
            element.LeftTwips += left; element.TopTwips += top;
            return item with { Element = element, NarrowedByFrame = item.NarrowedByFrame || element.WidthTwips < item.Element.WidthTwips };
        }
        bool SpanningLine(Item item) => item.Owner == this && item.Element.LineEndSection.Length > 0;
        int Height(Band band)
        {
            if (band.Suppressed) return 0;
            return Math.Max(band.Section.HeightTwips, ContentBottom(band));
        }
        // Empty trailing space in a transparent underlay paints nothing on a continuation page.
        // The designed height still controls foreground flow and the matching footer.
        int UnderlayInkHeight(Band band) => band.Section.BackgroundColor == "transparent" && !band.Items.Any(SpanningLine)
            ? ContentBottom(band) : Height(band);
        int ContentBottom(Band band)
        {
            var bottom = 0;
            foreach (var item in band.Items)
                if (!item.Element.IsSuppressed && !SpanningLine(item)) bottom = Math.Max(bottom, item.Element.TopTwips + Math.Max(15, ItemHeight(item)));
            return bottom;
        }
        int ItemHeight(Item item)
        {
            if (item.Element.IsSuppressed) return 0;
            if (!item.Element.CanGrow || item.Element.HasRotatedText) return item.Element.HeightTwips;
            var metric = TextMetrics(item);
            if (metric is not null && item.Element.CanGrow) return Math.Max(item.Element.HeightTwips, metric.Height);
            if (!item.Element.CanGrow || item.Measurement == -1) return item.Element.HeightTwips;
            if (item.Measurement >= 0 && measuredHeights is not null && measuredHeights.Count == Measurements.Count && measuredHeights[item.Measurement] is >= 0 and <= 10000000)
                return Math.Max(item.Element.HeightTwips, measuredHeights[item.Measurement]);
            var text = System.Text.RegularExpressions.Regex.Replace(item.Html, "<[^>]+>", "");
            var font = Math.Max(item.Element.FontSize, item.Element.Visual.Runs.Select(run => run.FontSize).DefaultIfEmpty(1).Max());
            var capacity = Math.Max(1, (int)(item.Element.WidthTwips / Math.Max(1, font * 10)));
            var lines = text.Split('\n').Sum(line => Math.Max(1, Math.Ceiling(line.Length / (double)capacity)));
            if (ReportObjectRenderer.TextLineLimit(item.Element) is > 0 and var limit) lines = Math.Min(lines, limit);
            return Math.Max(item.Element.HeightTwips, (int)Math.Ceiling(lines * (double)font * 23));
        }
        List<int> SafeBreaks(Band band)
        {
            var candidates = new List<int>((band.Breaks?.Count ?? 0) + band.Items.Count * 2 + 8);
            if (band.Breaks is not null) candidates.AddRange(band.Breaks);
            var blocked = new List<(int Start, int End)>(band.Items.Count + 4);
            foreach (var header in band.InlineHeaders ?? [])
                if (header.KeepThrough > header.Start) blocked.Add((header.Start, header.KeepThrough));
            foreach (var item in band.Items)
            {
                _paginationCancellationToken.ThrowIfCancellationRequested();
                if (item.Element.IsSuppressed) continue;
                var top = item.Element.TopTwips;
                var height = ItemHeight(item);
                // A fixed object that fits a page blocks its whole extent below, so its line bounds can add no break
                // point; only growing (or page-tall) text needs its metrics here.
                var metric = item.Element.CanGrow || height > size.ContentHeightTwips ? TextMetrics(item) : null;
                candidates.Add(top); candidates.Add(top + height);
                if (metric is not null)
                    foreach (var line in metric.Lines.Where(l => l.Bottom <= height))
                    { candidates.Add(top + line.Top); candidates.Add(top + line.Bottom); }
                if (item.Element.Kind is "Line" or "Box") continue;
                if (!item.Element.CanGrow && height <= size.ContentHeightTwips || metric is null)
                {
                    if (height > 0) blocked.Add((top, top + height));
                }
                else
                    foreach (var line in metric.Lines)
                    {
                        var start = Math.Max(0, line.Top);
                        var end = Math.Min(height, line.Bottom);
                        if (end > start) blocked.Add((top + start, top + end));
                    }
            }
            // Open intervals express the same rules as SafeAt. Sweep once instead of
            // rescanning every object (and its font metrics) at every candidate break.
            blocked.Sort((left, right) => left.Start.CompareTo(right.Start));
            var result = new List<int>(candidates.Count);
            var index = 0;
            var blockedUntil = int.MinValue;
            candidates.Sort();
            var previous = int.MinValue;
            foreach (var point in candidates)
            {
                if (point <= 0 || point == previous) continue;
                previous = point;
                _paginationCancellationToken.ThrowIfCancellationRequested();
                while (index < blocked.Count && blocked[index].Start < point)
                    blockedUntil = Math.Max(blockedUntil, blocked[index++].End);
                if (blockedUntil <= point) result.Add(point);
            }
            return result;
        }
        bool SafeAt(Band band, int point) => !(band.InlineHeaders?.Any(header => header.Start < point && point < header.KeepThrough) ?? false)
            && band.Items.All(item =>
            {
                var relative = point - item.Element.TopTwips;
                if (item.Element.IsSuppressed || relative <= 0 || relative >= ItemHeight(item) || item.Element.Kind is "Line" or "Box") return true;
                if (!item.Element.CanGrow && ItemHeight(item) <= size.ContentHeightTwips) return false;
                return TextMetrics(item) is { } metric && metric.Lines.All(line => relative <= line.Top || relative >= line.Bottom);
            });
        bool CanContinueAt(Band band, int point) => band.Items.All(item =>
        {
            var relative = point - item.Element.TopTwips;
            return relative <= 0 || relative >= ItemHeight(item) || item.Element.Kind is "Line" or "Box"
                || item.Element.CanGrow && TextMetrics(item) is { } metric && metric.Lines.All(l => l.Start >= 0 && l.End >= l.Start);
        });
        static string Px(int twips) => ReportObjectRenderer.Number(twips / 15d);
        // Distinct().Order().ToList() without the iterators: ascending, each value once.
        static List<int> SortedDistinct(List<int> values)
        {
            if (values.Count == 0) return [];
            values.Sort();
            var result = new List<int>(values.Count) { values[0] };
            for (var index = 1; index < values.Count; index++) if (values[index] != values[index - 1]) result.Add(values[index]);
            return result;
        }
    }
}
