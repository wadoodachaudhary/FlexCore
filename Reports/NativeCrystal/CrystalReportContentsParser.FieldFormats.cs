namespace Fx.ControlKit.Reports.NativeCrystal;

internal static partial class CrystalReportContentsParser
{
    // FieldProperties follows the font. Preserve authored formats, including their original archive.
    private static void ReadNativeFieldFormats(TslvArchiveReader reader, CrystalReportObjectModel reportObject, int endType = 160)
    {
        var start = -1;
        var end = -1;
        var systemDefault = false;
        var suppressDuplicates = false;
        var dateDefault = -1;
        ReportNativeDateComponents? dateComponents = null;
        ReportNativeTimeComponents? timeComponents = null;
        var order = -1;
        var separator = "";
        var conditional = false;
        var hasDateTime = false;
        var commonConditional = false;
        while (reader.BytesLeftInRecord > 0)
        {
            var record = reader.LoadAnyRecord();
            if (record.Type == 234) start = record.Offset;
            if (record.Type == 251)
            {
                _ = reader.LoadNextRecord(250, 1792, 160);
                _ = reader.LoadEnum(); // Word wrap.
                _ = reader.LoadInt32(); // First-line indent.
                _ = reader.LoadInt32(); // Left indent.
                _ = reader.LoadInt32(); // Right indent.
                // StringFieldProperties stores a UInt16 limit before text interpretation and spacing.
                // Zero means unlimited. This is a display limit; never truncate the bound field value.
                reportObject.MaxNumberOfLines = reader.LoadUInt16();
                if (reader.BytesLeftInRecord >= 2)
                {
                    var interpretation = reader.LoadEnum();
                    reportObject.InterpretRtf = interpretation == 1;
                    reportObject.InterpretHtml = interpretation == 2;
                }
                reader.SkipRestOfRecord();
            }
            else if (record.Type == 249)
                reportObject.NativeNumericCandidates.Add(ReadNumericFormat(reader, record.Offset));
            else if (record.Type is 241 or 243 or 245 or 247)
            {
                var propertyConditional = false;
                _ = reader.LoadNextRecord(record.Type - 1, 1792, 160);
                switch (record.Type)
                {
                    case 241:
                        suppressDuplicates = reader.LoadBoolean();
                        systemDefault = reader.LoadBoolean();
                        break;
                    case 243:
                        var dateOrder = reader.LoadEnum();
                        var yearType = reader.LoadEnum();
                        var monthType = reader.LoadEnum();
                        var dayType = reader.LoadEnum();
                        var dayOfWeekType = reader.LoadEnum();
                        dateDefault = reader.LoadEnum();
                        // System formatting does not use these custom components. Keep its existing metadata
                        // contract; a legacy short header in custom mode remains explicitly unextracted.
                        if (systemDefault || dateDefault != 2 || reader.BytesLeftInRecord == 0) break;
                        var eraType = reader.LoadEnum();
                        var calendarType = reader.LoadEnum();
                        var zeroSeparator = reader.LoadString() ?? "";
                        var firstSeparator = reader.LoadString() ?? "";
                        var secondSeparator = reader.LoadString() ?? "";
                        var thirdSeparator = reader.LoadString() ?? "";
                        var dayOfWeekSeparator = reader.LoadString() ?? "";
                        dateComponents = new(dateOrder, yearType, monthType, dayType, dayOfWeekType, eraType, calendarType,
                            zeroSeparator, firstSeparator, secondSeparator, thirdSeparator, dayOfWeekSeparator,
                            reader.LoadEnum(), reader.LoadEnum());
                        break;
                    case 245:
                        order = reader.LoadEnum();
                        separator = reader.LoadString() ?? "";
                        hasDateTime = true;
                        break;
                    case 247:
                        // The time components follow the date rule: system formatting does not use them, and they stay unextracted
                        // from a legacy short header. Order: time base, AM/PM position, hour, minute and second styles, then the AM
                        // and PM strings and the hour/minute and minute/second separators.
                        if (systemDefault || reader.BytesLeftInRecord == 0) break;
                        var timeBase = reader.LoadEnum();
                        var amPmFormat = reader.LoadEnum();
                        var hourType = reader.LoadEnum();
                        var minuteType = reader.LoadEnum();
                        var secondType = reader.LoadEnum();
                        string Text() => reader.BytesLeftInRecord > 0 ? reader.LoadString() ?? "" : "";
                        var amString = Text();
                        var pmString = Text();
                        var hourMinuteSeparator = Text();
                        timeComponents = new(timeBase, amPmFormat, hourType, minuteType, secondType, amString, pmString, hourMinuteSeparator, Text());
                        break;
                }
                reader.SkipRestOfRecord();
                // Format formula references follow the property header. Even an unresolved reference must be
                // diagnosed; index 65535 with an empty name is the native absence marker.
                while (reader.BytesLeftInRecord > 0)
                {
                    var name = reader.LoadString();
                    _ = reader.LoadEnum();
                    var index = reader.LoadUInt16();
                    propertyConditional |= !string.IsNullOrEmpty(name) || index != ushort.MaxValue;
                }
                conditional |= propertyConditional;
                if (record.Type == 241) commonConditional |= propertyConditional;
            }
            reader.SkipRestOfRecord();
            if (record.Type == 235) end = reader.Position;
            if (record.Type == endType) break;
        }
        reportObject.SuppressIfDuplicate = suppressDuplicates;
        for (var i = 0; i < reportObject.NativeNumericCandidates.Count; i++)
            reportObject.NativeNumericCandidates[i] = reportObject.NativeNumericCandidates[i] with
            {
                SystemDefaultFormatting = systemDefault, SuppressIfDuplicate = suppressDuplicates,
                HasConditionalFormatting = reportObject.NativeNumericCandidates[i].HasConditionalFormatting || commonConditional
            };
        if (hasDateTime || dateDefault >= 0)
            reportObject.NativeDateTimeFormat = new(systemDefault, dateDefault, hasDateTime ? order : 2, separator, suppressDuplicates, conditional,
                start >= 0 && end > start ? Convert.ToBase64String(reader.CopyRange(start, end - start)) : "", dateComponents, timeComponents);
        // Native order is Currency then Number, regardless of the field's bound type.
        if (reportObject.NativeNumericCandidates.Count == 2)
        {
            ReportNativeNumberFormat Complete(ReportNativeNumericFormat f) => new(f.SuppressIfZero, f.NegativeType,
                f.ThousandsSeparators, f.LeadingZero, f.DecimalPlaces, f.RoundingType, f.CurrencySymbolType,
                f.OneCurrencySymbolPerPage, f.CurrencyPosition, f.ThousandSymbol, f.DecimalSymbol, f.CurrencySymbol,
                f.AccountingFormat, f.ReverseSign, f.ZeroValue, f.SuppressIfNoHorizontalPages, f.Prefix, f.Suffix,
                f.SystemDefaultFormatting, f.SuppressIfDuplicate, f.HasConditionalFormatting,
                start >= 0 && end > start ? Convert.ToBase64String(reader.CopyRange(start, end - start)) : "");
            reportObject.NativeCurrencyFormat = Complete(reportObject.NativeNumericCandidates[0]);
            reportObject.NativeNumberFormat = Complete(reportObject.NativeNumericCandidates[1]);
        }
    }

    private static void ResolveNativeFieldFormats(CrystalDatabaseModel database, CrystalDataDefinitionModel definition)
    {
        foreach (var obj in definition.ReportDefinition.Areas.SelectMany(a => a.Sections).SelectMany(s => s.ReportObjects))
        {
            var resolved = ResolveFieldType(obj.DataSource, database, definition, out var type);
            obj.NativeNumberFormat = resolved && type is >= 0 and <= 7
                ? type == 7 ? obj.NativeCurrencyFormat : obj.NativeNumberFormat : null;
            obj.NativeCurrencyFormat = null;
            // A field embedded in a text object carries its own authored date/time format.
            foreach (var run in obj.TextRuns)
                if (run.NativeDateTimeFormat is { } runFormat)
                    run.NativeDateTimeFormat = ResolveDateTimeFormat(run.Binding, runFormat, database, definition);
            if (obj.NativeDateTimeFormat is not { } format) continue;
            obj.NativeDateTimeFormat = format = ResolveDateTimeFormat(obj.DataSource, format, database, definition, resolved, type);
            if (format?.UnsupportedReason is { } reason)
                definition.ParseWarnings.Add($"{obj.Name}: native date/time format retained but {reason}.");
        }
    }

    private static bool ResolveFieldType(string dataSource, CrystalDatabaseModel database, CrystalDataDefinitionModel definition, out int type)
    {
        var resolved = TryResolveFieldType(dataSource, database, definition, out type, out _);
        if (!resolved && definition.RunningTotalFields.FirstOrDefault(f => f.FormulaName == dataSource) is { } total)
        { type = total.ValueType; resolved = true; }
        if (!resolved && definition.SummaryFields.FirstOrDefault(f => SummaryFormulaName(f, definition) == dataSource) is { } summary)
        { type = summary.ValueType; resolved = true; }
        if (!resolved && dataSource.StartsWith("GroupName (") && dataSource.EndsWith(')'))
            resolved = TryResolveFieldType(dataSource[11..^1], database, definition, out type, out _);
        if (!resolved && dataSource is "PageNumber" or "TotalPageCount") { type = 4; resolved = true; }
        if (!resolved && dataSource is "PrintDate" or "DataDate") { type = 9; resolved = true; }
        return resolved;
    }

    private static ReportNativeDateTimeFormat? ResolveDateTimeFormat(string dataSource, ReportNativeDateTimeFormat format,
        CrystalDatabaseModel database, CrystalDataDefinitionModel definition, bool? resolved = null, int type = -1)
    {
        resolved ??= ResolveFieldType(dataSource, database, definition, out type);
        // PrintDate/PrintTime use only their component; other fields retain their resolved type.
        if (dataSource.Equals("PrintDate", StringComparison.OrdinalIgnoreCase)) return format with { Order = 2 };
        if (dataSource.Equals("PrintTime", StringComparison.OrdinalIgnoreCase)) return format with { Order = 3 };
        if (resolved != true || type is not (9 or 15)) return null;
        // A report may display database timestamps as dates without changing their database type
        // or the declared type of a formula/parameter. Embedded field runs use this same resolver.
        var databaseDate = type == 15 && definition.ConvertDatabaseDateTimeToDate && database.Tables
            .SelectMany(t => t.Fields).Any(f => f.FormulaName.Equals(dataSource, StringComparison.OrdinalIgnoreCase));
        return type == 9 || databaseDate ? format with { Order = 2 } : format;
    }
}
