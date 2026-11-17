using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fx.ControlKit.Reports;

namespace Fx.ControlKit.Artifacts.Crystal;

public enum CrystalReportDataMode { None, LayoutOnly, Fixture, SyntheticSqlite }

/// <summary>
/// Report data for the Crystal viewer: synthetic SQLite samples, an empty layout, or a matching fixture.
/// Report SQL is not sent to SQLite. A host's read-only SQL Server mode is not enabled here.
/// </summary>
public sealed class CrystalReportDataExecutor : IReportDefinitionDataExecutor
{
    public const int MaxRows = 10000;
    public CrystalReportDataMode Mode { get; set; }
    public string? SampleReportHash { get; set; }
    public string? FixtureName { get; private set; }
    private CrystalSampleStore? _samples;
    private List<FixtureResult> _fixtures = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public sealed record FixtureColumn(string Name, string Type);
    public sealed record FixtureResult(string ReportId, string SqlSha256, Dictionary<string, JsonElement> Parameters,
        List<FixtureColumn> Columns, List<JsonElement[]> Rows);
    public sealed record FixtureFile(List<FixtureResult> Reports);

    public void UseSamples(string databasePath) => _samples = new CrystalSampleStore(databasePath);

    public void LoadFixture(string name, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new InvalidDataException("Fixtures are limited to 16 MB.");
        var file = JsonSerializer.Deserialize<FixtureFile>(json, JsonOptions) ?? throw new InvalidDataException("Empty fixture.");
        if (file.Reports is null || file.Reports.Count is < 1 or > 256) throw new InvalidDataException("Provide 1 to 256 report result sets.");
        foreach (var result in file.Reports)
        {
            if (result is null || string.IsNullOrWhiteSpace(result.ReportId) || result.SqlSha256?.Length != 64 || result.Parameters is null
                || result.Columns is null || result.Rows is null || result.Columns.Count is < 1 or > 2048 || result.Rows.Count > MaxRows)
                throw new InvalidDataException("Invalid fixture report, query fingerprint, parameters, columns, or row limit.");
            _ = Materialize(result);
        }
        _fixtures = file.Reports;
        FixtureName = Path.GetFileName(name);
    }

    public DataTable Execute(ReportDefinition definition, IDictionary<string, object>? parameters)
    {
        if (Mode == CrystalReportDataMode.SyntheticSqlite)
            return (_samples ?? throw new InvalidOperationException("SQLite sample pack is not configured.")).Execute(
                SampleReportHash ?? throw new InvalidOperationException("Select a converted report with sample data."), definition);
        if (Mode == CrystalReportDataMode.LayoutOnly)
        {
            var empty = new DataTable(definition.ReportId);
            foreach (var column in RequiredColumns(definition)) empty.Columns.Add(column, typeof(object));
            return empty;
        }
        if (Mode != CrystalReportDataMode.Fixture) throw new InvalidOperationException("Select a report data source before running.");
        var matches = _fixtures.Where(f => string.Equals(f.ReportId, definition.ReportId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(f.SqlSha256, QueryHash(definition.Sql), StringComparison.OrdinalIgnoreCase)
            && ParametersMatch(f.Parameters, parameters)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"Fixture requires exactly one matching query and parameter set for '{definition.ReportId}'; found {matches.Length}.");
        var table = Materialize(matches[0]);
        var missing = RequiredColumns(definition).Where(c => !table.Columns.Contains(c)).ToArray();
        if (missing.Length != 0) throw new InvalidDataException($"Fixture '{definition.ReportId}' is missing columns: {string.Join(", ", missing)}.");
        return table;
    }

    public DataTable Execute(string sql, IDictionary<string, object>? parameters) =>
        throw new InvalidOperationException("The read-only SQL Server report database is not enabled. Use synthetic SQLite samples, a fixture, or layout-only.");

    public static string QueryHash(string sql) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));

    public static IEnumerable<string> RequiredColumns(ReportDefinition definition) =>
        (definition.PositionedLayout?.Bindings.Values ?? definition.Columns.Select(c => c.Field))
        .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase);

    public static Type ClrType(string type) => type switch
    {
        "integer" => typeof(long), "decimal" => typeof(decimal), "number" => typeof(double), "boolean" => typeof(bool),
        "datetime" => typeof(DateTime), "string" => typeof(string), _ => throw new InvalidDataException("Unknown sample column type: " + type)
    };

    public static object Parse(object value, string type) => type switch
    {
        "integer" => checked((long)decimal.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)),
        "boolean" => Convert.ToString(value, CultureInfo.InvariantCulture) is "1" or "0"
            ? Convert.ToString(value, CultureInfo.InvariantCulture) == "1"
            : bool.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
        "datetime" => DateTime.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        _ => Convert.ChangeType(value, ClrType(type), CultureInfo.InvariantCulture)
    };

    private static bool ParametersMatch(Dictionary<string, JsonElement> expected, IDictionary<string, object>? actual)
    {
        if (expected.Count != (actual?.Count ?? 0)) return false;
        return expected.All(e => actual!.Any(a => string.Equals(a.Key.TrimStart('@'), e.Key.TrimStart('@'), StringComparison.OrdinalIgnoreCase)
            && Scalar(e.Value) == Scalar(JsonSerializer.SerializeToElement(a.Value is DBNull ? null : a.Value))));
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDecimal().ToString("G29", CultureInfo.InvariantCulture),
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Null => "\0null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => throw new InvalidDataException("Fixture parameters must be scalar values.")
    };

    private static DataTable Materialize(FixtureResult fixture)
    {
        var table = new DataTable(fixture.ReportId) { Locale = CultureInfo.InvariantCulture };
        foreach (var column in fixture.Columns)
        {
            if (string.IsNullOrWhiteSpace(column.Name) || table.Columns.Contains(column.Name)) throw new InvalidDataException("Fixture column names must be unique and nonempty.");
            table.Columns.Add(column.Name, ClrType(column.Type?.ToLowerInvariant() ?? ""));
        }
        foreach (var row in fixture.Rows)
        {
            if (row is null || row.Length != fixture.Columns.Count) throw new InvalidDataException("Fixture row width does not match its columns.");
            var values = row.Select((value, i) => value.ValueKind == JsonValueKind.Null ? DBNull.Value
                : JsonSerializer.Deserialize(value.GetRawText(), table.Columns[i].DataType) ?? DBNull.Value).ToArray();
            table.Rows.Add(values);
        }
        return table;
    }
}

/// <summary>Session context for a viewer that has no host session values to inject.</summary>
public sealed class EmptyReportSessionContext : IReportSessionContext
{
    public object? Get(string parameterName) => null;
}

/// <summary>
/// Satisfies <see cref="ReportWriterControl"/> when a host asks for <see cref="IReportExporter"/>.
/// XML pagination does not call it. Binary Crystal export needs a SAP runtime this host does not have.
/// </summary>
public sealed class UnavailableCrystalExporter : IReportExporter
{
    public bool IsLoaded => false;
    public void LoadReport(string rptFilePath) => throw Unavailable();
    public void SetParameters(Dictionary<string, string> parameters) => throw Unavailable();
    public byte[] ExportToPdf() => throw Unavailable();
    public byte[] ExportToExcel() => throw Unavailable();
    public byte[] ExportToWord() => throw Unavailable();
    public byte[] ExportToRtf() => throw Unavailable();
    public byte[] ExportToCsv() => throw Unavailable();

    private static InvalidOperationException Unavailable() =>
        new("Crystal binary export is not available. The viewer renders the converted XML. There is no SAP runtime.");
}
