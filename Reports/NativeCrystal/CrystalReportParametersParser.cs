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
        var reader = new TslvArchiveReader(decoded.Body, decoded.HeaderSchema) { LegacyValueLengths = decoded.IsHeaderless };

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
            8 => reader.LoadBoolean() ? "true" : "false",
            9 => CrystalDate(reader.LoadInt32()),
            10 => CrystalTime(reader.LoadInt32()),
            11 or 13 => reader.LoadString() ?? "",
            15 => CrystalDateTime(reader.LoadInt32(), reader.LoadInt32()),
            _ => SkipCrystalValue(reader, length)
        };
    }

    private static string SkipCrystalValue(TslvArchiveReader reader, int length)
    {
        reader.SkipBytes(length);
        return "";
    }

    // Crystal's CRDate is the Julian day number minus one (DateValue: 1899-12-30, OLE date 0, is 2415018).
    internal static string CrystalDate(int crDate)
    {
        if (crDate <= 0)
        {
            return "";
        }

        var date = DateTime.FromOADate(crDate - 2415018);
        return date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    // Crystal times are whole seconds after midnight (TimeValue.fromCRTime): -1 is the null time, values past 86400
    // wrap, 86400 itself is the end of the day, and a negative time is rejected by TimeValue.
    internal static string CrystalTime(int crTime)
    {
        if (crTime == -1) return "";
        if (crTime > 86400) crTime %= 86400;
        if (crTime < 0) throw new InvalidDataException($"Crystal time value {crTime} is negative.");
        return crTime == 86400 ? "24:00:00" : TimeSpan.FromSeconds(crTime).ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    // A date-time ending at 24:00:00 is written as midnight of the next day, the same instant.
    internal static string CrystalDateTime(int crDate, int crTime)
    {
        var date = CrystalDate(crDate);
        var time = CrystalTime(crTime);
        if (time.Length == 0) return date;
        if (date.Length == 0) throw new InvalidDataException($"Crystal date-time value has the time {time} but no date.");
        return time == "24:00:00"
            ? CrystalDate(crDate + 1) + " 00:00:00"
            : date + " " + time;
    }

    private static string FormatCrystalScaledNumber(double scaled)
    {
        var value = scaled / 100d;
        return value.ToString("0.0##############", System.Globalization.CultureInfo.InvariantCulture);
    }
}
