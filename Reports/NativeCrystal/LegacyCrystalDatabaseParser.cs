using System.Buffers.Binary;
using System.Text;

namespace Fx.ControlKit.Reports.NativeCrystal;

/// <summary>Database metadata from the pre-v9, little-endian Database (TLV) stream.</summary>
internal static class LegacyCrystalDatabaseParser
{
    public static CrystalDatabaseModel Parse(CrystalRptStream stream)
    {
        var model = new CrystalDatabaseModel { UsesLegacyFieldNames = true };
        CrystalTableModel? table = null;
        CrystalDatabaseFieldModel? field = null;
        var expectedTables = -1;
        var expectedFields = -1;
        var expectedLinks = -1;
        var readLinks = 0;
        var sourceAlias = "";
        var targetAlias = "";
        var sourceFields = new List<string>();
        var sourceFieldCount = 0;
        var inLinks = false;
        var indexes = new Dictionary<CrystalTableModel, List<List<int>>>();
        List<int>? currentIndex = null;
        var nextId = 1;
        for (var offset = 0; offset < stream.Bytes.Length;)
        {
            var bytes = stream.Bytes.AsSpan(offset);
            if (bytes.Length < 4) throw new InvalidDataException("Truncated legacy database TLV header.");
            var tag = U16(bytes, 0);
            var size = U16(bytes, 2);
            if (size > bytes.Length - 4) throw new InvalidDataException($"Truncated legacy database TLV 0x{tag:X4}.");
            var value = bytes.Slice(4, size);
            offset += size + 4;
            switch (tag)
            {
                case 0x2003:
                    if (value.Length != 4) throw new InvalidDataException("Invalid legacy table count.");
                    expectedTables = BinaryPrimitives.ReadInt32LittleEndian(value);
                    break;
                case 0x2006:
                    FinishTable();
                    var connection = new CrystalConnectionModel { ObjectId = nextId++, DatabaseDll = Text(value) };
                    connection.DatabaseType = connection.DatabaseDll.Contains("odbc", StringComparison.OrdinalIgnoreCase) ? "ODBC" : connection.DatabaseDll;
                    model.Connections.Add(connection);
                    table = new CrystalTableModel { ObjectId = nextId++, Connection = connection, IsFlat = true, IsLinkable = true };
                    model.Tables.Add(table);
                    indexes.Add(table, []);
                    currentIndex = null;
                    model.TablesByObjectId.Add(table.ObjectId, table);
                    expectedFields = -1;
                    field = null;
                    break;
                case 0x2004: RequireTable().Name = Text(value); break;
                case 0x2005:
                    var qualifier = Text(value);
                    if (qualifier.Length != 0) RequireTable().Qualifiers.Add(qualifier);
                    break;
                case 0x2011: RequireTable().Alias = Text(value); break;
                case 0x2019: RequireTable().Description = Text(value); break;
                case 0x201a: RequireTable().Connection!.ServerName = Text(value); break;
                case 0x201b:
                    RequireTable().Connection!.LogonProperties.Add(new() { Name = "Database", Value = Text(value) });
                    break;
                case 0x201c:
                    RequireTable().Connection!.LogonProperties.Add(new() { Name = "User ID", Value = Text(value) });
                    break;
                case 0x2022: RequireTable().QualifiedName = Text(value); break;
                case 0x2007: expectedFields = U16(value, 0); break;
                case 0x2008:
                    field = new CrystalDatabaseFieldModel { ObjectId = nextId++, Table = RequireTable(), Name = Text(value) };
                    table!.Fields.Add(field);
                    model.FieldsByObjectId.Add(field.ObjectId, field);
                    break;
                case 0x2009:
                    if (field is null || value.Length < 18) throw new InvalidDataException("Invalid legacy database field descriptor.");
                    // Legacy descriptors store date-time as 12; database value types use 15.
                    field.DataType = U16(value, 0) switch { 12 => 15, var type => type };
                    field.Length = U16(value, 2);
                    field.Attributes = U16(value, 4);
                    field.Precision = U16(value, 16);
                    break;
                case 0x200b:
                    currentIndex = [];
                    indexes[RequireTable()].Add(currentIndex);
                    break;
                case 0x200c:
                    if (currentIndex is null || value.Length % 2 != 0)
                        throw new InvalidDataException("Invalid legacy table index fields.");
                    for (var i = 0; i < value.Length; i += 2) currentIndex.Add(U16(value, i));
                    break;
                case 0x200d:
                    FinishTable();
                    table = null;
                    inLinks = true;
                    expectedLinks = U16(value, 0);
                    break;
                case 0x200e when inLinks:
                    if (sourceAlias.Length == 0) sourceAlias = Text(value);
                    else targetAlias = Text(value);
                    break;
                case 0x2013 when inLinks: sourceFieldCount = U16(value, 0); break;
                case 0x200f when inLinks: sourceFields.Add(Text(value)); break;
                case 0x2010 when inLinks:
                    readLinks++;
                    var source = model.Tables.SingleOrDefault(t => t.Alias.Equals(sourceAlias, StringComparison.OrdinalIgnoreCase));
                    var target = model.Tables.SingleOrDefault(t => t.Alias.Equals(targetAlias, StringComparison.OrdinalIgnoreCase));
                    if (source is null || target is null) throw new InvalidDataException("Legacy database link references an unknown table alias.");
                    // The legacy descriptor includes stale native pointer/padding bytes.
                    // Its target is an index ordinal, not a field ordinal or pointer.
                    var targetIndex = value.Length == 32 ? BinaryPrimitives.ReadInt32LittleEndian(value.Slice(2, 4)) : -1;
                    var targetFields = targetIndex >= 0 && targetIndex < indexes[target].Count ? indexes[target][targetIndex] : [];
                    if (sourceFieldCount > 0 && sourceFields.Count == sourceFieldCount && targetFields.Count == sourceFieldCount &&
                        targetFields.All(i => i >= 0 && i < target.Fields.Count) && value.Length == 32 && U16(value, 28) is 3 or 4)
                    {
                        for (var i = 0; i < sourceFieldCount; i++)
                        {
                            var from = source.Fields.SingleOrDefault(f => f.Name.Equals(sourceFields[i], StringComparison.OrdinalIgnoreCase));
                            if (from is null) throw new InvalidDataException("Legacy database link references an unknown field.");
                            model.Links.Add(new()
                            {
                                ObjectId = nextId++, FromField = from, ToField = target.Fields[targetFields[i]],
                                LinkOperator = 1, JoinType = U16(value, 28) == 4 ? 2 : 1
                            });
                        }
                    }
                    else model.ParseWarnings.Add($"Legacy database link {sourceAlias} -> {targetAlias} has an unsupported descriptor; its join was not imported.");
                    sourceAlias = targetAlias = "";
                    sourceFields.Clear();
                    sourceFieldCount = 0;
                    break;
            }
        }
        FinishTable();
        if (expectedTables < 0 || expectedTables != model.Tables.Count)
            throw new InvalidDataException("Legacy database table count does not match its records.");
        if (expectedLinks < 0 || expectedLinks != readLinks || sourceAlias.Length != 0)
            throw new InvalidDataException("Legacy database link count does not match its records.");
        return model;

        CrystalTableModel RequireTable() => table ?? throw new InvalidDataException("Legacy database metadata appears outside a table.");
        void FinishTable()
        {
            if (table is null) return;
            if (expectedFields < 0 || expectedFields != table.Fields.Count)
                throw new InvalidDataException($"Legacy table '{table.Alias}' field count does not match its records.");
            if (table.Alias.Length == 0) table.Alias = table.Name;
            if (table.QualifiedName.Length == 0) table.QualifiedName = table.Name;
            // Fields are serialized in reverse ordinal order, unlike link references.
            table.Fields.Reverse();
        }
    }

    private static int U16(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset > bytes.Length - 2) throw new InvalidDataException("Truncated legacy database value.");
        return BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    }

    private static string Text(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("Unterminated legacy database string.");
        return Encoding.Latin1.GetString(bytes[..end]);
    }
}
