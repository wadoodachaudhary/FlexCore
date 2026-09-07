using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using ClosedXML.Excel;
using Fx.ControlKit.Excel;

namespace Fx.ControlKit.Data;

/// <summary>
/// General-purpose utility to read and write Excel (.xlsx) files.
/// </summary>
public class FilerControl
{
    /// <summary>
    /// Reads an Excel file (.xlsx) from a stream and returns a DataTable.
    /// Uses the first worksheet. Assumes the first row contains column headers.
    /// </summary>
    public static DataTable ReadExcel(Stream stream)
    {
        var dataTable = new DataTable();

        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.FirstOrDefault();
        if (worksheet == null)
            return dataTable;

        var firstRow = worksheet.FirstRowUsed();
        if (firstRow == null)
            return dataTable;

        // Initialize DataTable columns from header row
        var headerCells = firstRow.CellsUsed(XLCellsUsedOptions.AllContents).ToList();
        var colIndexMap = new Dictionary<int, string>();

        foreach (var cell in headerCells)
        {
            var columnName = cell.Value.ToString().Trim();
            if (string.IsNullOrEmpty(columnName))
                continue;

            // Handle duplicate column names
            var uniqueColName = columnName;
            int counter = 1;
            while (dataTable.Columns.Contains(uniqueColName))
            {
                uniqueColName = $"{columnName}_{counter++}";
            }

            dataTable.Columns.Add(uniqueColName, typeof(string)); // default to string for raw import
            colIndexMap[cell.Address.ColumnNumber] = uniqueColName;
        }

        // Read data rows
        var dataRows = worksheet.RowsUsed().Skip(1); // skip header row
        foreach (var xlRow in dataRows)
        {
            var dataRow = dataTable.NewRow();
            var hasData = false;

            // We iterate over all defined columns in our map
            foreach (var kvp in colIndexMap)
            {
                var colNum = kvp.Key;
                var colName = kvp.Value;
                var cell = xlRow.Cell(colNum);
                
                var val = cell.Value.ToString().Trim();
                if (!string.IsNullOrEmpty(val))
                {
                    dataRow[colName] = val;
                    hasData = true;
                }
            }

            if (hasData)
            {
                dataTable.Rows.Add(dataRow);
            }
        }

        return dataTable;
    }

    /// <summary>
    /// Reads an Excel file from a local path and returns a DataTable.
    /// </summary>
    public static DataTable ReadExcel(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return ReadExcel(stream);
    }

    /// <summary>
    /// Writes a DataTable to an Excel stream.
    /// </summary>
    public static void WriteExcel(Stream stream, DataTable dataTable, string sheetName = "Sheet1")
    {
        // Forwarded to FlexCore's native XlsxWriter — same output shape, no
        // ClosedXML/OpenXml dependency on the write path. Signature unchanged.
        var sheet = new XlsxSheet { Name = sheetName };

        var header = sheet.AddRow();
        for (var col = 0; col < dataTable.Columns.Count; col++)
            header.Add(new XlsxCell { Value = dataTable.Columns[col].ColumnName });

        foreach (System.Data.DataRow dataRow in dataTable.Rows)
        {
            var row = sheet.AddRow();
            for (var col = 0; col < dataTable.Columns.Count; col++)
            {
                var val = dataRow[col];
                row.Add(new XlsxCell { Value = val == DBNull.Value ? null : val });
            }
        }

        XlsxWriter.Write(stream, sheet);
    }

    /// <summary>
    /// Writes a DataTable to a local file path.
    /// </summary>
    public static void WriteExcel(string filePath, DataTable dataTable, string sheetName = "Sheet1")
    {
        using var stream = File.Create(filePath);
        WriteExcel(stream, dataTable, sheetName);
    }
}
