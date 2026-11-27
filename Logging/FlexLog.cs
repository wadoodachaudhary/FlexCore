using System.Collections.Concurrent;
using Fx.ControlKit.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Fx.ControlKit.Logging;

/// <summary>
/// The log of a database component (FlexKit's DbControl, or a host data layer). Every statement, connection
/// and transaction goes to the SQL telemetry, where it is measured and counted (time, rows, bytes); a failure
/// or a slow statement is also written to the shared log, beside the control actions that led to it.
/// </summary>
public sealed class FlexDataLog : FlexLog
{
    private readonly string _source;

    /// <param name="source">The data layer's name, as shown in the views.</param>
    public FlexDataLog(string source) => _source = source;

    public override string Channel => FlexLogChannels.Database;

    /// <summary>Where the statements are measured. Null uses the process's SQL telemetry store.</summary>
    public IFlexSqlTelemetryProvider? SqlSink { get; set; }

    public long Statements { get; private set; }
    public long Failures { get; private set; }

    public void Statement(string sql, string parameters, long elapsedMs, int? rowCount, Exception? error = null,
        FlexSqlContext? context = null, bool isBackgroundPoll = false)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return;
        Record(FlexSqlRecorder.Build(FlexSqlEventKinds.Statement, _source, sql, parameters, elapsedMs, rowCount, error, context, isBackgroundPoll));
    }

    /// <summary>Records an entry the data layer built itself.</summary>
    public void Record(FlexSqlTelemetryEntry entry)
    {
        try
        {
            (SqlSink ?? FlexSqlRecorder.Sink)?.RecordSql(entry);
            if (entry.EventKind == FlexSqlEventKinds.Statement)
                Statements++;
            if (entry.HasError)
                Failures++;
            // Background polls repeat every few seconds: their slow runs stay in the SQL telemetry only.
            if (entry.HasError || (entry.IsSlow && !entry.IsBackgroundPoll && entry.EventKind == FlexSqlEventKinds.Statement))
                Write(ToRecord(entry));
        }
        catch
        {
            // A logger never fails the call it describes.
        }
    }

    public void Connection(string target, long elapsedMs, Exception? error = null, FlexSqlContext? context = null)
    {
        FlexSqlRecorder.Connection(_source, target, elapsedMs, error, context);
        if (error is not null)
        {
            Failures++;
            Write(ToRecord(FlexSqlRecorder.Build(FlexSqlEventKinds.ConnectionFailed, _source, $"(connection failed: {target})", string.Empty, elapsedMs, null, error, context, false)));
        }
    }

    public void Transaction(bool committed, long elapsedMs, int statements, Exception? error = null, FlexSqlContext? context = null)
    {
        FlexSqlRecorder.Transaction(_source, committed, elapsedMs, statements, error, context);
        if (error is not null)
        {
            Failures++;
            Write(ToRecord(FlexSqlRecorder.Build(committed ? FlexSqlEventKinds.TransactionCommit : FlexSqlEventKinds.TransactionRollback,
                _source, $"(transaction: {statements} statements)", string.Empty, elapsedMs, statements, error, context, false)));
        }
    }

    private FlexControlLogRecord ToRecord(FlexSqlTelemetryEntry e) => new()
    {
        TimestampUtc = e.TimestampUtc,
        Level = e.HasError ? "ERROR" : "WARN",
        EventType = FlexLogChannels.DatabaseEventType,
        Tenant = e.Tenant,
        UserId = e.UserId.Length == 0 ? "anonymous" : e.UserId,
        SessionId = e.SessionId,
        Screen = e.Screen,
        Location = e.Screen.Length == 0 ? _source : e.Screen + " > " + _source,
        ControlType = _source,
        ControlId = e.Tables,
        Action = e.HasError ? "Database" + (e.ErrorClass.Length == 0 ? "Error" : e.ErrorClass) : "SlowStatement",
        Value = e.RowCount?.ToString(),
        Detail = e.HasError ? e.ErrorMessage + " | " + e.SqlPreview : e.SqlPreview,
        DurationMs = e.ElapsedMs
    };
}

/// <summary>The log the host application's own log calls arrive in (through <see cref="FlexLoggerProvider"/>).</summary>
public sealed class FlexAppLog : FlexLog
{
    public override string Channel => FlexLogChannels.Application;

    public void Write(string category, LogLevel level, string message, Exception? exception, FlexControlHostContext? host)
    {
        var dot = category.LastIndexOf('.');
        Write(new FlexControlLogRecord
        {
            Level = level switch
            {
                LogLevel.Critical => "FATAL",
                LogLevel.Error => "ERROR",
                LogLevel.Warning => "WARN",
                LogLevel.Debug or LogLevel.Trace => "DEBUG",
                _ => "INFO"
            },
            EventType = FlexLogChannels.ApplicationEventType,
            Application = host?.Application ?? "FlexKitApp",
            Tenant = host?.Tenant ?? string.Empty,
            DivisionId = host?.DivisionId ?? 0,
            UserId = string.IsNullOrEmpty(host?.UserId) ? "anonymous" : host.Value.UserId,
            SessionId = host?.SessionId ?? string.Empty,
            ClientIp = host?.ClientIp,
            Screen = host?.FallbackScreen ?? string.Empty,
            Route = host?.FallbackRoute ?? string.Empty,
            Location = category,
            ControlType = "Logger",
            ControlId = dot < 0 ? category : category[(dot + 1)..],
            Action = level.ToString(),
            Detail = exception is null ? message : message + " | " + exception.GetType().Name + ": " + exception.Message
        });
    }
}

public sealed class FlexLoggerProviderOptions
{
    /// <summary>The lowest level taken from the host's loggers.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
    /// <summary>The lowest level taken from the framework's own categories (Microsoft.*, System.*).</summary>
    public LogLevel FrameworkMinimumLevel { get; set; } = LogLevel.Warning;
    /// <summary>Categories never taken, by prefix. The library's own are always left out, as the log store writes to them.</summary>
    public List<string> ExcludedCategoryPrefixes { get; } = new();
    /// <summary>Longest message kept.</summary>
    public int MaxMessageLength { get; set; } = 2_000;
}

/// <summary>
/// Brings the host's standard log calls (<see cref="ILogger"/>) into the shared FlexKit log, so the pages and
/// services of an application log through the same parent as its controls and its database layer, with no
/// change to their code. Register with <c>AddFlexLoggerProvider</c>.
/// </summary>
public sealed class FlexLoggerProvider : ILoggerProvider
{
    private readonly FlexLoggerProviderOptions _options;
    private readonly IServiceProvider? _services;
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    [ThreadStatic] private static bool _writing;

    public FlexLoggerProvider(FlexLoggerProviderOptions? options = null, IServiceProvider? services = null)
    {
        _options = options ?? new FlexLoggerProviderOptions();
        _services = services;
    }

    /// <summary>The one log every category writes to.</summary>
    public FlexAppLog Log { get; } = new();

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new CategoryLogger(this, name));

    public void Dispose() => _loggers.Clear();

    private LogLevel MinimumFor(string category)
    {
        if (category.StartsWith("Fx.ControlKit", StringComparison.Ordinal))
            return LogLevel.None;
        foreach (var prefix in _options.ExcludedCategoryPrefixes)
            if (category.StartsWith(prefix, StringComparison.Ordinal))
                return LogLevel.None;
        return category.StartsWith("Microsoft", StringComparison.Ordinal) || category.StartsWith("System", StringComparison.Ordinal)
            ? _options.FrameworkMinimumLevel
            : _options.MinimumLevel;
    }

    private FlexControlHostContext? HostContext()
    {
        try
        {
            // Only a host that can answer outside a scope (a singleton provider) is asked here.
            return (_services?.GetService(typeof(IFlexAmbientContextProvider)) as IFlexAmbientContextProvider)?.GetAmbientContext();
        }
        catch
        {
            return null;
        }
    }

    private sealed class CategoryLogger(FlexLoggerProvider owner, string category) : ILogger
    {
        private readonly LogLevel _minimum = owner.MinimumFor(category);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || _writing)
                return;
            _writing = true;
            try
            {
                var message = formatter(state, exception) ?? string.Empty;
                if (message.Length > owner._options.MaxMessageLength)
                    message = message[..owner._options.MaxMessageLength] + "...";
                owner.Log.Write(category, logLevel, message, exception, owner.HostContext());
            }
            catch
            {
            }
            finally
            {
                _writing = false;
            }
        }
    }
}

/// <summary>
/// Who and where, for a log call made outside a component (a service, a background task). Optional; the host
/// implements it as a singleton that reads its own ambient state.
/// </summary>
public interface IFlexAmbientContextProvider
{
    FlexControlHostContext? GetAmbientContext();
}

public static class FlexLoggerProviderExtensions
{
    /// <summary>Adds the provider that brings <see cref="ILogger"/> calls into the shared FlexKit log.</summary>
    public static IServiceCollection AddFlexLoggerProvider(this IServiceCollection services, Action<FlexLoggerProviderOptions>? configure = null)
    {
        var options = new FlexLoggerProviderOptions();
        configure?.Invoke(options);
        // The implementation type is named so the provider can sit beside a host's other logger providers.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, FlexLoggerProvider>(sp => new FlexLoggerProvider(options, sp)));
        return services;
    }
}
