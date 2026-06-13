using System;
using System.Data;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using ClosedXML.Excel;

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
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add(sheetName);

        // Headers
        for (int col = 0; col < dataTable.Columns.Count; col++)
        {
            worksheet.Cell(1, col + 1).SetValue(dataTable.Columns[col].ColumnName);
        }

        // Data Rows
        for (int row = 0; row < dataTable.Rows.Count; row++)
        {
            for (int col = 0; col < dataTable.Columns.Count; col++)
            {
                var val = dataTable.Rows[row][col];
                if (val != DBNull.Value && val != null)
                {
                    if (val is double d) worksheet.Cell(row + 2, col + 1).SetValue(d);
                    else if (val is int i) worksheet.Cell(row + 2, col + 1).SetValue(i);
                    else if (val is decimal dec) worksheet.Cell(row + 2, col + 1).SetValue(dec);
                    else if (val is DateTime dt) worksheet.Cell(row + 2, col + 1).SetValue(dt);
                    else if (val is bool b) worksheet.Cell(row + 2, col + 1).SetValue(b);
                    else worksheet.Cell(row + 2, col + 1).SetValue(val.ToString());
                }
            }
        }

        workbook.SaveAs(stream);
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
