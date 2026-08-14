using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;

namespace PepperX.SqliteGate.Dapper;

/// <summary>
/// Dapper on top of the gate. Same methods you already write, routed onto the right side of the
/// concurrency guarantee.
/// </summary>
/// <remarks>
/// <para>
/// <b>The routing rule is the method name, and nothing else.</b> <c>Query*</c> runs on the reader
/// pool; <c>Execute*</c> goes through the single-writer queue. Dapper's own vocabulary already draws
/// that line, so following it keeps the rule something you can see at the call site instead of
/// something the library infers from your SQL. Nothing here parses a statement to decide where it
/// belongs — that guess is wrong often enough to matter, and being wrong means silently losing the
/// guarantee.
/// </para>
/// <para>
/// The one place the rule is worth stating out loud is <see cref="ExecuteScalarAsync{T}"/>: it is an
/// <c>Execute</c>, so it is a <em>write</em>. That is what makes <c>INSERT … RETURNING id</c> work
/// correctly. For a scalar read such as <c>SELECT COUNT(*)</c>, use
/// <see cref="QuerySingleAsync{T}"/>, which is on the read path where it belongs.
/// </para>
/// <para>
/// Every write here is wrapped in the gate's transaction and the transaction is passed to Dapper
/// explicitly, so a retry after contention rolls the whole attempt back rather than applying half of
/// it twice.
/// </para>
/// </remarks>
public static class SqliteGateDapperExtensions
{
    // -------------------------------------------------------------------------------------------
    // Reads — the pooled reader connections, never queued behind a writer
    // -------------------------------------------------------------------------------------------

    /// <summary>Runs a query on a reader connection and returns every row.</summary>
    /// <typeparam name="T">Row type.</typeparam>
    /// <param name="gate">The gate for the database.</param>
    /// <param name="sql">SQL to run.</param>
    /// <param name="param">Dapper parameter object.</param>
    /// <param name="commandTimeout">Command timeout in seconds, or <see langword="null"/> for the gate's default.</param>
    /// <param name="commandType">Command type, or <see langword="null"/> for text.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>The rows, fully materialised.</returns>
    /// <remarks>
    /// Results are buffered on purpose. The reader connection returns to the pool as soon as this
    /// completes, so a lazily-enumerated result would be reading from a connection somebody else
    /// already has.
    /// </remarks>
    public static Task<IEnumerable<T>> QueryAsync<T>(
        this ISqliteGate gate,
        string sql,
        object? param = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return gate.ReadAsync(
            (connection, token) => connection.QueryAsync<T>(Read(sql, param, commandTimeout, commandType, token)),
            cancellationToken);
    }

    /// <summary>Runs a query and returns the first row.</summary>
    /// <typeparam name="T">Row type.</typeparam>
    /// <param name="gate">The gate for the database.</param>
    /// <param name="sql">SQL to run.</param>
    /// <param name="param">Dapper parameter object.</param>
    /// <param name="commandTimeout">Command timeout in seconds.</param>
    /// <param name="commandType">Command type.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>The first row.</returns>
    public static Task<T> QueryFirstAsync<T>(
        this ISqliteGate gate,
        string sql,
        object? param = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return gate.ReadAsync(
            (connection, token) => connection.QueryFirstAsync<T>(Read(sql, param, commandTimeout, commandType, token)),
            cancellationToken);
    }

    /// <summary>Runs a query and returns the first row, or the default value when there are none.</summary>
    /// <typeparam name="T">Row type.</typeparam>
    /// <param name="gate">The gate for the database.</param>
    /// <param name="sql">SQL to run.</param>
    /// <param name="param">Dapper parameter object.</param>
    /// <param name="commandTimeout">Command timeout in seconds.</param>
    /// <param name="commandType">Command type.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>The first row, or <see langword="default"/>.</returns>
    public static Task<T?> QueryFirstOrDefaultAsync<T>(
        this ISqliteGate gate,
        string sql,
        object? param = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return gate.ReadAsync(
            (connection, token) => connection.QueryFirstOrDefaultAsync<T>(Read(sql, param, commandTimeout, commandType, token)),
            cancellationToken);
    }

    /// <summary>Runs a query that must return exactly one row.</summary>
    /// <typeparam name="T">Row type.</typeparam>
    /// <param name="gate">The gate for the database.</param>
    /// <param name="sql">SQL to run.</param>
    /// <param name="param">Dapper parameter object.</param>
    /// <param name="commandTimeout">Command timeout in seconds.</param>
    /// <param name="commandType">Command type.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>The single row.</returns>
    public static Task<T> QuerySingleAsync<T>(
        this ISqliteGate gate,
        string sql,
        object? param = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return gate.ReadAsync(
            (connection, token) => connection.QuerySingleAsync<T>(Read(sql, param, commandTimeout, commandType, token)),
            cancellationToken);
    }

    /// <summary>Runs a query that must return at most one row.</summary>
    /// <typeparam name="T">Row type.</typeparam>
    /// <param name="gate">The gate for the database.</param>
    /// <param name="sql">SQL to run.</param>
    /// <param name="param">Dapper parameter object.</param>
    /// <param name="commandTimeout">Command timeout in seconds.</param>
    /// <param name="commandType">Command type.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>The single row, or <see langword="default"/>.</returns>
    public static Task<T?> QuerySingleOrDefaultAsync<T>(
        this ISqliteGate gate,
        string sql,
        object? param = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return gate.ReadAsync(
            (connection, token) => connection.QuerySingleOrDefaultAsync<T>(Read(sql, param, commandTimeout, commandType, token)),
            cancellationToken);
    }

    /// <summary>
    /// Runs several Dapper reads against one pooled connection — a <c>QueryMultiple</c>, or a set of
    /// queries that should see the same snapshot.
    /// </summary>
    /// <typeparam name="T">Type produced by the batch.</typeparam>
    /// <param name="gate">The gate for the database.</param>
    /// <param name="reads">Receives the open reader connection.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>Whatever the delegate produced.</returns>
    public static Task<T> QueryBatchAsync<T>(
        this ISqliteGate gate,
        Func<SqliteConnection, CancellationToken, Task<T>> reads,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(reads);

        return gate.ReadAsync(reads, cancellationToken);
    }

    // -------------------------------------------------------------------------------------------
    // Writes — the single-writer queue, inside a transaction
    // -------------------------------------------------------------------------------------------

    /// <summary>Queues a write and runs it inside the gate's transaction.</summary>
    /// <param name="gate">The gate for the database.</param>
    /// <param name="sql">SQL to run.</param>
    /// <param name="param">Dapper parameter object. Pass an array to have Dapper repeat the statement per element.</param>
    /// <param name="commandTimeout">Command timeout in seconds.</param>
    /// <param name="commandType">Command type.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>Rows affected.</returns>
    public static Task<int> ExecuteAsync(
        this ISqliteGate gate,
        string sql,
        object? param = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return gate.WriteTransactionAsync(
            (connection, transaction, token) => connection.ExecuteAsync(Write(sql, param, transaction, commandTimeout, commandType, token)),
            cancellationToken);
    }

    /// <summary>
    /// Queues a write and returns its first column of the first row — the natural shape for
    /// <c>INSERT … RETURNING Id</c>.
    /// </summary>
    /// <typeparam name="T">Scalar type.</typeparam>
    /// <param name="gate">The gate for the database.</param>
    /// <param name="sql">SQL to run.</param>
    /// <param name="param">Dapper parameter object.</param>
    /// <param name="commandTimeout">Command timeout in seconds.</param>
    /// <param name="commandType">Command type.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>The scalar value.</returns>
    /// <remarks>
    /// This is on the <em>write</em> path, following the <c>Execute</c> prefix. Use
    /// <see cref="QuerySingleAsync{T}"/> for a scalar that only reads.
    /// </remarks>
    public static Task<T?> ExecuteScalarAsync<T>(
        this ISqliteGate gate,
        string sql,
        object? param = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return gate.WriteTransactionAsync(
            (connection, transaction, token) => connection.ExecuteScalarAsync<T>(Write(sql, param, transaction, commandTimeout, commandType, token)),
            cancellationToken);
    }

    /// <summary>
    /// Queues a write and returns the rows it produced, for statements that write and read back in
    /// one go — <c>INSERT … RETURNING *</c>, <c>UPDATE … RETURNING *</c>.
    /// </summary>
    /// <typeparam name="T">Row type.</typeparam>
    /// <param name="gate">The gate for the database.</param>
    /// <param name="sql">SQL to run.</param>
    /// <param name="param">Dapper parameter object.</param>
    /// <param name="commandTimeout">Command timeout in seconds.</param>
    /// <param name="commandType">Command type.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>The returned rows.</returns>
    public static Task<IEnumerable<T>> ExecuteReturningAsync<T>(
        this ISqliteGate gate,
        string sql,
        object? param = null,
        int? commandTimeout = null,
        CommandType? commandType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);

        return gate.WriteTransactionAsync(
            (connection, transaction, token) => connection.QueryAsync<T>(Write(sql, param, transaction, commandTimeout, commandType, token)),
            cancellationToken);
    }

    /// <summary>
    /// Queues several Dapper writes and runs them in one transaction, so they commit or roll back
    /// together and only occupy the write slot once.
    /// </summary>
    /// <param name="gate">The gate for the database.</param>
    /// <param name="writes">Receives the connection and the open transaction.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>A task that completes once the transaction has committed.</returns>
    /// <example>
    /// <code>
    /// await gate.ExecuteBatchAsync(async (connection, transaction, token) =&gt;
    /// {
    ///     await connection.ExecuteAsync(new CommandDefinition(
    ///         "INSERT INTO Orders (Reference) VALUES (@Reference)", order, transaction, cancellationToken: token));
    ///
    ///     await connection.ExecuteAsync(new CommandDefinition(
    ///         "INSERT INTO OrderLines (OrderId, Sku) VALUES (@OrderId, @Sku)", lines, transaction, cancellationToken: token));
    /// });
    /// </code>
    /// </example>
    public static Task ExecuteBatchAsync(
        this ISqliteGate gate,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> writes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(writes);

        return gate.WriteTransactionAsync(writes, cancellationToken);
    }

    /// <summary>Queues several Dapper writes in one transaction and returns a value from them.</summary>
    /// <typeparam name="T">Type produced by the batch.</typeparam>
    /// <param name="gate">The gate for the database.</param>
    /// <param name="writes">Receives the connection and the open transaction.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>Whatever the delegate produced, once committed.</returns>
    public static Task<T> ExecuteBatchAsync<T>(
        this ISqliteGate gate,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> writes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(writes);

        return gate.WriteTransactionAsync(writes, cancellationToken);
    }

    private static CommandDefinition Read(
        string sql,
        object? param,
        int? commandTimeout,
        CommandType? commandType,
        CancellationToken cancellationToken)
        => new(sql, param, transaction: null, commandTimeout, commandType, CommandFlags.Buffered, cancellationToken);

    private static CommandDefinition Write(
        string sql,
        object? param,
        IDbTransaction transaction,
        int? commandTimeout,
        CommandType? commandType,
        CancellationToken cancellationToken)
        => new(sql, param, transaction, commandTimeout, commandType, CommandFlags.Buffered, cancellationToken);
}
