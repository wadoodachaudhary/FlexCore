using System.Runtime.Versioning;

namespace Fx.ControlKit.Reports.NativeCrystal;

[UnsupportedOSPlatform("browser")]
internal static class CrystalReportContentsParser
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
        var decoded = TslvStreamReader.Decode(contentsStream.Bytes, defaultSchema: 3072);
        var reader = new TslvArchiveReader(decoded.Body, decoded.HeaderSchema);
        var core = new CrystalReportCore();
        var dataDefinition = new CrystalDataDefinitionModel();
        var fieldReferences = new FieldReferenceTable();

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
        _ = reader.LoadEnum();
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

        _ = reader.LoadNextRecord(351, 1792, 103);
        reader.SkipRestOfRecord();

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
            reader.SkipRestOfRecord();

            if (hasRecordSelectionFormula)
            {
                dataDefinition.RecordSelectionFormula = NormalizeSelectionFormula(ReadFormulaField(reader).FormulaText);
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

            ResolveDeferredGroupNameReferences(dataDefinition);
            ResolveDeferredSummaryReferences(dataDefinition);

            if (hasRuler)
            {
                var ruler = reader.LoadAnyRecord();
                reader.SkipRestOfRecord();
                _ = ruler;
            }
        }
        catch (Exception)
        {
            if (Environment.GetEnvironmentVariable("FLEXKIT_DEBUG_RPT") == "1")
            {
                throw;
            }

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
            reportObject.DataSource = ReplaceGroupNamePlaceholders(reportObject.DataSource, dataDefinition);
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
            @"Group #(\d+)",
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

        foreach (var reportObject in dataDefinition.ReportDefinition.Areas
                     .SelectMany(area => area.Sections)
                     .SelectMany(section => section.ReportObjects))
        {
            reportObject.Text = ReplaceSummaryPlaceholders(reportObject.Text, dataDefinition);
            reportObject.DataSource = ReplaceSummaryPlaceholders(reportObject.DataSource, dataDefinition);
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
            var runningTotal = ReadRunningTotalField(reader, fieldReferences);
            dataDefinition.RunningTotalFields.Add(runningTotal);
            fieldReferences.Add(9, runningTotal.FormulaName);
        }

        for (var i = 0; i < sqlExpressionFieldCount; i++)
        {
            fieldReferences.Add(10, SkipFieldLikeRecord(reader));
        }

        for (var i = 0; i < customFunctionFieldCount; i++)
        {
            fieldReferences.Add(11, SkipFieldLikeRecord(reader));
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
        try
        {
            _ = reader.LoadNextRecord(115, 1792, 101);
            _ = reader.LoadNextRecord(114, 1792, 101);
            field = ReadFieldHeader(reader);
            reader.SkipRestOfRecord();
            databaseIndex = reader.BytesLeftInRecord >= 4 ? reader.LoadInt32() : -1;
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
        _ = reader.LoadNextRecord(118, 1792, 101);
        var field = ReadFieldHeader(reader);

        var operandCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        for (var i = 0; i < operandCount; i++)
        {
            SkipFieldReference(reader);
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
            _ = reader.LoadBoolean();
            _ = reader.LoadBoolean();
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

        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadBoolean();
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
        var length = reader.LoadInt32();
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
            9 => CrystalDate(reader.LoadInt32()),
            10 => reader.LoadInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            11 or 13 => reader.LoadString() ?? "",
            15 => CrystalDate(reader.LoadInt32()) + " " + reader.LoadInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => SkipCrystalValue(reader, length)
        };
    }

    private static string SkipCrystalValue(TslvArchiveReader reader, int length)
    {
        reader.SkipBytes(length);
        return "";
    }

    private static string CrystalDate(int crDate)
    {
        if (crDate <= 0)
        {
            return "";
        }

        var date = DateTime.FromOADate(crDate - 2415019);
        return date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
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

        summary.SummaryKind = reader.BytesLeftInRecord >= 2 ? reader.LoadInt16() : 0;
        summary.GroupIndex = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        reader.SkipRestOfRecord();
        return summary;
    }

    private static CrystalRunningTotalFieldModel ReadRunningTotalField(
        TslvArchiveReader reader,
        FieldReferenceTable fieldReferences)
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

        runningTotal.ResetConditionType = ReadRunningTotalCondition(reader);
        runningTotal.EvaluationConditionType = ReadRunningTotalCondition(reader);
        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadBoolean();
        }

        reader.SkipRestOfRecord();
        return runningTotal;
    }

    private static int ReadRunningTotalCondition(TslvArchiveReader reader)
    {
        if (reader.BytesLeftInRecord <= 0)
        {
            return 0;
        }

        var conditionType = reader.LoadEnum();
        switch (conditionType)
        {
            case 1:
                SkipFieldReference(reader);
                break;
            case 2:
                if (reader.BytesLeftInRecord >= 2)
                {
                    _ = reader.LoadUInt16();
                }
                break;
            case 3:
                if (reader.BytesLeftInRecord > 0)
                {
                    SkipFieldReference(reader);
                }
                break;
        }

        return conditionType;
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

        if (type == 3)
        {
            return NormalizeSpecialFieldReference(name);
        }

        return name;
    }

    private static string NormalizeSpecialFieldReference(string name)
    {
        return string.Concat(name.Where(char.IsLetterOrDigit));
    }

    private static bool TryParseGroupReference(string name, out int groupNumber)
    {
        groupNumber = 0;
        const string prefix = "Group #";
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(
                   name[prefix.Length..],
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
                valueType = field.DataType;
                numberOfBytes = field.Length;
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
        if (record.Type == 136)
        {
            enableRepeatGroupHeader = reader.BytesLeftInRecord >= 2 && reader.LoadBoolean();
            if (reader.BytesLeftInRecord >= 2)
            {
                _ = reader.LoadBoolean();
            }
        }

        reader.SkipRestOfRecord();

        while (reader.BytesLeftInRecord > 0)
        {
            var next = reader.LoadAnyRecord();
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
                    enableRepeatGroupHeader);
                continue;
            }

            if (next.Type == 229)
            {
                var conditionField = ReadFieldReference(reader, fieldReferences);
                _ = reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0;
                var direction = reader.BytesLeftInRecord > 0 ? reader.LoadEnum() : 0;

                if (!string.IsNullOrWhiteSpace(conditionField))
                {
                    dataDefinition.Groups.Add(new CrystalGroupModel { ConditionField = conditionField });
                    dataDefinition.SortFields.Add(new CrystalSortFieldModel
                    {
                        Field = conditionField,
                        Direction = direction,
                        SortType = "GroupSortField"
                    });
                }

                reader.SkipRestOfRecord();
                continue;
            }

            reader.SkipRestOfRecord();
        }
    }

    private static void ReadArea(
        TslvArchiveReader reader,
        CrystalDataDefinitionModel dataDefinition,
        FieldReferenceTable fieldReferences,
        int areaPairOrdinal,
        bool enableRepeatGroupHeader)
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
                    Format = areaFormat
                };
                area.Format.EnableNewPageBefore = sectionKind == "ReportHeader";

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
                areaFormat = ReadSectionProperties(reader, area: true);
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
                var reportObject = ReadSubreportObject(reader, dataDefinition);
                if (reportObject is not null)
                {
                    reportObjects.Add(reportObject);
                }

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

        if (reader.BytesLeftInRecord >= 8)
        {
            var useResolvedReference = reader.LoadBoolean();
            var referenceIndex = reader.LoadUInt16();
            var referenceType = reader.LoadEnum();
            if (useResolvedReference)
            {
                var resolved = fieldReferences.Get(referenceType, referenceIndex);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    reportObject.DataSource = resolved;
                }
            }
        }

        reader.SkipRestOfRecord();
        ReadCommonReportObjectRecords(reader, reportObject);
        ReadFontColourProperties(reader, reportObject);
        SkipUntilRecord(reader, 160);
        return reportObject;
    }

    private static CrystalReportObjectModel ReadSubreportObject(
        TslvArchiveReader reader,
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
        SkipUntilRecord(reader, 164);
        return reportObject;
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
                    text.Append(ReadTextElement(reader, reportObject));
                    break;
                case 196:
                    text.Append(ReadFieldElement(reader, fieldReferences, dataDefinition, reportObject));
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
        ReadFontColourProperties(reader, reportObject);
        _ = reader.LoadNextRecord(195, 1792, 193);
        reader.SkipRestOfRecord();
        return text;
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

        if (reader.BytesLeftInRecord >= 8)
        {
            var useResolvedReference = reader.LoadBoolean();
            var referenceIndex = reader.LoadUInt16();
            var referenceType = reader.LoadEnum();
            if (useResolvedReference)
            {
                var resolved = fieldReferences.Get(referenceType, referenceIndex);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    text = resolved;
                }
            }
        }

        reader.SkipRestOfRecord();
        ReadFontColourProperties(reader, reportObject);
        SkipUntilRecord(reader, 197);
        return text;
    }

    private static void ReadFontColourProperties(
        TslvArchiveReader reader,
        CrystalReportObjectModel reportObject)
    {
        if (reader.BytesLeftInRecord <= 0)
        {
            return;
        }

        var fontColour = reader.LoadAnyRecord();
        if (fontColour.Type != 257)
        {
            reader.SkipRestOfRecord();
            return;
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
            reader.SkipRestOfRecord();
        }

        if (reader.BytesLeftInRecord > 0)
        {
            var font = reader.LoadAnyRecord();
            var parsedFont = reportObject.Font;
            if (font.Type == 8)
            {
                parsedFont = ReadLogicalFont(reader);
            }

            reader.SkipRestOfRecord();
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

            var suppressState = states.Count > 3 ? states[3] : 0;
            var hideForDrillDownState = states.Count > 4 ? states[4] : 0;
            var suppressBlankState = states.Count > 5 ? states[5] : 0;
            var newPageAfterState = states.Count > 6 ? states[6] : 0;
            var suppressIfBlankState = states.Count > 8 ? states[8] : 0;
            format.EnableSuppress = suppressState == 0;
            format.EnableHideForDrillDown = area && hideForDrillDownState == 0;
            format.EnableNewPageAfter = area && newPageAfterState == 1;
            format.EnableSuppressIfBlank = !area && (suppressBlankState == 2 || suppressIfBlankState == 1);

            reader.SkipRestOfRecord();
            if (!area &&
                fieldReferences is not null &&
                dataDefinition is not null &&
                reader.BytesLeftInRecord >= 4)
            {
                format.EnableSuppressConditionFormula =
                    ReadSectionSuppressConditionFormula(reader, fieldReferences, dataDefinition);
            }
        }
        catch
        {
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
        if (stringLength <= 1 || stringLength > reader.BytesLeftInRecord)
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
        var type = reader.BytesLeftInRecord >= 2 ? ReadUInt16LittleEndian(reader) : 0;
        var index = reader.BytesLeftInRecord >= 2 ? ReadUInt16LittleEndian(reader) : 0;
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

    private static int ReadUInt16LittleEndian(TslvArchiveReader reader)
    {
        var low = reader.LoadUInt8();
        var high = reader.LoadUInt8();
        return low | (high << 8);
    }

    private static string NormalizeConditionFormula(string formula)
    {
        return formula
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
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

    private sealed class FieldReferenceTable
    {
        private readonly Dictionary<int, List<string>> _references = [];

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
