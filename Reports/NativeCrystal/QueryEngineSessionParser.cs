using System.Globalization;
using System.Runtime.Versioning;

namespace Fx.ControlKit.Reports.NativeCrystal;

[UnsupportedOSPlatform("browser")]
internal static class QueryEngineSessionParser
{
    public static CrystalDatabaseModel Parse(CrystalRptStream queryEngineStream)
    {
        var decoded = TslvStreamReader.DecodeQueryEngineStream(queryEngineStream.Bytes);
        var reader = new TslvArchiveReader(decoded.Body, defaultSchema: 2304)
        {
            ReadObjectIds = true,
            ReadEnumsAsInt32 = true
        };

        var database = new CrystalDatabaseModel();
        var session = reader.LoadAnyRecord();

        var connectionCount = LoadCollectionCount(reader);
        for (var i = 0; i < connectionCount; i++)
        {
            database.Connections.Add(ReadConnection(reader, database));
        }

        SkipCollection(reader);
        var linkCount = LoadCollectionCount(reader);
        for (var i = 0; i < linkCount; i++)
        {
            database.Links.Add(ReadLink(reader, database));
        }

        if (session.Schema >= 2305)
        {
            SkipCollection(reader);
        }

        reader.SkipRestOfRecord();
        return database;
    }

    private static CrystalConnectionModel ReadConnection(TslvArchiveReader reader, CrystalDatabaseModel database)
    {
        var record = reader.LoadAnyRecord();
        var connection = new CrystalConnectionModel
        {
            ObjectId = record.ObjectId,
            DatabaseDll = reader.LoadString() ?? "",
            DatabaseType = reader.LoadString() ?? "",
            ServerName = reader.LoadString() ?? ""
        };

        var logonPropertyCount = LoadCollectionCount(reader);
        for (var i = 0; i < logonPropertyCount; i++)
        {
            connection.LogonProperties.Add(ReadProperty(reader));
        }

        var propertyCount = LoadCollectionCount(reader);
        for (var i = 0; i < propertyCount; i++)
        {
            connection.Properties.Add(ReadProperty(reader));
        }

        var tableCount = LoadCollectionCount(reader);
        for (var i = 0; i < tableCount; i++)
        {
            var table = ReadTable(reader, database, connection);
            connection.Properties.AddRange([]);
            database.Tables.Add(table);
            database.TablesByObjectId[table.ObjectId] = table;
        }

        SkipCollection(reader);

        if (record.Schema >= 2305)
        {
            _ = reader.LoadBinary();
        }

        if (record.Schema >= 2306)
        {
            SkipCollection(reader);
        }

        reader.SkipRestOfRecord();
        return connection;
    }

    private static CrystalTableModel ReadTable(
        TslvArchiveReader reader,
        CrystalDatabaseModel database,
        CrystalConnectionModel connection)
    {
        var record = reader.LoadAnyRecord();
        var table = new CrystalTableModel
        {
            ObjectId = record.ObjectId,
            Connection = connection,
            Name = reader.LoadString() ?? "",
            Description = reader.LoadString() ?? "",
            QualifiedName = reader.LoadString() ?? ""
        };

        var qualifierCount = LoadCollectionCount(reader);
        for (var i = 0; i < qualifierCount; i++)
        {
            table.Qualifiers.Add(reader.LoadString() ?? "");
        }

        table.TableType = reader.LoadEnum();
        table.Alias = reader.LoadString() ?? "";
        table.IsFlat = reader.LoadBoolean();
        table.IsLinkable = reader.LoadBoolean();

        var fieldCount = LoadCollectionCount(reader);
        for (var i = 0; i < fieldCount; i++)
        {
            var field = ReadDatabaseField(reader, table);
            table.Fields.Add(field);
            database.FieldsByObjectId[field.ObjectId] = field;
        }

        SkipCollection(reader);
        SkipCollection(reader);

        table.CommandText = reader.LoadString() ?? "";
        table.ExternalIndexes = record.Schema >= 2305 ? reader.LoadString() ?? "" : "";
        table.OverriddenName = record.Schema >= 2306 ? reader.LoadString() ?? "" : "";

        reader.SkipRestOfRecord();
        return table;
    }

    private static CrystalDatabaseFieldModel ReadDatabaseField(TslvArchiveReader reader, CrystalTableModel table)
    {
        var record = reader.LoadAnyRecord();
        var field = new CrystalDatabaseFieldModel
        {
            ObjectId = record.ObjectId,
            Table = table,
            Name = reader.LoadString() ?? "",
            Description = reader.LoadString() ?? "",
            DataType = reader.LoadEnum(),
            Length = reader.LoadInt32(),
            Attributes = record.Schema >= 2305 ? reader.LoadInt32() : 0,
            Precision = record.Schema >= 2306 ? reader.LoadInt32() : 0
        };

        reader.SkipRestOfRecord();
        return field;
    }

    private static CrystalTableLinkModel ReadLink(TslvArchiveReader reader, CrystalDatabaseModel database)
    {
        var record = reader.LoadAnyRecord();
        var fromId = reader.LoadInt32();
        var toId = reader.LoadInt32();
        var link = new CrystalTableLinkModel
        {
            ObjectId = record.ObjectId,
            FromField = database.FieldsByObjectId.GetValueOrDefault(fromId),
            ToField = database.FieldsByObjectId.GetValueOrDefault(toId),
            LinkOperator = reader.LoadEnum(),
            JoinType = reader.LoadEnum(),
            Enforced = record.Schema >= 2305 ? reader.LoadEnum() : 0
        };

        reader.SkipRestOfRecord();
        return link;
    }

    private static CrystalQePropertyModel ReadProperty(TslvArchiveReader reader)
    {
        var record = reader.LoadAnyRecord();
        var property = new CrystalQePropertyModel
        {
            Name = reader.LoadString() ?? ""
        };

        _ = reader.LoadString();
        _ = reader.LoadString();
        property.Value = ReadValue(reader);
        _ = reader.LoadEnum();
        _ = reader.LoadInt32();

        var nestedCount = LoadCollectionCount(reader);
        for (var i = 0; i < nestedCount; i++)
        {
            property.Children.Add(ReadProperty(reader));
        }

        _ = record;
        reader.SkipRestOfRecord();
        return property;
    }

    private static string ReadValue(TslvArchiveReader reader)
    {
        var value = reader.LoadAnyRecord();
        if (value.Type != 11)
        {
            reader.SkipRestOfRecord();
            return "";
        }

        var single = reader.LoadAnyRecord();
        if (single.Type != 12)
        {
            reader.SkipRestOfRecord();
            reader.SkipRestOfRecord();
            return "";
        }

        var text = ReadTypedValue(reader);
        reader.SkipRestOfRecord();
        reader.SkipRestOfRecord();
        return text;
    }

    private static string ReadTypedValue(TslvArchiveReader reader)
    {
        var type = reader.LoadUInt16();
        return type switch
        {
            0 or 1 => "",
            2 => reader.LoadInt16().ToString(CultureInfo.InvariantCulture),
            3 or 10 or 19 or 22 or 23 => reader.LoadInt32().ToString(CultureInfo.InvariantCulture),
            4 or 5 or 7 => reader.LoadDouble().ToString(CultureInfo.InvariantCulture),
            6 => (reader.LoadInt64() / 10000.0).ToString(CultureInfo.InvariantCulture),
            8 => reader.LoadString() ?? "",
            11 => reader.LoadBoolean().ToString(CultureInfo.InvariantCulture),
            16 => reader.LoadInt8().ToString(CultureInfo.InvariantCulture),
            17 => reader.LoadUInt8().ToString(CultureInfo.InvariantCulture),
            18 => reader.LoadUInt16().ToString(CultureInfo.InvariantCulture),
            8209 => Convert.ToHexString(reader.LoadBinary()),
            _ => ""
        };
    }

    private static int LoadCollectionCount(TslvArchiveReader reader)
    {
        return reader.LoadInt32();
    }

    private static void SkipCollection(TslvArchiveReader reader)
    {
        var count = LoadCollectionCount(reader);
        for (var i = 0; i < count; i++)
        {
            var record = reader.LoadAnyRecord();
            _ = record;
            reader.SkipRestOfRecord();
        }
    }
}
