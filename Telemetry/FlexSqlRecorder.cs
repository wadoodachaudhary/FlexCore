using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fx.ControlKit.Telemetry;

/// <summary>What a recorded database event was.</summary>
public static class FlexSqlEventKinds
{
    public const string Statement = "Statement";
    public const string ConnectionOpen = "ConnectionOpen";
    public const string ConnectionFailed = "ConnectionFailed";
    public const string TransactionCommit = "TransactionCommit";
    public const string TransactionRollback = "TransactionRollback";
}

/// <summary>Why a database call failed, as far as the provider's error tells.</summary>
public static class FlexSqlErrorClasses
{
    public const string None = "";
    public const string Deadlock = "Deadlock";
    public const string LockTimeout = "LockTimeout";
    public const string CommandTimeout = "CommandTimeout";
    public const string ConnectionFailure = "ConnectionFailure";
    public const string Cancelled = "Cancelled";
    public const string Other = "Other";
}

/// <summary>Who and where a database call came from. A host supplies it; every field may be empty.</summary>
public sealed record FlexSqlContext(string Tenant = "", string UserId = "", string SessionId = "", string Screen = "");

/// <summary>What one statement does, read from its text: its kind and the tables it names.</summary>
public sealed record FlexSqlShape(string StatementKind, bool IsWrite, IReadOnlyList<string> Tables);

/// <summary>
/// Reads a statement's kind and tables from its text. It is a text reading, not a parser: it strips comments and
/// string literals, then looks at the words after FROM, JOIN, INTO, UPDATE, DELETE, MERGE, TRUNCATE and EXEC.
/// It can miss a table (dynamic SQL, a view's base tables) and never invents one that is not written there.
/// </summary>
public static class FlexSqlAnalyzer
{
    private const int MaxCached = 4000;
    private const int MaxTables = 12;
    private static readonly ConcurrentDictionary<string, FlexSqlShape> Cache = new(StringComparer.Ordinal);
    private static readonly FlexSqlShape Empty = new("Other", false, Array.Empty<string>());

    private static readonly Regex Noise = new(@"--[^\r\n]*|/\*.*?\*/|N?'(?:[^']|'')*'", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TableRefs = new(
        @"\b(?<verb>FROM|JOIN|INTO|UPDATE|MERGE(?:\s+INTO)?|TRUNCATE\s+TABLE|DELETE(?:\s+FROM)?|EXEC(?:UTE)?)\s+(?<name>(?:\[[^\]]+\]|[#@\w]+)(?:\s*\.\s*(?:\[[^\]]+\]|\w+)){0,2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Writes = new(@"\b(?<kind>INSERT|UPDATE|DELETE|MERGE|TRUNCATE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Ddl = new(@"\b(CREATE|ALTER|DROP)\s+(TABLE|INDEX|VIEW|PROC|PROCEDURE|FUNCTION|TRIGGER)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Exec = new(@"^\s*(?:DECLARE\b[^;]*;\s*)*EXEC(?:UTE)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareProcedure = new(@"^\s*(?:\[?\w+\]?\.)?\[?\w+\]?\s*$", RegexOptions.Compiled);
    private static readonly HashSet<string> NotTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "SET", "WHERE", "VALUES", "OUTPUT", "TOP", "FROM", "INTO", "WITH", "AS", "ON", "OPENJSON", "STRING_SPLIT", "DELETED", "INSERTED"
    };

    public static FlexSqlShape Analyze(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return Empty;
        if (Cache.TryGetValue(sql, out var known))
            return known;

        var text = Noise.Replace(sql, " ");
        var tables = new List<string>();
        var procedures = new List<string>();
        foreach (Match m in TableRefs.Matches(text))
        {
            var name = CleanName(m.Groups["name"].Value);
            if (name.Length == 0 || name[0] is '@' or '#' || NotTables.Contains(name) || char.IsDigit(name[0]))
                continue;
            var target = m.Groups["verb"].Value.StartsWith("EXEC", StringComparison.OrdinalIgnoreCase) ? procedures : tables;
            if (!target.Contains(name, StringComparer.OrdinalIgnoreCase) && target.Count < MaxTables)
                target.Add(name);
        }

        string kind;
        var write = Writes.Match(text);
        if (Ddl.IsMatch(text)) kind = "Schema";
        else if (write.Success) kind = Capitalize(write.Groups["kind"].Value);
        else if (Exec.IsMatch(text) || BareProcedure.IsMatch(text)) kind = "Exec";
        else if (text.Contains("SELECT", StringComparison.OrdinalIgnoreCase)) kind = "Select";
        else kind = "Other";

        if (kind == "Exec" && procedures.Count == 0 && BareProcedure.IsMatch(text))
            procedures.Add(CleanName(text.Trim()));
        // A procedure is listed with the tables: what it touches inside is not in the statement's text.
        tables.AddRange(procedures.Select(p => "EXEC " + p));

        var shape = new FlexSqlShape(kind, kind is "Insert" or "Update" or "Delete" or "Merge" or "Truncate" or "Schema", tables);
        if (Cache.Count >= MaxCached)
            Cache.Clear();
        Cache[sql] = shape;
        return shape;
    }

    /// <summary>The provider's error number (SQL Server's <c>Number</c>), found without a reference to any provider.</summary>
    public static int? ErrorNumberOf(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbException)
            {
                var number = e.GetType().GetProperty("Number")?.GetValue(e);
                if (number is int n)
                    return n;
            }
        }
        return null;
    }

    public static string ClassifyError(Exception? ex, int? number = null)
    {
        if (ex is null)
            return FlexSqlErrorClasses.None;
        number ??= ErrorNumberOf(ex);
        switch (number)
        {
            case 1205: return FlexSqlErrorClasses.Deadlock;
            case 1222: return FlexSqlErrorClasses.LockTimeout;
            case -2: return FlexSqlErrorClasses.CommandTimeout;
            // No server, login refused, database unavailable, transport dropped.
            case -1 or 2 or 53 or 64 or 121 or 233 or 258 or 4060 or 10053 or 10054 or 10060 or 10061 or 18456 or 40613:
                return FlexSqlErrorClasses.ConnectionFailure;
        }
        // SQL Server's client reports a connection it could not make as error 0 of class 20 or above.
        if (number is null or 0 && SeverityOf(ex) >= 20)
            return FlexSqlErrorClasses.ConnectionFailure;
        if (ex is OperationCanceledException)
            return FlexSqlErrorClasses.Cancelled;
        if (ex is TimeoutException)
            return FlexSqlErrorClasses.CommandTimeout;
        return FlexSqlErrorClasses.Other;
    }

    private static int SeverityOf(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DbException && e.GetType().GetProperty("Class")?.GetValue(e) is byte severity)
                return severity;
        }
        return 0;
    }

    private static string CleanName(string raw)
    {
        var last = raw.Split('.')[^1].Trim();
        return last.Trim('[', ']').Trim();
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();
}

/// <summary>One hour of database calls of one kind, by one user, from one screen, on one table.</summary>
public sealed class FlexSqlRollupRow
{
    public DateTime HourUtc { get; set; }
    public string Tenant { get; set; } = string.Empty;
    public string Screen { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    /// <summary>A table, "EXEC procedure", or empty for the call itself (also a connection or a transaction).</summary>
    public string Table { get; set; } = string.Empty;
    /// <summary>Select, Insert, Update, Delete, Merge, Exec, Schema, Other, or an event kind.</summary>
    public string Kind { get; set; } = string.Empty;
    public long Count { get; set; }
    public long Errors { get; set; }
    public long Deadlocks { get; set; }
    public long LockTimeouts { get; set; }
    public long CommandTimeouts { get; set; }
    public long ConnectionFailures { get; set; }
    public long TotalMs { get; set; }
    public long MaxMs { get; set; }
    public long Rows { get; set; }
}

/// <summary>
/// Hourly counters of database calls per table, screen, user and kind. Small enough to keep for good where the
/// statements themselves are kept only briefly. Every call counts once in the row with an empty
/// <see cref="FlexSqlRollupRow.Table"/> (use those rows for totals by screen, user or hour) and once more on each
/// table it names (use those rows for totals by table; they overlap for a statement naming several tables).
/// </summary>
public sealed class FlexSqlRollup
{
    private readonly ConcurrentDictionary<string, FlexSqlRollupRow> _rows = new(StringComparer.Ordinal);
    private readonly int _maxRows;
    private readonly TimeSpan _retain;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public FlexSqlRollup(int maxRows = 100_000, int retainHours = 24 * 35)
    {
        _maxRows = Math.Max(1_000, maxRows);
        _retain = TimeSpan.FromHours(Math.Max(1, retainHours));
    }

    public int RowCount => _rows.Count;

    public void Add(FlexSqlTelemetryEntry entry)
    {
        var hour = new DateTime(entry.TimestampUtc.Year, entry.TimestampUtc.Month, entry.TimestampUtc.Day, entry.TimestampUtc.Hour, 0, 0, DateTimeKind.Utc);
        var statement = entry.EventKind == FlexSqlEventKinds.Statement;
        var kind = statement ? entry.StatementKind : entry.EventKind;
        var tables = statement ? entry.TableList : Array.Empty<string>();
        // The row with no table counts the call itself, once; each table it names gets a row as well.
        Bump(hour, entry, string.Empty, kind);
        foreach (var table in tables)
            Bump(hour, entry, table, kind);

        if (_rows.Count > _maxRows)
            Prune(DateTime.UtcNow - _retain, force: true);
    }

    private void Bump(DateTime hour, FlexSqlTelemetryEntry e, string table, string kind)
    {
        var key = string.Concat(hour.Ticks.ToString(), "|", e.Tenant, "|", e.Screen, "|", e.UserId, "|", table, "|", kind);
        var row = _rows.GetOrAdd(key, _ => new FlexSqlRollupRow
        {
            HourUtc = hour, Tenant = e.Tenant, Screen = e.Screen, UserId = e.UserId, Table = table, Kind = kind
        });
        lock (row)
        {
            row.Count++;
            row.TotalMs += e.ElapsedMs;
            if (e.ElapsedMs > row.MaxMs) row.MaxMs = e.ElapsedMs;
            if (e.RowCount is int rows && rows > 0) row.Rows += rows;
            if (e.HasError)
            {
                row.Errors++;
                switch (e.ErrorClass)
                {
                    case FlexSqlErrorClasses.Deadlock: row.Deadlocks++; break;
                    case FlexSqlErrorClasses.LockTimeout: row.LockTimeouts++; break;
                    case FlexSqlErrorClasses.CommandTimeout: row.CommandTimeouts++; break;
                    case FlexSqlErrorClasses.ConnectionFailure: row.ConnectionFailures++; break;
                }
            }
        }
    }

    /// <summary>A copy of the counters, newest hour first.</summary>
    public List<FlexSqlRollupRow> Snapshot(DateTime? sinceUtc = null, string? tenant = null)
    {
        var result = new List<FlexSqlRollupRow>(_rows.Count);
        foreach (var row in _rows.Values)
        {
            if (sinceUtc is DateTime since && row.HourUtc < since)
                continue;
            if (!string.IsNullOrEmpty(tenant) && tenant != "*" && !string.Equals(row.Tenant, tenant, StringComparison.OrdinalIgnoreCase))
                continue;
            lock (row)
                result.Add(new FlexSqlRollupRow
                {
                    HourUtc = row.HourUtc, Tenant = row.Tenant, Screen = row.Screen, UserId = row.UserId, Table = row.Table, Kind = row.Kind,
                    Count = row.Count, Errors = row.Errors, Deadlocks = row.Deadlocks, LockTimeouts = row.LockTimeouts,
                    CommandTimeouts = row.CommandTimeouts, ConnectionFailures = row.ConnectionFailures,
                    TotalMs = row.TotalMs, MaxMs = row.MaxMs, Rows = row.Rows
                });
        }
        result.Sort((a, b) => b.HourUtc.CompareTo(a.HourUtc));
        return result;
    }

    public void Prune(DateTime olderThanUtc, bool force = false)
    {
        foreach (var kv in _rows)
            if (kv.Value.HourUtc < olderThanUtc)
                _rows.TryRemove(kv.Key, out _);
        if (force && _rows.Count > _maxRows)
            foreach (var kv in _rows.OrderBy(k => k.Value.HourUtc).Take(_rows.Count - _maxRows))
                _rows.TryRemove(kv.Key, out _);
    }

    /// <summary>Writes the counters to a file, replaced whole.</summary>
    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, Snapshot(), Json, ct);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Adds a saved file's counters to those in memory (at startup). A missing or unreadable file adds nothing.</summary>
    public async Task<int> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return 0;
        try
        {
            await using var stream = File.OpenRead(path);
            var rows = await JsonSerializer.DeserializeAsync<List<FlexSqlRollupRow>>(stream, Json, ct) ?? new();
            var cutoff = DateTime.UtcNow - _retain;
            var loaded = 0;
            foreach (var r in rows.Where(r => r.HourUtc >= cutoff))
            {
                var key = string.Concat(r.HourUtc.Ticks.ToString(), "|", r.Tenant, "|", r.Screen, "|", r.UserId, "|", r.Table, "|", r.Kind);
                if (_rows.TryAdd(key, r))
                    loaded++;
            }
            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>The hourly counters added up by table, screen, user or hour, for a view.</summary>
public sealed class FlexSqlRollupSummary
{
    public string Name { get; set; } = string.Empty;
    public long Calls { get; set; }
    public long Reads { get; set; }
    public long Writes { get; set; }
    public int Users { get; set; }
    public int Screens { get; set; }
    public long TotalMs { get; set; }
    public long MaxMs { get; set; }
    public double AverageMs { get; set; }
    public long Errors { get; set; }
    public long Deadlocks { get; set; }
    public long LockTimeouts { get; set; }
    public long CommandTimeouts { get; set; }
    public long ConnectionFailures { get; set; }

    private static readonly HashSet<string> WriteKinds = new(StringComparer.Ordinal) { "Insert", "Update", "Delete", "Merge", "Truncate", "Schema" };

    public static FlexSqlRollupSummary Total(IEnumerable<FlexSqlRollupRow> rows, string name = "")
    {
        var list = rows as IReadOnlyCollection<FlexSqlRollupRow> ?? rows.ToList();
        var calls = list.Sum(r => r.Count);
        var total = list.Sum(r => r.TotalMs);
        return new FlexSqlRollupSummary
        {
            Name = name,
            Calls = calls,
            Reads = list.Where(r => r.Kind == "Select").Sum(r => r.Count),
            Writes = list.Where(r => WriteKinds.Contains(r.Kind)).Sum(r => r.Count),
            Users = list.Select(r => r.UserId).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Screens = list.Select(r => r.Screen).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            TotalMs = total,
            MaxMs = list.Count == 0 ? 0 : list.Max(r => r.MaxMs),
            AverageMs = calls == 0 ? 0 : Math.Round((double)total / calls, 1),
            Errors = list.Sum(r => r.Errors),
            Deadlocks = list.Sum(r => r.Deadlocks),
            LockTimeouts = list.Sum(r => r.LockTimeouts),
            CommandTimeouts = list.Sum(r => r.CommandTimeouts),
            ConnectionFailures = list.Sum(r => r.ConnectionFailures)
        };
    }

    /// <summary>By "Table" uses the per-table rows; by "Screen", "User" or "Hour" uses the once-per-call rows.</summary>
    public static List<FlexSqlRollupSummary> Group(IEnumerable<FlexSqlRollupRow> rows, string by)
    {
        var groups = by switch
        {
            "Table" => rows.Where(r => r.Table.Length > 0).GroupBy(r => r.Table, StringComparer.OrdinalIgnoreCase),
            "Screen" => rows.Where(r => r.Table.Length == 0).GroupBy(r => r.Screen.Length == 0 ? "(no screen)" : r.Screen, StringComparer.OrdinalIgnoreCase),
            "User" => rows.Where(r => r.Table.Length == 0).GroupBy(r => r.UserId.Length == 0 ? "(no user)" : r.UserId, StringComparer.OrdinalIgnoreCase),
            _ => rows.Where(r => r.Table.Length == 0).GroupBy(r => r.HourUtc.ToLocalTime().ToString("yyyy-MM-dd HH:00"), StringComparer.Ordinal)
        };
        var result = groups.Select(g => Total(g.ToList(), g.Key));
        return (by == "Hour" ? result.OrderByDescending(s => s.Name, StringComparer.Ordinal) : result.OrderByDescending(s => s.Calls)).ToList();
    }
}

/// <summary>
/// The one place database calls are reported from: FlexKit's own <c>DbControl</c> and any host data layer call it.
/// It reads the statement's kind and tables and the error's class, then hands the entry to the sink.
/// A recorder must never break the call it describes: every method swallows its own failures.
/// </summary>
public static class FlexSqlRecorder
{
    /// <summary>Where entries go. The first <see cref="FlexSqlTelemetryStore"/> created sets itself here.</summary>
    public static IFlexSqlTelemetryProvider? Sink { get; set; }

    /// <summary>Supplies who and where for callers that pass no context (FlexKit's DbControl). Optional.</summary>
    public static Func<FlexSqlContext?>? AmbientContext { get; set; }

    /// <summary>A connection open slower than this is kept as an entry of its own; faster ones are only counted.</summary>
    public static int SlowConnectionMs { get; set; } = 250;

    public static void Statement(string source, string sql, string parameters, long elapsedMs, int? rowCount,
        Exception? error = null, FlexSqlContext? context = null, bool isBackgroundPoll = false)
    {
        var sink = Sink;
        if (sink is null || string.IsNullOrWhiteSpace(sql))
            return;
        try
        {
            sink.RecordSql(Build(FlexSqlEventKinds.Statement, source, sql, parameters, elapsedMs, rowCount, error, context, isBackgroundPoll));
        }
        catch
        {
            // Telemetry never fails the statement it describes.
        }
    }

    /// <summary>A connection opened, or failed to open. <paramref name="target"/> is the database or data source name, never a connection string.</summary>
    public static void Connection(string source, string target, long elapsedMs, Exception? error = null, FlexSqlContext? context = null)
    {
        var sink = Sink;
        if (sink is null)
            return;
        try
        {
            var kind = error is null ? FlexSqlEventKinds.ConnectionOpen : FlexSqlEventKinds.ConnectionFailed;
            var entry = Build(kind, source, $"({(error is null ? "connection open" : "connection failed")}: {target})", string.Empty, elapsedMs, null, error, context, false);
            if (error is null && elapsedMs < SlowConnectionMs && sink is FlexSqlTelemetryStore store)
                store.Rollup.Add(entry);     // counted, not listed
            else
                sink.RecordSql(entry);
        }
        catch
        {
        }
    }

    /// <summary>A transaction ended. <paramref name="elapsedMs"/> runs from its begin to its commit or rollback.</summary>
    public static void Transaction(string source, bool committed, long elapsedMs, int statements, Exception? error = null, FlexSqlContext? context = null)
    {
        var sink = Sink;
        if (sink is null)
            return;
        try
        {
            var kind = committed ? FlexSqlEventKinds.TransactionCommit : FlexSqlEventKinds.TransactionRollback;
            sink.RecordSql(Build(kind, source, $"({(committed ? "transaction committed" : "transaction rolled back")}: {statements} statements)", string.Empty, elapsedMs, statements, error, context, false));
        }
        catch
        {
        }
    }

    private static FlexSqlTelemetryEntry Build(string kind, string source, string sql, string parameters, long elapsedMs, int? rowCount,
        Exception? error, FlexSqlContext? context, bool isBackgroundPoll)
    {
        context ??= AmbientContext?.Invoke() ?? new FlexSqlContext();
        var number = FlexSqlAnalyzer.ErrorNumberOf(error);
        return new FlexSqlTelemetryEntry
        {
            EventKind = kind,
            Source = source,
            Sql = sql,
            Parameters = parameters,
            ElapsedMs = elapsedMs,
            RowCount = rowCount,
            ErrorMessage = error?.Message,
            ErrorNumber = number,
            ErrorClass = FlexSqlAnalyzer.ClassifyError(error, number),
            Tenant = context.Tenant,
            UserId = context.UserId,
            SessionId = context.SessionId,
            Screen = context.Screen,
            IsBackgroundPoll = isBackgroundPoll
        };
    }
}
