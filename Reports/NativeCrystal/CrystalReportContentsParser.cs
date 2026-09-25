using System.Runtime.Versioning;

namespace Fx.ControlKit.Reports.NativeCrystal;

[UnsupportedOSPlatform("browser")]
internal static partial class CrystalReportContentsParser
{
    public static CrystalReportCore ParseCore(CrystalRptStream contentsStream)
    {
        return ParseHeader(contentsStream).Core;
    }

    public static CrystalDataDefinitionModel ParseDataDefinition(
        CrystalRptStream contentsStream,
        CrystalDatabaseModel database)
    {
        return ParseHeader(contentsStream, database).DataDefinition;
    }

    private static CrystalReportContents ParseHeader(
        CrystalRptStream contentsStream,
        CrystalDatabaseModel? database = null)
    {
        // These Contents archives use the legacy 100-series record family.
        // Unstated schemas inherit 0x0700, not the newer 8000-series schema.
        var decoded = TslvStreamReader.Decode(contentsStream.Bytes, defaultSchema: 1792);
        var reader = new TslvArchiveReader(decoded.Body, decoded.HeaderSchema) { LegacyValueLengths = decoded.IsHeaderless };
        var core = new CrystalReportCore();
        var dataDefinition = new CrystalDataDefinitionModel();
        var fieldReferences = new FieldReferenceTable(dataDefinition.FieldManagerReferences);

        var document = reader.LoadNextRecord(100, 1792, 101);
        core.VersionMajor = reader.LoadUInt16();
        core.VersionMinor = reader.LoadUInt16();
        core.VersionPatch = reader.LoadUInt8();
        _ = reader.LoadBoolean();
        core.ReportName = reader.LoadString() ?? "";
        _ = reader.LoadEnum();
        _ = reader.LoadInt32();
        _ = reader.LoadInt32();
        _ = reader.LoadBoolean();
        var saveFlags = reader.LoadInt16();
        core.HasSavedData = reader.LoadBoolean();
        core.EnableSaveDataWithReport = (saveFlags & 2) != 0;
        core.EnableSaveSummariesWithReport = (saveFlags & 1) != 0;
        reader.SkipRestOfRecord();

        _ = document;
        _ = reader.LoadNextRecord(102, 1792, 101);
        dataDefinition.ReportDefinition.ReportKind = reader.LoadEnum();
        _ = reader.LoadEnum();
        _ = reader.LoadEnum();
        core.LeftMargin = reader.LoadInt32();
        core.RightMargin = reader.LoadInt32();
        core.TopMargin = reader.LoadInt32();
        core.BottomMargin = reader.LoadInt32();
        _ = TryLoadBoolean(reader);
        var hasPrinter = TryLoadBoolean(reader);
        var hasRecordSelectionFormula = TryLoadBoolean(reader);
        var hasGroupSelectionFormula = TryLoadBoolean(reader);
        _ = TryLoadBoolean(reader);
        _ = TryLoadBoolean(reader);
        var hasRuler = TryLoadBoolean(reader);
        _ = TryLoadBoolean(reader);
        _ = TryLoadBoolean(reader);
        var recordSortCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        var groupSortCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        var areaPairCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        reader.SkipRestOfRecord();

        if (reader.Fork().LoadAnyRecord().Type == 351)
        {
            _ = reader.LoadAnyRecord();
            reader.SkipRestOfRecord();
        }

        if (hasPrinter)
        {
            ParsePrinter(reader, core);
        }
        else
        {
            NormalizeMargins(core, 360);
            ApplyPageContentSize(core, 12240, 15840);
        }

        if (database is not null)
        {
            TryParseDataDefinition(
                reader,
                database,
                dataDefinition,
                fieldReferences,
                hasRecordSelectionFormula,
                hasGroupSelectionFormula,
                recordSortCount,
                groupSortCount,
                areaPairCount,
                hasRuler);
        }

        return new CrystalReportContents(core, dataDefinition);
    }

    private static void TryParseDataDefinition(
        TslvArchiveReader reader,
        CrystalDatabaseModel database,
        CrystalDataDefinitionModel dataDefinition,
        FieldReferenceTable fieldReferences,
        bool hasRecordSelectionFormula,
        bool hasGroupSelectionFormula,
        int recordSortCount,
        int groupSortCount,
        int areaPairCount,
        bool hasRuler)
    {
        try
        {
            var next = reader.LoadAnyRecord();
            if (next.Type == 365)
            {
                reader.SkipRestOfRecord();
                _ = reader.LoadNextRecord(111, 1792, 101);
            }
            else if (next.Type != 111)
            {
                reader.SkipRestOfRecord();
                _ = reader.LoadNextRecord(111, 1792, 101);
            }

            ParseFieldManager(reader, database, dataDefinition, fieldReferences);
            _ = reader.LoadNextRecord(108, 1792, 101);
            dataDefinition.ReportDefinition.MultiColumn = ReadMultiColumn(reader, dataDefinition);
            reader.SkipRestOfRecord();

            if (hasRecordSelectionFormula)
            {
                var recordSelection = ReadFormulaField(reader);
                dataDefinition.RecordSelectionFormula = NormalizeSelectionFormula(recordSelection.FormulaText);
                dataDefinition.RecordSelectionFormulaSyntax = recordSelection.Syntax;
            }

            if (hasGroupSelectionFormula)
            {
                dataDefinition.GroupSelectionFormula = NormalizeSelectionFormula(ReadFormulaField(reader).FormulaText);
            }

            for (var i = 0; i < recordSortCount; i++)
            {
                dataDefinition.SortFields.Add(ReadSortField(reader, fieldReferences, "RecordSortField"));
            }

            for (var i = 0; i < groupSortCount; i++)
            {
                dataDefinition.SortFields.Add(ReadSortField(reader, fieldReferences, "GroupSortField"));
            }

        for (var i = 0; i < areaPairCount; i++)
        {
                ReadAreaPair(reader, dataDefinition, fieldReferences, i + 1);
        }

            NormalizeGroupConditions(database, dataDefinition);
            ResolveDeferredGroupNameReferences(dataDefinition);
            ResolveDeferredSummaryReferences(dataDefinition);
            foreach (var (reportObject, analysisReader) in fieldReferences.AnalyticalObjects)
            {
                try { reportObject.Analysis = ReadAnalysisBindings(analysisReader, fieldReferences, dataDefinition); }
                catch (Exception error) when (error is InvalidDataException or EndOfStreamException or OverflowException or ArgumentException or NotSupportedException)
                { reportObject.AnalysisDiagnostic = error.Message; }
            }
            fieldReferences.AnalyticalObjects.Clear();
            foreach (var obj in dataDefinition.ReportDefinition.Areas.SelectMany(a => a.Sections).SelectMany(s => s.ReportObjects))
            {
                ResolveConditions(obj.Format.ConditionReferences, obj.Format.ConditionFormulas);
                ResolveConditions(obj.FontConditionReferences, obj.FontConditionFormulas);
                ResolveConditions(obj.Border.ConditionReferences, obj.Border.ConditionFormulas);
            }
            void ResolveConditions(List<(string Property, string Name, int Type, int Index)> references, Dictionary<string, string> formulas)
            {
                foreach (var condition in references)
                {
                    var expression = condition.Type == 1 && condition.Index < dataDefinition.FormulaFields.Count
                        ? dataDefinition.FormulaFields[condition.Index].FormulaText : fieldReferences.Get(condition.Type, condition.Index);
                    if (!string.IsNullOrWhiteSpace(expression)) formulas[condition.Property] = NormalizeConditionFormula(expression);
                }
            }

            if (hasRuler)
            {
                var ruler = reader.LoadAnyRecord();
                reader.SkipRestOfRecord();
                _ = ruler;
            }
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or OverflowException or ArgumentException or NotSupportedException)
        {
            if (Environment.GetEnvironmentVariable("FLEXKIT_DEBUG_RPT") == "1")
            {
                throw;
            }

            dataDefinition.ParseWarnings.Add($"Report definition extraction stopped at TSLV record {reader.CurrentRecord?.Type}: {error.Message}");
        }
    }

    private static void ResolveDeferredGroupNameReferences(CrystalDataDefinitionModel dataDefinition)
    {
        if (dataDefinition.Groups.Count == 0)
        {
            return;
        }

        foreach (var reportObject in dataDefinition.ReportDefinition.Areas
                     .SelectMany(area => area.Sections)
                     .SelectMany(section => section.ReportObjects))
        {
            reportObject.Text = ReplaceGroupNamePlaceholders(reportObject.Text, dataDefinition);
            for (var runIndex = 0; runIndex < reportObject.TextRuns.Count; runIndex++)
            {
                var run = reportObject.TextRuns[runIndex];
                reportObject.TextRuns[runIndex] = run with { Text = ReplaceGroupNamePlaceholders(run.Text, dataDefinition), Binding = ReplaceGroupNamePlaceholders(run.Binding, dataDefinition) };
            }
            reportObject.DataSource = ReplaceGroupNamePlaceholders(reportObject.DataSource, dataDefinition);
            foreach (var link in reportObject.StoredSubreportLinks)
                link.MainReportFieldName = ReplaceGroupNamePlaceholders(link.MainReportFieldName, dataDefinition);
        }
    }

    private static string ReplaceGroupNamePlaceholders(
        string value,
        CrystalDataDefinitionModel dataDefinition)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return System.Text.RegularExpressions.Regex.Replace(
            value,
            @"Group #(\d+)(?: Name\b)?",
            match =>
            {
                var groupNumber = int.Parse(
                    match.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                return groupNumber > 0 && groupNumber <= dataDefinition.Groups.Count
                    ? "GroupName (" + dataDefinition.Groups[groupNumber - 1].ConditionField + ")"
                    : match.Value;
            });
    }

    private static void ResolveDeferredSummaryReferences(CrystalDataDefinitionModel dataDefinition)
    {
        if (dataDefinition.SummaryFields.Count == 0)
        {
            return;
        }

        foreach (var sortField in dataDefinition.SortFields)
        {
            var placeholder = System.Text.RegularExpressions.Regex.Match(sortField.Field, @"^__FLEXKIT_SUMMARY_REF_(\d+)__$");
            if (placeholder.Success &&
                int.TryParse(placeholder.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var summaryNumber) &&
                summaryNumber < dataDefinition.SummaryFields.Count &&
                dataDefinition.SummaryFields[summaryNumber] is { SummaryKind: 1, GroupIndex: > 0 } summary)
            {
                sortField.SummaryGroupNumber = summary.GroupIndex;
            }

            sortField.Field = ReplaceSummaryPlaceholders(sortField.Field, dataDefinition);
        }

        foreach (var reportObject in dataDefinition.ReportDefinition.Areas
                     .SelectMany(area => area.Sections)
                     .SelectMany(section => section.ReportObjects))
        {
            reportObject.Text = ReplaceSummaryPlaceholders(reportObject.Text, dataDefinition);
            for (var runIndex = 0; runIndex < reportObject.TextRuns.Count; runIndex++)
            {
                var run = reportObject.TextRuns[runIndex];
                reportObject.TextRuns[runIndex] = run with { Text = ReplaceSummaryPlaceholders(run.Text, dataDefinition), Binding = ReplaceSummaryPlaceholders(run.Binding, dataDefinition) };
            }
            reportObject.DataSource = ReplaceSummaryPlaceholders(reportObject.DataSource, dataDefinition);
            foreach (var link in reportObject.StoredSubreportLinks)
                link.MainReportFieldName = ReplaceSummaryPlaceholders(link.MainReportFieldName, dataDefinition);
        }
    }

    private static string ReplaceSummaryPlaceholders(
        string value,
        CrystalDataDefinitionModel dataDefinition)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return System.Text.RegularExpressions.Regex.Replace(
            value,
            @"__FLEXKIT_SUMMARY_REF_(\d+)__",
            match =>
            {
                var summaryNumber = int.Parse(
                    match.Groups[1].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                return summaryNumber >= 0 && summaryNumber < dataDefinition.SummaryFields.Count
                    ? SummaryFormulaName(dataDefinition.SummaryFields[summaryNumber], dataDefinition)
                    : match.Value;
            });
    }

    private static string SummaryReferencePlaceholder(int summaryIndex)
    {
        return "__FLEXKIT_SUMMARY_REF_" +
               summaryIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) +
               "__";
    }

    private static void ParseFieldManager(
        TslvArchiveReader reader,
        CrystalDatabaseModel database,
        CrystalDataDefinitionModel dataDefinition,
        FieldReferenceTable fieldReferences)
    {
        _ = reader.LoadNextRecord(110, 1792, 101);
        _ = reader.LoadBoolean();
        var databaseFieldCount = reader.LoadUInt16();
        var formulaFieldCount = reader.LoadUInt16();
        var specialFieldCount = reader.LoadUInt16();
        var summaryFieldCount = reader.LoadUInt16();
        var groupNameFieldCount = reader.LoadUInt16();
        var parameterFieldCount = reader.LoadUInt16();
        var runningTotalFieldCount = reader.LoadUInt16();
        var sqlExpressionFieldCount = reader.LoadUInt16();
        var customFunctionFieldCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        reader.SkipRestOfRecord();

        if (reader.BytesLeftInRecord >= 6)
        {
            _ = reader.LoadUInt16();
            _ = reader.LoadUInt16();
            _ = reader.LoadBoolean();
        }

        reader.SkipRestOfRecord();

        for (var i = 0; i < databaseFieldCount; i++)
        {
            var fieldName = ReadDatabaseFieldDefinition(reader, database, i);
            fieldReferences.Add(0, fieldName);
        }

        for (var i = 0; i < formulaFieldCount; i++)
        {
            var formula = ReadFormulaField(reader);
            dataDefinition.FormulaFields.Add(formula);
            fieldReferences.Add(1, formula.FormulaName);
        }

        for (var i = 0; i < specialFieldCount; i++)
        {
            fieldReferences.Add(3, ReadSpecialField(reader));
        }

        // Newer Crystal versions append special fields beyond the stored count; Crystal's own loader skips them by
        // record type, so the single-record loops below must not consume them as group-name or other fields.
        while (reader.BytesLeftInRecord > 0 && reader.Fork().LoadAnyRecord().Type == 120)
        {
            fieldReferences.Add(3, ReadSpecialField(reader));
        }

        for (var i = 0; i < summaryFieldCount; i++)
        {
            var summary = ReadSummaryField(reader, fieldReferences);
            ApplySummaryFieldApiType(summary, database, dataDefinition);
            dataDefinition.SummaryFields.Add(summary);
            fieldReferences.Add(2, SummaryReferencePlaceholder(i));
        }

        for (var i = 0; i < groupNameFieldCount; i++)
        {
            fieldReferences.Add(4, SkipFieldLikeRecord(reader));
        }

        for (var i = 0; i < parameterFieldCount; i++)
        {
            var parameter = ReadParameterField(reader);
            dataDefinition.Parameters.Add(parameter);
            fieldReferences.Add(5, parameter.FormulaName);
        }

        for (var i = 0; i < runningTotalFieldCount; i++)
        {
            var runningTotal = ReadRunningTotalField(reader, fieldReferences, dataDefinition);
            dataDefinition.RunningTotalFields.Add(runningTotal);
            fieldReferences.Add(9, runningTotal.FormulaName);
        }

        for (var i = 0; i < sqlExpressionFieldCount; i++)
        {
            fieldReferences.Add(10, SkipFieldLikeRecord(reader));
        }

        // Like Crystal's loader, each custom function skips records until its own (335); reaching the end of the
        // field manager (112) first means the report stores them in a layout this reader does not know.
        var customFunctionSlots = new List<(CrystalCustomFunctionModel? Function, List<int> Called)>();
        for (var i = 0; i < customFunctionFieldCount; i++)
        {
            int recordType;
            while ((recordType = reader.Fork().LoadAnyRecord().Type) is not (335 or 112))
            {
                reader.LoadAnyRecord();
                reader.SkipRestOfRecord();
            }

            if (recordType == 112)
            {
                dataDefinition.ParseWarnings.Add($"The report declares {customFunctionFieldCount} custom function(s) but stores {i} in record 335; the others are not emitted.");
                break;
            }

            var customFunction = ReadCustomFunction(reader, dataDefinition.ParseWarnings, i, out var calledIndexes);
            customFunctionSlots.Add((customFunction, calledIndexes));
            if (customFunction is not null) dataDefinition.CustomFunctions.Add(customFunction);
            fieldReferences.Add(11, customFunction is null ? "" : "{" + customFunction.Name + "}");
        }

        foreach (var (function, called) in customFunctionSlots)
        {
            if (function is null) continue;
            foreach (var index in called)
            {
                if (index >= 0 && index < customFunctionSlots.Count && customFunctionSlots[index].Function is { } target)
                    function.CalledFunctions.Add(target.Name);
                else
                    dataDefinition.ParseWarnings.Add($"Custom function '{function.Name}' calls custom function #{index}, which the report does not define readably.");
            }
        }

        _ = reader.LoadNextRecord(112, 1792, 101);
        reader.SkipRestOfRecord();
    }

    private static string ReadDatabaseFieldDefinition(
        TslvArchiveReader reader,
        CrystalDatabaseModel database,
        int fieldNumber)
    {
        CrystalFieldHeader field;
        int databaseIndex;
        string? legacyName = null;
        try
        {
            _ = reader.LoadNextRecord(115, 1792, 101);
            _ = reader.LoadNextRecord(114, 1792, 101);
            field = ReadFieldHeader(reader);
            reader.SkipRestOfRecord();
            if (database.UsesLegacyFieldNames)
            {
                if (reader.LoadUInt16() != 0x1008)
                    throw new InvalidDataException("Unsupported legacy database field-name encoding.");
                var nameBytes = reader.LoadBlock(reader.LoadUInt16());
                if (nameBytes.Length == 0 || nameBytes[^1] != 0)
                    throw new InvalidDataException("Unterminated legacy database field name.");
                legacyName = System.Text.Encoding.Latin1.GetString(nameBytes, 0, nameBytes.Length - 1);
                databaseIndex = -1;
            }
            else databaseIndex = reader.BytesLeftInRecord >= 4 ? reader.LoadInt32() : -1;
            reader.SkipRestOfRecord();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not read Crystal database field definition #{fieldNumber}.", ex);
        }

        if (database.FieldsByObjectId.TryGetValue(databaseIndex, out var databaseField))
        {
            return databaseField.FormulaName;
        }

        if (!string.IsNullOrWhiteSpace(legacyName))
        {
            var qualifiedField = database.Tables.SelectMany(t => t.Fields)
                .FirstOrDefault(f => f.LongName.Equals(legacyName, StringComparison.OrdinalIgnoreCase));
            return qualifiedField?.FormulaName ?? "{" + legacyName.Trim('{', '}') + "}";
        }

        var databaseFields = database.Tables.SelectMany(table => table.Fields).ToArray();
        var fieldsWithSameName = databaseFields
            .Where(databaseField => string.Equals(databaseField.Name, field.Name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (fieldsWithSameName.Length == 1)
        {
            return fieldsWithSameName[0].FormulaName;
        }

        if (fieldsWithSameName.Length > 1)
        {
            return fieldsWithSameName[0].FormulaName;
        }

        return string.IsNullOrWhiteSpace(field.Name) ? "" : "{" + field.Name + "}";
    }

    private static CrystalFormulaFieldModel ReadFormulaField(TslvArchiveReader reader)
    {
        _ = reader.LoadNextRecord(119, 1792, 101);
        var formula = ReadFormulaDefinition(reader, null);
        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadBoolean();
        }

        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadBoolean();
        }

        reader.SkipRestOfRecord();
        return formula;
    }

    // Record 335 wraps the shared formula definition (118) and appends the repository metadata
    // CustomFunctionFieldDefinition persists: id, version, flags, category, author, summary, help,
    // argument descriptions, and per-argument default-value pick lists. A record that does not have this
    // layout is skipped with a warning instead of failing the whole conversion.
    private static CrystalCustomFunctionModel? ReadCustomFunction(TslvArchiveReader reader, List<string> warnings, int slot, out List<int> calledIndexes)
    {
        var record = reader.LoadAnyRecord();
        calledIndexes = [];
        try
        {
            if (record.Schema > 1792)
                throw new InvalidDataException($"record schema {record.Schema} is newer than 1792");
            var function = ReadCustomFunctionBody(reader, out var called);
            calledIndexes = called;
            return function;
        }
        catch (InvalidDataException error)
        {
            warnings.Add($"Custom function #{slot} could not be read ({error.Message}); it is not emitted.");
            return null;
        }
        finally
        {
            while (reader.CurrentRecord is { } current && current.Offset != record.Offset) reader.SkipRestOfRecord();
            reader.SkipRestOfRecord();
        }
    }

    private static CrystalCustomFunctionModel ReadCustomFunctionBody(TslvArchiveReader reader, out List<int> calledIndexes)
    {
        var operands = new List<(int Type, int Index)>();
        var definition = ReadFormulaDefinition(reader, operands);
        if (definition.FormulaType != 11)
            throw new InvalidDataException($"'{definition.Name}' has formula type {definition.FormulaType}; expected 11");
        var function = new CrystalCustomFunctionModel
        {
            Name = definition.Name,
            FormulaText = definition.FormulaText,
            ValueType = definition.ValueType,
            Syntax = definition.Syntax
        };
        calledIndexes = operands.Where(operand => operand.Type == 11).Select(operand => operand.Index).Distinct().ToList();
        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadString();
            _ = reader.LoadInt32();
            _ = reader.LoadBoolean();
            _ = reader.LoadBoolean();
            function.Category = reader.LoadString() ?? "";
            function.Author = reader.LoadString() ?? "";
            _ = reader.LoadString();
            function.Summary = reader.LoadString() ?? "";
            _ = reader.LoadString();
            var argumentCount = reader.LoadUInt16();
            for (var i = 0; i < argumentCount; i++)
            {
                function.ArgumentDescriptions.Add(reader.LoadString() ?? "");
            }

            if (reader.BytesLeftInRecord > 0)
            {
                _ = reader.LoadBoolean();
            }

            if (reader.BytesLeftInRecord > 0)
            {
                for (var i = 0; i < argumentCount; i++)
                {
                    var values = new List<string>();
                    var valueCount = reader.LoadUInt16();
                    for (var j = 0; j < valueCount; j++)
                    {
                        values.Add(reader.LoadString() ?? "");
                    }

                    function.ArgumentDefaultValues.Add(values);
                }
            }
        }

        return function;
    }

    private static CrystalFormulaFieldModel ReadFormulaDefinition(TslvArchiveReader reader, List<(int Type, int Index)>? operands)
    {
        _ = reader.LoadNextRecord(118, 1792, 101);
        var field = ReadFieldHeader(reader);

        var operandCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        for (var i = 0; i < operandCount; i++)
        {
            reader.SkipString();
            var type = reader.LoadEnum();
            var index = reader.LoadUInt16();
            operands?.Add((type, index));
        }

        var formula = new CrystalFormulaFieldModel
        {
            Name = field.Name,
            ValueType = field.ValueType,
            NumberOfBytes = field.NumberOfBytes,
            FormulaText = reader.BytesLeftInRecord > 0 ? reader.LoadString() ?? "" : ""
        };

        if (reader.BytesLeftInRecord > 0)
        {
            formula.FormulaType = reader.LoadEnum();
        }

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadEnum();
        }

        for (var i = 0; i < 7 && reader.BytesLeftInRecord >= 2; i++)
        {
            _ = reader.LoadBoolean();
        }

        if (reader.BytesLeftInRecord > 0)
        {
            formula.Syntax = reader.LoadEnum();
        }

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadEnum();
        }

        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadBoolean();
        }

        reader.SkipRestOfRecord();
        return formula;
    }

    private static CrystalSortFieldModel ReadSortField(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences,
        string sortType)
    {
        _ = reader.LoadNextRecord(41, 1793, 103);
        var field = ReadFieldReference(reader, fieldReferences);
        var direction = reader.LoadEnum();
        reader.SkipRestOfRecord();
        return new CrystalSortFieldModel
        {
            Field = field,
            Direction = direction,
            SortType = sortType
        };
    }

    private static CrystalParameterFieldModel ReadParameterField(TslvArchiveReader reader)
    {
        _ = reader.LoadNextRecord(122, 2304, 101);
        var field = ReadFieldHeader(reader);
        var parameter = new CrystalParameterFieldModel
        {
            Name = field.Name,
            NumberOfBytes = field.NumberOfBytes,
            PromptText = field.Name
        };

        if (reader.BytesLeftInRecord >= 2)
        {
            parameter.Id = reader.LoadUInt16();
        }

        parameter.PromptText = reader.BytesLeftInRecord > 0 ? reader.LoadString() ?? field.Name : field.Name;
        _ = reader.BytesLeftInRecord >= 2 && reader.LoadBoolean();
        if (reader.BytesLeftInRecord > 0)
        {
            parameter.HasBrowseField = ReadFieldReferencePresence(reader);
        }

        parameter.ValueType = reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : field.ValueType;
        var defaultCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        for (var i = 0; i < defaultCount; i++)
        {
            var value = ReadCrystalValue(reader, parameter.ValueType);
            if (value is not null)
            {
                parameter.DefaultValues.Add(new CrystalParameterDefaultValueModel { Value = value });
            }
        }

        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadBoolean();
        }

        if (reader.BytesLeftInRecord >= 16)
        {
            _ = reader.LoadDouble();
            _ = reader.LoadDouble();
        }

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadString();
        }

        if (reader.BytesLeftInRecord >= 32)
        {
            for (var i = 0; i < 8; i++)
            {
                _ = reader.LoadInt32();
            }
        }

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadEnum();
            parameter.AllowNull = reader.BytesLeftInRecord >= 2 && reader.LoadBoolean();
        }

        if (reader.BytesLeftInRecord >= 2)
        {
            parameter.AllowCustomValues = !reader.LoadBoolean();
        }

        if (reader.BytesLeftInRecord >= 6)
        {
            parameter.AllowMultiple = reader.LoadBoolean();
            parameter.AllowDiscrete = reader.LoadBoolean();
            parameter.AllowRange = reader.LoadBoolean();
        }

        if (reader.BytesLeftInRecord >= 6)
        {
            _ = reader.LoadBoolean();
            _ = reader.LoadUInt16();
            _ = reader.LoadBoolean();
        }

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadString();
        }

        if (reader.BytesLeftInRecord >= 2 && reader.LoadBoolean())
        {
            parameter.AllowDiscrete = true;
            parameter.AllowRange = true;
        }

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadEnum();
        }

        if (reader.BytesLeftInRecord >= 6)
        {
            _ = reader.LoadBoolean();
            _ = reader.LoadEnum();
            _ = reader.LoadBoolean();
        }

        for (var i = 0; i < parameter.DefaultValues.Count && reader.BytesLeftInRecord > 0; i++)
        {
            parameter.DefaultValues[i].Description = reader.LoadString() ?? "";
        }

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadEnum();
        }

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadString();
        }

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadString();
            if (reader.BytesLeftInRecord >= 2)
            {
                _ = reader.LoadBoolean();
            }
        }

        if (reader.BytesLeftInRecord >= 32)
        {
            var networkHigh = reader.LoadInt64();
            var networkLow = reader.LoadInt64();
            var promptHigh = reader.LoadInt64();
            var promptLow = reader.LoadInt64();
            parameter.HasPromptMetadata =
                networkHigh != 0 ||
                networkLow != 0 ||
                promptHigh != 0 ||
                promptLow != 0;
        }

        reader.SkipRestOfRecord();
        if (parameter.Name.StartsWith("Pm-", StringComparison.OrdinalIgnoreCase))
        {
            parameter.AllowNull = true;
        }

        return parameter;
    }

    private static string? ReadCrystalValue(TslvArchiveReader reader, int valueType)
    {
        var length = reader.LegacyValueLengths ? reader.LoadUInt16() : reader.LoadInt32();
        if (length == 0)
        {
            return null;
        }

        return valueType switch
        {
            2 => reader.LoadInt16().ToString(System.Globalization.CultureInfo.InvariantCulture),
            4 or 5 => reader.LoadInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            6 or 7 or 16 => FormatCrystalScaledNumber(reader.LoadDouble()),
            8 => LowerBool(reader.LoadBoolean()),
            9 => CrystalReportParametersParser.CrystalDate(reader.LoadInt32()),
            10 => CrystalReportParametersParser.CrystalTime(reader.LoadInt32()),
            11 or 13 => reader.LoadString() ?? "",
            15 => CrystalReportParametersParser.CrystalDateTime(reader.LoadInt32(), reader.LoadInt32()),
            _ => SkipCrystalValue(reader, length)
        };
    }

    private static string SkipCrystalValue(TslvArchiveReader reader, int length)
    {
        reader.SkipBytes(length);
        return "";
    }

    private static string LowerBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string FormatCrystalScaledNumber(double scaled)
    {
        var value = scaled / 100d;
        return value.ToString("0.0##############", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadSpecialField(TslvArchiveReader reader)
    {
        return SkipFieldLikeRecord(reader);
    }

    private static CrystalSummaryFieldModel ReadSummaryField(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences)
    {
        _ = reader.LoadNextRecord(127, 1792, 101);
        _ = reader.LoadNextRecord(126, 1792, 128);
        var field = ReadFieldHeader(reader);
        var summary = new CrystalSummaryFieldModel
        {
            Name = field.Name,
            ValueType = field.ValueType,
            NumberOfBytes = field.NumberOfBytes,
            Operation = reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0
        };

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadEnum();
        }

        summary.OperationParameter = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        summary.SummarizedField = reader.BytesLeftInRecord > 0
            ? ReadFieldReference(reader, fieldReferences)
            : "";

        if (reader.BytesLeftInRecord > 0)
        {
            _ = ReadFieldReference(reader, fieldReferences);
        }

        if (reader.BytesLeftInRecord > 0)
        {
            var hasPercentSummary = reader.LoadBoolean();
            if (hasPercentSummary && reader.BytesLeftInRecord >= 2)
            {
                _ = reader.LoadInt16();
            }
        }

        if (reader.BytesLeftInRecord > 0)
        {
            // HierarchicalSummaryType: 1 = summarize across the hierarchy.
            summary.AcrossHierarchy = reader.LoadEnum() == 1;
        }

        if (reader.BytesLeftInRecord > 0)
        {
            var summaryType = reader.LoadEnum();
            if (summaryType != 0 && reader.BytesLeftInRecord >= 2)
            {
                _ = reader.LoadInt16();
            }
        }

        reader.SkipRestOfRecord();

        summary.SummaryKind = reader.BytesLeftInRecord >= 2 ? reader.LoadInt16() : 0;
        summary.GroupIndex = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        reader.SkipRestOfRecord();
        return summary;
    }

    private static CrystalRunningTotalFieldModel ReadRunningTotalField(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel dataDefinition)
    {
        _ = reader.LoadNextRecord(128, 1792, 101);
        _ = reader.LoadNextRecord(126, 1792, 129);
        var field = ReadFieldHeader(reader);
        var runningTotal = new CrystalRunningTotalFieldModel
        {
            Name = field.Name,
            ValueType = field.ValueType,
            NumberOfBytes = field.NumberOfBytes,
            Operation = reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0
        };

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadEnum();
        }

        runningTotal.OperationParameter = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        runningTotal.SummarizedField = reader.BytesLeftInRecord > 0
            ? ReadFieldReference(reader, fieldReferences)
            : "";

        if (reader.BytesLeftInRecord > 0)
        {
            _ = ReadFieldReference(reader, fieldReferences);
        }

        if (reader.BytesLeftInRecord > 0)
        {
            var hasPercentSummary = reader.LoadBoolean();
            if (hasPercentSummary && reader.BytesLeftInRecord >= 2)
            {
                _ = reader.LoadInt16();
            }
        }

        if (reader.BytesLeftInRecord > 0)
        {
            _ = reader.LoadEnum();
        }

        if (reader.BytesLeftInRecord > 0)
        {
            var summaryType = reader.LoadEnum();
            if (summaryType != 0 && reader.BytesLeftInRecord >= 2)
            {
                _ = reader.LoadInt16();
            }
        }

        reader.SkipRestOfRecord();

        (runningTotal.ResetConditionType, runningTotal.ResetConditionField, runningTotal.ResetConditionGroup, runningTotal.ResetConditionFormula) = ReadRunningTotalCondition(reader, fieldReferences, dataDefinition);
        (runningTotal.EvaluationConditionType, runningTotal.EvaluationConditionField, runningTotal.EvaluationConditionGroup, runningTotal.EvaluationConditionFormula) = ReadRunningTotalCondition(reader, fieldReferences, dataDefinition);
        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadBoolean();
        }

        reader.SkipRestOfRecord();
        return runningTotal;
    }

    // RunningTotalCondition: condition type 3 references the RunningTotalConditionFormula in the formula field list.
    private static (int Type, string Field, int Group, CrystalFormulaFieldModel? Formula) ReadRunningTotalCondition(TslvArchiveReader reader, FieldReferenceTable fieldReferences, CrystalDataDefinitionModel dataDefinition)
    {
        if (reader.BytesLeftInRecord <= 0)
        {
            return (0, "", 0, null);
        }

        var conditionType = reader.LoadEnum();
        var field = "";
        var group = 0;
        CrystalFormulaFieldModel? formula = null;
        switch (conditionType)
        {
            case 1:
                field = ReadFieldReference(reader, fieldReferences);
                break;
            case 2:
                if (reader.BytesLeftInRecord >= 2)
                {
                    group = reader.LoadUInt16();
                }
                break;
            case 3:
                if (reader.BytesLeftInRecord > 0)
                {
                    reader.SkipString();
                    var type = reader.LoadEnum();
                    var index = reader.LoadUInt16();
                    formula = type == 1 && index < dataDefinition.FormulaFields.Count
                        ? dataDefinition.FormulaFields[index]
                        : throw new InvalidDataException($"Running total condition formula reference ({type}, {index}) does not name a formula field.");
                }
                break;
        }

        return (conditionType, field, group, formula);
    }

    private static string SkipFieldLikeRecord(TslvArchiveReader reader)
    {
        var record = reader.LoadAnyRecord();
        var result = "";
        try
        {
            var nested = reader.LoadAnyRecord();
            if (nested.Type is 113 or 114 or 118)
            {
                if (nested.Type == 113)
                {
                    var field = ReadFieldHeaderBody(reader);
                    result = string.IsNullOrWhiteSpace(field.Name) ? "" : "{" + field.Name + "}";
                }
                else
                {
                    var header = reader.LoadNextRecord(113, 1792, 101);
                    _ = header;
                    var field = ReadFieldHeaderBody(reader);
                    result = string.IsNullOrWhiteSpace(field.Name) ? "" : "{" + field.Name + "}";
                }
            }
        }
        catch
        {
            // Best effort for field references that are not needed by current XML.
        }
        finally
        {
            while (reader.CurrentRecord?.Type != record.Type && reader.CurrentRecord is not null)
            {
                reader.SkipRestOfRecord();
            }

            reader.SkipRestOfRecord();
        }

        return result;
    }

    private static CrystalFieldHeader ReadFieldHeader(TslvArchiveReader reader)
    {
        _ = reader.LoadNextRecord(113, 1792, 101);
        var field = ReadFieldHeaderBody(reader);
        reader.SkipRestOfRecord();
        return field;
    }

    private static CrystalFieldHeader ReadFieldHeaderBody(TslvArchiveReader reader)
    {
        var name = reader.LoadString() ?? "";
        var valueType = reader.LoadEnum();
        var numberOfBytes = reader.LoadUInt16();
        if (valueType == 11)
        {
            numberOfBytes *= 2;
        }

        _ = reader.LoadString();
        if (reader.BytesLeftInRecord >= 4)
        {
            numberOfBytes = reader.LoadInt32();
        }

        return new CrystalFieldHeader(name, valueType, numberOfBytes);
    }

    private static string ReadFieldReference(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel? dataDefinition = null)
    {
        var name = reader.LoadString() ?? "";
        var type = reader.LoadEnum();
        var index = reader.LoadUInt16();
        var resolved = fieldReferences.Get(type, index);
        if (type == 4 &&
            dataDefinition is not null &&
            TryParseGroupReference(string.IsNullOrWhiteSpace(resolved) ? name : resolved, out var resolvedGroupNumber))
        {
            return resolvedGroupNumber > 0 && resolvedGroupNumber <= dataDefinition.Groups.Count
                ? "GroupName (" + dataDefinition.Groups[resolvedGroupNumber - 1].ConditionField + ")"
                : "Group #" + resolvedGroupNumber;
        }

        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        if (type == 3)
        {
            return NormalizeSpecialFieldReference(name);
        }

        return name;
    }

    // After a field's reference Crystal stores the field's own index and type again (the record's last five bytes; older versions put four more bytes
    // before them), flagged when the reference is a stand-in: a special field an older reader does not know (page N of M, current user, time zones,
    // locales) is referenced as the nearest one it does (field manager byte()/try()). Only that flagged special-field identity replaces the reference.
    private static string? ReadSpecialFieldIdentity(TslvArchiveReader reader, FieldReferenceTable fieldReferences)
    {
        var length = reader.ReadEnumsAsInt32 ? 8 : 5;
        if (reader.BytesLeftInRecord < length) return null;
        reader.SkipBytes(reader.BytesLeftInRecord - length);
        var flagged = reader.LoadBoolean();
        var index = reader.LoadUInt16();
        var type = reader.LoadEnum();
        return flagged && type == 3 && fieldReferences.Get(type, index) is { Length: > 0 } special ? special : null;
    }

    private static string NormalizeSpecialFieldReference(string name)
    {
        return string.Concat(name.Where(char.IsLetterOrDigit));
    }

    private static bool TryParseGroupReference(string name, out int groupNumber)
    {
        groupNumber = 0;
        // Legacy group-field identities include a trailing "Name"; it is not display text.
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^Group #(\d+)(?: Name)?$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success &&
               int.TryParse(
                   match.Groups[1].Value,
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out groupNumber);
    }

    private static void SkipFieldReference(TslvArchiveReader reader)
    {
        reader.SkipString();
        _ = reader.LoadEnum();
        _ = reader.LoadUInt16();
    }

    private static bool ReadFieldReferencePresence(TslvArchiveReader reader)
    {
        var name = reader.LoadString() ?? "";
        var type = reader.LoadEnum();
        var index = reader.LoadUInt16();
        return !string.IsNullOrWhiteSpace(name) || type != 0 || index != 65535;
    }

    private static string SummaryFormulaName(
        CrystalSummaryFieldModel summary,
        CrystalDataDefinitionModel dataDefinition)
    {
        var groupField = SummaryGroupField(summary, dataDefinition);
        return string.IsNullOrWhiteSpace(groupField)
            ? $"{SummaryOperationName(summary.Operation)} ({summary.SummarizedField})"
            : $"{SummaryOperationName(summary.Operation)} ({summary.SummarizedField}, {groupField})";
    }

    private static string SummaryGroupField(
        CrystalSummaryFieldModel summary,
        CrystalDataDefinitionModel dataDefinition)
    {
        if (summary.SummaryKind != 1 || summary.GroupIndex == 0)
        {
            return "";
        }

        var index = summary.GroupIndex - 1;
        return index >= 0 && index < dataDefinition.Groups.Count
            ? dataDefinition.Groups[index].ConditionField
            : "";
    }

    private static string SummaryOperationName(int operation)
    {
        return operation switch
        {
            1 => "Average",
            2 => "Variance",
            3 => "Standard Deviation",
            4 => "Maximum",
            5 => "Minimum",
            6 => "Count",
            7 => "Population Variance",
            8 => "Population Standard Deviation",
            9 => "DistinctCount",
            10 => "Correlation",
            11 => "Covariance",
            12 => "Weighted Average",
            13 => "Median",
            14 => "Percentile",
            15 => "Nth Largest",
            16 => "Nth Smallest",
            17 => "Mode",
            18 => "Nth Most Frequest",
            19 => "Place Holder Operation",
            20 => "No Operation",
            21 => "Sorted Values",
            _ => "Sum"
        };
    }

    private static void ApplySummaryFieldApiType(
        CrystalSummaryFieldModel summary,
        CrystalDatabaseModel database,
        CrystalDataDefinitionModel dataDefinition)
    {
        if (summary.Operation is not (4 or 5 or 6 or 9))
        {
            return;
        }

        if (TryResolveFieldType(summary.SummarizedField, database, dataDefinition, out var valueType, out var numberOfBytes))
        {
            summary.ValueType = valueType;
            summary.NumberOfBytes = numberOfBytes;
        }
    }

    // Crystal resets a group condition its field's type cannot take to 0 when it loads the report (GroupOptions.a): a date takes 0-7, a time 0-3,
    // a date-time 0-11 and a Boolean 0-6 only in original order; any other type groups on every change. An unresolved field keeps its condition.
    private static void NormalizeGroupConditions(CrystalDatabaseModel database, CrystalDataDefinitionModel dataDefinition)
    {
        foreach (var group in dataDefinition.Groups)
        {
            if (group.Condition == 0 || !TryResolveFieldType(group.ConditionField, database, dataDefinition, out var valueType, out _)) continue;
            var valid = valueType switch
            {
                9 => group.Condition is > 0 and < 8,
                10 => group.Condition is > 0 and < 4,
                15 => group.Condition is > 0 and < 12,
                8 => group.Condition is > 0 and < 7 && group.Direction == 2,
                _ => false
            };
            if (!valid) group.Condition = 0;
        }
    }

    private static bool TryResolveFieldType(
        string formulaName,
        CrystalDatabaseModel database,
        CrystalDataDefinitionModel dataDefinition,
        out int valueType,
        out int numberOfBytes)
    {
        foreach (var formula in dataDefinition.FormulaFields)
        {
            if (string.Equals(formula.FormulaName, formulaName, StringComparison.OrdinalIgnoreCase))
            {
                valueType = formula.ValueType;
                numberOfBytes = formula.NumberOfBytes;
                return true;
            }
        }

        foreach (var parameter in dataDefinition.Parameters)
        {
            if (string.Equals(parameter.FormulaName, formulaName, StringComparison.OrdinalIgnoreCase))
            {
                valueType = parameter.ValueType;
                numberOfBytes = parameter.NumberOfBytes;
                return true;
            }
        }

        foreach (var field in database.Tables.SelectMany(table => table.Fields))
        {
            if (string.Equals(field.FormulaName, formulaName, StringComparison.OrdinalIgnoreCase))
            {
                valueType = CrystalFieldTypeMapper.FieldValueType(field.DataType);
                numberOfBytes = valueType == field.DataType ? field.Length : 8;
                return true;
            }
        }

        valueType = 0;
        numberOfBytes = 0;
        return false;
    }

    private static void ReadAreaPair(
        TslvArchiveReader reader,
        CrystalDataDefinitionModel dataDefinition,
        FieldReferenceTable fieldReferences,
        int areaPairOrdinal)
    {
        var record = reader.LoadAnyRecord();
        var endType = record.Type switch
        {
            130 => 131,
            132 => 133,
            134 => 135,
            136 => 137,
            _ => 0
        };

        if (endType == 0)
        {
            reader.SkipRestOfRecord();
            return;
        }

        var layoutOrdinal = record.Type == 136
            ? dataDefinition.ReportDefinition.Areas.Count(area => area.Kind == "GroupHeader") + 1
            : 1;
        var enableRepeatGroupHeader = false;
        var keepGroupTogether = false;
        var groupIndent = 0;
        if (record.Type == 136)
        {
            // GroupAreaPair: repeat group header, then keep group together.
            enableRepeatGroupHeader = reader.BytesLeftInRecord >= 2 && reader.LoadBoolean();
            keepGroupTogether = reader.BytesLeftInRecord >= 2 && reader.LoadBoolean();

            // GroupAreaPair: the hierarchical group indent (twips per hierarchy level) follows the two flags.
            if (reader.BytesLeftInRecord >= 4)
            {
                groupIndent = Math.Max(0, reader.LoadInt32());
            }
        }

        reader.SkipRestOfRecord();

        CrystalGroupModel? rangesTarget = null;
        while (reader.BytesLeftInRecord > 0)
        {
            var next = reader.LoadAnyRecord();
            var ranges = rangesTarget;
            rangesTarget = null;
            if (next.Type == endType)
            {
                reader.SkipRestOfRecord();
                break;
            }

            if (next.Type == 138)
            {
                ReadArea(
                    reader,
                    dataDefinition,
                    fieldReferences,
                    layoutOrdinal,
                    enableRepeatGroupHeader,
                    keepGroupTogether);
                continue;
            }

            if (next.Type == 229)
            {
                var conditionField = ReadFieldReference(reader, fieldReferences);
                var condition = reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0;
                var direction = reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0;
                var group = new CrystalGroupModel { ConditionField = conditionField, Condition = condition, Direction = direction };
                var hasRanges = ReadGroupOptions(reader, group, dataDefinition, fieldReferences);
                group.HierarchicalIndent = group.ParentIdField.Length > 0 ? groupIndent : 0;

                if (!string.IsNullOrWhiteSpace(conditionField))
                {
                    dataDefinition.Groups.Add(group);
                    dataDefinition.SortFields.Add(new CrystalSortFieldModel
                    {
                        Field = conditionField,
                        Direction = direction,
                        SortType = "GroupSortField"
                    });
                    rangesTarget = hasRanges ? group : null;
                }

                reader.SkipRestOfRecord();
                continue;
            }

            // The specified-order ValueRangeList follows its GroupOptions: the unspecified-values type, one record per named group
            // (name, selection expression) and an end record.
            if (ranges is not null && next.Type is 231 or 232 or 233)
            {
                switch (next.Type)
                {
                    case 231:
                        ranges.UnspecifiedValues = reader.LoadEnum();
                        if (ranges.UnspecifiedValues is < 0 or > 2)
                            throw new InvalidDataException($"Group {ranges.ConditionField} has an invalid unspecified-values type {ranges.UnspecifiedValues}.");
                        rangesTarget = ranges;
                        break;
                    case 233:
                        ranges.SpecifiedGroups.Add((reader.LoadString() ?? "", reader.LoadString() ?? ""));
                        rangesTarget = ranges;
                        break;
                }

                reader.SkipRestOfRecord();
                continue;
            }

            reader.SkipRestOfRecord();
        }
    }

    // GroupOptions after the condition, type and direction (GroupOptions.load): a reserved enum, the specified-order others name,
    // TopNGroupInfo (group count, discard others, others name), the group-name field, the group-order formula, the legacy
    // summary-sort index, showLastDateInPeriod, a flag, whether a ValueRangeList follows, two optional flags, the group-name formula,
    // the hierarchical options (group hierarchically, parent ID field, instance ID field), the GroupNameFormat, the top percentage,
    // cross-tab sort pairs, with ties, the Top N value formula, the group sort-order formula and the Top N count, percentage and
    // direction the SDK reports. Older records end early. A hierarchy needs two distinct fields; the group-name formula names the
    // group unless the format is useConditionField (0). Returns whether the specified-order ValueRangeList records follow.
    private static bool ReadGroupOptions(
        TslvArchiveReader reader, CrystalGroupModel group, CrystalDataDefinitionModel dataDefinition, FieldReferenceTable fieldReferences)
    {
        bool More() => reader.BytesLeftInRecord > 0;
        if (!More()) return false;
        _ = reader.LoadEnum();
        group.SpecifiedOthersName = reader.LoadString() is { Length: > 0 } others ? others : "Others";
        var count = reader.LoadInt16();
        group.DiscardOthers = reader.LoadBoolean();
        group.OthersName = reader.LoadString() ?? "";
        SkipFieldReference(reader);
        SkipFieldReference(reader);
        _ = reader.LoadInt16();
        group.ShowLastDateInPeriod = reader.LoadBoolean();
        _ = reader.LoadBoolean();
        var hasRanges = reader.LoadBoolean();
        if (More()) _ = reader.LoadBoolean();
        if (More()) _ = reader.LoadBoolean();
        CrystalFormulaFieldModel? nameFormula = null;
        if (More())
        {
            reader.SkipString();
            var nameType = reader.LoadEnum();
            var nameIndex = reader.LoadUInt16();
            nameFormula = nameType == 1 && nameIndex < dataDefinition.FormulaFields.Count ? dataDefinition.FormulaFields[nameIndex] : null;
        }
        if (More())
        {
            var hierarchical = reader.LoadBoolean();
            var parent = ReadFieldReference(reader, fieldReferences);
            var instance = ReadFieldReference(reader, fieldReferences);
            group.ParentIdField = hierarchical && parent.Length > 0 && instance.Length > 0 && !parent.Equals(instance, StringComparison.OrdinalIgnoreCase) ? parent : "";
            group.InstanceIdField = instance;
        }
        var nameFormat = More() ? reader.LoadEnum() : 0;
        group.NameFormula = nameFormat != 0 && !string.IsNullOrWhiteSpace(nameFormula?.FormulaText) ? nameFormula : null;
        var percentage = More() ? reader.LoadDouble() : 0d;
        if (More())
        {
            var pairs = reader.LoadUInt16();
            for (var pair = 0; pair < pairs; pair++)
            {
                _ = reader.LoadInt32();
                _ = reader.LoadInt32();
            }
        }
        if (More()) group.WithTies = reader.LoadBoolean();
        if (More())
        {
            var name = reader.LoadString() ?? "";
            var type = reader.LoadEnum();
            var index = reader.LoadUInt16();
            if (!string.IsNullOrWhiteSpace(name) || type != 0 || index != 65535)
            {
                var reference = fieldReferences.Get(type, index);
                group.TopNFormula = dataDefinition.FormulaFields.FirstOrDefault(formula =>
                    reference.Length > 0 && formula.FormulaName.Equals(reference, StringComparison.OrdinalIgnoreCase));
                if (group.TopNFormula is null)
                    dataDefinition.ParseWarnings.Add($"The Top N count formula '{name}' of group {group.ConditionField} could not be resolved; it is not emitted.");
                group.TopNHasFormula = true;
            }

            group.TopNFormulaIsPercentage = reader.LoadBoolean();
        }
        if (More())
        {
            SkipFieldReference(reader);
            _ = reader.LoadBoolean();
        }
        if (More())
        {
            count = reader.LoadInt16();
            percentage = reader.LoadDouble();
            _ = reader.LoadEnum();
        }

        group.TopNCount = Math.Max(count, 0);
        group.TopNPercentage = double.IsFinite(percentage) && percentage > 0 ? percentage : 0;
        return hasRanges;
    }

    private static void ReadArea(
        TslvArchiveReader reader,
        CrystalDataDefinitionModel dataDefinition,
        FieldReferenceTable fieldReferences,
        int areaPairOrdinal,
        bool enableRepeatGroupHeader,
        bool keepGroupTogether)
    {
        var areaHeader = ReadAreaHeader(reader);
        var areaFormat = new CrystalSectionFormatModel { EnableKeepTogether = false };
        CrystalReportAreaModel? area = null;

        while (reader.BytesLeftInRecord > 0)
        {
            var next = reader.LoadAnyRecord();
            if (next.Type == 139)
            {
                reader.SkipRestOfRecord();
                break;
            }

            if (SectionKind(next.Type) is { Length: > 0 } sectionKind)
            {
                area ??= new CrystalReportAreaModel
                {
                    Kind = sectionKind,
                    Name = string.IsNullOrWhiteSpace(areaHeader.Name)
                        ? AreaName(sectionKind, areaPairOrdinal)
                        : areaHeader.Name,
                    GroupIndex = AreaOrdinal(areaHeader.Name, areaPairOrdinal),
                    GroupPairOrder = sectionKind is "GroupHeader" or "GroupFooter" ? areaPairOrdinal : 0,
                    EnableRepeatGroupHeader = enableRepeatGroupHeader,
                    EnableKeepGroupTogether = keepGroupTogether,
                    Format = areaFormat
                };

                var sectionIndex = area.Sections.Count + 1;
                area.Sections.Add(ReadSection(
                    reader,
                    dataDefinition,
                    fieldReferences,
                    next.Type,
                    sectionKind,
                    areaPairOrdinal,
                    sectionIndex));
                continue;
            }

            if (next.Type == 255)
            {
                areaFormat = ReadSectionProperties(reader, area: true, fieldReferences, dataDefinition);
                if (area is not null)
                {
                    area.Format = areaFormat;
                }

                continue;
            }

            reader.SkipRestOfRecord();
        }

        if (area is not null)
        {
            dataDefinition.ReportDefinition.Areas.Add(area);
        }
    }

    private static CrystalAreaHeader ReadAreaHeader(TslvArchiveReader reader)
    {
        try
        {
            if (reader.BytesLeftInRecord >= 4)
            {
                _ = reader.LoadInt32();
            }

            if (reader.BytesLeftInRecord >= 2)
            {
                _ = reader.LoadBoolean();
            }

            var name = reader.BytesLeftInRecord > 0 ? reader.LoadString() ?? "" : "";
            reader.SkipRestOfRecord();
            return new CrystalAreaHeader(name);
        }
        catch
        {
            reader.SkipRestOfRecord();
            return new CrystalAreaHeader("");
        }
    }

    private static CrystalReportSectionModel ReadSection(
        TslvArchiveReader reader,
        CrystalDataDefinitionModel dataDefinition,
        FieldReferenceTable fieldReferences,
        int startType,
        string kind,
        int areaPairOrdinal,
        int sectionIndex)
    {
        var sectionHeader = ReadSectionHeader(reader, startType);
        reader.SkipRestOfRecord();

        var endType = startType + 1;
        var format = new CrystalSectionFormatModel();
        var reportObjects = new List<CrystalReportObjectModel>();
        while (reader.BytesLeftInRecord > 0)
        {
            var next = reader.LoadAnyRecord();
            if (next.Type == endType)
            {
                reader.SkipRestOfRecord();
                break;
            }

            if (next.Type == 163)
            {
                var reportObject = ReadSubreportObject(reader, fieldReferences, dataDefinition);
                if (reportObject is not null)
                {
                    reportObjects.Add(reportObject);
                }

                continue;
            }

            if (next.Type is 180 or 185)
            {
                reportObjects.Add(ReadUnsupportedObject(reader, endType, fieldReferences, dataDefinition));
                continue;
            }

            if (IsReportObjectStart(next.Type))
            {
                var reportObject = ReadReportObject(reader, next.Type, fieldReferences, dataDefinition);
                if (reportObject is not null)
                {
                    reportObjects.Add(reportObject);
                }

                continue;
            }

            if (next.Type == 255)
            {
                format = ReadSectionProperties(
                    reader,
                    area: false,
                    fieldReferences,
                    dataDefinition);
                continue;
            }

            reader.SkipRestOfRecord();
        }

        var section = new CrystalReportSectionModel
        {
            Kind = kind,
            Name = string.IsNullOrWhiteSpace(sectionHeader.Name)
                ? SectionName(kind, areaPairOrdinal, sectionIndex)
                : sectionHeader.Name,
            Height = sectionHeader.Height,
            Format = format
        };
        section.ReportObjects.AddRange(reportObjects);
        return section;
    }

    private static CrystalSectionHeader ReadSectionHeader(TslvArchiveReader reader, int startType)
    {
        _ = reader.LoadNextRecord(140, 1792, startType + 1);
        var height = reader.BytesLeftInRecord >= 4 ? reader.LoadInt32() : 0;
        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadBoolean();
        }

        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadUInt16();
        }

        var name = reader.BytesLeftInRecord > 0 ? reader.LoadString() ?? "" : "";
        reader.SkipRestOfRecord();
        return new CrystalSectionHeader(name, height);
    }

    private static CrystalReportObjectModel ReadUnsupportedObject(TslvArchiveReader reader, int sectionEndType,
        FieldReferenceTable fieldReferences, CrystalDataDefinitionModel dataDefinition)
    {
        var bindingReader = reader.Fork();
        var start = reader.CurrentRecord!;
        var kind = start.Type == 180 ? "Chart" : "CrossTab";
        var source = new CrystalUnsupportedObjectSource
        {
            Offset = start.Offset, RecordType = start.Type, Schema = start.Schema
        };
        var reportObject = new CrystalReportObjectModel
        {
            ElementName = kind + "Object", Kind = kind, Name = kind + "@" + start.Offset,
            UnsupportedSource = source
        };

        // Legacy ChartObject -> AnalysisObject (179) -> OleObject (174) -> base (158).
        // Legacy CrossTabObject -> GridObject (184) -> base (158).
        ReadMetadata("identity/size", reader.Fork(), probe =>
        {
            if (kind == "Chart")
            {
                probe.LoadNextRecord(179, 1792, 181);
                probe.LoadNextRecord(174, 1792, 181);
            }
            else probe.LoadNextRecord(184, 1792, 186);
            probe.LoadNextRecord(158, 1792, start.Type + 1);
            reportObject.Width = Math.Abs(probe.LoadInt32());
            reportObject.Height = Math.Abs(probe.LoadInt32());
            if (probe.BytesLeftInRecord >= 8) { probe.LoadInt32(); probe.LoadInt32(); }
            var name = probe.LoadString();
            if (!string.IsNullOrWhiteSpace(name)) reportObject.Name = name;
        });

        reader.SkipRestOfRecord();
        var nestedEnds = new Stack<int>();
        while (reader.BytesLeftInRecord > 0)
        {
            var next = reader.Fork().LoadAnyRecord();
            // An incomplete object must not consume the next object or section.
            if (next.Type == sectionEndType || next.Type is 101 or 139 || SectionKind(next.Type).Length > 0) break;
            if (nestedEnds.Count == 0 && (next.Type is 255 or 163 or 172 or 177 or 180 or 182 or 185 or 187 or 386
                || IsReportObjectStart(next.Type))) break;
            // Cross-tab column/row/cell bodies contain their own report objects and common records.
            // Chart definitions likewise own all records through their matching terminator.
            if (next.Type is 206 or 210 or 215 or 296) nestedEnds.Push(next.Type + 1);
            reader.LoadAnyRecord();
            if (nestedEnds.Count == 0 && next.Type == 190)
                ReadMetadata("position", reader.Fork(), probe =>
                {
                    reportObject.Left = probe.LoadInt32Compressed();
                    reportObject.Top = probe.LoadInt32Compressed();
                });
            else if (nestedEnds.Count == 0 && next.Type == 253)
                ReadMetadata("format", reader.Fork(), probe => reportObject.Format = ReadObjectFormat(probe));
            else if (nestedEnds.Count == 0 && next.Type == 237)
                ReadMetadata("border", reader.Fork(), probe => reportObject.Border = ReadObjectBorder(probe));
            reader.SkipRestOfRecord();
            if (nestedEnds.TryPeek(out var nestedEnd) && next.Type == nestedEnd) nestedEnds.Pop();
            if (next.Type == start.Type + 1) { source.Complete = nestedEnds.Count == 0; break; }
        }
        source.ArchiveBytes = reader.CopyRange(start.Offset, reader.Position - start.Offset);
        if (!source.Complete) source.MetadataDiagnostics.Add("Object end record is missing; retained bytes stop before the next object/section.");
        if (source.Complete) fieldReferences.AnalyticalObjects.Add((reportObject, bindingReader));
        return reportObject;

        void ReadMetadata(string label, TslvArchiveReader probe, Action<TslvArchiveReader> read)
        {
            try { read(probe); }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException or ArgumentException)
            { source.MetadataDiagnostics.Add($"Could not fully decode {label}: {ex.Message}"); }
        }
    }

    private static bool IsReportObjectStart(int recordType)
    {
        return recordType is 159 or 165 or 170 or 175;
    }

    private static CrystalReportObjectModel? ReadReportObject(
        TslvArchiveReader reader,
        int startType,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel dataDefinition)
    {
        try
        {
            return startType switch
            {
                159 => ReadFieldObject(reader, fieldReferences, dataDefinition),
                165 => ReadTextObject(reader, fieldReferences, dataDefinition),
                170 => ReadLineObject(reader),
                175 => ReadPictureObject(reader),
                _ => null
            };
        }
        catch
        {
            if (Environment.GetEnvironmentVariable("FLEXKIT_DEBUG_RPT") == "1")
            {
                throw;
            }

            while (reader.CurrentRecord is not null)
            {
                reader.SkipRestOfRecord();
            }

            return null;
        }
    }

    private static CrystalReportObjectModel ReadFieldObject(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel dataDefinition)
    {
        var reportObject = ReadReportObjectBase(reader, "FieldObject", "Field", 160);
        reportObject.DataSource = ReadFieldReference(reader, fieldReferences, dataDefinition);
        reportObject.DataSource = ReadSpecialFieldIdentity(reader, fieldReferences) ?? reportObject.DataSource;

        reader.SkipRestOfRecord();
        ReadCommonReportObjectRecords(reader, reportObject);
        ReadFontColourProperties(reader, reportObject);
        SkipUntilRecord(reader, 160);
        return reportObject;
    }

    private static CrystalReportObjectModel ReadSubreportObject(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel dataDefinition)
    {
        var reportObject = ReadReportObjectBase(reader, "SubreportObject", "Subreport", 164);
        if (reader.BytesLeftInRecord >= 4)
        {
            reportObject.SubreportDocumentIndex = reader.LoadInt32();
            if (reportObject.SubreportDocumentIndex >= 0)
            {
                dataDefinition.ReportDefinition.SubreportDocumentIndexes.Add(reportObject.SubreportDocumentIndex);
            }
        }

        if (reader.BytesLeftInRecord >= 2)
        {
            var firstFlag = reader.LoadBoolean();
            reportObject.EnableOnDemand = reader.BytesLeftInRecord >= 2
                ? reader.LoadBoolean()
                : firstFlag;
        }

        reader.SkipRestOfRecord();
        ReadCommonReportObjectRecords(reader, reportObject);
        reportObject.Format.EnableCanGrow = true;
        while (reader.BytesLeftInRecord > 0)
        {
            var next = reader.LoadAnyRecord();
            if (next.Type == 260)
            {
                ReadSubreportParameterLinks(reader, fieldReferences, dataDefinition, reportObject);
                continue;
            }

            reader.SkipRestOfRecord();
            if (next.Type == 164) break;
        }

        return reportObject;
    }

    // SubreportParameterLinkObject: record 260 wraps the item count (259), then one ParameterLinkItem (262) per link and
    // the closing record 261. An item holds the subreport parameter id, the main-report field reference and, unless the
    // link only feeds the parameter, the subreport field's definition type and field-manager index.
    private static void ReadSubreportParameterLinks(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel dataDefinition,
        CrystalReportObjectModel reportObject)
    {
        _ = reader.LoadNextRecord(259, 3072, 261);
        var count = reader.LoadUInt16();
        reader.SkipRestOfRecord();
        reader.SkipRestOfRecord();
        for (var i = 0; i < count; i++)
        {
            _ = reader.LoadNextRecord(262, 3072, 261);
            var parameterId = reader.LoadUInt16();
            var mainField = ReadFieldReference(reader, fieldReferences, dataDefinition);
            var parameterOnly = reader.LoadBoolean();
            var subreportFieldType = parameterOnly ? -1 : reader.LoadEnum();
            var subreportFieldIndex = parameterOnly ? -1 : reader.LoadUInt16();
            reader.SkipRestOfRecord();
            reportObject.StoredSubreportLinks.Add(new CrystalStoredSubreportLink
            {
                ParameterId = parameterId,
                MainReportFieldName = mainField,
                SubreportFieldType = subreportFieldType,
                SubreportFieldIndex = subreportFieldIndex
            });
        }

        _ = reader.LoadNextRecord(261, 3072, 164);
        reader.SkipRestOfRecord();
    }

    /// <summary>Resolves a field-manager slot of an already parsed report to its Crystal field reference, or "".</summary>
    internal static string ResolveFieldManagerReference(CrystalDataDefinitionModel dataDefinition, int type, int index)
    {
        var reference = new FieldReferenceTable(dataDefinition.FieldManagerReferences).Get(type, index);
        return ReplaceSummaryPlaceholders(ReplaceGroupNamePlaceholders(reference, dataDefinition), dataDefinition);
    }

    private static CrystalReportObjectModel ReadTextObject(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel dataDefinition)
    {
        var reportObject = ReadReportObjectBase(reader, "TextObject", "Text", 166);

        if (reader.BytesLeftInRecord >= 16)
        {
            _ = reader.LoadBoolean();
            _ = reader.LoadBoolean();
            _ = reader.LoadBoolean();
            reportObject.MaxNumberOfLines = reader.LoadUInt16();
            var paragraphCount = reader.LoadUInt16();
            _ = reader.LoadInt32();
            var isHeading = reader.LoadBoolean();
            reader.SkipRestOfRecord();

            ReadCommonReportObjectRecords(reader, reportObject);
            reportObject.Text = ReadTextParagraphs(reader, paragraphCount, fieldReferences, dataDefinition, reportObject);

            if (isHeading)
            {
                reportObject.ElementName = "FieldHeadingObject";
                reportObject.Kind = "FieldHeading";
                if (reader.BytesLeftInRecord > 0)
                {
                    var next = reader.LoadAnyRecord();
                    if (next.Type == 358)
                    {
                        reportObject.FieldObjectName = reader.LoadString() ?? "";
                    }

                    reader.SkipRestOfRecord();
                }
            }

            SkipUntilRecord(reader, 166);
            return reportObject;
        }

        reader.SkipRestOfRecord();
        ReadCommonReportObjectRecords(reader, reportObject);
        SkipUntilRecord(reader, 166);
        return reportObject;
    }

    private static CrystalReportObjectModel ReadLineObject(TslvArchiveReader reader)
    {
        _ = reader.LoadNextRecord(169, 1792, 171);
        var reportObject = ReadReportObjectBase(reader, "LineObject", "Line", 171);
        var endLeft = reportObject.Left + reportObject.Width;
        var endTop = reportObject.Top + reportObject.Height;

        if (reader.BytesLeftInRecord >= 8)
        {
            _ = reader.LoadInt16();
            endLeft = reader.LoadInt32Compressed();
            endTop = reader.LoadInt32Compressed();
            _ = reader.LoadBoolean();
        }

        reader.SkipRestOfRecord();
        var horizontal = false;
        if (reader.BytesLeftInRecord >= 2)
        {
            horizontal = reader.LoadBoolean();
        }

        reader.SkipRestOfRecord();
        if (horizontal)
        {
            if (reportObject.Width == 0)
            {
                reportObject.Width = Math.Abs(endLeft - reportObject.Left);
            }

            reportObject.Height = 0;
        }
        else
        {
            reportObject.Width = 0;
            if (reportObject.Height == 0)
            {
                reportObject.Height = Math.Abs(endTop - reportObject.Top);
            }
        }

        ReadCommonReportObjectRecords(reader, reportObject);
        SkipUntilRecord(reader, 171);
        return reportObject;
    }

    private static CrystalReportObjectModel ReadPictureObject(TslvArchiveReader reader)
    {
        _ = reader.LoadNextRecord(174, 1792, 176);
        var reportObject = ReadReportObjectBase(reader, "PictureObject", "Picture", 176);
        if (reader.BytesLeftInRecord >= 4)
        {
            _ = reader.LoadInt32();
        }

        reader.SkipRestOfRecord();
        if (reader.BytesLeftInRecord >= 4)
        {
            _ = reader.LoadInt32();
        }

        reader.SkipRestOfRecord();
        ReadCommonReportObjectRecords(reader, reportObject);
        // OleObject.cfr_renamed_0: record 189 identifies the compound-file storage.
        _ = reader.LoadNextRecord(189, 1792, 176);
        reportObject.PictureStorageIndex = reader.LoadInt32();
        _ = reader.LoadBoolean();
        reportObject.PictureAspect = reader.LoadInt32();
        reader.SkipRestOfRecord();
        SkipUntilRecord(reader, 176);
        return reportObject;
    }

    private static CrystalReportObjectModel ReadReportObjectBase(
        TslvArchiveReader reader,
        string elementName,
        string kind,
        int stopType)
    {
        _ = reader.LoadNextRecord(158, 1792, stopType);
        var width = reader.LoadInt32();
        var height = reader.LoadInt32();
        if (reader.BytesLeftInRecord >= 8)
        {
            _ = reader.LoadInt32();
            _ = reader.LoadInt32();
        }

        var name = reader.LoadString() ?? "";
        reader.SkipRestOfRecord();

        return new CrystalReportObjectModel
        {
            ElementName = elementName,
            Kind = kind,
            Name = name,
            Width = Math.Abs(width),
            Height = Math.Abs(height)
        };
    }

    private static void ReadCommonReportObjectRecords(
        TslvArchiveReader reader,
        CrystalReportObjectModel reportObject)
    {
        if (reader.BytesLeftInRecord <= 0)
        {
            return;
        }

        var position = reader.LoadAnyRecord();
        if (position.Type == 190)
        {
            reportObject.Left = reader.BytesLeftInRecord > 0 ? reader.LoadInt32Compressed() : 0;
            reportObject.Top = reader.BytesLeftInRecord > 0 ? reader.LoadInt32Compressed() : 0;
        }

        reader.SkipRestOfRecord();

        if (reader.BytesLeftInRecord <= 0)
        {
            return;
        }

        var format = reader.LoadAnyRecord();
        if (format.Type == 253)
        {
            reportObject.Format = ReadObjectFormat(reader);
        }
        else
        {
            reader.SkipRestOfRecord();
        }

        if (reader.BytesLeftInRecord <= 0)
        {
            return;
        }

        var border = reader.LoadAnyRecord();
        if (border.Type == 237)
        {
            reportObject.Border = ReadObjectBorder(reader);
        }
        else
        {
            reader.SkipRestOfRecord();
        }
    }

    private static CrystalObjectFormatModel ReadObjectFormat(TslvArchiveReader reader)
    {
        var format = new CrystalObjectFormatModel();
        try
        {
            _ = reader.LoadNextRecord(252, 1792, 254);
            format.EnableSuppress = !reader.LoadBoolean();
            format.HorizontalAlignment = HorizontalAlignmentName(reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0);
            if (reader.BytesLeftInRecord > 0)
            {
                reader.SkipBytes(1);
            }

            format.EnableKeepTogether = reader.BytesLeftInRecord >= 2 && reader.LoadBoolean();
            format.EnableCloseAtPageBreak = !(reader.BytesLeftInRecord >= 2 && reader.LoadBoolean());
            format.EnableCanGrow = reader.BytesLeftInRecord >= 2 && reader.LoadBoolean();
            reader.SkipRestOfRecord();
            // ReportObjectProperties.w: only the called formatting surface is migrated.
            ReadConditionReferences(reader, format.ConditionReferences, ["EnableSuppress", "HorizontalAlignment", "", "EnableKeepTogether", "EnableCloseAtPageBreak",
                "EnableCanGrow", "ToolTipText", "HyperlinkText", "TextRotation", "CssClass", "DisplayString"]);
        }
        finally
        {
            while (reader.CurrentRecord is not null && reader.CurrentRecord.Type != 253)
            {
                reader.SkipRestOfRecord();
            }

            if (reader.CurrentRecord?.Type == 253)
            {
                reader.SkipRestOfRecord();
            }
        }

        return format;
    }

    private static CrystalBorderModel ReadObjectBorder(TslvArchiveReader reader)
    {
        var border = new CrystalBorderModel();
        try
        {
            _ = reader.LoadNextRecord(236, 1792, 238);
            border.LeftLineStyle = LineStyleName(reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0);
            border.RightLineStyle = LineStyleName(reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0);
            border.TopLineStyle = LineStyleName(reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0);
            border.BottomLineStyle = LineStyleName(reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0);
            if (reader.BytesLeftInRecord >= 2)
            {
                _ = reader.LoadBoolean();
            }

            if (reader.BytesLeftInRecord >= 2)
            {
                _ = reader.LoadBoolean();
            }

            border.HasDropShadow = reader.BytesLeftInRecord >= 2 && reader.LoadBoolean();
            if (reader.BytesLeftInRecord >= 4)
            {
                border.BorderColor = ReadCrystalColor(reader, nullColor: CrystalColorModel.Black());
            }

            if (reader.BytesLeftInRecord >= 4)
            {
                border.BackgroundColor = ReadCrystalColor(reader, nullColor: CrystalColorModel.TransparentWhite());
            }
            reader.SkipRestOfRecord();
            ReadConditionReferences(reader, border.ConditionReferences, ["LeftLineStyle", "RightLineStyle", "TopLineStyle", "BottomLineStyle",
                "EnableTightHorizontal", "", "HasDropShadow", "BorderColor", "BackgroundColor", "LineWidth", "FillStyle"]);
        }
        finally
        {
            while (reader.CurrentRecord is not null && reader.CurrentRecord.Type != 237)
            {
                reader.SkipRestOfRecord();
            }

            if (reader.CurrentRecord?.Type == 237)
            {
                reader.SkipRestOfRecord();
            }
        }

        return border;
    }

    private static string ReadTextParagraphs(
        TslvArchiveReader reader,
        int paragraphCount,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel dataDefinition,
        CrystalReportObjectModel reportObject)
    {
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < paragraphCount; i++)
        {
            if (i > 0)
            {
                text.Append('\n');
                CaptureTextRun(reportObject, "\n", "");
            }

            text.Append(ReadTextParagraph(reader, fieldReferences, dataDefinition, reportObject));
        }

        return text.ToString().Replace('\u00a0', ' ');
    }

    private static string ReadTextParagraph(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel dataDefinition,
        CrystalReportObjectModel reportObject)
    {
        _ = reader.LoadNextRecord(192, 1792, 193);
        _ = reader.BytesLeftInRecord >= 4 ? reader.LoadInt32() : 0;
        _ = reader.BytesLeftInRecord >= 4 ? reader.LoadInt32() : 0;
        _ = reader.BytesLeftInRecord >= 4 ? reader.LoadInt32() : 0;
        var alignment = reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0;
        if (alignment != 0)
        {
            reportObject.Format.HorizontalAlignment = HorizontalAlignmentName(alignment);
        }

        var tabCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        var elementCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        reader.SkipRestOfRecord();

        for (var i = 0; i < tabCount; i++)
        {
            var tab = reader.LoadAnyRecord();
            reader.SkipRestOfRecord();
            _ = tab;
        }

        var text = new System.Text.StringBuilder();
        for (var i = 0; i < elementCount; i++)
        {
            var element = reader.LoadAnyRecord();
            switch (element.Type)
            {
                case 194:
                    var literal = ReadTextElement(reader, reportObject);
                    text.Append(literal);
                    break;
                case 196:
                    var binding = ReadFieldElement(reader, fieldReferences, dataDefinition, reportObject);
                    text.Append(binding);
                    break;
                default:
                    reader.SkipRestOfRecord();
                    break;
            }
        }

        _ = alignment;
        _ = reader.LoadNextRecord(193, 1792, 166);
        reader.SkipRestOfRecord();
        return text.ToString();
    }

    private static string ReadTextElement(
        TslvArchiveReader reader,
        CrystalReportObjectModel reportObject)
    {
        var text = reader.LoadString() ?? "";
        if (reader.BytesLeftInRecord >= 4)
        {
            _ = reader.LoadInt32();
        }

        reader.SkipRestOfRecord();
        var style = ReadFontColourProperties(reader, reportObject);
        _ = reader.LoadNextRecord(195, 1792, 193);
        reader.SkipRestOfRecord();
        CaptureTextRun(reportObject, text, "", style.Font, style.Color);
        return text;
    }

    private static void CaptureTextRun(CrystalReportObjectModel item, string text, string binding, CrystalFontModel? font = null, CrystalColorModel? color = null)
    {
        font ??= item.Font;
        color ??= item.Color;
        item.TextRuns.Add(new(text.Replace('\u00a0', ' '), binding, font.FontFamily, font.Size,
            font.Bold, font.Italic, font.Underline, $"#{color.R:x2}{color.G:x2}{color.B:x2}"));
    }

    private static string ReadFieldElement(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel dataDefinition,
        CrystalReportObjectModel reportObject)
    {
        var text = ReadFieldReference(reader, fieldReferences, dataDefinition);
        if (reader.BytesLeftInRecord >= 4)
        {
            _ = reader.LoadInt32();
        }

        if (reader.BytesLeftInRecord >= 4)
        {
            _ = reader.LoadInt32();
        }

        text = ReadSpecialFieldIdentity(reader, fieldReferences) ?? text;

        reader.SkipRestOfRecord();
        var style = ReadFontColourProperties(reader, reportObject);
        SkipUntilRecord(reader, 197);
        CaptureTextRun(reportObject, "", text, style.Font, style.Color);
        return text;
    }

    private static (CrystalFontModel Font, CrystalColorModel Color) ReadFontColourProperties(
        TslvArchiveReader reader,
        CrystalReportObjectModel reportObject)
    {
        if (reader.BytesLeftInRecord <= 0)
        {
            return (reportObject.Font, reportObject.Color);
        }

        var fontColour = reader.LoadAnyRecord();
        if (fontColour.Type != 257)
        {
            reader.SkipRestOfRecord();
            return (reportObject.Font, reportObject.Color);
        }

        _ = reader.LoadNextRecord(256, 1792, 258);
        var parsedColor = reportObject.Color;
        if (reader.BytesLeftInRecord >= 4)
        {
            parsedColor = ReadCrystalColor(reader, nullColor: CrystalColorModel.Black());
        }

        while (reader.CurrentRecord is not null && reader.CurrentRecord.Type != 257)
        {
            reader.SkipRestOfRecord();
        }

        if (reader.CurrentRecord?.Type == 257)
        {
            var references = reportObject.HasFont ? [] : reportObject.FontConditionReferences;
            ReadConditionReferences(reader, references, ["Color", "Size", "Strikeout", "Underline", "Style", "Name"]);
            reader.SkipRestOfRecord();
        }

        var runStyle = (Font: reportObject.Font, Color: parsedColor);
        if (reader.BytesLeftInRecord > 0)
        {
            var font = reader.LoadAnyRecord();
            var parsedFont = reportObject.Font;
            if (font.Type == 8)
            {
                parsedFont = ReadLogicalFont(reader);
            }

            reader.SkipRestOfRecord();
            runStyle = (parsedFont, parsedColor);
            if (!reportObject.HasFont)
            {
                reportObject.Color = parsedColor;
                reportObject.Font = parsedFont;
                reportObject.HasFont = true;
            }
        }

        if (reader.BytesLeftInRecord > 0)
        {
            var end = reader.LoadAnyRecord();
            reader.SkipRestOfRecord();
            _ = end;
        }
        return runStyle;
    }

    private static CrystalFontModel ReadLogicalFont(TslvArchiveReader reader)
    {
        var font = new CrystalFontModel();
        var name = reader.LoadString() ?? "Arial";
        font.Name = name;
        font.FontFamily = name;
        font.OriginalFontName = name;
        _ = reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0;
        _ = reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0;
        _ = reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0;
        var sizeInPoints = reader.BytesLeftInRecord >= 2 ? (double)reader.LoadUInt16() : 10.0;
        font.Italic = reader.BytesLeftInRecord >= 2 && reader.LoadUInt16() != 0;
        font.Underline = reader.BytesLeftInRecord >= 2 && reader.LoadUInt16() != 0;
        font.Strikeout = reader.BytesLeftInRecord >= 2 && reader.LoadUInt16() != 0;
        font.Weight = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 400;
        if (reader.BytesLeftInRecord >= 4)
        {
            sizeInPoints = reader.LoadInt32() / 20.0;
        }

        font.Size = sizeInPoints;
        font.Bold = font.Weight > 400;
        return font;
    }

    private static CrystalColorModel ReadCrystalColor(
        TslvArchiveReader reader,
        CrystalColorModel nullColor)
    {
        var colorRef = reader.LoadInt32();
        if (colorRef == -1)
        {
            return nullColor;
        }

        var red = colorRef & 0xFF;
        var green = (colorRef >> 8) & 0xFF;
        var blue = (colorRef >> 16) & 0xFF;
        return new CrystalColorModel
        {
            Name = red == 0 && green == 0 && blue == 0
                ? "Black"
                : red == 255 && green == 255 && blue == 255
                    ? "White"
                : "ff" + red.ToString("x2", System.Globalization.CultureInfo.InvariantCulture) +
                  green.ToString("x2", System.Globalization.CultureInfo.InvariantCulture) +
                  blue.ToString("x2", System.Globalization.CultureInfo.InvariantCulture),
            A = 255,
            R = red,
            G = green,
            B = blue
        };
    }

    private static void SkipUntilRecord(TslvArchiveReader reader, int endType)
    {
        while (reader.BytesLeftInRecord > 0)
        {
            var next = reader.LoadAnyRecord();
            reader.SkipRestOfRecord();
            if (next.Type == endType)
            {
                break;
            }
        }
    }

    private static string HorizontalAlignmentName(int alignment)
    {
        return alignment switch
        {
            1 => "Left",
            2 => "HorizontalCenter",
            3 => "Right",
            4 => "Justified",
            5 => "Decimal",
            _ => "Default"
        };
    }

    private static string LineStyleName(int lineStyle)
    {
        return lineStyle switch
        {
            1 => "Single",
            2 => "Double",
            3 => "Dash",
            4 => "Dot",
            _ => "NoLine"
        };
    }

    private static CrystalSectionFormatModel ReadSectionProperties(
        TslvArchiveReader reader,
        bool area,
        FieldReferenceTable? fieldReferences = null,
        CrystalDataDefinitionModel? dataDefinition = null)
    {
        var format = new CrystalSectionFormatModel
        {
            EnableKeepTogether = !area
        };

        try
        {
            _ = reader.LoadNextRecord(254, 1792, 256);
            var states = new List<int>();
            for (var i = 0; i < 12 && reader.BytesLeftInRecord >= 2; i++)
            {
                states.Add(ReadFormatState(reader));
            }

            // After the area code (kind, header/footer, section flag) come SectionProperties' flags in their saved order: visible, not hidden,
            // new page before, new page after, keep together, suppress blank section, reset page number after, print at bottom of page, underlay.
            // An area keeps its own keep-together and print-at-bottom (FormattedArea); hiding and suppress-if-blank apply to sections only.
            int State(int index) => index < states.Count ? states[index] : 0;
            format.EnableSuppress = State(3) == 0;
            format.EnableHideForDrillDown = area && State(4) == 0;
            format.EnableNewPageBefore = State(5) == 1;
            format.EnableNewPageAfter = State(6) == 1;
            format.EnableKeepTogether = State(7) == 1;
            format.EnableSuppressIfBlank = !area && State(8) == 1;
            format.EnableResetPageNumberAfter = State(9) == 1;
            format.EnablePrintAtBottomOfPage = State(10) == 1;
            format.EnableUnderlaySection = !area && State(11) == 1;

            reader.SkipRestOfRecord();
            if (fieldReferences is not null &&
                dataDefinition is not null &&
                reader.BytesLeftInRecord >= 4)
            {
                // SectionProperties.l serializes these field references in a fixed order.
                foreach (var property in new[] { "EnableSuppress", "EnableHideForDrillDown", "EnableNewPageBefore", "EnableNewPageAfter",
                    "EnableKeepTogether", "EnableSuppressIfBlank", "EnableResetPageNumberAfter", "EnablePrintAtBottomOfPage",
                    "EnableUnderlaySection", "BackgroundColor", "IndentAmount", "CssClass", "NewPageAfterNVisibleRecords", "ClampPageFooter" })
                {
                    if (reader.BytesLeftInRecord < 8) break;
                    var value = ReadSectionSuppressConditionFormula(reader, fieldReferences, dataDefinition);
                    if (!string.IsNullOrWhiteSpace(value)) format.ConditionFormulas[property] = value;
                }
                format.EnableSuppressConditionFormula = format.ConditionFormulas.GetValueOrDefault("EnableSuppress", "");
            }
        }
        catch
        {
            // Keep default section formatting when a properties record variant is not understood yet.
        }
        finally
        {
            while (reader.CurrentRecord is not null && reader.CurrentRecord.Type != 255)
            {
                reader.SkipRestOfRecord();
            }

            if (reader.CurrentRecord?.Type == 255)
            {
                reader.SkipRestOfRecord();
            }
        }

        return format;
    }

    private static string ReadSectionSuppressConditionFormula(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel dataDefinition)
    {
        var stringLength = reader.PeekInt32();
        if (stringLength < 0 || stringLength > reader.BytesLeftInRecord)
        {
            return "";
        }

        var formulaReference = ReadSectionConditionFieldReference(reader, fieldReferences, dataDefinition);
        if (formulaReference.StartsWith("{@", StringComparison.Ordinal) &&
            formulaReference.EndsWith('}'))
        {
            var formulaName = formulaReference[2..^1];
            var formula = dataDefinition.FormulaFields.FirstOrDefault(field =>
                string.Equals(field.Name, formulaName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field.FormulaName, formulaReference, StringComparison.OrdinalIgnoreCase));
            if (formula is not null)
            {
                return NormalizeConditionFormula(formula.FormulaText);
            }
        }

        return NormalizeConditionFormula(formulaReference);
    }

    private static string ReadSectionConditionFieldReference(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences,
        CrystalDataDefinitionModel dataDefinition)
    {
        var name = reader.LoadString() ?? "";
        var type = reader.LoadEnum();
        var index = reader.LoadUInt16();
        if (type == 1 &&
            index >= 0 &&
            index < dataDefinition.FormulaFields.Count)
        {
            return dataDefinition.FormulaFields[index].FormulaText;
        }

        var resolved = fieldReferences.Get(type, index);
        if (string.IsNullOrWhiteSpace(resolved) &&
            type == 1 &&
            index >= 0 &&
            index < dataDefinition.FormulaFields.Count)
        {
            resolved = dataDefinition.FormulaFields[index].FormulaName;
        }

        if (type == 4 &&
            TryParseGroupReference(string.IsNullOrWhiteSpace(resolved) ? name : resolved, out var resolvedGroupNumber) &&
            resolvedGroupNumber > 0 &&
            resolvedGroupNumber <= dataDefinition.Groups.Count)
        {
            return "GroupName (" + dataDefinition.Groups[resolvedGroupNumber - 1].ConditionField + ")";
        }

        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        return type == 3 ? NormalizeSpecialFieldReference(name) : name;
    }

    private static void ReadConditionReferences(TslvArchiveReader reader, List<(string Property, string Name, int Type, int Index)> references, string[] properties)
    {
        foreach (var property in properties)
        {
            if (reader.BytesLeftInRecord < 8) break;
            var name = reader.LoadString() ?? "";
            var type = reader.LoadEnum();
            var index = reader.LoadUInt16();
            if (index != ushort.MaxValue && property.Length > 0) references.Add((property, name, type, index));
        }
    }

    private static string NormalizeConditionFormula(string formula)
    {
        // XML attributes escape newlines. Flattening them turns executable lines after // into comments.
        return formula.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static int ReadFormatState(TslvArchiveReader reader)
    {
        if (reader.BytesLeftInRecord < 2)
        {
            return 0;
        }

        var state = reader.LoadUInt8();
        reader.SkipBytes(1);
        return state;
    }

    private static string SectionKind(int recordType)
    {
        return recordType switch
        {
            141 => "ReportHeader",
            143 => "ReportFooter",
            145 => "PageHeader",
            147 => "PageFooter",
            149 => "Detail",
            151 => "GroupHeader",
            153 => "GroupFooter",
            _ => ""
        };
    }

    private static string AreaName(string kind, int ordinal)
    {
        return kind switch
        {
            "ReportHeader" => "ReportHeaderArea1",
            "ReportFooter" => "ReportFooterArea1",
            "PageHeader" => "PageHeaderArea1",
            "PageFooter" => "PageFooterArea1",
            "Detail" => "DetailArea1",
            "GroupHeader" => "GroupHeaderArea" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "GroupFooter" => "GroupFooterArea" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => kind + "Area" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static int AreaOrdinal(string areaName, int fallback)
    {
        var match = System.Text.RegularExpressions.Regex.Match(areaName, @"Area(\d+)$");
        return match.Success &&
               int.TryParse(
                   match.Groups[1].Value,
                   System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var ordinal)
            ? ordinal
            : fallback;
    }

    private static string SectionName(string kind, int areaOrdinal, int sectionIndex)
    {
        var ordinal = kind is "GroupHeader" or "GroupFooter"
            ? areaOrdinal
            : sectionIndex;
        return kind + "Section" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryLoadBoolean(TslvArchiveReader reader)
    {
        return reader.BytesLeftInRecord >= 2 && reader.LoadBoolean();
    }

    // MultiColumnInfo: label type and part name (designer only), detail width and height, horizontal and vertical gaps, printing direction
    // (0 across then down, 1 down then across) and whether group sections are formatted in the columns.
    private static CrystalMultiColumnModel? ReadMultiColumn(TslvArchiveReader reader, CrystalDataDefinitionModel dataDefinition)
    {
        try
        {
            reader.SkipString();
            reader.SkipString();
            int width = reader.LoadInt32(), height = reader.LoadInt32(), horizontal = reader.LoadInt32(), vertical = reader.LoadInt32();
            var direction = reader.LoadEnum();
            var groups = reader.LoadBoolean();
            if (width >= 0 && height >= 0 && horizontal >= 0 && vertical >= 0 && direction is 0 or 1)
                return new(width, height, horizontal, vertical, direction == 0, groups);
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException) { }
        if (dataDefinition.ReportDefinition.ReportKind != 0)
            dataDefinition.ParseWarnings.Add("The multiple-column layout of the details area could not be read; its details print in one column.");
        return null;
    }

    private static void ParsePrinter(TslvArchiveReader reader, CrystalReportCore core)
    {
        var printer = reader.LoadAnyRecord();
        switch (printer.Type)
        {
            case 1:
            {
                NormalizeMargins(core, 245);
                var hasSettings = reader.LoadBoolean();
                reader.SkipRestOfRecord();
                if (hasSettings)
                {
                    ParsePrinterSettings(reader, core, useLegacyLandscapeLetterAdjustment: false);
                }

                _ = reader.LoadNextRecord(2, 1792, 101);
                reader.SkipRestOfRecord();
                break;
            }
            case 3:
            {
                NormalizeMargins(core, 245);
                var hasSettings = reader.LoadBoolean();
                _ = reader.LoadString();
                core.PrinterName = reader.LoadString() ?? "";
                _ = reader.LoadString();
                reader.SkipRestOfRecord();
                if (hasSettings)
                {
                    ParsePrinterSettings(reader, core, useLegacyLandscapeLetterAdjustment: true);
                }

                _ = reader.LoadNextRecord(4, 1792, 101);
                reader.SkipRestOfRecord();
                break;
            }
            case 5:
            {
                NormalizeMargins(core, 360);
                var hasSettings = reader.LoadBoolean();
                reader.SkipRestOfRecord();
                if (hasSettings)
                {
                    ParsePrinterSettings(reader, core, useLegacyLandscapeLetterAdjustment: false);
                }

                _ = reader.LoadNextRecord(6, 1792, 101);
                reader.SkipRestOfRecord();
                break;
            }
            default:
                throw new InvalidDataException($"Unexpected Crystal printer record type {printer.Type}.");
        }

        if (core.PageContentWidth == 11520 && core.PageContentHeight == 15120)
        {
            ApplyPageContentSize(core, 12240, 15840);
        }
    }

    private static void ParsePrinterSettings(
        TslvArchiveReader reader,
        CrystalReportCore core,
        bool useLegacyLandscapeLetterAdjustment)
    {
        _ = reader.LoadNextRecord(7, 1792, 101);
        var fields = reader.LoadInt32();

        var orientation = (fields & 1) != 0 ? reader.LoadInt16() : 1;
        var hasPaperSize = (fields & 2) != 0;
        var hasPaperWidth = (fields & 4) != 0;
        var hasPaperLength = (fields & 8) != 0;
        var paperSize = hasPaperSize ? reader.LoadInt16() : 1;
        var paperWidth = hasPaperWidth ? reader.LoadInt16() : 0;
        var paperLength = hasPaperLength ? reader.LoadInt16() : 0;

        if ((fields & 0x10) != 0)
        {
            _ = reader.LoadInt16();
        }

        if ((fields & 0x100) != 0)
        {
            _ = reader.LoadInt16();
        }

        var defaultSource = 0;
        if ((fields & 0x200) != 0)
        {
            defaultSource = reader.LoadInt16();
            core.PaperSource = PaperSourceName(defaultSource);
        }

        var hasQuality = (fields & 0x400) != 0;
        var hasResolution = (fields & 0x2000) != 0;
        if (hasQuality)
        {
            _ = reader.LoadInt16();
        }

        if ((fields & 0x800) != 0)
        {
            _ = reader.LoadInt16();
        }

        var duplex = (fields & 0x1000) != 0 ? reader.LoadInt16() : 1;
        if (hasResolution)
        {
            _ = reader.LoadInt16();
        }

        if ((fields & 0x4000) != 0)
        {
            _ = reader.LoadInt16();
        }

        if ((fields & 0x8000) != 0)
        {
            _ = reader.LoadInt16();
        }

        if ((fields & 0x10000) != 0)
        {
            _ = reader.LoadString();
        }

        reader.SkipRestOfRecord();

        core.PaperOrientation = orientation == 2 ? "Landscape" : "Portrait";
        core.PrinterDuplex = DuplexName(duplex);
        core.PaperSize = PaperSizeName(paperSize);
        var (paperTwipsWidth, paperTwipsHeight) = PaperTwips(paperSize, paperWidth, paperLength);
        if (orientation == 2)
        {
            (paperTwipsWidth, paperTwipsHeight) = (paperTwipsHeight, paperTwipsWidth);
        }

        ApplyPageContentSize(core, paperTwipsWidth, paperTwipsHeight);
        if (useLegacyLandscapeLetterAdjustment &&
            orientation == 2 &&
            paperSize == 1 &&
            (fields & 0x10) == 0 &&
            defaultSource == 15)
        {
            core.PageContentWidth += 2;
            core.PageContentHeight += 2;
        }
    }

    private static void ApplyPageContentSize(CrystalReportCore core, int paperWidth, int paperHeight)
    {
        core.PageContentWidth = Math.Max(0, paperWidth - core.LeftMargin - core.RightMargin);
        core.PageContentHeight = Math.Max(0, paperHeight - core.TopMargin - core.BottomMargin);
    }

    private static void NormalizeMargins(CrystalReportCore core, int fallback)
    {
        core.LeftMargin = NormalizeMargin(core.LeftMargin, fallback);
        core.RightMargin = NormalizeMargin(core.RightMargin, fallback);
        core.TopMargin = NormalizeMargin(core.TopMargin, fallback);
        core.BottomMargin = NormalizeMargin(core.BottomMargin, fallback);
    }

    private static int NormalizeMargin(int margin, int fallback)
    {
        return margin == int.MinValue || margin <= 0 ? fallback : margin;
    }

    private static (int Width, int Height) PaperTwips(int paperSize, int paperWidth, int paperLength)
    {
        if (paperSize == 256 && paperWidth > 0 && paperLength > 0)
        {
            return (PrinterDeviceUnitToTwips(paperWidth), PrinterDeviceUnitToTwips(paperLength));
        }

        return paperSize switch
        {
            3 or 4 => (15840, 24480),
            5 => (12240, 20160),
            9 => (11907, 16839),
            11 => (14580, 20640),
            12 => (20640, 29160),
            13 => (29160, 41280),
            14 => (12240, 18720),
            _ => (12240, 15840)
        };
    }

    private static int PrinterDeviceUnitToTwips(int value)
    {
        return (int)Math.Round(value * 567d / 100d);
    }

    private static string PaperSizeName(int paperSize)
    {
        return paperSize switch
        {
            0 => "PaperDefault",
            1 => "PaperLetter",
            2 => "PaperLetterSmall",
            3 => "PaperTabloid",
            4 => "PaperLedger",
            5 => "PaperLegal",
            6 => "PaperStatement",
            7 => "PaperExecutive",
            8 => "PaperA3",
            9 => "PaperA4",
            10 => "PaperA4Small",
            11 => "PaperA5",
            12 => "PaperB4",
            13 => "PaperB5",
            14 => "PaperFolio",
            15 => "PaperQuarto",
            16 => "Paper10x14",
            17 => "Paper11x17",
            18 => "PaperNote",
            19 => "PaperEnvelope9",
            20 => "PaperEnvelope10",
            21 => "PaperEnvelope11",
            22 => "PaperEnvelope12",
            23 => "PaperEnvelope14",
            24 => "PaperCsheet",
            25 => "PaperDsheet",
            26 => "PaperEsheet",
            27 => "PaperEnvelopeDL",
            28 => "PaperEnvelopeC5",
            29 => "PaperEnvelopeC3",
            30 => "PaperEnvelopeC4",
            31 => "PaperEnvelopeC6",
            32 => "PaperEnvelopeC65",
            33 => "PaperEnvelopeB4",
            34 => "PaperEnvelopeB5",
            35 => "PaperEnvelopeB6",
            36 => "PaperEnvelopeItaly",
            37 => "PaperEnvelopeMonarch",
            38 => "PaperEnvelopePersonal",
            39 => "PaperFanfoldUS",
            40 => "PaperFanfoldStdGerman",
            41 => "PaperFanfoldLegalGerman",
            256 => "PaperUser",
            _ => "PaperLetter"
        };
    }

    private static string PaperSourceName(int paperSource)
    {
        return paperSource switch
        {
            1 => "Upper",
            2 => "Lower",
            3 => "Middle",
            4 => "Manual",
            5 => "Envelope",
            6 => "ManualEnvelope",
            7 => "Auto",
            8 => "Tractor",
            9 => "SmallFormat",
            10 => "LargeFormat",
            11 => "LargeCapacity",
            14 => "Cassette",
            15 => "FormSource",
            _ => "Auto"
        };
    }

    private static string DuplexName(int duplex)
    {
        return duplex switch
        {
            2 => "Vertical",
            3 => "Horizontal",
            _ => "Simplex"
        };
    }

    private static string NormalizeSelectionFormula(string formula)
    {
        var lines = formula
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .ToList();

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines).Replace(" in {?", " = {?", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record CrystalReportContents(
        CrystalReportCore Core,
        CrystalDataDefinitionModel DataDefinition);

    private sealed record CrystalFieldHeader(
        string Name,
        int ValueType,
        int NumberOfBytes);

    private sealed record CrystalAreaHeader(string Name);

    private sealed record CrystalSectionHeader(string Name, int Height);

    private sealed class FieldReferenceTable(Dictionary<int, List<string>> references)
    {
        public List<(CrystalReportObjectModel Object, TslvArchiveReader Reader)> AnalyticalObjects { get; } = [];
        private readonly Dictionary<int, List<string>> _references = references;

        public void Add(int type, string reference)
        {
            if (!_references.TryGetValue(type, out var values))
            {
                values = [];
                _references[type] = values;
            }

            values.Add(reference);
        }

        public string Get(int type, int index)
        {
            return _references.TryGetValue(type, out var values) &&
                   index >= 0 &&
                   index < values.Count
                ? values[index]
                : "";
        }
    }
}
