namespace Fx.ControlKit.Reports;

public sealed record ReportDesignerFieldDrop(ReportDesignerSection Section, ReportDesignerField Field, int LeftTwips, int TopTwips);

/// <summary>Document edits the report designer experts and field explorer perform, shared with the designer surface.</summary>
public static class ReportDesignerCommands
{
    public static readonly string[] SummaryOperations = ["Sum", "Average", "Count", "DistinctCount", "Minimum", "Maximum"];

    /// <summary>Built-in fields the positioned runtime can print. The caption is the Field Explorer label; the binding is the Crystal data source.</summary>
    public static IReadOnlyList<(string Caption, string Binding)> SpecialFields { get; } =
    [
        ("Print Date", "PrintDate"),
        ("Print Time", "PrintTime"),
        ("Data Date", "DataDate"),
        ("Data Time", "DataTime"),
        ("Page Number", "PageNumber"),
        ("Total Page Count", "TotalPageCount"),
        ("Page N of M", "PageNofM"),
        ("Record Number", "RecordNumber"),
        ("Group Number", "GroupNumber"),
        ("Report Title", "ReportTitle"),
        ("Report Comments", "ReportComments"),
        ("File Author", "FileAuthor"),
        ("File Path and Name", "ReportFileName")
    ];

    public static ReportDesignerField SpecialField(string caption)
    {
        var known = SpecialFields.FirstOrDefault(field => field.Caption.Equals(caption, StringComparison.OrdinalIgnoreCase));
        var binding = known.Binding ?? caption.Replace(" ", "", StringComparison.Ordinal);
        return new ReportDesignerField { Name = known.Caption ?? caption, CrystalBinding = binding };
    }

    public static ReportDesignerField GroupNameField(ReportDesignerGroup group) =>
        new()
        {
            Name = "GroupName (" + group.Condition + ")",
            CrystalBinding = "GroupName (" + group.Condition + ")"
        };

    public static void ApplyTables(ReportDesignerDocument document, IEnumerable<ReportDesignerDataTable> tables)
    {
        document.DataSources = tables.Select(CloneTable).ToList();
        var formulas = document.Fields.Where(field => field.IsFormula).Select(CloneField).ToList();
        document.Fields = document.DataSources.SelectMany(table => table.Fields).Select(CloneField)
            .Concat(formulas)
            .GroupBy(field => field.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>Columns of a validated SQL command become database fields under the Command table.</summary>
    public static void PromoteCommandFields(ReportDesignerDocument document, IEnumerable<ReportDesignerField> columns)
    {
        var kept = document.Fields.Where(field => field.IsFormula || !IsCommandField(field)).Select(CloneField);
        var command = columns
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .Select(column => new ReportDesignerField
            {
                Name = column.Name,
                Table = "Command",
                LongName = "Command." + column.Name,
                Type = string.IsNullOrWhiteSpace(column.Type) ? "Xsd:stringField" : column.Type
            });
        document.Fields = kept.Concat(command)
            .GroupBy(field => field.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    public static bool IsCommandField(ReportDesignerField field) =>
        !field.IsFormula && (string.IsNullOrWhiteSpace(field.Table) || field.Table.Equals("Command", StringComparison.OrdinalIgnoreCase));

    /// <summary>One group-sort entry per group. Record sorts stay in the record-sort list.</summary>
    public static void SyncGroupSorts(ReportDesignerDocument document)
    {
        var conditions = document.Groups.Select(group => group.Condition).ToHashSet(StringComparer.OrdinalIgnoreCase);
        document.Sorts.RemoveAll(sort => sort.SortType == "GroupSortField" && !conditions.Contains(sort.Field.Reference));
        foreach (var group in document.Groups)
        {
            var sort = document.Sorts.FirstOrDefault(item => item.SortType == "GroupSortField"
                && item.Field.Reference.Equals(group.Condition, StringComparison.OrdinalIgnoreCase));
            if (sort is null)
            {
                var field = document.Fields.FirstOrDefault(item => item.Reference.Equals(group.Condition, StringComparison.OrdinalIgnoreCase));
                document.Sorts.Add(new ReportDesignerSort
                {
                    Field = field is null ? FieldFromReference(group.Condition) : CloneField(field),
                    Direction = NormalizeDirection(group.SortDirection),
                    SortType = "GroupSortField"
                });
            }
            else sort.Direction = NormalizeDirection(group.SortDirection);
        }
    }

    public static void ReplaceRecordSorts(ReportDesignerDocument document, IEnumerable<ReportDesignerSort> recordSorts)
    {
        document.Sorts = document.Sorts.Where(sort => sort.SortType != "RecordSortField")
            .Concat(recordSorts.Select(sort =>
            {
                sort.SortType = "RecordSortField";
                sort.Direction = NormalizeDirection(sort.Direction);
                return sort;
            })).ToList();
    }

    public static string NormalizeDirection(string? direction) =>
        direction is not null && direction.StartsWith("Desc", StringComparison.OrdinalIgnoreCase) ? "Descending" : "Ascending";

    public static ReportDesignerField FieldFromReference(string reference)
    {
        var match = System.Text.RegularExpressions.Regex.Match(reference.Trim(), @"^\{([^.}]+)\.([^}]+)\}$");
        return match.Success
            ? new ReportDesignerField { Table = match.Groups[1].Value.Trim(), Name = match.Groups[2].Value.Trim() }
            : new ReportDesignerField { Name = reference.Trim().Trim('{', '}'), CrystalBinding = reference.Trim() };
    }

    public static ReportNativeNumericFormat CurrencyFormat() => new(
        false, 3, true, true, 2, 9, 2, false, 1, ",", ".", "$", false, false, false,
        "<Default Format>", false, "", "", SystemDefaultFormatting: true, ValueType: 6);

    public static ReportNativeNumericFormat NumberFormat(int decimalPlaces) => new(
        false, 1, true, true, Math.Clamp(decimalPlaces, 0, 10), 11 - Math.Clamp(decimalPlaces, 0, 10), 0, false, 0, ",", ".", "", false, false, false,
        "<Default Format>", false, "", "", SystemDefaultFormatting: false, ValueType: decimalPlaces == 0 ? 4 : 6);

    /// <summary>Crystal's integer field format: no thousands separator, parentheses for negatives, value type 4.</summary>
    public static ReportNativeNumericFormat IntegerFormat() => new(
        false, 3, false, true, 0, 11, 0, false, 0, ",", ".", "$", false, false, false,
        "<Default Format>", false, "", "", SystemDefaultFormatting: false, ValueType: 4);

    public static ReportNativeDateTimeFormat DateAndTimeFormat() => new(true, 1, 0, "  ");

    /// <summary>Crystal's short date: system default, Windows short date, date only, two-space separator.</summary>
    public static ReportNativeDateTimeFormat ShortDateFormat() => new(true, 1, 2, "  ");

    public static void SetFieldType(ReportDesignerDocument document, string table, string field, string type)
    {
        if (string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(type)) return;
        foreach (var item in document.Fields.Concat(document.DataSources.SelectMany(source => source.Fields)))
            if (item.Table.Equals(table, StringComparison.OrdinalIgnoreCase) && item.Name.Equals(field, StringComparison.OrdinalIgnoreCase))
                item.Type = type;
    }

    /// <summary>A native cross-tab grid using the analysis dimensions and the object's font. Cell templates stay empty so Count and Sum both render.</summary>
    public static void ApplyStandardCrossTabGrid(ReportAnalysisDefinition analysis, ReportDesignerElement element)
    {
        var font = string.IsNullOrWhiteSpace(element.FontFamily) ? "Times New Roman" : element.FontFamily;
        var size = element.FontSize <= 0 ? 10m : element.FontSize;
        ReportNativeCrossTabCellStyle Style(bool bold, string align, decimal points) =>
            new(font, points, bold, false, false, "#000000", "transparent", align);
        var rowWidth = Math.Clamp(analysis.RowHeaderWidthTwips, 360, 100000);
        var valueWidth = Math.Clamp(analysis.ValueColumnWidthTwips, 360, 100000);
        var cellHeight = Math.Clamp(Math.Min(analysis.RowHeightTwips, 2880), 180, 100000);
        analysis.NativeCrossTab = new ReportNativeCrossTabLayout(rowWidth, valueWidth, cellHeight, 100, 100, font, size, true)
        {
            HeaderHeight = Math.Clamp(analysis.RowHeightTwips, 0, 100000),
            Appearance = new ReportNativeCrossTabAppearance("Total", "Total", "transparent", "transparent", "transparent",
                string.Join(';', Enumerable.Repeat("0:#000000:Single", 16)) + ";-1:#000000:Single;-1:#000000:Single;-1:#000000:Single;-1:#000000:Single",
                Math.Max(1, size - 1), Math.Max(1, size - 1)),
            RowStyle = Style(false, "left", size),
            ColumnStyle = Style(false, "left", size),
            ValueStyle = Style(true, "right", size),
            RowTotalStyle = Style(true, "right", size),
            ColumnTotalStyle = Style(true, "right", size),
            GrandTotalStyle = Style(true, "right", size),
            RowTotalLabelStyle = Style(false, "left", Math.Max(1, size - 1)),
            ColumnTotalLabelStyle = Style(false, "left", Math.Max(1, size - 1))
        };
        ApplyCrossTabOptions(analysis);
    }

    /// <summary>Copies the cross-tab grid options onto the native layout so save and render keep the same choices.</summary>
    public static void ApplyCrossTabOptions(ReportAnalysisDefinition analysis)
    {
        if (analysis.NativeCrossTab is not { } native) return;
        var appearance = native.Appearance;
        if (appearance is not null)
        {
            var grid = analysis.ShowGrid
                ? GridVisible(appearance.GridLines) ? appearance.GridLines : StandardGridLines()
                : "";
            appearance = appearance with { GridLines = grid };
        }
        var paddingX = analysis.ShowCellMargins ? native.PaddingX > 0 ? native.PaddingX : 100 : 0;
        var paddingY = analysis.ShowCellMargins ? native.PaddingY > 0 ? native.PaddingY : 100 : 0;
        static int Content(int outer, int padding) => Math.Clamp(outer - 2 * Math.Max(0, padding), 360, 100000);
        analysis.NativeCrossTab = native with
        {
            PaddingX = paddingX,
            PaddingY = paddingY,
            RowWidth = Content(analysis.RowHeaderWidthTwips, paddingX),
            ValueWidth = Content(analysis.ValueColumnWidthTwips, paddingX),
            CellHeight = Math.Clamp(analysis.RowHeightTwips - 2 * paddingY, 180, 100000),
            HeaderHeight = Math.Clamp(analysis.RowHeightTwips, 0, 100000),
            SuppressRowGrandTotals = !analysis.ShowRowGrandTotals,
            SuppressColumnGrandTotals = !analysis.ShowColumnGrandTotals,
            Appearance = appearance
        };
        analysis.ShowTotals = analysis.ShowRowGrandTotals || analysis.ShowColumnGrandTotals;
    }

    private static bool GridVisible(string lines) =>
        lines.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(part => int.TryParse(part.Split(':')[0], out var width) && width >= 0);

    private static string StandardGridLines() =>
        string.Join(';', Enumerable.Repeat("0:#000000:Single", 16)) + ";-1:#000000:Single;-1:#000000:Single;-1:#000000:Single;-1:#000000:Single";

    public static ReportDesignerField CloneField(ReportDesignerField field) => new()
    {
        Id = field.Id, Name = field.Name, Table = field.Table, LongName = field.LongName, Formula = field.Formula,
        Expression = field.Expression, Syntax = field.Syntax, Type = field.Type, IsFormula = field.IsFormula,
        CrystalBinding = field.CrystalBinding
    };

    public static ReportDesignerDataTable CloneTable(ReportDesignerDataTable table) => new()
    {
        Id = table.Id, Name = table.Name, Schema = table.Schema, IsView = table.IsView, SourceName = table.SourceName,
        CommandText = table.CommandText, DataFile = table.DataFile, Fields = table.Fields.Select(CloneField).ToList()
    };
}
