using System.Data;

namespace Fx.ControlKit.Reports;

/// <summary>Loads query results while retaining provider types that CLR types alone cannot distinguish.</summary>
public static class ReportDataSchema
{
    public const string ProviderTypeProperty = "Fx.Report.ProviderType";

    /// <summary>Like DataTable.Load, consumes the first result. The reader remains owned by the caller.</summary>
    public static DataTable Load(IDataReader reader)
    {
        var types = Enumerable.Range(0, reader.FieldCount).Select(reader.GetDataTypeName).ToArray();
        var table = new DataTable();
        table.Load(reader);
        for (var i = 0; i < types.Length && i < table.Columns.Count; i++)
            table.Columns[i].ExtendedProperties[ProviderTypeProperty] = types[i];
        return table;
    }

    internal static bool IsCurrency(DataColumn column) =>
        column.ExtendedProperties[ProviderTypeProperty] is string type
        && (type.Equals("money", StringComparison.OrdinalIgnoreCase) || type.Equals("smallmoney", StringComparison.OrdinalIgnoreCase));
}
