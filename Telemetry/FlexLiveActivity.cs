using System.Collections.Concurrent;
using System.Diagnostics;

namespace Fx.ControlKit.Telemetry;

/// <summary>One minute of database calls for one tenant ("" = every tenant).</summary>
public sealed class FlexSqlMinute
{
    public DateTime MinuteUtc { get; init; }
    public long Statements { get; set; }
    public long Reads { get; set; }
    public long Writes { get; set; }
    public long Errors { get; set; }
    public long Slow { get; set; }
    public long TotalMs { get; set; }
    public long MaxMs { get; set; }
    public long RowsRead { get; set; }
    public long RowsAffected { get; set; }
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
    public long ConnectionsOpened { get; set; }
    public long ConnectionFailures { get; set; }
    public long Commits { get; set; }
    public long Rollbacks { get; set; }
    /// <summary>Up to <see cref="FlexSqlLiveMeter.MaxSamplesPerMinute"/> statement times of the minute, for percentiles.</summary>
    public List<int> Samples { get; } = new();

    internal FlexSqlMinute Copy()
    {
        var copy = (FlexSqlMinute)MemberwiseClone();
        var fresh = new FlexSqlMinute
        {
            MinuteUtc = MinuteUtc, Statements = Statements, Reads = Reads, Writes = Writes, Errors = Errors, Slow = Slow,
            TotalMs = TotalMs, MaxMs = MaxMs, RowsRead = RowsRead, RowsAffected = RowsAffected, BytesReceived = BytesReceived,
            BytesSent = BytesSent, ConnectionsOpened = ConnectionsOpened, ConnectionFailures = ConnectionFailures,
            Commits = Commits, Rollbacks = Rollbacks
        };
        fresh.Samples.AddRange(copy.Samples);
        return fresh;
    }
}

/// <summary>
/// Per-minute counters of database calls, kept for a few hours, and the gauges of what is happening now.
/// Fed by the SQL telemetry store with every entry it records or counts.
/// </summary>
public sealed class FlexSqlLiveMeter
{
    public const int MaxSamplesPerMinute = 400;
    private readonly ConcurrentDictionary<(string Tenant, long Minute), FlexSqlMinute> _minutes = new();
    private readonly int _retainMinutes;
    private long _lastPruneMinute;

    private static long _openConnections, _peakOpenConnections, _runningStatements, _peakRunningStatements;

    public FlexSqlLiveMeter(int retainMinutes = 24 * 60) => _retainMinutes = Math.Max(15, retainMinutes);

    /// <summary>Connections open now in this process, as the provider reported their opens and closes.</summary>
    public static long OpenConnections => Math.Max(0, Interlocked.Read(ref _openConnections));
    public static long PeakOpenConnections => Interlocked.Read(ref _peakOpenConnections);
    /// <summary>Statements sent and not yet answered.</summary>
    public static long RunningStatements => Math.Max(0, Interlocked.Read(ref _runningStatements));
    public static long PeakRunningStatements => Interlocked.Read(ref _peakRunningStatements);

    internal static void ConnectionOpen(int delta) => Move(ref _openConnections, ref _peakOpenConnections, delta);
    internal static void StatementRunning(int delta) => Move(ref _runningStatements, ref _peakRunningStatements, delta);

    private static void Move(ref long gauge, ref long peak, int delta)
    {
        var now = Interlocked.Add(ref gauge, delta);
        if (now < 0)
            Interlocked.CompareExchange(ref gauge, 0, now);   // an end seen without its start
        long seen;
        while (now > (seen = Interlocked.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
    }

    public void Add(FlexSqlTelemetryEntry entry)
    {
        var minute = entry.TimestampUtc.Ticks / TimeSpan.TicksPerMinute;
        Bump(string.Empty, minute, entry);
        if (entry.Tenant.Length > 0)
            Bump(entry.Tenant.ToUpperInvariant(), minute, entry);
        if (minute != Interlocked.Exchange(ref _lastPruneMinute, minute))
            Prune(minute - _retainMinutes);
    }

    private void Bump(string tenant, long minute, FlexSqlTelemetryEntry e)
    {
        var row = _minutes.GetOrAdd((tenant, minute), k => new FlexSqlMinute { MinuteUtc = new DateTime(k.Minute * TimeSpan.TicksPerMinute, DateTimeKind.Utc) });
        lock (row)
        {
            switch (e.EventKind)
            {
                case FlexSqlEventKinds.Statement:
                    row.Statements++;
                    if (e.IsWrite) row.Writes++; else row.Reads++;
                    if (e.HasError) row.Errors++;
                    if (e.IsSlow) row.Slow++;
                    row.TotalMs += e.ElapsedMs;
                    if (e.ElapsedMs > row.MaxMs) row.MaxMs = e.ElapsedMs;
                    if (row.Samples.Count < MaxSamplesPerMinute)
                        row.Samples.Add((int)Math.Min(int.MaxValue, e.ElapsedMs));
                    else
                        row.Samples[(int)(row.Statements % MaxSamplesPerMinute)] = (int)Math.Min(int.MaxValue, e.ElapsedMs);
                    break;
                case FlexSqlEventKinds.ConnectionOpen: row.ConnectionsOpened++; break;
                case FlexSqlEventKinds.ConnectionFailed: row.ConnectionFailures++; row.Errors++; break;
                case FlexSqlEventKinds.TransactionCommit: row.Commits++; break;
                case FlexSqlEventKinds.TransactionRollback: row.Rollbacks++; break;
            }
            row.RowsRead += e.RowsRead;
            row.RowsAffected += e.RowsAffected;
            row.BytesReceived += e.BytesReceived ?? 0;
            row.BytesSent += e.BytesSent ?? 0;
        }
    }

    private void Prune(long olderThanMinute)
    {
        foreach (var key in _minutes.Keys)
            if (key.Minute < olderThanMinute)
                _minutes.TryRemove(key, out _);
    }

    /// <summary>One entry per minute of the window, oldest first, empty minutes included.</summary>
    public List<FlexSqlMinute> Minutes(string? tenant, int minutes, DateTime? nowUtc = null)
    {
        var key = string.IsNullOrWhiteSpace(tenant) || tenant == "*" ? string.Empty : tenant.ToUpperInvariant();
        var last = (nowUtc ?? DateTime.UtcNow).Ticks / TimeSpan.TicksPerMinute;
        minutes = Math.Clamp(minutes, 1, _retainMinutes);
        var result = new List<FlexSqlMinute>(minutes);
        for (var minute = last - minutes + 1; minute <= last; minute++)
        {
            if (_minutes.TryGetValue((key, minute), out var row))
                lock (row) result.Add(row.Copy());
            else
                result.Add(new FlexSqlMinute { MinuteUtc = new DateTime(minute * TimeSpan.TicksPerMinute, DateTimeKind.Utc) });
        }
        return result;
    }
}

/// <summary>The database side of a live view: a window's totals, latency and the gauges.</summary>
public sealed class FlexSqlLiveSummary
{
    public int WindowMinutes { get; init; }
    public long Statements { get; init; }
    public long Reads { get; init; }
    public long Writes { get; init; }
    public long Errors { get; init; }
    public long Slow { get; init; }
    public long RowsRead { get; init; }
    public long RowsAffected { get; init; }
    public long BytesReceived { get; init; }
    public long BytesSent { get; init; }
    public long ConnectionsOpened { get; init; }
    public long ConnectionFailures { get; init; }
    public long Commits { get; init; }
    public long Rollbacks { get; init; }
    public double AverageMs { get; init; }
    public long MedianMs { get; init; }
    public long P95Ms { get; init; }
    public long MaxMs { get; init; }
    /// <summary>Statements per minute over the window.</summary>
    public double PerMinute { get; init; }
    /// <summary>Time spent waiting on the database as a share of the window: 100 = one statement running all the time.</summary>
    public double LoadPercent { get; init; }
    public long OpenConnections { get; init; }
    public long PeakOpenConnections { get; init; }
    public long RunningStatements { get; init; }

    public static FlexSqlLiveSummary From(IReadOnlyList<FlexSqlMinute> minutes)
    {
        var samples = minutes.SelectMany(m => m.Samples).OrderBy(x => x).ToList();
        long At(double share) => samples.Count == 0 ? 0 : samples[Math.Min(samples.Count - 1, (int)Math.Ceiling(share * samples.Count) - 1)];
        var statements = minutes.Sum(m => m.Statements);
        var totalMs = minutes.Sum(m => m.TotalMs);
        return new FlexSqlLiveSummary
        {
            WindowMinutes = minutes.Count,
            Statements = statements,
            Reads = minutes.Sum(m => m.Reads),
            Writes = minutes.Sum(m => m.Writes),
            Errors = minutes.Sum(m => m.Errors),
            Slow = minutes.Sum(m => m.Slow),
            RowsRead = minutes.Sum(m => m.RowsRead),
            RowsAffected = minutes.Sum(m => m.RowsAffected),
            BytesReceived = minutes.Sum(m => m.BytesReceived),
            BytesSent = minutes.Sum(m => m.BytesSent),
            ConnectionsOpened = minutes.Sum(m => m.ConnectionsOpened),
            ConnectionFailures = minutes.Sum(m => m.ConnectionFailures),
            Commits = minutes.Sum(m => m.Commits),
            Rollbacks = minutes.Sum(m => m.Rollbacks),
            AverageMs = statements == 0 ? 0 : Math.Round((double)totalMs / statements, 1),
            MedianMs = At(0.5),
            P95Ms = At(0.95),
            MaxMs = minutes.Count == 0 ? 0 : minutes.Max(m => m.MaxMs),
            PerMinute = minutes.Count == 0 ? 0 : Math.Round((double)statements / minutes.Count, 1),
            LoadPercent = minutes.Count == 0 ? 0 : Math.Round(100.0 * totalMs / (minutes.Count * 60_000.0), 1),
            OpenConnections = FlexSqlLiveMeter.OpenConnections,
            PeakOpenConnections = FlexSqlLiveMeter.PeakOpenConnections,
            RunningStatements = FlexSqlLiveMeter.RunningStatements
        };
    }
}

/// <summary>What the server process is using now: memory, processor, threads and garbage collection.</summary>
public sealed class FlexProcessLoad
{
    public DateTime TakenUtc { get; init; }
    public long WorkingSetBytes { get; init; }
    public long ManagedHeapBytes { get; init; }
    /// <summary>Memory in use on the machine as a share of what the process may use.</summary>
    public double MachineMemoryPercent { get; init; }
    public long MachineMemoryBytes { get; init; }
    /// <summary>Processor time of this process since the previous reading, across all cores (100 = every core busy).</summary>
    public double CpuPercent { get; init; }
    public int Threads { get; init; }
    public int ThreadPoolThreads { get; init; }
    public long ThreadPoolQueued { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public double GcPausePercent { get; init; }
    public TimeSpan Uptime { get; init; }

    private static readonly object Gate = new();
    private static TimeSpan _lastCpu;
    private static long _lastStamp;
    private static double _lastPercent;

    public static FlexProcessLoad Capture()
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        double cpu;
        lock (Gate)
        {
            var now = Stopwatch.GetTimestamp();
            var used = process.TotalProcessorTime;
            var elapsed = _lastStamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_lastStamp, now);
            // Readings closer than half a second repeat the last figure: several viewers share one process.
            if (elapsed.TotalMilliseconds >= 500)
            {
                _lastPercent = Math.Clamp(100.0 * (used - _lastCpu).TotalMilliseconds / (elapsed.TotalMilliseconds * Environment.ProcessorCount), 0, 100);
                _lastCpu = used;
                _lastStamp = now;
            }
            else if (_lastStamp == 0)
            {
                _lastCpu = used;
                _lastStamp = now;
            }
            cpu = _lastPercent;
        }
        return new FlexProcessLoad
        {
            TakenUtc = DateTime.UtcNow,
            WorkingSetBytes = process.WorkingSet64,
            ManagedHeapBytes = GC.GetTotalMemory(false),
            MachineMemoryBytes = gc.TotalAvailableMemoryBytes,
            MachineMemoryPercent = gc.TotalAvailableMemoryBytes <= 0 ? 0 : Math.Round(100.0 * gc.MemoryLoadBytes / gc.TotalAvailableMemoryBytes, 1),
            CpuPercent = Math.Round(cpu, 1),
            Threads = process.Threads.Count,
            ThreadPoolThreads = ThreadPool.ThreadCount,
            ThreadPoolQueued = ThreadPool.PendingWorkItemCount,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2),
            GcPausePercent = Math.Round(gc.PauseTimePercentage, 2),
            Uptime = DateTime.UtcNow - process.StartTime.ToUniversalTime()
        };
    }
}

/// <summary>A user signed in to the host application now.</summary>
public sealed record FlexUserSession(
    string UserId,
    string Tenant = "",
    string SessionId = "",
    DateTime? SignedInUtc = null,
    DateTime? LastActivityUtc = null,
    string Client = "",
    string Screen = "");

/// <summary>The host tells the live view who is signed in. Optional: without it the view counts the users seen in the telemetry.</summary>
public interface IFlexSessionProvider
{
    IReadOnlyList<FlexUserSession> GetSessions(string? tenant);
}

/// <summary>What the database server itself reports about a database, where the host can ask it.</summary>
public sealed class FlexDatabaseServerLoad
{
    public int? Sessions { get; init; }
    public int? RunningRequests { get; init; }
    public int? BlockedRequests { get; init; }
    public long? DataBytes { get; init; }
    public long? LogBytes { get; init; }
    public long? Rows { get; init; }
    public int? Tables { get; init; }
    public int? ServerCpuPercent { get; init; }
    /// <summary>Why a figure is missing (for example no permission to read the server's views).</summary>
    public string Note { get; init; } = string.Empty;
}

/// <summary>The host reads the database server's own load for the live view. Optional.</summary>
public interface IFlexDatabaseLoadProvider
{
    Task<FlexDatabaseServerLoad> GetLoadAsync(string? tenant, CancellationToken cancellationToken = default);
}
