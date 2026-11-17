using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fx.ControlKit.Reports;
using Microsoft.Data.Sqlite;

namespace Fx.ControlKit.Artifacts.Crystal;

public sealed record CrystalSampleIssue(string Category, string Message);
public sealed record CrystalSampleReport(string Hash, string Name, string RelativePath, string Collection,
    string Status, int Pages, int Rows, List<CrystalSampleIssue> Issues, int Id = 0);
public sealed record CrystalSampleColumn(string Name, string Type, string Reference);
public sealed record CrystalSampleDataset(string Key, List<CrystalSampleColumn> Columns,
    Dictionary<string, string> Parameters, List<JsonElement[]> Rows);

/// <summary>Fingerprint and database path a translated report should open.</summary>
public readonly record struct CrystalSampleBinding(string Fingerprint, string DataPath);

/// <summary>
/// The synthetic SQLite sample pack the Crystal viewer reads. Report SQL is never sent to SQLite.
/// The shared corpus is <see cref="CrystalSampleDatabase.Path"/>. A translation writes a session pack
/// named <see cref="DefaultFileName"/> only when that corpus does not already hold the report.
/// The store uses SQLite and runs in the server host, the same place report conversion runs.
/// </summary>
public sealed class CrystalSampleStore
{
    /// <summary>File name a translation uses for the session pack it writes beside the XML.</summary>
    public const string DefaultFileName = "samples.db";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public string DatabasePath { get; }
    public bool Available => File.Exists(DatabasePath);

    /// <summary>Opens the shared corpus at <see cref="CrystalSampleDatabase.Path"/>.</summary>
    public CrystalSampleStore() : this(CrystalSampleDatabase.Path) { }

    /// <summary>Opens a pack at an explicit path. Session packs use <see cref="DefaultFileName"/>.</summary>
    public CrystalSampleStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A sample database path is required.", nameof(path));
        DatabasePath = Path.GetFullPath(path);
    }

    public bool Contains(string hash)
    {
        if (!Available) return false;
        try
        {
            using var connection = Open(readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM ReportCatalog WHERE Hash=$hash";
            command.Parameters.AddWithValue("$hash", hash);
            return command.ExecuteScalar() is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public IReadOnlyList<CrystalSampleReport> ReadCatalog()
    {
        if (!Available) return [];
        using var connection = Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Metadata FROM ReportCatalog ORDER BY Name COLLATE NOCASE, Hash LIMIT 10000";
        using var reader = command.ExecuteReader();
        var reports = new List<CrystalSampleReport>();
        while (reader.Read()) reports.Add(JsonSerializer.Deserialize<CrystalSampleReport>(reader.GetString(0), Json)!);
        if (reports.Any(r => r.Id <= 0) || reports.Select(r => r.Id).Distinct().Count() != reports.Count)
            throw new InvalidDataException("Sample catalog needs unique report IDs.");
        return reports.OrderBy(r => r.Id).ToArray();
    }

    public Dictionary<string, string> Parameters(string hash, ReportDefinition definition) => ReadDataset(hash, definition).Parameters;

    public DataTable Execute(string hash, ReportDefinition definition)
    {
        var dataset = ReadDataset(hash, definition);
        using var connection = Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {TableName(hash, dataset.Key)} ORDER BY rowid LIMIT 10001";
        using var reader = command.ExecuteReader();
        var table = new DataTable(definition.ReportId) { Locale = CultureInfo.InvariantCulture };
        foreach (var column in dataset.Columns) table.Columns.Add(column.Name, CrystalReportDataExecutor.ClrType(column.Type));
        while (reader.Read())
        {
            if (table.Rows.Count >= CrystalReportDataExecutor.MaxRows) throw new InvalidDataException("Sample dataset exceeds the row limit.");
            var values = dataset.Columns.Select((c, i) => reader.IsDBNull(i) ? (object)DBNull.Value : CrystalReportDataExecutor.Parse(reader.GetValue(i), c.Type)).ToArray();
            table.Rows.Add(values);
        }
        return table;
    }

    /// <summary>
    /// Points a converted report at sample rows. The corpus is <paramref name="external"/> when one is passed,
    /// and otherwise the shared pack at <see cref="CrystalSampleDatabase.Path"/>. When that corpus already
    /// contains the .rpt fingerprint and the schema keys, it is used and this store is not written.
    /// Otherwise deterministic rows for <paramref name="definition"/> and <paramref name="subreports"/> are seeded here.
    /// </summary>
    public CrystalSampleBinding Bind(string rptPath, string name, string relativePath, string status, ReportDefinition definition, IEnumerable<ReportDefinition>? subreports = null, CrystalSampleStore? external = null)
    {
        var fingerprint = Fingerprint(rptPath);
        var others = subreports ?? [];
        var corpus = external ?? SharedCorpus();
        if (corpus is not null && corpus.CanServe(fingerprint, definition, others))
            return new CrystalSampleBinding(fingerprint, corpus.DatabasePath);
        Seed(new CrystalSampleReport(fingerprint, name, relativePath, "Translation", status, 0, 0, []), definition, others);
        return new CrystalSampleBinding(fingerprint, DatabasePath);
    }

    /// <summary>Writes deterministic rows for one report into this pack. An existing catalog row for the same binary is replaced.</summary>
    public void Seed(CrystalSampleReport report, ReportDefinition definition, IEnumerable<ReportDefinition> also)
    {
        if (IsShippedCorpus)
            throw new InvalidOperationException("The shipped Crystal sample corpus is read-only. Seed a session pack, or pass another database path.");
        var datasets = also.Prepend(definition).Select(DatasetFor).GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(32).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        using var connection = Open(readOnly: false);
        using var tx = connection.BeginTransaction();
        Execute(connection, "CREATE TABLE IF NOT EXISTS ReportCatalog (Hash TEXT PRIMARY KEY, Name TEXT, Metadata TEXT)");
        Execute(connection, "CREATE TABLE IF NOT EXISTS SampleDatasets (ReportHash TEXT, DatasetKey TEXT, Metadata TEXT, PRIMARY KEY (ReportHash, DatasetKey))");
        Execute(connection, "DELETE FROM ReportCatalog WHERE Hash=$hash", ("$hash", report.Hash));
        Execute(connection, "DELETE FROM SampleDatasets WHERE ReportHash=$hash", ("$hash", report.Hash));
        var id = report.Id > 0 ? report.Id : NextId(connection);
        var stored = report with { Id = id, Rows = datasets[0].Rows.Count };
        Execute(connection, "INSERT INTO ReportCatalog (Hash, Name, Metadata) VALUES ($hash, $name, $meta)",
            ("$hash", stored.Hash), ("$name", stored.Name), ("$meta", JsonSerializer.Serialize(stored)));
        foreach (var dataset in datasets)
        {
            Execute(connection, "INSERT INTO SampleDatasets (ReportHash, DatasetKey, Metadata) VALUES ($hash, $key, $meta)",
                ("$hash", stored.Hash), ("$key", dataset.Key), ("$meta", JsonSerializer.Serialize(dataset)));
            var table = TableName(stored.Hash, dataset.Key);
            Execute(connection, $"DROP TABLE IF EXISTS {table}");
            var columns = string.Join(", ", dataset.Columns.Select((_, i) => $"c{i} TEXT"));
            Execute(connection, $"CREATE TABLE {table} ({columns})");
            foreach (var row in dataset.Rows)
            {
                var names = string.Join(", ", row.Select((_, i) => $"$p{i}"));
                Execute(connection, $"INSERT INTO {table} VALUES ({names})", row.Select((value, i) => ($"$p{i}", (object?)ElementText(value))).ToArray());
            }
        }
        tx.Commit();
    }

    private CrystalSampleDataset ReadDataset(string hash, ReportDefinition definition)
    {
        ValidateHash(hash);
        using var connection = Open(readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Metadata FROM SampleDatasets WHERE ReportHash=$hash AND DatasetKey=$key";
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$key", Key(definition));
        var json = command.ExecuteScalar() as string ?? throw new InvalidDataException(
            "No matching SQLite sample schema. Generate a sample pack for this RPT or load a matching data fixture; no substitute data was used.");
        return JsonSerializer.Deserialize<CrystalSampleDataset>(json, Json)!;
    }

    private static CrystalSampleStore? SharedCorpus()
    {
        var path = CrystalSampleDatabase.Path;
        return File.Exists(path) ? new CrystalSampleStore(path) : null;
    }

    private bool IsShippedCorpus => string.Equals(DatabasePath, Path.GetFullPath(CrystalSampleDatabase.Path), StringComparison.OrdinalIgnoreCase);

    private bool CanServe(string hash, ReportDefinition definition, IEnumerable<ReportDefinition> also)
    {
        if (!Contains(hash)) return false;
        foreach (var key in also.Prepend(definition).Select(Key).Distinct(StringComparer.OrdinalIgnoreCase).Take(32))
            if (!HasDataset(hash, key)) return false;
        return true;
    }

    private bool HasDataset(string hash, string key)
    {
        try
        {
            using var connection = Open(readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM SampleDatasets WHERE ReportHash=$hash AND DatasetKey=$key";
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() is not null;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private SqliteConnection Open(bool readOnly)
    {
        if (readOnly && !Available) throw new FileNotFoundException("The Crystal sample database is not there.", DatabasePath);
        var builder = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = readOnly ? "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;" : "PRAGMA journal_mode=DELETE;";
        command.ExecuteNonQuery();
        return connection;
    }

    public static string Fingerprint(string rptPath)
    {
        using var stream = File.Open(rptPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>The dataset key the viewer uses: the positioned schema, or the SQL, plus the field bindings.</summary>
    public static string Key(ReportDefinition definition) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        (definition.PositionedLayout is { } layout ? JsonSerializer.Serialize(new { layout.Document.CustomSql,
            Tables = layout.Document.DataSources.Select(t => new { t.Name, t.Schema, t.SourceName, t.CommandText }),
            Links = layout.Document.Links.Select(l => new { l.LeftTable, l.LeftField, l.RightTable, l.RightField, l.JoinType }),
            Fields = layout.Document.Fields.Where(f => !f.IsFormula).Select(f => new { f.Reference, f.Type }) }) : definition.Sql)
        + "\n" + string.Join("\n", definition.PositionedLayout?.Bindings.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => x.Key + "=" + x.Value) ?? CrystalReportDataExecutor.RequiredColumns(definition)))));

    public static string TableName(string hash, string key) { ValidateHash(hash); ValidateHash(key); return "sample_" + hash + "_" + key; }

    private static CrystalSampleDataset DatasetFor(ReportDefinition definition)
    {
        var names = CrystalReportDataExecutor.RequiredColumns(definition).ToList();
        if (names.Count == 0) names.Add("Sample");
        var columns = names.Select(name => new CrystalSampleColumn(name, ColumnType(definition, name), name)).ToList();
        var rows = new List<JsonElement[]>();
        for (var row = 0; row < 4; row++)
            rows.Add(columns.Select(column => JsonSerializer.SerializeToElement(SampleValue(column.Type, column.Name, row))).ToArray());
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in definition.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
            parameters[parameter.Name] = string.IsNullOrWhiteSpace(parameter.DefaultValue) ? SampleValue(ParameterType(parameter.ParameterType), parameter.Name, 0) : parameter.DefaultValue;
        return new CrystalSampleDataset(Key(definition), columns, parameters, rows);
    }

    private static string ColumnType(ReportDefinition definition, string name)
    {
        var layout = definition.PositionedLayout;
        var reference = layout?.Bindings.FirstOrDefault(pair => string.Equals(pair.Value, name, StringComparison.OrdinalIgnoreCase)).Key;
        var field = layout?.Document.Fields.FirstOrDefault(f =>
            string.Equals(f.Reference, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)
            || (reference is not null && string.Equals(f.Reference, reference, StringComparison.OrdinalIgnoreCase)));
        var type = field?.Type ?? "";
        if (type.Contains("Bool", StringComparison.OrdinalIgnoreCase)) return "boolean";
        if (type.Contains("Date", StringComparison.OrdinalIgnoreCase) || type.Contains("Time", StringComparison.OrdinalIgnoreCase)) return "datetime";
        if (type.Contains("Int", StringComparison.OrdinalIgnoreCase)) return "integer";
        if (type.Contains("Number", StringComparison.OrdinalIgnoreCase) || type.Contains("Currency", StringComparison.OrdinalIgnoreCase) || type.Contains("Decimal", StringComparison.OrdinalIgnoreCase))
            return "decimal";
        return "string";
    }

    private static string ParameterType(ReportParameterType type) => type switch
    {
        ReportParameterType.Integer => "integer",
        ReportParameterType.Decimal => "decimal",
        ReportParameterType.Boolean => "boolean",
        ReportParameterType.Date => "datetime",
        _ => "string"
    };

    private static string SampleValue(string type, string name, int row) => type switch
    {
        "integer" => (row + 1).ToString(CultureInfo.InvariantCulture),
        "decimal" => (10.5m * (row + 1)).ToString(CultureInfo.InvariantCulture),
        "number" => (1.25d * (row + 1)).ToString(CultureInfo.InvariantCulture),
        "boolean" => row % 2 == 0 ? "1" : "0",
        "datetime" => new DateTime(2024, 3, 1).AddDays(row).ToString("o", CultureInfo.InvariantCulture),
        _ => row switch { 0 => "North", 1 => "South", 2 => "East", _ => "West" } + " " + name
    };

    private static string? ElementText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.Null => null,
        _ => value.ToString()
    };

    private static int NextId(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Metadata FROM ReportCatalog";
        using var reader = command.ExecuteReader();
        var max = 0;
        while (reader.Read())
        {
            var stored = JsonSerializer.Deserialize<CrystalSampleReport>(reader.GetString(0), Json);
            if (stored is not null && stored.Id > max) max = stored.Id;
        }
        return max + 1;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void ValidateHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c))) throw new InvalidDataException("Invalid sample fingerprint.");
    }
}
