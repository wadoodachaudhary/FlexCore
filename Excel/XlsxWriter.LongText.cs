using System.Globalization;
using System.Text;

namespace Fx.ControlKit.Excel;

public static partial class XlsxWriter
{
    private const int CellTextLimit = 32767;
    private const int SheetRowLimit = 1048576;

    private static List<XlsxSheet> PreserveLongText(XlsxSheet source)
    {
        var main = new XlsxSheet { Name = SanitizeSheetName(source.Name) };
        foreach (var entry in source.ColumnWidths) main.ColumnWidths.Add(entry.Key, entry.Value);
        foreach (var entry in source.InternalHyperlinks) main.InternalHyperlinks.Add(entry.Key, entry.Value);
        var sheets = new List<XlsxSheet> { main };
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { main.Name };
        XlsxSheet? continuation = null;
        for (var rowIndex = 0; rowIndex < source.Rows.Count; rowIndex++)
        {
            var row = main.AddRow();
            for (var columnIndex = 0; columnIndex < source.Rows[rowIndex].Count; columnIndex++)
            {
                var cell = source.Rows[rowIndex][columnIndex];
                if (cell?.Value is not string text || text.Length <= CellTextLimit || !string.IsNullOrWhiteSpace(cell.Formula))
                {
                    row.Add(cell!);
                    continue;
                }
                var parts = TextParts(text);
                if (continuation is null || continuation.Rows.Count + parts.Count > SheetRowLimit)
                {
                    var name = "Long text";
                    for (var number = 2; !names.Add(name); number++) name = $"Long text {number}";
                    continuation = new XlsxSheet { Name = name };
                    var header = continuation.AddRow();
                    foreach (var heading in new[] { "Source cell", "Part", "Characters", "Text" })
                        header.Add(new() { Value = heading, Bold = true, FillHex = "eeeeee" });
                    continuation.ColumnWidths[0] = 36;
                    continuation.ColumnWidths[1] = 8;
                    continuation.ColumnWidths[2] = 12;
                    continuation.ColumnWidths[3] = 80;
                    sheets.Add(continuation);
                }
                var sourceCell = ColumnName(columnIndex) + (rowIndex + 1).ToString(CultureInfo.InvariantCulture);
                var back = SheetAddress(main.Name, sourceCell);
                var target = SheetAddress(continuation.Name, "D" + (continuation.Rows.Count + 1).ToString(CultureInfo.InvariantCulture));
                main.InternalHyperlinks[sourceCell] = target;
                for (var part = 0; part < parts.Count; part++)
                {
                    continuation.AddRow().AddRange([
                        new() { Value = back, FontColorHex = "0563c1" },
                        new() { Value = part + 1 }, new() { Value = parts[part].Length }, new() { Value = parts[part] }
                    ]);
                    continuation.InternalHyperlinks["A" + continuation.Rows.Count.ToString(CultureInfo.InvariantCulture)] = back;
                }
                var preview = cell.Copy();
                var notice = $"[Full text: {target}, {parts.Count} parts]\n";
                var length = CellTextLimit - notice.Length;
                if (char.IsHighSurrogate(text[length - 1])) length--;
                preview.Value = notice + text[..length];
                preview.FontColorHex = "0563c1";
                row.Add(preview);
            }
        }
        return sheets;
    }

    private static List<string> TextParts(string value)
    {
        var parts = new List<string>();
        for (var offset = 0; offset < value.Length;)
        {
            var length = Math.Min(CellTextLimit, value.Length - offset);
            if (offset + length < value.Length && char.IsHighSurrogate(value[offset + length - 1])) length--;
            parts.Add(value.Substring(offset, length));
            offset += length;
        }
        return parts;
    }

    private static string SheetAddress(string sheet, string cell) => "'" + sheet.Replace("'", "''", StringComparison.Ordinal) + "'!" + cell;

    private static string BuildWorkbook(IReadOnlyList<XlsxSheet> sheets)
    {
        if (sheets.Count == 1) return BuildWorkbook(sheets[0].Name);
        var items = string.Concat(sheets.Select((sheet, index) =>
            $"<sheet name=\"{Escape(sheet.Name)}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>"));
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" + items + "</sheets></workbook>";
    }

    private static string BuildContentTypes(int sheets)
    {
        var extra = string.Concat(Enumerable.Range(2, sheets - 1).Select(index =>
            $"<Override PartName=\"/xl/worksheets/sheet{index}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"));
        return ContentTypes.Replace("</Types>", extra + "</Types>", StringComparison.Ordinal);
    }

    private static string BuildWorkbookRels(int sheets)
    {
        if (sheets == 1) return WorkbookRels;
        const string types = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        var result = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var index = 1; index <= sheets; index++)
            result.Append($"<Relationship Id=\"rId{index}\" Type=\"{types}worksheet\" Target=\"worksheets/sheet{index}.xml\"/>");
        result.Append($"<Relationship Id=\"rId{sheets + 1}\" Type=\"{types}styles\" Target=\"styles.xml\"/>");
        result.Append($"<Relationship Id=\"rId{sheets + 2}\" Type=\"{types}sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>");
        return result.ToString();
    }
}
