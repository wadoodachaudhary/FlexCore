using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Dynamic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fx.ControlKit.Data;

/// <summary>
/// Database-agnostic data access wrapper.
///
/// Works with any ADO.NET provider (SQL Server, PostgreSQL, SQLite, MySQL,
/// Oracle, etc.) — the host supplies a <see cref="DbProviderFactory"/> or
/// a connection-factory delegate.
///
/// <para><b>Registration (SQL Server example):</b></para>
/// <code>
/// builder.Services.AddScoped(_ =>
///     new DbControl(Microsoft.Data.SqlClient.SqlClientFactory.Instance,
///                   builder.Configuration.GetConnectionString("Default")!));
/// </code>
///
/// <para><b>Registration (SQLite example):</b></para>
/// <code>
/// builder.Services.AddScoped(_ =>
///     new DbControl(Microsoft.Data.Sqlite.SqliteFactory.Instance,
///                   "Data Source=app.db"));
/// </code>
///
/// <para><b>Registration (PostgreSQL example):</b></para>
/// <code>
/// builder.Services.AddScoped(_ =>
///     new DbControl(Npgsql.NpgsqlFactory.Instance,
///                   builder.Configuration.GetConnectionString("Default")!));
/// </code>
/// </summary>
public class DbControl
{
    private readonly Func<DbConnection> _connectionFactory;
    private readonly ILogger<DbControl> _logger;

    /// <summary>Logging options for this instance (slow-query threshold, SQL text logging).</summary>
    public DbLoggingOptions LoggingOptions { get; set; } = new();

    #region Constructors

    /// <summary>
    /// Create a DbControl using a <see cref="DbProviderFactory"/> and connection string.
    /// This is the primary constructor — works with any ADO.NET provider.
    /// </summary>
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

    /// <summary>
    /// Create a DbControl using a custom connection-factory delegate.
    /// Use when you need full control over connection creation (pooling, multi-tenant, etc.).
    /// </summary>
    public DbControl(Func<DbConnection> connectionFactory, ILoggerFactory? loggerFactory = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<DbControl>();
    }

    #endregion

    #region Query — DataTable (sync)

    /// <summary>
    /// Execute a SQL query and return results as a <see cref="DataTable"/>.
    /// Parameters can be an anonymous object, a Dictionary&lt;string, object&gt;,
    /// or an IDictionary&lt;string, object?&gt;.
    /// </summary>
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

    /// <summary>
    /// Execute a SQL query and return rows as a list of dictionaries
    /// (case-insensitive column keys).
    /// </summary>
    public async Task<List<Dictionary<string, object>>> QueryAsync(
        string sql, object? parameters = null)
        => await QueryAsync(sql, parameters, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Execute a SQL query and return rows as a list of dictionaries
    /// (case-insensitive column keys), with cancellation support.
    /// </summary>
    public async Task<List<Dictionary<string, object>>> QueryAsync(
        string sql, object? parameters, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var results = new List<Dictionary<string, object>>();
            await using var connection = _connectionFactory();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(nameof(QueryAsync), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    #endregion

    #region Query — ExpandoObject rows (async)

    /// <summary>
    /// Execute a SQL query and return rows as a list of <see cref="ExpandoObject"/>,
    /// enabling dynamic member access (e.g. <c>row.FirstName</c>).
    /// </summary>
    public async Task<List<ExpandoObject>> QueryDynamicAsync(
        string sql, object? parameters = null)
        => await QueryDynamicAsync(sql, parameters, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Execute a SQL query and return rows as a list of <see cref="ExpandoObject"/>,
    /// with cancellation support.
    /// </summary>
    public async Task<List<ExpandoObject>> QueryDynamicAsync(
        string sql, object? parameters, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var results = new List<ExpandoObject>();
            await using var connection = _connectionFactory();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(nameof(QueryDynamicAsync), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    #endregion

    #region Query — Typed rows (async)

    /// <summary>
    /// Execute a SQL query and map rows to <typeparamref name="T"/> using a
    /// caller-supplied mapping function.
    /// </summary>
    public async Task<List<T>> QueryAsync<T>(
        string sql, Func<DbDataReader, T> map, object? parameters = null)
        => await QueryAsync(sql, map, parameters, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Execute a SQL query and map rows to <typeparamref name="T"/> using a
    /// caller-supplied mapping function, with cancellation support.
    /// </summary>
    public async Task<List<T>> QueryAsync<T>(
        string sql, Func<DbDataReader, T> map, object? parameters,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var results = new List<T>();
            await using var connection = _connectionFactory();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(map(reader));

            LogSuccess(nameof(QueryAsync), sql, sw.ElapsedMilliseconds, results.Count);
            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(nameof(QueryAsync), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    #endregion

    #region Execute — non-query (async + sync)

    /// <summary>Execute a non-query command (INSERT/UPDATE/DELETE) and return the affected row count.</summary>
    public async Task<int> ExecuteAsync(string sql, object? parameters = null)
        => await ExecuteAsync(sql, parameters, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Execute a non-query command (INSERT/UPDATE/DELETE) and return the affected
    /// row count, with cancellation support.
    /// </summary>
    public async Task<int> ExecuteAsync(
        string sql, object? parameters, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = _connectionFactory();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);

            LogSuccess(nameof(ExecuteAsync), sql, sw.ElapsedMilliseconds, affected);
            return affected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(nameof(ExecuteAsync), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    /// <summary>Synchronous non-query execution.</summary>
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

    /// <summary>Execute a query and return the first column of the first row, cast to <typeparamref name="T"/>.</summary>
    public async Task<T?> ExecuteScalarAsync<T>(string sql, object? parameters = null)
        => await ExecuteScalarAsync<T>(sql, parameters, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Execute a query and return the first column of the first row, converted to
    /// <typeparamref name="T"/>, with cancellation support.
    /// </summary>
    public async Task<T?> ExecuteScalarAsync<T>(
        string sql, object? parameters, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = _connectionFactory();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AddParameters(command, parameters);
            var result = await command.ExecuteScalarAsync(cancellationToken);

            LogSuccess(nameof(ExecuteScalarAsync), sql, sw.ElapsedMilliseconds, result is null or DBNull ? 0 : 1);
            return ConvertScalarResult<T>(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError(nameof(ExecuteScalarAsync), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    /// <summary>Synchronous scalar execution.</summary>
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
            return ConvertScalarResult<T>(result);
        }
        catch (Exception ex)
        {
            LogError(nameof(ExecuteScalar), sql, sw.ElapsedMilliseconds, ex);
            throw;
        }
    }

    #endregion

    #region Transaction support

    /// <summary>
    /// Execute multiple operations inside a single transaction.
    /// The transaction is committed if the action completes without exceptions;
    /// rolled back otherwise.
    /// </summary>
    public async Task ExecuteInTransactionAsync(Func<DbConnection, DbTransaction, Task> action)
        => await ExecuteInTransactionAsync(action, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Execute multiple operations inside a single transaction, with cancellation support.
    /// The supplied token is used while opening, beginning, committing, and rolling back
    /// the transaction. The action can capture the same token for its commands.
    /// </summary>
    public async Task ExecuteInTransactionAsync(
        Func<DbConnection, DbTransaction, Task> action,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await action(connection, tx);
            await tx.CommitAsync(cancellationToken);
        }
        catch
        {
            // A canceled token must not prevent cleanup of an active transaction.
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Execute multiple operations inside a single transaction. The token is also
    /// passed to the action so command execution can participate in cancellation.
    /// </summary>
    public async Task ExecuteInTransactionAsync(
        Func<DbConnection, DbTransaction, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await ExecuteInTransactionAsync(
            (connection, transaction) => action(connection, transaction, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Execute multiple operations inside a single transaction with a return value.
    /// </summary>
    public async Task<T> ExecuteInTransactionAsync<T>(Func<DbConnection, DbTransaction, Task<T>> action)
        => await ExecuteInTransactionAsync(action, CancellationToken.None).ConfigureAwait(false);

    /// <summary>
    /// Execute multiple operations inside a single transaction with a return value
    /// and cancellation support.
    /// </summary>
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<DbConnection, DbTransaction, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await using var connection = _connectionFactory();
        await connection.OpenAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await action(connection, tx);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            // A canceled token must not prevent cleanup of an active transaction.
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// Execute multiple operations inside a single transaction with a return value.
    /// The token is also passed to the action.
    /// </summary>
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<DbConnection, DbTransaction, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        return await ExecuteInTransactionAsync(
            (connection, transaction) => action(connection, transaction, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Open an explicit provider-neutral transaction session. Committing is explicit;
    /// disposing an uncommitted session rolls it back.
    /// </summary>
    public Task<DbTransactionSession> BeginTransactionAsync()
        => BeginTransactionAsync(CancellationToken.None);

    /// <summary>
    /// Open an explicit provider-neutral transaction session with cancellation support.
    /// </summary>
    public async Task<DbTransactionSession> BeginTransactionAsync(
        CancellationToken cancellationToken)
    {
        var connection = _connectionFactory();
        try
        {
            await connection.OpenAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            return new DbTransactionSession(this, connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Open an explicit provider-neutral transaction session using the requested
    /// isolation level.
    /// </summary>
    public async Task<DbTransactionSession> BeginTransactionAsync(
        IsolationLevel isolationLevel, CancellationToken cancellationToken = default)
    {
        var connection = _connectionFactory();
        try
        {
            await connection.OpenAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            return new DbTransactionSession(this, connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A single open connection and transaction created by <see cref="BeginTransactionAsync()"/>.
    /// All commands share that transaction. Call <see cref="CommitAsync()"/> to persist;
    /// otherwise <see cref="DisposeAsync"/> rolls the transaction back.
    /// </summary>
    public sealed class DbTransactionSession : IAsyncDisposable
    {
        private readonly DbControl _owner;
        private readonly DbConnection _connection;
        private readonly DbTransaction _transaction;
        private bool _completed;
        private bool _disposed;

        internal DbTransactionSession(
            DbControl owner, DbConnection connection, DbTransaction transaction)
        {
            _owner = owner;
            _connection = connection;
            _transaction = transaction;
        }

        /// <summary>The provider-neutral ADO.NET connection owned by this session.</summary>
        public DbConnection Connection
        {
            get
            {
                ThrowIfDisposed();
                return _connection;
            }
        }

        /// <summary>The provider-neutral ADO.NET transaction owned by this session.</summary>
        public DbTransaction Transaction
        {
            get
            {
                ThrowIfDisposed();
                return _transaction;
            }
        }

        /// <summary>True after the session has committed or explicitly rolled back.</summary>
        public bool IsCompleted => _completed;

        /// <summary>Execute a query inside this transaction and return dictionary rows.</summary>
        public Task<List<Dictionary<string, object>>> QueryAsync(
            string sql, object? parameters = null)
            => QueryAsync(sql, parameters, CancellationToken.None);

        /// <summary>Execute a cancellable query inside this transaction.</summary>
        public async Task<List<Dictionary<string, object>>> QueryAsync(
            string sql, object? parameters, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            var sw = Stopwatch.StartNew();
            try
            {
                var results = new List<Dictionary<string, object>>();
                await using var command = CreateCommand(sql, parameters);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 0; i < reader.FieldCount; i++)
                        row[reader.GetName(i)] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                    results.Add(row);
                }

                _owner.LogSuccess(nameof(QueryAsync), sql, sw.ElapsedMilliseconds, results.Count);
                return results;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _owner.LogError(nameof(QueryAsync), sql, sw.ElapsedMilliseconds, ex);
                throw;
            }
        }

        /// <summary>Execute a query inside this transaction and return dynamic rows.</summary>
        public Task<List<ExpandoObject>> QueryDynamicAsync(
            string sql, object? parameters = null)
            => QueryDynamicAsync(sql, parameters, CancellationToken.None);

        /// <summary>Execute a cancellable query inside this transaction and return dynamic rows.</summary>
        public async Task<List<ExpandoObject>> QueryDynamicAsync(
            string sql, object? parameters, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            var sw = Stopwatch.StartNew();
            try
            {
                var results = new List<ExpandoObject>();
                await using var command = CreateCommand(sql, parameters);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new ExpandoObject();
#pragma warning disable CS8619 // ExpandoObject implements IDictionary<string, object?>
                    var dict = (IDictionary<string, object>)row;
#pragma warning restore CS8619
                    for (var i = 0; i < reader.FieldCount; i++)
                        dict[reader.GetName(i)] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                    results.Add(row);
                }

                _owner.LogSuccess(nameof(QueryDynamicAsync), sql, sw.ElapsedMilliseconds, results.Count);
                return results;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _owner.LogError(nameof(QueryDynamicAsync), sql, sw.ElapsedMilliseconds, ex);
                throw;
            }
        }

        /// <summary>Execute and map a typed query inside this transaction.</summary>
        public Task<List<T>> QueryAsync<T>(
            string sql, Func<DbDataReader, T> map, object? parameters = null)
            => QueryAsync(sql, map, parameters, CancellationToken.None);

        /// <summary>Execute and map a cancellable typed query inside this transaction.</summary>
        public async Task<List<T>> QueryAsync<T>(
            string sql, Func<DbDataReader, T> map, object? parameters,
            CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            var sw = Stopwatch.StartNew();
            try
            {
                var results = new List<T>();
                await using var command = CreateCommand(sql, parameters);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    results.Add(map(reader));

                _owner.LogSuccess(nameof(QueryAsync), sql, sw.ElapsedMilliseconds, results.Count);
                return results;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _owner.LogError(nameof(QueryAsync), sql, sw.ElapsedMilliseconds, ex);
                throw;
            }
        }

        /// <summary>Execute a non-query command inside this transaction.</summary>
        public Task<int> ExecuteAsync(string sql, object? parameters = null)
            => ExecuteAsync(sql, parameters, CancellationToken.None);

        /// <summary>Execute a cancellable non-query command inside this transaction.</summary>
        public async Task<int> ExecuteAsync(
            string sql, object? parameters, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            var sw = Stopwatch.StartNew();
            try
            {
                await using var command = CreateCommand(sql, parameters);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                _owner.LogSuccess(nameof(ExecuteAsync), sql, sw.ElapsedMilliseconds, affected);
                return affected;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _owner.LogError(nameof(ExecuteAsync), sql, sw.ElapsedMilliseconds, ex);
                throw;
            }
        }

        /// <summary>Execute a scalar command inside this transaction.</summary>
        public Task<T?> ExecuteScalarAsync<T>(string sql, object? parameters = null)
            => ExecuteScalarAsync<T>(sql, parameters, CancellationToken.None);

        /// <summary>Execute a cancellable scalar command inside this transaction.</summary>
        public async Task<T?> ExecuteScalarAsync<T>(
            string sql, object? parameters, CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            var sw = Stopwatch.StartNew();
            try
            {
                await using var command = CreateCommand(sql, parameters);
                var result = await command.ExecuteScalarAsync(cancellationToken);
                _owner.LogSuccess(
                    nameof(ExecuteScalarAsync), sql, sw.ElapsedMilliseconds,
                    result is null or DBNull ? 0 : 1);
                return ConvertScalarResult<T>(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _owner.LogError(nameof(ExecuteScalarAsync), sql, sw.ElapsedMilliseconds, ex);
                throw;
            }
        }

        /// <summary>Commit this session's transaction.</summary>
        public Task CommitAsync() => CommitAsync(CancellationToken.None);

        /// <summary>Commit this session's transaction with cancellation support.</summary>
        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            await _transaction.CommitAsync(cancellationToken);
            _completed = true;
        }

        /// <summary>Explicitly roll back this session's transaction.</summary>
        public Task RollbackAsync() => RollbackAsync(CancellationToken.None);

        /// <summary>Explicitly roll back this session's transaction with cancellation support.</summary>
        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            ThrowIfUnavailable();
            await _transaction.RollbackAsync(cancellationToken);
            _completed = true;
        }

        /// <summary>
        /// Dispose the transaction and connection. An active transaction is rolled
        /// back first; rollback uses a non-cancelable token so cleanup is reliable.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            if (!_completed)
            {
                try
                {
                    await _transaction.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // Closing the owning connection is the final provider-neutral
                    // rollback guarantee when the provider can no longer communicate.
                }
            }

            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
        }

        private DbCommand CreateCommand(string sql, object? parameters)
        {
            var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Transaction = _transaction;
            AddParameters(command, parameters);
            return command;
        }

        private void ThrowIfUnavailable()
        {
            ThrowIfDisposed();
            if (_completed)
                throw new InvalidOperationException("The transaction has already completed.");
        }

        private void ThrowIfDisposed()
            => ObjectDisposedException.ThrowIf(_disposed, this);
    }

    #endregion

    #region Parameter binding

    /// <summary>
    /// Add parameters to a <see cref="DbCommand"/> from an anonymous object,
    /// IDictionary&lt;string, object&gt;, or IDictionary&lt;string, object?&gt;.
    /// Uses the provider-agnostic <see cref="DbCommand.CreateParameter()"/> method.
    /// </summary>
    public static void AddParameters(DbCommand command, object? parameters)
    {
        if (parameters is null) return;

        // Dictionary<string, object?> — used by report loaders and dynamic parameter bags
        if (parameters is IDictionary<string, object?> dictNullable)
        {
            foreach (var kv in dictNullable)
                AddSingleParameter(command, kv.Key, kv.Value);
            return;
        }

        // Dictionary<string, object>
        if (parameters is IDictionary<string, object> dict)
        {
            foreach (var kv in dict)
                AddSingleParameter(command, kv.Key, kv.Value);
            return;
        }

        // Anonymous object / POCO — reflect public properties
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

    /// <summary>
    /// Convert a provider scalar to the requested type. Nullable value types are
    /// converted through their underlying type because <see cref="Convert.ChangeType(object, Type)"/>
    /// cannot target <see cref="Nullable{T}"/> directly.
    /// </summary>
    private static T? ConvertScalarResult<T>(object? result)
    {
        if (result is null or DBNull)
            return default;

        if (result is T typedResult)
            return typedResult;

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        object converted;

        if (targetType.IsEnum)
        {
            converted = result is string text
                ? Enum.Parse(targetType, text, ignoreCase: true)
                : Enum.ToObject(
                    targetType,
                    Convert.ChangeType(result, Enum.GetUnderlyingType(targetType))!);
        }
        else if (targetType == typeof(Guid))
        {
            converted = result switch
            {
                Guid guid => guid,
                byte[] bytes when bytes.Length == 16 => new Guid(bytes),
                _ => Guid.Parse(Convert.ToString(result)!)
            };
        }
        else
        {
            converted = Convert.ChangeType(result, targetType)!;
        }

        return (T)converted;
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
