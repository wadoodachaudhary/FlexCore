using System.Runtime.Versioning;

namespace Fx.ControlKit.Reports.NativeCrystal;

[UnsupportedOSPlatform("browser")]
internal static class CrystalReportParametersParser
{
    public static void ApplySavedParameterValues(
        CrystalRptStream parametersStream,
        CrystalDataDefinitionModel dataDefinition)
    {
        var decoded = TslvStreamReader.Decode(parametersStream.Bytes, defaultSchema: 1792);
        var reader = new TslvArchiveReader(decoded.Body, decoded.HeaderSchema);

        _ = reader.LoadNextRecord(303, 1792, 304);
        var reportParameterSetCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        var alertParameterSetCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        reader.SkipRestOfRecord();

        for (var i = 0; i < reportParameterSetCount; i++)
        {
            ReadReportParameterSet(reader, dataDefinition);
        }

        for (var i = 0; i < alertParameterSetCount; i++)
        {
            SkipReportParameterSet(reader);
        }

        _ = reader.LoadNextRecord(304, 1792, 304);
        reader.SkipRestOfRecord();
    }

    private static void ReadReportParameterSet(
        TslvArchiveReader reader,
        CrystalDataDefinitionModel dataDefinition)
    {
        _ = reader.LoadNextRecord(59, 1793, 60);
        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadUInt16();
        }

        var isMainReport = reader.BytesLeftInRecord < 2 || reader.LoadBoolean();
        if (!isMainReport && reader.BytesLeftInRecord >= 4)
        {
            _ = reader.LoadInt32();
        }

        var dataSourceParameterSetCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadBoolean();
        }

        reader.SkipRestOfRecord();

        for (var i = 0; i < dataSourceParameterSetCount; i++)
        {
            ReadDataSourceParameterSet(reader, dataDefinition);
        }

        _ = reader.LoadNextRecord(60, 1793, 60);
        reader.SkipRestOfRecord();
    }

    private static void SkipReportParameterSet(TslvArchiveReader reader)
    {
        _ = reader.LoadNextRecord(59, 1793, 60);
        reader.SkipRestOfRecord();
        _ = reader.LoadNextRecord(60, 1793, 60);
        reader.SkipRestOfRecord();
    }

    private static void ReadDataSourceParameterSet(
        TslvArchiveReader reader,
        CrystalDataDefinitionModel dataDefinition)
    {
        _ = reader.LoadNextRecord(48, 1794, 51);
        var isMainReport = reader.BytesLeftInRecord < 2 || reader.LoadBoolean();
        if (!isMainReport && reader.BytesLeftInRecord >= 4)
        {
            _ = reader.LoadInt32();
        }

        var parameterValueCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        var hasDrillDownParameters = reader.BytesLeftInRecord >= 2 && reader.LoadBoolean();
        var groupPathLength = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        if (reader.BytesLeftInRecord >= 4)
        {
            _ = reader.LoadInt32();
        }

        for (var i = 0; i < groupPathLength && reader.BytesLeftInRecord >= 4; i++)
        {
            _ = reader.LoadInt32();
        }

        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadBoolean();
        }

        if (reader.BytesLeftInRecord >= 2)
        {
            _ = reader.LoadBoolean();
        }

        reader.SkipRestOfRecord();

        for (var i = 0; i < parameterValueCount; i++)
        {
            ReadParameterValues(reader, dataDefinition);
        }

        if (hasDrillDownParameters)
        {
            var drillDownRecord = reader.LoadAnyRecord();
            reader.SkipRestOfRecord();
            _ = drillDownRecord;
        }

        _ = reader.LoadNextRecord(51, 1794, 51);
        reader.SkipRestOfRecord();
    }

    private static void ReadParameterValues(
        TslvArchiveReader reader,
        CrystalDataDefinitionModel dataDefinition)
    {
        var record = reader.LoadNextRecord(49, 1794, 2);
        var parameterId = reader.LoadInt32();
        var parameter = dataDefinition.Parameters.FirstOrDefault(candidate => candidate.Id == parameterId);
        var valueType = reader.LoadEnum();

        if (parameter is null)
        {
            reader.SkipRestOfRecord();
            return;
        }

        if (record.Schema == 1792)
        {
            AddValue(parameter.CurrentValues, ReadCrystalValue(reader, valueType));
            reader.SkipRestOfRecord();
            return;
        }

        var isRangeValue = reader.BytesLeftInRecord >= 2 && reader.LoadBoolean();
        var valueSlotCount = reader.BytesLeftInRecord >= 2 ? reader.LoadUInt16() : 0;
        var values = new List<string>(valueSlotCount);
        for (var i = 0; i < valueSlotCount; i++)
        {
            var value = ReadCrystalValue(reader, valueType);
            if (value is not null)
            {
                values.Add(value);
            }
        }

        if (!isRangeValue)
        {
            foreach (var value in values)
            {
                AddValue(parameter.CurrentValues, value);
            }
        }

        reader.SkipRestOfRecord();
    }

    private static void AddValue(List<CrystalParameterDefaultValueModel> values, string? value)
    {
        if (value is not null)
        {
            values.Add(new CrystalParameterDefaultValueModel { Value = value });
        }
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
            8 => reader.LoadBoolean() ? "true" : "false",
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

    private static string FormatCrystalScaledNumber(double scaled)
    {
        var value = scaled / 100d;
        return value.ToString("0.0##############", System.Globalization.CultureInfo.InvariantCulture);
    }
}
