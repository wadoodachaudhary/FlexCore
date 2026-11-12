using Fx.ControlKit.Charts;

namespace Fx.ControlKit.Reports.NativeCrystal;

internal static partial class CrystalReportContentsParser
{
    // Read analytical bindings and the fixed-cell geometry of simple legacy cross-tabs.
    private static ReportAnalysisDefinition ReadAnalysisBindings(TslvArchiveReader reader,
        FieldReferenceTable fields, CrystalDataDefinitionModel data, CrystalReportObjectModel owner)
    {
        var objectType = reader.CurrentRecord!.Type;
        var definition = new ReportAnalysisDefinition();
        var groups = new List<string>();
        var boundSummaries = new HashSet<CrystalSummaryFieldModel>();
        var containers = new Stack<int>();
        var styleFound = false;
        bool? savedThreeDimensionalLegend = null;
        var categoryLegend = false;
        ReportChartRightLegend? rightLegend = null;
        bool? savedLegendEnabled = null;
        bool isPerspectivePie = false;
        ReportPerspectiveBarStyle? perspectiveStyle = null;
        var crossTabChart = false;
        var bindingFound = false;
        var expectedMeasures = -1;
        var expectedGroups = -1;
        TslvArchiveReader? categoryOptions = null;
        var sortLists = 0;
        var chartMeasureFlags = new List<int>();
        var complete = false;
        var nativeCells = new List<(int Axis, int Column, int Row, int Group, int PaddingX, int PaddingY, CrystalReportObjectModel Cell)>();
        var axisLevels = new Dictionary<int, int>();
        var groupGeometry = new Dictionary<(int Axis, int Level), (int Width, int Height)>();
        var groupFills = new Dictionary<int, List<string>>();
        var totalStyles = new Dictionary<int, CrystalReportObjectModel>();
        var rowSubtotalLabels = new Dictionary<int, CrystalReportObjectModel>();
        var cellColumn = 0;
        var cellRow = 0;
        CrystalReportObjectModel? nativeCell = null;
        // Legacy GridCell defaults apply when no explicit geometry record is saved.
        var paddingX = 100;
        var paddingY = 100;
        (int X, int Y)? explicitPadding = null;
        var uniformPadding = true;
        CrystalGroupModel? pendingGroup = null;
        ReportAnalysisGroup? analyticalGroup = null;
        var totalLabels = new Dictionary<int, string>();
        var summaryLabels = new List<CrystalReportObjectModel>();
        var columnCaptions = new Dictionary<int, CrystalReportObjectModel>();
        var gridLines = new List<string>();
        CrystalGroupModel? ranked = null;
        var drawGrid = true;
        var showCellMargins = true;
        var suppressColumnTotals = false;
        var suppressRowTotals = false;
        var rowSubtotals = new List<int>(); var columnSubtotals = new List<int>();
        if (objectType == 185)
        {
            var probe = reader.Fork();
            probe.LoadNextRecord(184, 1792, 186);
            _ = ReadReportObjectBase(probe, "", "", 186);
            drawGrid = probe.LoadBoolean();
            showCellMargins = probe.LoadBoolean();
            // GridObject's stored ID connects CrossTabValueGridDefinition charts to this exact grid.
            var identity = probe.Fork();
            try
            {
                _ = ReadCrystalColor(identity, CrystalColorModel.TransparentWhite());
                _ = identity.LoadInt32(); _ = identity.LoadInt32();
                _ = identity.LoadInt32Compressed(); _ = identity.LoadInt32Compressed();
                owner.GridLinkId = identity.LoadUInt8();
            }
            catch (Exception identityError) when (identityError is InvalidDataException or EndOfStreamException) { /* A grid without a link ID can still render on its own. */ }
            if (!showCellMargins) paddingX = paddingY = 0;
            probe.SkipRestOfRecord();
            suppressColumnTotals = probe.LoadBoolean();
            suppressRowTotals = probe.LoadBoolean();
        }
        reader.SkipRestOfRecord();
        for (var records = 0; records < 100000 && reader.BytesLeftInRecord > 0; records++)
        {
            var before = reader.Fork();
            var record = reader.LoadAnyRecord();
            if (objectType == 185)
            {
                if (record.Type is 206 or 210)
                {
                    axisLevels[record.Type == 206 ? 207 : 211] = axisLevels.GetValueOrDefault(record.Type == 206 ? 207 : 211, -1) + 1;
                    var p = reader.Fork(); p.LoadNextRecord(record.Type == 206 ? 204 : 205, 1792, record.Type + 1);
                    p.LoadNextRecord(203, 1792, record.Type + 1);
                    var color = Css(ReadCrystalColor(p, CrystalColorModel.TransparentWhite()));
                    var groupWidth = p.LoadInt32Compressed(); var groupHeight = p.LoadInt32Compressed();
                    var groupAxis = record.Type == 206 ? 207 : 211;
                    groupGeometry[(groupAxis, axisLevels[groupAxis])] = (groupWidth, groupHeight);
                    if (!groupFills.TryGetValue(record.Type, out var fills)) groupFills[record.Type] = fills = [];
                    fills.Add(color);
                }
                if (record.Type == 215)
                {
                    var p = reader.Fork(); p.LoadNextRecord(214, 1792, 216);
                    paddingX = p.LoadInt32(); paddingY = p.LoadInt32();
                    if (explicitPadding is { } previous && previous != (paddingX, paddingY)) uniformPadding = false;
                    explicitPadding = (paddingX, paddingY);
                    for (var part = 0; part < 4; part++) _ = p.LoadInt32Compressed();
                    cellColumn = p.LoadUInt16(); cellRow = p.LoadUInt16();
                }
                if (record.Type == 167)
                {
                    nativeCell = null;
                    var axis = containers.Contains(211) ? 211 : containers.Contains(207) ? 207 : 0;
                    // Each column group stores its subtotal caption before any summary labels; the caption heads no summary.
                    var groupCaption = axis == 207 && axisLevels.GetValueOrDefault(207) > 0 && !columnCaptions.ContainsKey(axisLevels[207]);
                    if (axis != 0)
                    {
                        var p = reader.Fork(); p.LoadNextRecord(165, 1792, 168);
                        var label = ReadReportObjectBase(p, "TextObject", "Text", 168);
                        _ = p.LoadBoolean(); _ = p.LoadBoolean(); _ = p.LoadBoolean();
                        label.MaxNumberOfLines = p.LoadUInt16();
                        var paragraphs = p.LoadUInt16();
                        p.SkipRestOfRecord(); p.SkipRestOfRecord();
                        ReadCommonReportObjectRecords(p, label);
                        label.Text = ReadTextParagraphs(p, paragraphs, fields, data, label);
                        if (groupCaption) columnCaptions[axisLevels[207]] = label;
                        else if (!totalStyles.ContainsKey(axis))
                        {
                            // A caption's trailing paragraph break adds no line (sample "Field in Crosstab Text object").
                            totalLabels[axis] = label.Text.TrimEnd('\n');
                            totalStyles[axis] = label;
                        }
                        else if (axis == 211 && axisLevels[211] > 0 && label.Height > 0)
                            rowSubtotalLabels.TryAdd(axisLevels[211], label);
                        // After its total caption the row axis's root container carries the summarized fields' labels ("Show Labels").
                        else if (axis == 211 && axisLevels.GetValueOrDefault(211) == 0 && label.Height > 0) summaryLabels.Add(label);
                        else if (axis == 207 && label.Height > 0) nativeCells.Add((axis, cellColumn, cellRow, axisLevels.GetValueOrDefault(axis), 0, 0, label));
                    }
                }
                if (record.Type == 325)
                {
                    var probe = reader.Fork();
                    var width = probe.LoadInt32(); var style = probe.LoadEnum();
                    var color = ReadCrystalColor(probe, CrystalColorModel.Black());
                    var enabled = probe.LoadBoolean();
                    gridLines.Add($"{(drawGrid && enabled && style != 0 ? width : -1)}:{Css(color)}:{LineStyleName(style)}");
                }
                if (record.Type == 237 && nativeCell is not null && !nativeCell.HasFont)
                    nativeCell.Border = ReadObjectBorder(reader.Fork());
            }
            if (record.Type == objectType + 1) { complete = true; break; }
            if (record.Type is 206 or 210 or 215) containers.Push(record.Type + 1);
            if (objectType == 185 && record.Type == 253 && nativeCell is not null && !nativeCell.HasFont)
                nativeCell.Format = ReadObjectFormat(reader.Fork());
            if (objectType == 185 && record.Type == 8 && nativeCell is not null && !nativeCell.HasFont)
            {
                nativeCell.Font = ReadLogicalFont(reader.Fork()); nativeCell.HasFont = true;
            }
            if (record.Type == 289 && objectType == 180)
            {
                var nativeType = reader.LoadEnum();
                var subtype = reader.LoadEnum();
                definition.ChartType = (nativeType, subtype) switch
                {
                    (0, 0 or 3) => ChartType.Bar,
                    (0, 1 or 4) => ChartType.StackedBar,
                    (0, 2 or 5) => ChartType.StackedBar100,
                    (2, 20 or 22) => ChartType.Area,
                    (1, 10 or 13 or 1000) => ChartType.Line,
                    (3, 30 or 31) => ChartType.Pie,
                    (4, 40 or 41 or 42) => ChartType.Donut,
                    (14, 140) => ChartType.Funnel,
                    (12, 120) => ChartType.Gauge,
                    (7, 70) => ChartType.Scatter,
                    (8, 80) => ChartType.Radar,
                    (9, 90) => ChartType.Bubble,
                    (6, 60) => ChartType.Surface,
                    (5, 50) => ChartType.Bar,
                    (15, 150) => ChartType.Histogram,
                    (10, 100) => ChartType.HighLow,
                    (10, 101) => ChartType.Ohlc,
                    (11, 114) => ChartType.Line,
                    _ => throw new NotSupportedException($"Native chart type {nativeType}, subtype {subtype} has no exact report adapter.")
                };
                definition.Title = reader.LoadString() ?? "";
                definition.ChartThreeDimensional = nativeType == 0 && subtype is 3 or 4 or 5;
                isPerspectivePie = nativeType == 3 && subtype == 31;
                if (nativeType == 1 && subtype == 1000) definition.LineStyle = new("0.##", true, true);
                if (definition.ChartThreeDimensional || nativeType is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 10 or 11 or 12 or 15)
                {
                    // The legacy graph style stores eight captions and eight font families before its legend flag.
                    // Probe independently so shorter style records retain their existing defaults.
                    var probe = reader.Fork();
                    try
                    {
                        var captions = Enumerable.Range(0,7).Select(_ => probe.LoadString() ?? "").ToArray();
                        if (captions.Any(caption => caption.Length > 4096)) throw new InvalidDataException("Invalid graph caption length.");
                        for (var font = 0; font < 8; font++)
                            if ((probe.LoadString()?.Length ?? 0) > 256) throw new InvalidDataException("Invalid graph font-family length.");
                        var visible = probe.LoadUInt16();
                        var placement = probe.LoadEnum();
                        if (visible > 1 || placement is < 0 or > 3) throw new InvalidDataException("Unknown graph legend style.");
                        if (definition.ChartThreeDimensional) savedThreeDimensionalLegend = visible == 1;
                        else categoryLegend = nativeType == 0 && visible == 1 && placement == 3;
                        savedLegendEnabled = visible == 1;
                        if (nativeType is 5 or 6 or 7 or 8 or 10 or 11 or 15 || (nativeType, subtype) is (4, 41 or 42) or (2, 22))
                            definition.SpatialStyle = new() { Family = nativeType, CategoryTitle = captions[4], ValueTitle = captions[5],
                                DepthTitle = nativeType is 5 or 6 ? captions[6] : "", ProportionalRings = subtype == 42 };
                        if (nativeType == 1 && subtype == 13 && groups.Count == 2 && visible == 0)
                        {
                            var line = probe.Fork();
                            var vertical = line.LoadBoolean();
                            _ = line.LoadEnum(); // bar size
                            var markerSize = line.LoadEnum();
                            var markerShape = line.LoadEnum();
                            if (markerSize > 0 && markerShape == 9) // automatic series markers
                                definition.SpatialStyle = new() { Family = 1, Horizontal = !vertical };
                        }
                        if (nativeType == 12 && visible == 0 && definition.Measures.Count > 1)
                            definition.SpatialStyle = new() { Family = 12 };
                        var readBarDrawing = nativeType == 0 && (subtype == 0 && visible == 1 && placement is 0 or 1
                            || subtype == 1 && visible == 0);
                        if (nativeType is 6 or 12 || definition.SpatialStyle?.Family == 1 || definition.ChartThreeDimensional || readBarDrawing)
                        {
                            owner.HiddenGaugeLegend = nativeType == 12 && visible == 0;
                            var saved = probe.Fork();
                            _ = saved.LoadBoolean();
                            for (var option = 0; option < 7; option++) _ = saved.LoadEnum();
                            for (var bound = 0; bound < 6; bound++) _ = saved.LoadDouble();
                            for (var mode = 0; mode < 9; mode++) _ = saved.LoadEnum();
                            for (var count = 0; count < 3; count++) _ = saved.LoadInt32();
                            for (var format = 0; format < 3; format++) _ = saved.LoadEnum();
                            owner.GraphDrawingId = saved.LoadInt32();
                        }
                        if (nativeType == 3)
                        {
                            var pie = probe.Fork();
                            _ = pie.LoadBoolean(); _ = pie.LoadEnum(); _ = pie.LoadEnum();
                            var detach = pie.LoadEnum();
                            if (detach is >= 1 and <= 3) definition.SpatialStyle = new() { Family = 3, PieDetachment = detach };
                            if (isPerspectivePie && detach == 0 && pie.BytesLeftInRecord >= 82)
                            {
                                // Marker and grid enums (6), six axis bounds (48), formats and modes (9),
                                // three division counts (12), then color, data point and data format (3).
                                pie.SkipBytes(78);
                                owner.PerspectivePieDrawingId = pie.LoadInt32();
                            }
                        }

                        if (definition.ChartThreeDimensional)
                        {
                            var axes = probe.Fork();
                            _ = axes.LoadBoolean();
                            for (var i = 0; i < 3; i++) _ = axes.LoadEnum();
                            var categoryGrid = axes.LoadEnum() != 0;
                            _ = axes.LoadEnum();
                            var valueGrid = axes.LoadEnum() != 0;
                            perspectiveStyle = new(captions[4].Trim(), captions[5].Trim(), categoryGrid, valueGrid);
                        }
                        if (nativeType is 0 or 2 && subtype is 0 or 1 or 2 or 20 || definition.LineStyle is not null)
                        {
                            var vertical = probe.LoadBoolean(); _ = probe.LoadEnum(); // series orientation and bar size
                            var markerSize = probe.LoadEnum(); _ = probe.LoadEnum(); // marker size/shape
                            var grids = Enumerable.Range(0,4).Select(_ => probe.LoadEnum()).ToArray();
                            if (vertical && nativeType == 0 && visible == 1 && placement is 0 or 1)
                                rightLegend = new(captions[4], captions[5], grids[0] != 0, grids[2] != 0) { OnLeft = placement == 1 };
                            if (vertical && nativeType == 0 && subtype == 1 && visible == 0 && (captions[4].Length > 0 || captions[5].Length > 0))
                                rightLegend = new(captions[4], captions[5], grids[0] != 0, grids[2] != 0);
                            var bounds = Enumerable.Range(0,6).Select(_ => probe.LoadDouble()).ToArray();
                            var formats = Enumerable.Range(0,3).Select(_ => probe.LoadEnum()).ToArray();
                            var automatic = Enumerable.Range(0,3).Select(_ => probe.LoadEnum()).ToArray();
                            if (definition.LineStyle is not null) definition.LineStyle = new(formats[0] == 3 ? "C0" : formats[0] == 4 ? "C2" : "0.##", markerSize > 0, grids[2] != 0);
                            _ = probe.LoadEnum(); _ = probe.LoadEnum(); _ = probe.LoadEnum();
                            var divisions = probe.LoadInt32();
                            // The fixed value-axis bounds apply to ordinary numeric formats as well as currency.
                            var tickFormat = formats[0] switch { 0 => "F0", 1 => "F1", 2 => "F2", 3 => "C0", 4 => "C2", _ => null };
                            if (definition.LineStyle is null && subtype != 1 && automatic[0] == 0 && tickFormat is not null
                                && double.IsFinite(bounds[0]) && double.IsFinite(bounds[1]) && bounds[1] > bounds[0] && divisions is >= 1 and < 60)
                                definition.PrintAxis = new(bounds[0], bounds[1], divisions+1, tickFormat,
                                    captions[4],captions[5],grids[2]!=0);
                        }
                    }
                    catch (Exception legendError) when (legendError is InvalidDataException or EndOfStreamException)
                    { /* Older graph records keep the editable default legend policy. */ }
                }
                styleFound = true;
            }
            else if (record.Type == 294 && objectType == 180)
            {
                expectedMeasures = Count(reader.LoadInt32(), 64);
                expectedGroups = Count(reader.LoadInt32(), 2);
                if (reader.BytesLeftInRecord > 0)
                {
                    definition.EachRecord = reader.LoadBoolean();
                    _ = reader.LoadBoolean(); // Obsolete saved-data flag.
                }
                if (reader.BytesLeftInRecord > 0)
                    for (var flags = Count(reader.LoadUInt16(), 128); flags > 0; flags--)
                        chartMeasureFlags.Add(reader.LoadInt32());
                if (reader.BytesLeftInRecord > 0 && reader.LoadBoolean())
                {
                    if (!definition.EachRecord && expectedGroups == 0 && expectedMeasures > 0) definition.SummariesAsCategories = true;
                    else throw new NotSupportedException("Native alternate chart evaluation requires an explicit adapter.");
                }
                if (reader.BytesLeftInRecord > 0) definition.CategoryField = ReadFieldReference(reader, fields, data);
                owner.ChartCategoryField = definition.CategoryField;
                bindingFound = true;
            }
            else if (record.Type == 287 && objectType == 180)
            {
                definition.ReportGroupChart = true;
                var firstGroup = reader.LoadUInt16();
                var count = Count(reader.LoadInt32(), 64);
                for (var index = 0; index < count; index++) AddSummary(ReadFieldReference(reader, fields, data));
                var groupCount = Count(reader.LoadInt32(), 2);
                if (firstGroup + groupCount > data.Groups.Count)
                    throw new NotSupportedException("The chart's report-group references are unavailable at this location.");
                groups.AddRange(data.Groups.Skip(firstGroup).Take(groupCount).Select(group => group.ConditionField));
                bindingFound = true;
            }
            else if (record.Type == 127 && objectType == 180)
            {
                AddMeasure(ReadSummaryField(before, fields));
            }
            else if (record.Type == 229)
            {
                var field = ReadFieldReference(reader, fields, data);
                var grouping = reader.LoadEnum();
                var direction = reader.LoadEnum();
                pendingGroup = new() { ConditionField = field, Condition = grouping, Direction = direction };
                var hasRanges = objectType == 185 && ReadGroupOptions(reader, pendingGroup, data, fields);
                if (objectType == 180 && groups.Count == 0 && reader.BytesLeftInRecord > 0)
                    categoryOptions = reader.Fork();
                analyticalGroup = null;
                if (!string.IsNullOrWhiteSpace(field))
                {
                    if (grouping is >= 1 and <= 7) definition.GroupKinds[field] = grouping;
                    if ((grouping is < 0 or > 7) || direction is < 0 or > 3 || objectType == 180 && direction > 1)
                        throw new NotSupportedException("Native analytical date/Top N grouping is not supported.");
                    if (pendingGroup.TopNCount > 0 && objectType == 185 && ranked is null && containers.Contains(211)
                        && direction is 0 or 1 && !pendingGroup.TopNHasFormula && pendingGroup.TopNPercentage == 0
                        && pendingGroup.ParentIdField.Length == 0)
                        ranked = pendingGroup;
                    else if (pendingGroup.TopNCount > 0 || pendingGroup.TopNPercentage > 0 || pendingGroup.ParentIdField.Length > 0)
                        throw new NotSupportedException("Native analytical Top N/hierarchical grouping is not supported.");
                    if (direction == 1) definition.DescendingFields.Add(field);
                    if (objectType == 185 && (direction >= 2 || hasRanges))
                    {
                        analyticalGroup = new() { Field = field, OriginalOrder = direction == 2,
                            OthersName = pendingGroup.SpecifiedOthersName };
                        definition.Groups.Add(analyticalGroup);
                    }
                    if (objectType == 185 && analyticalGroup is null && pendingGroup.NameFormula is { } nameFormula)
                    {
                        var caption = new ReportGridFormula(nameFormula.FormulaText, nameFormula.Syntax == 1 ? "Basic" : "Crystal");
                        // Only replay names determined by the group key; unsupported names keep the existing raw label.
                        try
                        {
                            if (ReportAnalysisDefinition.IsAxisLabelFormula(caption.Compile(), field))
                                definition.Groups.Add(new() { Field = field, NameFormula = caption });
                        }
                        catch (Exception e) when (e is InvalidDataException or NotSupportedException) { }
                    }
                    if (objectType == 180) groups.Add(field);
                    else if (containers.Contains(207)) definition.ColumnFields.Add(field);
                    else if (containers.Contains(211)) definition.RowFields.Add(field);
                    if (objectType == 185 && pendingGroup.SuppressSubtotal)
                    {
                        if (containers.Contains(207)) columnSubtotals.Add(definition.ColumnFields.Count);
                        else if (containers.Contains(211)) rowSubtotals.Add(definition.RowFields.Count);
                    }
                }
            }
            else if (record.Type == 231 && analyticalGroup is not null)
            {
                analyticalGroup.UnspecifiedValues = reader.LoadEnum();
                if (analyticalGroup.UnspecifiedValues is < 0 or > 2)
                    throw new NotSupportedException("Unknown analytical unspecified-values policy.");
            }
            else if (record.Type == 233 && analyticalGroup is not null)
                analyticalGroup.SpecifiedGroups.Add(new() { Name = reader.LoadString() ?? "", Expression = reader.LoadString() ?? "" });
            else if (record.Type is 159 or 161 && objectType == 185 && containers.Contains(216))
            {
                var probe = reader.Fork();
                // FieldObject in a GridObject is wrapped as 161 -> 159 -> 158.
                // Summary bindings and fixed-cell geometry are independent of the drawing records.
                if (record.Type == 161) probe.LoadNextRecord(159, 1792, 162);
                _ = ReadReportObjectBase(probe, "FieldObject", "Field", record.Type == 161 ? 162 : 160);
                AddSummary(ReadFieldReference(probe, fields, data));
                CaptureCell(reader, record.Type, 216);
                bindingFound = true;
            }
            else if (record.Type is 159 or 161 && objectType == 185 && (containers.Contains(207) || containers.Contains(211)))
                CaptureCell(reader, record.Type, containers.Contains(211) ? 211 : 207);
            else if (record.Type == 382)
            {
                var count = reader.BytesLeftInRecord == 0 ? 0 : Count(reader.LoadInt32(), 128);
                for (var index = 0; index < count; index++)
                {
                    reader.LoadNextRecord(384, 1792, 383);
                    var name = reader.LoadString() ?? "";
                    var level = reader.LoadUInt16();
                    var isRow = reader.LoadBoolean();
                    var insertAfter = reader.LoadBoolean();
                    var memberValue = ReadGridFormula();
                    var header = ReadGridFormula();
                    var insertion = ReadGridFormula();
                    var values = Enumerable.Range(0, Count(reader.LoadUInt16(), 64)).Select(_ => ReadGridFormula()).ToArray();
                    definition.CalculatedMembers.Add(new(name, level, isRow, insertAfter, memberValue, header, insertion, values));
                    reader.SkipRestOfRecord();
                    reader.LoadNextRecord(385, 1792, 383);
                    reader.SkipRestOfRecord();
                }
            }
            else if (record.Type == 290 && objectType == 180)
            {
                var id = reader.LoadUInt8();
                var orientation = reader.LoadEnum();
                var measure = reader.LoadUInt16();
                var secondGrouping = reader.LoadBoolean();
                var hasTarget = reader.BytesLeftInRecord == 0 || reader.LoadBoolean();
                var grids = fields.AnalyticalObjects.Where(item => item.Item1.Kind == "CrossTab" && item.Item1.GridLinkId == id)
                    .Select(item => item.Item1).Take(2).ToArray();
                if (!hasTarget || grids.Length != 1 || grids[0].Analysis is not {} grid || orientation is < 0 or > 3
                    || measure >= grid.Measures.Count || grid.Groups.Any(rule => rule.NameFormula is null) || grid.RowRanking is not null
                    || grid.CalculatedMembers.Count != 0 || grid.GroupScope.Length != 0)
                    throw new NotSupportedException("Cross-tab chart target, grouping or summary cannot be resolved.");
                // Orientations 2 and 3 traverse opposite axes. The single-axis variants may use two levels on the same axis.
                var rowsFirst = orientation is 0 or 2;
                var primary = rowsFirst ? grid.RowFields : grid.ColumnFields;
                var opposite = rowsFirst ? grid.ColumnFields : grid.RowFields;
                if (primary.Count == 0) throw new NotSupportedException("The chart's selected cross-tab axis has no group.");
                AddAxis(primary[0]);
                if (secondGrouping)
                {
                    definition.CrossTabSeries = true;
                    if (orientation < 2 && primary.Count > 1) AddAxis(primary[1]);
                    else if (opposite.Count > 0) AddAxis(opposite[0]);
                    else throw new NotSupportedException("The chart's second cross-tab grouping cannot be resolved.");
                }
                void AddAxis(string field)
                {
                    groups.Add(field);
                    if (grid.GroupKinds.TryGetValue(field, out var kind)) definition.GroupKinds[field] = kind;
                    if (grid.DescendingFields.Contains(field)) definition.DescendingFields.Add(field);
                    if (grid.Groups.FirstOrDefault(rule => rule.Field.Equals(field, StringComparison.OrdinalIgnoreCase)) is { } label) definition.Groups.Add(label.Clone());
                }
                // The chart plots the summary's values; the column-total percentage is the grid's own.
                var charted = grid.Measures[measure].Clone();
                charted.PercentOfColumnTotal = null;
                definition.Measures.Add(charted);
                bindingFound = crossTabChart = true;
            }
            else if (record.Type == 292)
                throw new NotSupportedException("OLAP chart bindings are retained but not evaluated.");
            else if (record.Type == 319 && objectType == 180)
            {
                var count = reader.LoadUInt16();
                if (count > 0)
                {
                    if (sortLists != 0 || count != 1 || reader.LoadInt16() != 0 || categoryOptions is null)
                        throw new NotSupportedException("Unsupported analytical ranking rule.");
                    var options = new CrystalGroupModel();
                    ReadGroupOptions(categoryOptions, options, data, fields);
                    if (!options.DiscardOthers || options.WithTies || options.TopNHasFormula || options.TopNPercentage > 0 || options.TopNCount <= 0)
                        throw new NotSupportedException("Unsupported analytical Top N options.");
                    reader.SkipRestOfRecord();
                    var rule = ReadSortField(reader, fields, "ChartSortField");
                    if (rule.Direction != 1) throw new NotSupportedException("Unsupported analytical ranking direction.");
                    reader.LoadNextRecord(320, 1792, objectType + 1);
                    definition.RankedCategoryCount = options.TopNCount;
                }
                sortLists++;
            }
            else if (record.Type == 319 && reader.LoadUInt16() != 0)
                throw new NotSupportedException("Native analytical sort rules require an explicit sort adapter.");
            reader.SkipRestOfRecord();
            if (containers.TryPeek(out var end) && end == record.Type) containers.Pop();
        }
        if (!complete) throw new InvalidDataException("Native analytical binding records ended before the object terminator.");
        definition.RowFields = definition.RowFields.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        definition.ColumnFields = definition.ColumnFields.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (objectType == 180)
        {
            if (!styleFound || !bindingFound || groups.Count > 2 || !definition.EachRecord && groups.Count == 0 && !definition.SummariesAsCategories
                || expectedMeasures >= 0 && expectedMeasures != definition.Measures.Count
                || expectedGroups >= 0 && expectedGroups != groups.Count)
                throw new InvalidDataException("Incomplete native chart data/style binding records.");
            if (groups.Count > 0) definition.CategoryField = groups[0];
            // The label-only marker needs the category even when the bindings below are refused.
            owner.ChartCategoryField = definition.CategoryField;
            definition.SortCategories = !definition.EachRecord;
            definition.SeriesField = groups.Count > 1 ? groups[1] : "";
            if (!definition.ChartThreeDimensional && definition.ReportGroupChart && groups.Count == 1 && definition.Measures.Count == 1
                && definition.ChartType is ChartType.Bar or ChartType.StackedBar or ChartType.StackedBar100
                    or ChartType.HorizontalBar or ChartType.Line or ChartType.Area)
                definition.ShowLegend = false;
            definition.PerspectivePie = isPerspectivePie;
            // Existing simple plots keep their established renderer when no saved axis styling is present.
            if (definition.SpatialStyle is { Family:7 or 8, CategoryTitle.Length:0, ValueTitle.Length:0 }) definition.SpatialStyle=null;
            if (definition.SpatialStyle is { Family:10 } && definition.ChartType == ChartType.Ohlc && definition.Measures.Count == 4) definition.SpatialStyle=null;
            if (definition.ChartType == ChartType.Gauge && groups.Count == 1 && definition.Measures.Count == 1 && savedLegendEnabled == true)
                definition.SpatialStyle = new() { Family = 12 };
            if (definition.SpatialStyle is not null) definition.ShowLegend = savedLegendEnabled ?? definition.ShowLegend;
            if (definition.PrintAxis is not null || definition.SummariesAsCategories || isPerspectivePie || definition.LineStyle is not null
                || (definition.ChartType == ChartType.Line && !definition.ReportGroupChart) || definition.ChartType == ChartType.Ohlc)
                definition.ShowLegend = savedLegendEnabled ?? definition.ShowLegend;
            if (definition.SummariesAsCategories)
            {
                definition.PerspectivePie = isPerspectivePie;
                foreach (var measure in definition.Measures)
                    if (string.IsNullOrEmpty(measure.Caption))
                        measure.Caption = measure.Aggregate + " of " + measure.Field.Trim('{','}');
            }
            if (savedThreeDimensionalLegend is { } savedLegend) definition.ShowLegend = savedLegend;
            // A one-series data chart can key each category (one swatch per bar).
            // Report-group charts and charts with fixed scales have their own presentation rules.
            if (rightLegend is not null && definition.ReportGroupChart && definition.ChartType == ChartType.StackedBar
                && groups.Count == 1 && definition.Measures.Count == 1 && savedLegendEnabled == false)
            {
                definition.RightCategoryLegend = rightLegend;
                definition.ChartCategoryLegend = true;
            }
            if (rightLegend is not null && groups.Count == 1 && definition.Measures.Count == 1
                && (!definition.ReportGroupChart || definition.ChartType == ChartType.Bar && owner.GraphDrawingId.HasValue)
                && definition.PrintAxis is null && !definition.SummariesAsCategories)
            {
                definition.RightCategoryLegend = rightLegend with { RotateCategories = crossTabChart };
                categoryLegend = true;
            }
            if (definition.ChartType == ChartType.StackedBar100 && rightLegend is not null)
                definition.RightCategoryLegend = rightLegend with { KeyBySeries = true };
            if (definition.HasFixedSeriesBarAxis)
            {
                definition.RightCategoryLegend = rightLegend;
                categoryLegend = false;
                // Crystal keys the series by their summary captions, independent of the numeric axis format.
                foreach (var measure in definition.Measures.Where(m => m.Caption.Length == 0))
                {
                    var field = measure.Field.Trim('{', '}').Split('.').Last();
                    measure.Caption = (measure.Aggregate == ReportAggregateType.Average ? "Avg" : measure.Aggregate.ToString()) + " of " + field;
                }
            }
            if (categoryLegend)
            {
                definition.ChartCategoryLegend = true;
                definition.ShowLegend = true;
            }
            if (definition.ChartType == ChartType.Surface && groups.Count == 2)
                (definition.CategoryField, definition.SeriesField) = (definition.SeriesField, definition.CategoryField);
            if (chartMeasureFlags.Any(flag => flag != 0))
            {
                // The first vector selects formulas used without a second summary; the reserved vector stays zero.
                if (chartMeasureFlags.Count != 2 * definition.Measures.Count
                    || chartMeasureFlags.Take(definition.Measures.Count).Any(flag => flag is not (0 or 1))
                    || chartMeasureFlags.Skip(definition.Measures.Count).Any(flag => flag != 0))
                    throw new NotSupportedException("Native per-measure chart transformations require an explicit adapter.");
                for (var index = 0; index < definition.Measures.Count; index++)
                {
                    if (chartMeasureFlags[index] == 0) continue;
                    var field = ChartFormula(definition.Measures[index].Field);
                    if (field is null || !CrystalFormula.Compile(field.FormulaText, field.Syntax == 1 ? "Basic" : "Crystal").UsesAggregates)
                        throw new NotSupportedException("Unsummarized native chart bindings require an aggregate formula adapter.");
                    definition.Measures[index].DoNotSummarize = true;
                }
            }
            if (definition.ChartThreeDimensional)
            {
                if (crossTabChart) definition.PerspectiveStyle = perspectiveStyle;
                else if (groups.Count == 2 && owner.GraphDrawingId.HasValue && perspectiveStyle is not null)
                    definition.PerspectiveStyle = perspectiveStyle with { RotateCategoryLabels = definition.GroupKinds.ContainsKey(definition.CategoryField) };
                foreach (var measure in definition.Measures.Where(measure => measure.Caption.Length == 0))
                    measure.Caption = measure.Field.Trim('{', '}');
                if (ChartFormula(definition.CategoryField)?.ValueType is 6 or 7)
                    definition.CategoryFormat = "F2";
            }
            if (definition.ChartType is ChartType.Pie or ChartType.Donut && definition.Measures.Count > 1
                && groups.Count == 1 && ConstantChartCategory(definition.CategoryField))
            {
                // A single constant group is one pie whose slices are the separate summary fields.
                definition.SummariesAsCategories = true;
                definition.GroupKinds.Remove(definition.CategoryField);
                definition.CategoryField = "";
                definition.SeriesField = "";
                definition.SortCategories = false;
            }
        }
        var measureCount = definition.Measures.Count;
        if (objectType == 185 && definition.RowFields.Count == 0 && definition.ColumnFields.Count == 1 && measureCount >= 1
            && definition.Measures.All(measure => measure.Aggregate is ReportAggregateType.Sum or ReportAggregateType.Count or ReportAggregateType.Average or ReportAggregateType.Median)
            && totalStyles.TryGetValue(211, out var rowCaption))
        {
            var column = nativeCells.LastOrDefault(c => c.Axis == 207 && c.Cell.Kind != "Text").Cell;
            // Several summaries stack inside each cell at one left edge, one below the other; one summary is the last saved cell.
            var details = nativeCells.Where(c => c.Axis == 216 && c.Row == 1).Select(c => c.Cell).TakeLast(measureCount).ToArray();
            var totals = nativeCells.Where(c => c.Axis == 216 && c.Row == 0).Select(c => c.Cell).TakeLast(measureCount).ToArray();
            static bool Stacks(CrystalReportObjectModel[] cells) => cells.Select(cell => cell.Left).Distinct().Count() == 1
                && cells.Zip(cells.Skip(1)).All(pair => pair.First.Top + pair.First.Height == pair.Second.Top);
            if (column is not null && details.Length == measureCount && totals.Length == measureCount && Stacks(details) && Stacks(totals))
            {
                var detail = details[0]; var total = totals[0];
                var stackHeight = details[^1].Top + details[^1].Height - detail.Top;
                var fill = groupFills.GetValueOrDefault(206)?.LastOrDefault() ?? "transparent";
                var totalFill = groupFills.GetValueOrDefault(206)?.FirstOrDefault() ?? "transparent";
                var corner = totalStyles.GetValueOrDefault(207);
                definition.NativeCrossTab = new(rowCaption.Width, detail.Width, stackHeight, paddingX, paddingY,
                    detail.Font.FontFamily, (decimal)detail.Font.Size, detail.Font.Bold)
                {
                    ColumnSumFormat = string.Join(",", details.Select(StackFormat)), TotalSumFormat = string.Join(",", totals.Select(StackFormat)),
                    // The header row is as tall as its tallest object: the column heading or the corner caption.
                    HeaderHeight = Math.Max(column.Height, corner?.Height ?? 0) + 2 * paddingY, TotalWidth = total.Width + 2 * paddingX,
                    ColumnStyle = CellStyle(column, fill, "left"), RowStyle = CellStyle(rowCaption, "transparent", "left"),
                    RowTotalLabelStyle = CellStyle(rowCaption, "transparent", "left"), ValueStyle = CellStyle(detail, fill, "right"),
                    GrandTotalStyle = CellStyle(total, totalFill, "right"), ColumnTotalStyle = CellStyle(detail, fill, "right"),
                    ColumnTotalLabelStyle = CellStyle(corner ?? column, totalFill, "left"),
                    SuppressRowGrandTotals = suppressRowTotals,
                    // An older row-less grid omits its stroke records when it uses the default hairline grid.
                    Appearance = new(totalLabels.GetValueOrDefault(211, ""), totalLabels.GetValueOrDefault(207, ""),
                        "transparent", fill, fill, string.Join(";", drawGrid && gridLines.Count == 0 ? Enumerable.Repeat("0:#000000:Single", 20) : gridLines),
                        (decimal)rowCaption.Font.Size, (decimal)column.Font.Size)
                        { CaptionFields = CaptionFields(rowCaption, corner) }
                };
                definition.RowHeaderWidthTwips = Math.Max(360, rowCaption.Width + 2 * paddingX);
                definition.ValueColumnWidthTwips = Math.Max(360, detail.Width + 2 * paddingX);
                definition.RowHeightTwips = Math.Max(180, stackHeight + 2 * paddingY);
            }
        }
        var summaryCells = nativeCells.Where(c => c.Axis == 216).GroupBy(c => (c.Column, c.Row)).ToArray();
        // Vertical summaries share a left edge and occupy consecutive authored positions inside each cell.
        var depth = definition.ColumnFields.Count;
        var stacked = measureCount > 1 && depth is 1 or 2 && definition.CalculatedMembers.Count == 0
            && new[] { (1, depth), (1, 0), (0, depth), (0, 0) }.All(key => summaryCells.Any(g => g.Key == key))
            && summaryCells.All(g => g.Count() == measureCount && g.Select(c => c.Cell.Left).Distinct().Count() == 1
                && g.Zip(g.Skip(1)).All(pair => pair.First.Cell.Top + pair.First.Cell.Height == pair.Second.Cell.Top));
        // With no column group, the only numeric column is itself the total.
        // Recognize complete upright stacks without changing the column-group adapters.
        var totalColumnStack = depth == 0 && definition.RowFields.Count == 1 && measureCount > 1
            && definition.CalculatedMembers.Count == 0 && definition.Cells.Count == 0
            && summaryCells.Length == 2
            && new[] { (1, 0), (0, 0) }.All(key => summaryCells.Any(group => group.Key == key))
            && summaryCells.All(group => group.Count() == measureCount
                && group.Select(item => (item.Cell.Font.FontFamily, item.Cell.Font.Size, item.Cell.Font.Bold,
                    item.Cell.Font.Italic, item.Cell.Font.Underline, Color: Css(item.Cell.Color), item.Cell.Format.HorizontalAlignment)).Distinct().Count() == 1
                && group.Zip(group.Skip(1)).All(pair => pair.First.Cell.Top + pair.First.Cell.Height == pair.Second.Cell.Top))
            && summaryCells.SelectMany(group => group).Select(item => (item.Cell.Left, item.Cell.Width)).Distinct().Count() == 1
            && summaryCells.Select(group => string.Join("|", group.Select(item => item.Cell.DataSource))).Distinct().Count() == 1
            && summaryCells.First(group => group.Key == (0, 0)).First().Cell.Top
                >= summaryCells.First(group => group.Key == (1, 0)).Last().Cell.Top
                    + summaryCells.First(group => group.Key == (1, 0)).Last().Cell.Height;
        stacked |= totalColumnStack;
        // Nested row groups can also print several summaries down the same numeric column.
        // The multiline renderer shares a text style within a cell; require compatible saved lines.
        var stackedRowGroups = stacked && depth == 1 && definition.RowFields.Count > 1
            && summaryCells.Length == (definition.RowFields.Count + 1) * (depth + 1)
            && summaryCells.All(group => group.All(item => !item.Cell.Format.EnableCanGrow
                && item.Cell.FontConditionReferences.Count == 0 && item.Cell.Border.ConditionReferences.Count == 0
                && item.Cell.Format.ConditionReferences.Count == 0)
                && group.Select(item => (item.Cell.Width, item.Cell.Height, item.Cell.Font.FontFamily, item.Cell.Font.Size,
                    item.Cell.Font.Bold, item.Cell.Font.Italic, item.Cell.Font.Underline, Css(item.Cell.Color),
                    item.Cell.Format.HorizontalAlignment, Css(item.Cell.Border.BackgroundColor))).Distinct().Count() == 1);
        // Older row-only grids omit stroke records when using the default hairline grid.
        if (totalColumnStack && drawGrid && gridLines.Count == 0)
            gridLines.AddRange(Enumerable.Repeat("0:#000000:Single", 20));
        if (objectType == 185 && definition.RowFields.Count == 1 && (stacked || (definition.ColumnFields.Count == 1 || drawGrid && depth == 0) && measureCount == 1))
        {
            var row = nativeCells.FirstOrDefault(c => c.Axis == 211).Cell;
            var value = nativeCells.LastOrDefault(c => c.Axis == 216).Cell;
            var column = nativeCells.LastOrDefault(c => c.Axis == 207 && c.Cell.Kind != "Text").Cell ?? (depth == 0 ? totalStyles.GetValueOrDefault(207) : null);
            var parent = nativeCells.FirstOrDefault(c => c.Axis == 207 && c.Cell.Kind != "Text").Cell;
            if (row is { HasFont: true } && value is { HasFont: true } && column is { HasFont: true } && paddingX >= 0 && paddingY >= 0
                && row.Width > 0 && value.Width > 0 && value.Height > 0
                && !row.Format.EnableCanGrow && !value.Format.EnableCanGrow && uniformPadding
                && nativeCells.Where(c => c.Axis == 216).All(c => (stacked || c.Cell.Width == value.Width)
                    && !c.Cell.Format.EnableCanGrow && c.Cell.HasFont)
                && (depth == 0 || column.Width == value.Width)
                && !column.Format.EnableCanGrow)
            {
                var rowFill = groupFills.GetValueOrDefault(210)?.LastOrDefault() ?? Css(row.Border.BackgroundColor);
                var columnFill = groupFills.GetValueOrDefault(206)?.LastOrDefault() ?? Css(column.Border.BackgroundColor);
                var rowTotalFill = groupFills.GetValueOrDefault(210)?.FirstOrDefault() ?? rowFill;
                var columnTotalFill = groupFills.GetValueOrDefault(206)?.FirstOrDefault() ?? columnFill;
                var height = Math.Max(row.Height, value.Height);
                ReportNativeCrossTabCellStyle Summary(int c, int r, string fill) => CellStyle(
                    nativeCells.FirstOrDefault(item => item.Axis == 216 && item.Column == c && item.Row == r).Cell ?? value, fill, "right");
                definition.NativeCrossTab = new(row.Width, value.Width, height, paddingX, paddingY,
                    row.Font.FontFamily, (decimal)row.Font.Size, value.Font.Bold)
                {
                    ParentHeaderHeight = stacked && depth == 2 ? parent!.Height + 2 * paddingY : 0,
                    ParentHeaderStyle = stacked && depth == 2 ? CellStyle(parent!, columnFill, "center") : null,
                    TotalWidth = stacked ? summaryCells.First(g => g.Key.Row == 0).First().Cell.Width + 2 * paddingX : 0,
                    StackFormats = stacked ? string.Join(";", new[] { (1, depth), (1, 0), (0, depth), (0, 0) }.Select(key =>
                        string.Join(",", summaryCells.FirstOrDefault(g => g.Key == key)?.Select(c => StackFormat(c.Cell)) ?? []))) : "",
                    HeaderHeight = column.Height + 2 * paddingY,
                    SuppressColumnGrandTotals = suppressColumnTotals, SuppressRowGrandTotals = suppressRowTotals,
                    RowStyle = CellStyle(row, rowFill, "left"), ColumnStyle = CellStyle(column, columnFill, "left"),
                    ValueStyle = Summary(1, totalColumnStack ? 0 : 1, columnFill), RowTotalStyle = Summary(1, 0, columnTotalFill),
                    ColumnTotalStyle = Summary(0, totalColumnStack ? 0 : 1, rowTotalFill), GrandTotalStyle = Summary(0, 0, rowTotalFill),
                    RowTotalLabelStyle = CellStyle(totalStyles.GetValueOrDefault(211) ?? row, rowTotalFill, "left"),
                    ColumnTotalLabelStyle = CellStyle(totalStyles.GetValueOrDefault(207) ?? column, columnTotalFill, "left"),
                    // "Show Labels": each summary's label prints in a column of its own after the row labels, one line per summary.
                    SummaryLabels = stacked && summaryLabels.Count == measureCount ? summaryLabels.Select((label, index) => new ReportNativeCrossTabTemplate(211, 0, 0, index,
                        label.Left, label.Top, label.Width, label.Height, paddingX, paddingY, "", CellStyle(label, rowFill, "left")) { HeaderText = label.Text.TrimEnd('\n') }).ToArray() : [],
                    Appearance = new ReportNativeCrossTabAppearance(totalLabels.GetValueOrDefault(211, ""),
                        totalLabels.GetValueOrDefault(207, ""), rowFill,
                        columnFill, columnFill, string.Join(";", gridLines), (decimal)(totalStyles.GetValueOrDefault(211) ?? row).Font.Size,
                        (decimal)(totalStyles.GetValueOrDefault(207) ?? column).Font.Size)
                    { RowTotalBackground = rowTotalFill, ColumnTotalBackground = columnTotalFill,
                        CaptionFields = CaptionFields(totalStyles.GetValueOrDefault(211), totalStyles.GetValueOrDefault(207)) }
                };
                definition.RowHeaderWidthTwips = row.Width + 2 * paddingX;
                definition.ValueColumnWidthTwips = value.Width + 2 * paddingX;
                definition.RowHeightTwips = height + 2 * paddingY;
            }
        }
        if (objectType == 185 && definition.RowFields.Count > 0 && definition.ColumnFields.Count > 0 && measureCount > 0)
        {
            var row = nativeCells.LastOrDefault(c => c.Axis == 211).Cell;
            var column = nativeCells.LastOrDefault(c => c.Axis == 207 && c.Cell.Kind != "Text").Cell;
            var value = nativeCells.LastOrDefault(c => c.Axis == 216).Cell;
            if (row is { HasFont: true, Width: > 0 } && column is { HasFont: true, Width: > 0 } && value is { HasFont: true, Width: > 0, Height: > 0 })
            {
                var templates = new List<ReportNativeCrossTabTemplate>();
                var summaries = new Dictionary<(int Column, int Row), int>();
                foreach (var item in nativeCells)
                {
                    var cell = item.Cell;
                    if (cell.Kind == "Text" && (stacked || measureCount <= 1 || !summaryCells.All(g =>
                        g.Count() == measureCount && g.Select(c => c.Cell.Top).Distinct().Count() == 1))) continue;
                    var level = item.Axis == 216 ? 0 : item.Group;
                    // GridCell's first group coordinate is the row; the second is the column.
                    var colLevel = item.Axis == 216 ? item.Row : item.Axis == 207 ? level : 0;
                    var rowLevel = item.Axis == 216 ? item.Column : item.Axis == 211 ? level : 0;
                    var summary = item.Axis == 216 ? summaries.GetValueOrDefault((colLevel, rowLevel)) : cell.Kind == "Text" ? nativeCells.Where(c => c.Axis == 207 && c.Group == item.Group && c.Cell.Kind == "Text" && c.Cell.Left < cell.Left).Count() : 0;
                    if (item.Axis == 216) summaries[(colLevel, rowLevel)] = summary + 1;
                    var rowSubtotal = stackedRowGroups && item.Axis == 216 && rowLevel < definition.RowFields.Count;
                    var fillAxis = item.Axis == 211 || item.Axis == 216 && rowLevel == 0 || rowSubtotal ? 210 : 206;
                    var fillLevel = fillAxis == 210 ? rowLevel : colLevel;
                    var fill = groupFills.GetValueOrDefault(fillAxis)?.ElementAtOrDefault(fillLevel) ?? "transparent";
                    ReportNativeNumericFormat? numeric = null;
                    var dateTime = cell.NativeDateTimeFormat;
                    if (item.Axis == 216 && summary < definition.Measures.Count && cell.NativeNumericCandidates.Count == 2)
                    {
                        var nativeSummary = data.SummaryFields.Select((field, index) => (field, index)).FirstOrDefault(entry =>
                            SummaryReferencePlaceholder(entry.index) == cell.DataSource || SummaryFormulaName(entry.field, data) == cell.DataSource).field;
                        var type = nativeSummary?.ValueType ?? 6;
                        if (type is >= 0 and <= 7) numeric = cell.NativeNumericCandidates[type == 7 ? 0 : 1] with { ValueType = type };
                    }
                    if (dateTime is not null && item.Axis == 216)
                    {
                        var summaryType = data.SummaryFields.Select((field, index) => (field, index)).FirstOrDefault(entry =>
                            SummaryReferencePlaceholder(entry.index) == cell.DataSource || SummaryFormulaName(entry.field, data) == cell.DataSource).field?.ValueType;
                        if (summaryType == 9) dateTime = dateTime with { Order = 2 };
                    }
                    var geometry = groupGeometry.GetValueOrDefault((item.Axis, level));
                    templates.Add(new(item.Axis, colLevel, rowLevel, summary, cell.Left, cell.Top, cell.Width, cell.Height,
                        showCellMargins ? item.Axis == 216 || !uniformPadding ? item.PaddingX : paddingX : 0,
                        showCellMargins ? item.Axis == 216 || !uniformPadding ? item.PaddingY : paddingY : 0, "",
                        CellStyle(cell, fill, item.Axis == 216 ? "right" : "left"), numeric, dateTime));
                    if (stacked && numeric?.SymbolFormulaIndex is >= 0 and var symbol && symbol < data.FormulaFields.Count)
                    {
                        var formula = data.FormulaFields[symbol];
                        var last = templates[^1];
                        templates[^1] = last with { Style = last.Style with { Conditions = [..last.Style.Conditions,
                            new("CurrencySymbol", new(formula.FormulaText, formula.Syntax == 1 ? "Basic" : "Crystal"))] }};
                    }
                    if (item.Axis is 207 or 211) templates[^1] = templates[^1] with { CellWidth = geometry.Width, CellHeight = geometry.Height, HeaderText = cell.Kind == "Text" ? cell.Text : null };
                }
                if (stackedRowGroups)
                {
                    foreach (var (level, label) in rowSubtotalLabels.Where(pair => pair.Key < definition.RowFields.Count))
                    {
                        var fill = groupFills.GetValueOrDefault(210)?.ElementAtOrDefault(level) ?? "transparent";
                        templates.Add(new(211, 0, level, 0, label.Left, label.Top, label.Width, label.Height,
                            paddingX, paddingY, "", CellStyle(label, fill, "left")) { HeaderText = label.Text });
                    }
                }
                foreach (var total in totalStyles)
                {
                    var fill = groupFills.GetValueOrDefault(total.Key == 211 ? 210 : 206)?.FirstOrDefault() ?? "transparent";
                    templates.RemoveAll(template => template.Axis == total.Key && template.RowLevel == 0 && template.ColumnLevel == 0 && template.HeaderText is null);
                    var cell = total.Value;
                    templates.Add(new(total.Key, 0, 0, 0, cell.Left, cell.Top, cell.Width, cell.Height, paddingX, paddingY, "", CellStyle(cell, fill, "left")));
                }
                definition.NativeCrossTab ??= new(row.Width, value.Width, Math.Max(row.Height, value.Height), paddingX, paddingY,
                    row.Font.FontFamily, (decimal)row.Font.Size, value.Font.Bold)
                {
                    HeaderHeight = column.Height + 2 * paddingY,
                    SuppressColumnGrandTotals = suppressColumnTotals, SuppressRowGrandTotals = suppressRowTotals,
                    RowStyle = CellStyle(row, "transparent", "left"), ColumnStyle = CellStyle(column, "transparent", "left"),
                    ValueStyle = CellStyle(value, "transparent", "right"),
                    Appearance = new(totalLabels.GetValueOrDefault(211, ""), totalLabels.GetValueOrDefault(207, ""),
                        "transparent", "transparent", "transparent", string.Join(";", gridLines), (decimal)row.Font.Size, (decimal)column.Font.Size)
                        { CaptionFields = CaptionFields(totalStyles.GetValueOrDefault(211), totalStyles.GetValueOrDefault(207)) }
                };
                var leaf = templates.FirstOrDefault(template => template.Axis == 216 && template.RowLevel == definition.RowFields.Count && template.ColumnLevel == definition.ColumnFields.Count);
                var rowTotal = templates.FirstOrDefault(template => template.Axis == 216 && template.RowLevel == 0 && template.ColumnLevel == definition.ColumnFields.Count);
                var columnTotal = templates.FirstOrDefault(template => template.Axis == 216 && template.ColumnLevel == 0 && template.RowLevel == definition.RowFields.Count);
                // A column group whose subtotal prints keeps its own caption and format.
                var subtotalCaptions = columnCaptions.Where(caption => caption.Key < definition.ColumnFields.Count && !columnSubtotals.Contains(caption.Key))
                    .OrderBy(caption => caption.Key).Select(caption => new ReportNativeCrossTabCaption(caption.Key, caption.Value.Text,
                        CellStyle(caption.Value, groupFills.GetValueOrDefault(206)?.ElementAtOrDefault(caption.Key) ?? "transparent", "left"))).ToArray();
                definition.NativeCrossTab = definition.NativeCrossTab with { Templates = templates, StackedRowGroups = stackedRowGroups, ColumnSubtotalCaptions = subtotalCaptions,
                    ColumnTotalsBefore = leaf is not null && columnTotal is not null && columnTotal.Left < leaf.Left,
                    RowTotalsBefore = leaf is not null && rowTotal is not null && rowTotal.Top < leaf.Top,
                    SuppressedRowSubtotals = rowSubtotals, SuppressedColumnSubtotals = columnSubtotals,
                    CompactRowHeaders = templates.Any(t => t.Axis == 207 && t.HeaderText is not null) && definition.RowFields.Count > 1 && templates.Where(t => t.Axis == 211 && t.RowLevel > 0)
                        .All(t => leaf is not null && t.Left < leaf.Left && t.Top >= leaf.Top),
                    SummaryHeaderHeight = templates.Where(t => t.Axis == 207 && t.HeaderText is not null).Select(t => t.Height).DefaultIfEmpty(0).Max() };
                definition.RowHeaderWidthTwips = Math.Max(360, row.Width + 2 * paddingX);
                definition.ValueColumnWidthTwips = Math.Max(360, value.Width + 2 * paddingX);
                definition.RowHeightTwips = Math.Max(180, Math.Max(row.Height, value.Height) + 2 * paddingY);
            }
        }
        // A borderless grid can be authored as a list of row descriptions, with
        // every summary and total label suppressed. Preserve that presentation.
        if (objectType == 185 && ranked is null && !drawGrid && definition.RowFields.Count == 1
            && definition.ColumnFields.Count == 0 && definition.CalculatedMembers.Count == 0
            && nativeCells.Count(c => c.Axis == 211) == 1 && nativeCells.Any(c => c.Axis == 216)
            && nativeCells.Where(c => c.Axis == 216).All(c => c.Cell.Format.EnableSuppress && c.Cell.Format.ConditionReferences.Count == 0)
            && totalStyles.Values.All(c => c.Format.EnableSuppress && c.Format.ConditionReferences.Count == 0))
        {
            var label = nativeCells.Single(c => c.Axis == 211).Cell;
            if (!label.Format.EnableSuppress && label.Format.ConditionReferences.Count == 0 && label.Width > 0 && label.Height > 0)
            {
                var style = CellStyle(label, "transparent", "left");
                definition.NativeCrossTab = new(label.Width, label.Width, label.Height, paddingX, paddingY,
                    label.Font.FontFamily, (decimal)label.Font.Size, label.Font.Bold)
                {
                    RowLabelsOnly = true, RowStyle = style,
                    Appearance = new("", "", style.Background, "transparent", "transparent", "", style.FontSize, style.FontSize)
                };
                definition.RowHeaderWidthTwips = Math.Max(360, label.Width + 2 * paddingX);
                definition.RowHeightTwips = Math.Max(180, label.Height + 2 * paddingY);
            }
        }
        if (ranked is not null && drawGrid && definition.NativeCrossTab?.Appearance is not null
            && definition.RowFields.Count == 1 && definition.Measures.Count == 1
            && (ranked.DiscardOthers
                ? definition.ColumnFields.Count == 0 && definition.Measures[0].Aggregate == ReportAggregateType.Sum
                : definition.Measures[0].Aggregate is ReportAggregateType.Sum or ReportAggregateType.Count)
            && definition.Cells.Count == 0 && definition.CalculatedMembers.Count == 0
            && nativeCells.All(cell => cell.Cell.Format.ConditionReferences.Count == 0))
        {
            definition.RowRanking = new(ranked.TopNCount, ranked.Direction == 0, ranked.WithTies)
            { OthersCaption = ranked.DiscardOthers ? null : string.IsNullOrWhiteSpace(ranked.OthersName) ? "Others" : ranked.OthersName };
        }
        else if (ranked is not null)
        {
            var assignments = nativeCells.Where(c => c.Axis == 216 && c.Column == 1 && c.Row == 0)
                .SelectMany(c => c.Cell.Format.ConditionReferences).ToArray();
            if (!ranked.DiscardOthers || ranked.Direction != 1 || drawGrid || definition.RowFields.Count != 1 || definition.ColumnFields.Count != 0 ||
                definition.Measures.Count != 1 || definition.Measures[0].Aggregate != ReportAggregateType.Sum ||
                nativeCells.Any(c => !c.Cell.Format.EnableSuppress) || totalStyles.Values.Any(c => !c.Format.EnableSuppress) ||
                assignments.Length != 1 || assignments[0].Property != "EnableKeepTogether" ||
                assignments[0].Type != 1 || assignments[0].Index >= data.FormulaFields.Count)
                throw new NotSupportedException("Unsupported ranked cross-tab layout or cell calculation.");
            // Recognize the additive print-time cell formula. Other formulas require a full grid evaluation context.
            var expression = data.FormulaFields[assignments[0].Index].FormulaText;
            var match = System.Text.RegularExpressions.Regex.Match(expression,
                @"^\s*WhilePrintingRecords\s*;\s*CurrencyVar\s+(\w+)\s*;\s*\1\s*:=\s*\1\s*\+\s*CurrentFieldValue\s*;\s*True\s*;?\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (!match.Success) throw new NotSupportedException("Unsupported ranked cross-tab cell formula.");
            definition.AccumulatorVariable = match.Groups[1].Value;
            definition.AccumulatorCount = ranked.TopNCount;
            definition.AccumulatorWithTies = ranked.WithTies;
        }
        if (!bindingFound)
            throw new NotSupportedException("No supported native analytical measure-binding records were found.");
        if (definition.Validate(objectType == 180 ? "Chart" : "CrossTab") is { } error)
            throw new NotSupportedException("Native analytical bindings: " + error);
        return definition;

        CrystalFormulaFieldModel? ChartFormula(string reference) => data.FormulaFields.FirstOrDefault(formula =>
            formula.FormulaName.Equals(reference, StringComparison.OrdinalIgnoreCase));
        bool ConstantChartCategory(string reference)
        {
            if (ChartFormula(reference) is not { } field) return false;
            // Only a literal numeric condition is proven constant without evaluating chart records.
            if (field.Syntax != 0 || !System.Text.RegularExpressions.Regex.IsMatch(field.FormulaText,
                @"^\s*(?:(?:WhileReadingRecords|BeforeReadingRecords)\s*;\s*)?[+-]?(?:\d+(?:\.\d*)?|\.\d+)\s*;?\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) return false;
            var formula = CrystalFormula.Compile(field.FormulaText, "Crystal");
            return !formula.IsEmpty && formula.References.Count == 0 && formula.UnknownSymbols.Count == 0
                && !formula.UsesVariables && !formula.UsesAggregates && !formula.UsesPageContext
                && !formula.UsesRecordContext && !formula.UsesGridContext && !formula.RequiresPrintPass
                && formula.CustomFunctions.Count == 0;
        }

        // A field embedded in a total caption keeps the numeric format authored on it, resolved by the field's type as a field object's is.
        ReportNativeCrossTabCaptionField[] CaptionFields(params CrystalReportObjectModel?[] captions) => captions.OfType<CrystalReportObjectModel>()
            .SelectMany(caption => caption.TextRuns).Where(run => run.Binding.Length > 0).DistinctBy(run => run.Binding)
            .Select(run =>
            {
                var type = data.FormulaFields.FirstOrDefault(field => field.FormulaName == run.Binding)?.ValueType
                    ?? data.SummaryFields.Select((field, index) => (field, index)).Where(entry => SummaryReferencePlaceholder(entry.index) == run.Binding
                        || SummaryFormulaName(entry.field, data) == run.Binding).Select(entry => (int?)(entry.field.Operation is 6 or 9 ? 4 : entry.field.ValueType)).FirstOrDefault()
                    ?? data.RunningTotalFields.FirstOrDefault(field => field.FormulaName == run.Binding)?.ValueType ?? 6;
                var candidates = run.NativeNumericCandidates;
                var numeric = type is >= 0 and <= 7 && candidates.Count == 2 ? candidates[type == 7 ? 0 : 1] with { ValueType = type } : null;
                return new ReportNativeCrossTabCaptionField(run.Binding, numeric is { UnsupportedReason: null } ? numeric : null);
            }).ToArray();

        string StackFormat(CrystalReportObjectModel cell)
        {
            var summary = data.SummaryFields.Select((item, index) => (item, index))
                .FirstOrDefault(entry => SummaryReferencePlaceholder(entry.index) == cell.DataSource).item;
            var format = summary?.ValueType == 7 ? cell.NativeCurrencyFormat : cell.NativeNumberFormat;
            return (format?.ThousandsSeparators == false ? "F" : "N") + (format?.DecimalPlaces ?? 0);
        }

        static string Css(CrystalColorModel color) => color.A == 0 ? "transparent" : $"#{color.R:x2}{color.G:x2}{color.B:x2}";
        ReportNativeCrossTabCellStyle CellStyle(CrystalReportObjectModel cell, string fill, string defaultAlignment) => new(
            cell.Font.FontFamily, (decimal)cell.Font.Size, cell.Font.Bold, cell.Font.Italic, cell.Font.Underline, Css(cell.Color),
            cell.Border.BackgroundColor.A == 0 ? fill : Css(cell.Border.BackgroundColor), cell.Format.HorizontalAlignment switch
            { "HorizontalCenter" => "center", "RightAlign" or "Right" => "right", "LeftAlign" or "Left" => "left", _ => defaultAlignment })
            { Suppressed = cell.Format.EnableSuppress, Conditions = cell.FontConditionReferences.Concat(cell.Border.ConditionReferences)
                .Concat(cell.Format.ConditionReferences).Where(reference => reference.Property != "ToolTipText").Select(reference =>
                {
                    if (reference.Type != 1 || reference.Index < 0 || reference.Index >= data.FormulaFields.Count)
                        throw new NotSupportedException("A cross-tab conditional formula reference is unavailable.");
                    var formula = data.FormulaFields[reference.Index];
                    return new ReportCrossTabCondition(reference.Property, new(formula.FormulaText, formula.Syntax == 1 ? "Basic" : "Crystal"));
                }).ToArray() };

        ReportGridFormula ReadGridFormula()
        {
            _ = reader.LoadString();
            var type = reader.LoadEnum();
            var index = reader.LoadUInt16();
            if (index == ushort.MaxValue) return new("");
            if (type != 1 || index >= data.FormulaFields.Count)
                throw new NotSupportedException("A calculated-member formula reference is unavailable.");
            var formula = data.FormulaFields[index];
            return new(formula.FormulaText, formula.Syntax == 1 ? "Basic" : "Crystal");
        }

        void CaptureCell(TslvArchiveReader source, int type, int axis)
        {
            var probe = source.Fork();
            if (type == 161)
            {
                probe.LoadNextRecord(159, 1792, 162);
                nativeCell = ReadReportObjectBase(probe, "FieldObject", "Field", 162);
                nativeCell.DataSource = ReadFieldReference(probe, fields, data);
                probe.SkipRestOfRecord(); probe.SkipRestOfRecord();
                ReadCommonReportObjectRecords(probe, nativeCell);
                ReadFontColourProperties(probe, nativeCell);
                ReadNativeFieldFormats(probe, nativeCell, endType: 162);
            }
            else nativeCell = ReadFieldObject(probe, fields, data);
            nativeCells.Add((axis, cellColumn, cellRow, axisLevels.GetValueOrDefault(axis), showCellMargins ? paddingX : 0, showCellMargins ? paddingY : 0, nativeCell));
        }

        void AddSummary(string reference)
        {
            var summary = data.SummaryFields.Select((value, index) => (value, index)).FirstOrDefault(pair =>
                reference == SummaryReferencePlaceholder(pair.index) || reference == pair.value.Name
                || reference == SummaryFormulaName(pair.value, data)).value;
            if (summary is null) throw new NotSupportedException($"Native summary reference '{reference}' could not be resolved.");
            AddMeasure(summary);
        }
        void AddMeasure(CrystalSummaryFieldModel summary)
        {
            var operation = summary.Operation switch
            {
                0 => ReportAggregateType.Sum, 1 => ReportAggregateType.Average, 4 => ReportAggregateType.Max,
                5 => ReportAggregateType.Min, 6 => ReportAggregateType.Count,
                9 => ReportAggregateType.DistinctCount, 12 when objectType == 185 && summary.WeightField.Length > 0 => ReportAggregateType.WeightedAverage, 13 => ReportAggregateType.Median,
                _ => throw new NotSupportedException($"Native analytical aggregate {SummaryOperationName(summary.Operation)} is not supported.")
            };
            if (objectType == 185 && !boundSummaries.Add(summary)) return;
            if (objectType == 185 && summary.SummaryType is not (0 or 1))
                throw new NotSupportedException("Unsupported native cross-tab percentage direction.");
            definition.Measures.Add(new() { PercentOfColumnTotal = objectType == 185 && summary.SummaryType == 1 ? summary.SummaryTypeIndex : null, Field = summary.SummarizedField, Caption = summary.Name,
                Aggregate = operation, WeightField = operation == ReportAggregateType.WeightedAverage ? summary.WeightField : "", Format = operation is ReportAggregateType.Count or ReportAggregateType.DistinctCount ? "N0"
                    : summary.ValueType switch { 9 => "d", 10 => "t", 15 => "g", 11 or 12 or 13 => "", _ => "N2" } });
        }
        static int Count(int value, int limit) => value >= 0 && value <= limit ? value
            : throw new InvalidDataException("Native analytical record count exceeds its supported bound.");
    }
}
