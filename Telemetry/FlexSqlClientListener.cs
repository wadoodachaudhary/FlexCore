using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;

namespace Fx.ControlKit.Telemetry;

/// <summary>
/// Reports the database calls a host makes outside its own data layer. SQL Server's client publishes every
/// command, connection open and transaction end on a diagnostic listener; this subscribes to it and hands each
/// one to <see cref="FlexSqlRecorder"/>, so code that opens a connection by hand is seen without being edited.
/// It reads the events by property name and needs no reference to the client library. Parameter values are
/// never read. Calls the host already reports itself are left out through the <c>alreadyRecorded</c> test.
/// </summary>
public sealed class FlexSqlClientListener : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private const string ListenerName = "SqlClientDiagnosticListener";
    private const string Source = "SqlClient";
    private static FlexSqlClientListener? _started;
    private static readonly object Gate = new();

    private readonly Func<DbConnection, bool>? _alreadyRecorded;
    private readonly ConcurrentDictionary<Guid, long> _began = new();
    // The client reports the end of a rollback under the commit event's name, so the kind is taken from the start.
    private readonly ConcurrentDictionary<Guid, bool> _rollbacks = new();
    private readonly List<IDisposable> _subscriptions = new();

    private FlexSqlClientListener(Func<DbConnection, bool>? alreadyRecorded) => _alreadyRecorded = alreadyRecorded;

    /// <summary>Starts listening, once per process. <paramref name="alreadyRecorded"/> answers true for a connection whose calls the host reports itself.</summary>
    public static void Start(Func<DbConnection, bool>? alreadyRecorded = null)
    {
        lock (Gate)
        {
            if (_started is not null)
                return;
            _started = new FlexSqlClientListener(alreadyRecorded);
            _started._subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(_started));
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _started?.Dispose();
            _started = null;
        }
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == ListenerName)
            lock (_subscriptions)
                _subscriptions.Add(listener.Subscribe(this));
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> evt)
    {
        try
        {
            Handle(evt.Key, evt.Value);
        }
        catch
        {
            // Telemetry never fails the call it describes.
        }
    }

    private void Handle(string name, object? payload)
    {
        if (payload is null)
            return;
        // Names look like "Microsoft.Data.SqlClient.WriteCommandBefore".
        var dot = name.LastIndexOf('.');
        var what = dot < 0 ? name : name[(dot + 1)..];
        if (!what.StartsWith("Write", StringComparison.Ordinal))
            return;

        var id = Read<Guid?>(payload, "OperationId") ?? Guid.Empty;
        if (what.EndsWith("Before", StringComparison.Ordinal))
        {
            if (id != Guid.Empty)
            {
                _began[id] = Stopwatch.GetTimestamp();
                if (what.StartsWith("WriteTransactionRollback", StringComparison.Ordinal))
                    _rollbacks[id] = true;
                if (_began.Count > 20_000)
                {
                    _began.Clear();     // operations that never ended (a dropped process path)
                    _rollbacks.Clear();
                }
            }
            return;
        }

        var failed = what.EndsWith("Error", StringComparison.Ordinal);
        if (!failed && !what.EndsWith("After", StringComparison.Ordinal))
            return;
        long elapsed = id != Guid.Empty && _began.TryRemove(id, out var start) ? (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds : 0;
        var error = failed ? Read<Exception>(payload, "Exception") : null;

        if (what.StartsWith("WriteCommand", StringComparison.Ordinal))
        {
            var command = Read<DbCommand>(payload, "Command");
            var connection = command?.Connection;
            if (command is null || Skip(connection))
                return;
            FlexSqlRecorder.Statement(Source, command.CommandText, string.Empty, elapsed, null, error, Context(connection));
        }
        else if (what.StartsWith("WriteConnectionOpen", StringComparison.Ordinal))
        {
            var connection = Read<DbConnection>(payload, "Connection");
            if (Skip(connection))
                return;
            FlexSqlRecorder.Connection(Source, SafeDatabase(connection), elapsed, error, Context(connection));
        }
        else if (what.StartsWith("WriteTransactionCommit", StringComparison.Ordinal) || what.StartsWith("WriteTransactionRollback", StringComparison.Ordinal))
        {
            var connection = Read<DbConnection>(payload, "Connection");
            if (Skip(connection))
                return;
            var rolledBack = _rollbacks.TryRemove(id, out _) || what.StartsWith("WriteTransactionRollback", StringComparison.Ordinal);
            var committed = !rolledBack && !failed;
            FlexSqlRecorder.Transaction(Source, committed, elapsed, 0, error, Context(connection));
        }
    }

    private bool Skip(DbConnection? connection) => connection is not null && _alreadyRecorded?.Invoke(connection) == true;

    private static FlexSqlContext Context(DbConnection? connection)
    {
        var ambient = FlexSqlRecorder.AmbientContext?.Invoke();
        return ambient is not null && ambient.Tenant.Length > 0 ? ambient : (ambient ?? new FlexSqlContext()) with { Tenant = SafeDatabase(connection) };
    }

    private static string SafeDatabase(DbConnection? connection)
    {
        try { return connection?.Database ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    private static T? Read<T>(object payload, string property)
    {
        var value = payload.GetType().GetProperty(property)?.GetValue(payload);
        return value is T typed ? typed : default;
    }

    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }
    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }

    public void Dispose()
    {
        lock (_subscriptions)
        {
            foreach (var s in _subscriptions)
                s.Dispose();
            _subscriptions.Clear();
        }
    }
}
