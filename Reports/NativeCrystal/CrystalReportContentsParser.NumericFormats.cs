namespace Fx.ControlKit.Reports.NativeCrystal;

internal static partial class CrystalReportContentsParser
{
    // FieldProperties saves currency first, then ordinary numeric properties (two 249/248 pairs).
    // The type is resolved after deferred summary/group references, so do not choose by record position alone.
    private static ReportNativeNumericFormat ReadNumericFormat(TslvArchiveReader reader, int start)
    {
        _ = reader.LoadNextRecord(248, 1792, 160);
        var suppressZero = reader.LoadBoolean();
        var negative = reader.LoadEnum();
        var thousands = reader.LoadBoolean();
        var leadingZero = reader.LoadBoolean();
        var decimals = reader.LoadUInt16();
        var rounding = reader.LoadEnum();
        var currencyType = reader.LoadEnum();
        var onePerPage = reader.LoadBoolean();
        var currencyPosition = reader.LoadEnum();
        var thousandSymbol = reader.LoadString() ?? "";
        var decimalSymbol = reader.LoadString() ?? "";
        var currencySymbol = reader.LoadString() ?? "";
        var clipping = reader.LoadBoolean();
        var accounting = false; var reverse = false; var zeroValue = "<Default Format>";
        var noHorizontalPages = false; var prefix = ""; var suffix = "";
        if (reader.BytesLeftInRecord > 0)
        {
            accounting = reader.LoadBoolean(); reverse = reader.LoadBoolean(); zeroValue = reader.LoadString() ?? "";
        }
        if (reader.BytesLeftInRecord > 0)
        {
            noHorizontalPages = reader.LoadBoolean(); prefix = reader.LoadString() ?? ""; suffix = reader.LoadString() ?? "";
        }
        reader.SkipRestOfRecord();
        var conditional = false;
        while (reader.BytesLeftInRecord > 0)
        {
            var name = reader.LoadString(); _ = reader.LoadEnum(); var index = reader.LoadUInt16();
            conditional |= !string.IsNullOrEmpty(name) || index != ushort.MaxValue;
        }
        return new(suppressZero, negative, thousands, leadingZero, decimals, rounding, currencyType, onePerPage,
            currencyPosition, thousandSymbol, decimalSymbol, currencySymbol, clipping, accounting, reverse, zeroValue,
            noHorizontalPages, prefix, suffix, conditional, NativeArchive: Convert.ToBase64String(reader.CopyRange(start, reader.Position - start)));
    }

    private static void ResolveNumericFormats(CrystalDatabaseModel database, CrystalDataDefinitionModel definition)
    {
        foreach (var obj in definition.ReportDefinition.Areas.SelectMany(a => a.Sections).SelectMany(s => s.ReportObjects))
        {
            if (obj.NativeNumericCandidates.Count == 0) continue;
            var reference = obj.DataSource;
            var summary = definition.SummaryFields.FirstOrDefault(s => SummaryFormulaName(s, definition).Equals(reference, StringComparison.OrdinalIgnoreCase));
            int type;
            if (summary is not null) type = summary.Operation is 6 or 9 ? 4 : summary.ValueType;
            else if (!TryResolveFieldType(reference, database, definition, out type, out _))
            {
                if (definition.RunningTotalFields.FirstOrDefault(f => f.FormulaName == reference) is { } total)
                    type = total.ValueType;
                else if (reference.StartsWith("GroupName (") && reference.EndsWith(')'))
                    { if (!TryResolveFieldType(reference[11..^1], database, definition, out type, out _)) type = -1; }
                else
                // Page counters are numeric special fields; unrelated special/string fields must not receive numeric defaults.
                type = reference.Equals("PageNumber", StringComparison.OrdinalIgnoreCase) || reference.Equals("TotalPageCount", StringComparison.OrdinalIgnoreCase) ? 4 : -1;
            }
            if (type is >= 0 and <= 7)
            {
                var index = type == 7 ? 0 : 1;
                if (obj.NativeNumericCandidates.Count <= index)
                    definition.ParseWarnings.Add($"{obj.Name}: native numeric properties are incomplete.");
                else
                {
                    obj.NativeNumericFormat = obj.NativeNumericCandidates[index] with { ValueType = type };
                    if (obj.NativeNumericFormat.UnsupportedReason is { } reason)
                        definition.ParseWarnings.Add($"{obj.Name}: native numeric format retained but {reason}.");
                }
            }
            obj.NativeNumericCandidates.Clear();
        }
    }
}
