using Fx.ControlKit.Charts;

namespace Fx.ControlKit.Reports.NativeCrystal;

internal static partial class CrystalReportContentsParser
{
    // Only the legacy analytical binding records are read. Drawing/style payloads remain opaque.
    private static ReportAnalysisDefinition ReadAnalysisBindings(TslvArchiveReader reader,
        FieldReferenceTable fields, CrystalDataDefinitionModel data)
    {
        var objectType = reader.CurrentRecord!.Type;
        var definition = new ReportAnalysisDefinition();
        var groups = new List<string>();
        var containers = new Stack<int>();
        var styleFound = false;
        var bindingFound = false;
        var expectedMeasures = -1;
        var expectedGroups = -1;
        var complete = false;
        reader.SkipRestOfRecord();
        for (var records = 0; records < 100000 && reader.BytesLeftInRecord > 0; records++)
        {
            var before = reader.Fork();
            var record = reader.LoadAnyRecord();
            if (record.Type == objectType + 1) { complete = true; break; }
            if (record.Type is 206 or 210 or 215) containers.Push(record.Type + 1);
            if (record.Type == 289 && objectType == 180)
            {
                var nativeType = reader.LoadEnum();
                var subtype = reader.LoadEnum();
                definition.ChartType = (nativeType, subtype) switch
                {
                    (0, 0 or 3) => ChartType.Bar,
                    (0, 1 or 4) => ChartType.StackedBar,
                    (0, 2 or 5) => ChartType.StackedBar100,
                    (1, 10 or 13) => ChartType.Line,
                    (3, 30 or 31) => ChartType.Pie,
                    (4, 40) => ChartType.Donut,
                    _ => throw new NotSupportedException($"Native chart type {nativeType}, subtype {subtype} has no exact report adapter.")
                };
                definition.Title = reader.LoadString() ?? "";
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
                    for (var flags = Count(reader.LoadUInt16(), 64); flags > 0; flags--)
                        if (reader.LoadInt32() != 0) throw new NotSupportedException("Native per-measure chart transformations require an explicit adapter.");
                if (reader.BytesLeftInRecord > 0 && reader.LoadBoolean())
                    throw new NotSupportedException("Native alternate chart evaluation requires an explicit adapter.");
                if (reader.BytesLeftInRecord > 0) definition.CategoryField = ReadFieldReference(reader, fields, data);
                bindingFound = true;
            }
            else if (record.Type == 287 && objectType == 180)
            {
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
                if (!string.IsNullOrWhiteSpace(field))
                {
                    if (grouping != 0 || direction is not (0 or 1))
                        throw new NotSupportedException("Native date/custom/original-order analytical grouping requires an explicit grouping adapter.");
                    if (direction == 1) definition.DescendingFields.Add(field);
                    if (objectType == 180) groups.Add(field);
                    else if (containers.Contains(207)) definition.ColumnFields.Add(field);
                    else if (containers.Contains(211)) definition.RowFields.Add(field);
                }
            }
            else if (record.Type == 159 && objectType == 185 && containers.Contains(216))
            {
                var probe = reader.Fork();
                _ = ReadReportObjectBase(probe, "FieldObject", "Field", 160);
                AddSummary(ReadFieldReference(probe, fields, data));
                bindingFound = true;
            }
            else if (record.Type is 290 or 292 or 382)
                throw new NotSupportedException("Linked-chart, OLAP or calculated-member bindings are retained but not evaluated.");
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
            if (!styleFound || !bindingFound || groups.Count > 2 || !definition.EachRecord && groups.Count == 0
                || expectedMeasures >= 0 && expectedMeasures != definition.Measures.Count
                || expectedGroups >= 0 && expectedGroups != groups.Count)
                throw new InvalidDataException("Incomplete native chart data/style binding records.");
            if (groups.Count > 0) definition.CategoryField = groups[0];
            definition.SortCategories = !definition.EachRecord;
            definition.SeriesField = groups.Count > 1 ? groups[1] : "";
        }
        if (!bindingFound || definition.Validate(objectType == 180 ? "Chart" : "CrossTab") is { })
            throw new NotSupportedException("Native analytical bindings are incomplete or outside the supported report contract.");
        return definition;

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
                9 => ReportAggregateType.DistinctCount, 13 => ReportAggregateType.Median,
                _ => throw new NotSupportedException($"Native analytical aggregate {SummaryOperationName(summary.Operation)} is not supported.")
            };
            if (objectType == 185 && definition.Measures.Any(m => m.Field == summary.SummarizedField && m.Aggregate == operation && m.Caption == summary.Name)) return;
            definition.Measures.Add(new() { Field = summary.SummarizedField, Caption = summary.Name,
                Aggregate = operation, Format = operation is ReportAggregateType.Count or ReportAggregateType.DistinctCount ? "N0" : "N2" });
        }
        static int Count(int value, int limit) => value >= 0 && value <= limit ? value
            : throw new InvalidDataException("Native analytical record count exceeds its supported bound.");
    }
}
