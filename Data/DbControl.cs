using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Dynamic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fx.ControlKit.Data;

public class DbControl
{
    private readonly Func<DbConnection> _connectionFactory;
    private readonly ILogger<DbControl> _logger;

    public DbLoggingOptions LoggingOptions { get; set; } = new();

    #region Constructors

    public DbControl(DbProviderFactory providerFactory, string connectionString,
                     ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        if (string.IsNullOrEmpty(connectionString))
            throw new ArgumentException("Connection string must not be empty.", nameof(connectionString));

        _connectionFactory = () =>
        {
            var conn = providerFactory.CreateConnection()
                       ?? throw new InvalidOperationException(
                           $"DbProviderFactory ({providerFactory.GetType().Name}) returned null from CreateConnection().");
            conn.ConnectionString = connectionString;
            return conn;
        };
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<DbControl>();
    }

    public DbControl(Func<DbConnection> connectionFactory, ILoggerFactory? loggerFactory = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<DbControl>();
    }

    #endregion

    #region Query — DataTable (sync)

    public DataTable SqlExec(string sql, object? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var connection = _connectionFactory();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);

            var dt = new DataTable();
            using var reader = command.ExecuteReader();
            dt.Load(reader);

            LogSuccess(nameof(SqlExec), sql, sw.ElapsedMilliseconds, dt.Rows.Count);
            return dt;
        }
        catch (Exception ex)
        {
            LogError(nameof(SqlExec), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    #endregion

    #region Query — Dictionary rows (async)

    public async Task<List<Dictionary<string, object>>> QueryAsync(
        string sql, object? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var results = new List<Dictionary<string, object>>();
            await using var connection = _connectionFactory();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                }
                results.Add(row);
            }

            LogSuccess(nameof(QueryAsync), sql, sw.ElapsedMilliseconds, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            LogError(nameof(QueryAsync), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    #endregion

    #region Query — ExpandoObject rows (async)

    public async Task<List<ExpandoObject>> QueryDynamicAsync(
        string sql, object? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var results = new List<ExpandoObject>();
            await using var connection = _connectionFactory();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new ExpandoObject();
#pragma warning disable CS8619 // ExpandoObject implements IDictionary<string, object?>
                var dict = (IDictionary<string, object>)row;
#pragma warning restore CS8619
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    dict[reader.GetName(i)] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                }
                results.Add(row);
            }

            LogSuccess(nameof(QueryDynamicAsync), sql, sw.ElapsedMilliseconds, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            LogError(nameof(QueryDynamicAsync), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    #endregion

    #region Query — Typed rows (async)

    public async Task<List<T>> QueryAsync<T>(
        string sql, Func<DbDataReader, T> map, object? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var results = new List<T>();
            await using var connection = _connectionFactory();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                results.Add(map(reader));

            LogSuccess(nameof(QueryAsync), sql, sw.ElapsedMilliseconds, results.Count);
            return results;
        }
        catch (Exception ex)
        {
            LogError(nameof(QueryAsync), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    #endregion

    #region Execute — non-query (async + sync)

    public async Task<int> ExecuteAsync(string sql, object? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = _connectionFactory();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            var affected = await command.ExecuteNonQueryAsync();

            LogSuccess(nameof(ExecuteAsync), sql, sw.ElapsedMilliseconds, affected);
            return affected;
        }
        catch (Exception ex)
        {
            LogError(nameof(ExecuteAsync), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    public int ExecuteNonQuery(string sql, object? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var connection = _connectionFactory();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            var affected = command.ExecuteNonQuery();

            LogSuccess(nameof(ExecuteNonQuery), sql, sw.ElapsedMilliseconds, affected);
            return affected;
        }
        catch (Exception ex)
        {
            LogError(nameof(ExecuteNonQuery), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    #endregion

    #region Scalar (async + sync)

    public async Task<T?> ExecuteScalarAsync<T>(string sql, object? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = _connectionFactory();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            var result = await command.ExecuteScalarAsync();

            LogSuccess(nameof(ExecuteScalarAsync), sql, sw.ElapsedMilliseconds, result is null or DBNull ? 0 : 1);
            return result is null or DBNull ? default : (T)Convert.ChangeType(result, typeof(T));
        }
        catch (Exception ex)
        {
            LogError(nameof(ExecuteScalarAsync), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    public T? ExecuteScalar<T>(string sql, object? parameters = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var connection = _connectionFactory();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            var result = command.ExecuteScalar();

            LogSuccess(nameof(ExecuteScalar), sql, sw.ElapsedMilliseconds, result is null or DBNull ? 0 : 1);
            return result is null or DBNull ? default : (T)Convert.ChangeType(result, typeof(T));
        }
        catch (Exception ex)
        {
            LogError(nameof(ExecuteScalar), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    #endregion

    #region Transaction support

    public async Task ExecuteInTransactionAsync(Func<DbConnection, DbTransaction, Task> action)
    {
        await using var connection = _connectionFactory();
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        try
        {
            await action(connection, tx);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<DbConnection, DbTransaction, Task<T>> action)
    {
        await using var connection = _connectionFactory();
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        try
        {
            var result = await action(connection, tx);
            await tx.CommitAsync();
            return result;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    #endregion

    #region Parameter binding

    public static void AddParameters(DbCommand command, object? parameters)
    {
        if (parameters is null) return;

        if (parameters is IDictionary<string, object?> dictNullable)
        {
            foreach (var kv in dictNullable)
                AddSingleParameter(command, kv.Key, kv.Value);
            return;
        }

        if (parameters is IDictionary<string, object> dict)
        {
            foreach (var kv in dict)
                AddSingleParameter(command, kv.Key, kv.Value);
            return;
        }

        foreach (var prop in parameters.GetType().GetProperties())
        {
            AddSingleParameter(command, prop.Name, prop.GetValue(parameters));
        }
    }

    private static void AddSingleParameter(DbCommand command, string name, object? value)
    {
        var paramName = name.StartsWith('@') ? name : "@" + name;
        var p = command.CreateParameter();
        p.ParameterName = paramName;
        p.Value = value ?? DBNull.Value;
        command.Parameters.Add(p);
    }

    #endregion

    #region Logging

    private void LogSuccess(string operation, string sql, long elapsedMs, int? rowCount)
    {
        var threshold = Math.Max(0, LoggingOptions.SlowQueryThresholdMs);
        if (threshold <= 0 || elapsedMs < threshold) return;

        if (LoggingOptions.LogSqlText)
        {
            _logger.LogWarning(
                "{Operation} completed slowly in {ElapsedMs}ms. RowCount={RowCount}. SQL={SqlText}",
                operation, elapsedMs, rowCount, SummarizeSql(sql));
        }
        else
        {
            _logger.LogWarning(
                "{Operation} completed slowly in {ElapsedMs}ms. RowCount={RowCount}.",
                operation, elapsedMs, rowCount);
        }
    }

    private void LogError(string operation, string sql, long elapsedMs, Exception ex)
    {
        if (LoggingOptions.LogSqlText)
        {
            _logger.LogError(ex,
                "{Operation} failed after {ElapsedMs}ms. SQL={SqlText}",
                operation, elapsedMs, SummarizeSql(sql));
        }
        else
        {
            _logger.LogError(ex, "{Operation} failed after {ElapsedMs}ms.", operation, elapsedMs);
        }
    }

    private static string SummarizeSql(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return string.Empty;
        var condensed = Regex.Replace(sql, @"\s+", " ").Trim();
        return condensed.Length <= 600 ? condensed : condensed[..600] + " ...";
    }

    #endregion
}
