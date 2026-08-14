using Microsoft.Data.Sqlite;

namespace PepperX.SqliteGate;

/// <summary>
/// The concurrency gate in front of one SQLite database file: writes are serialized through a
/// single-writer queue, reads run concurrently against a pooled set of connections.
/// </summary>
/// <remarks>
/// <para>
/// Obtain one with <see cref="SqliteGate.For(string, Action{SqliteGateOptions}?)"/> or from
/// dependency injection. There is exactly one gate per database file per process, so every caller —
/// raw ADO.NET, Dapper, or EF Core — shares the same queue.
/// </para>
/// <para>
/// Read and write intent is always explicit. The gate never inspects SQL text to guess which path an
/// operation belongs on: that guess would be wrong for common statements (a <c>SELECT</c> over a
/// virtual table can write; <c>INSERT … RETURNING</c> reads), and being wrong means silently losing
/// the guarantee. Call <see cref="ReadAsync{T}"/> for reads and one of the write methods for writes.
/// </para>
/// </remarks>
public interface ISqliteGate : IAsyncDisposable
{
    /// <summary>
    /// The canonical full path of the database file this gate guards, or the literal data source
    /// (<c>:memory:</c> and friends) for an in-memory database.
    /// </summary>
    string DatabasePath { get; }

    /// <summary>
    /// The connection string the gate opens its own connections with. Hand this to any library that
    /// insists on managing its own connection — the EF Core adapter does exactly that.
    /// </summary>
    string ConnectionString { get; }

    /// <summary>
    /// The options this gate was created with. Mutating them afterwards has no effect.
    /// </summary>
    SqliteGateOptions Options { get; }

    /// <summary>
    /// Number of writes currently queued or executing, including the one holding the write slot.
    /// Useful as a contention signal; also published as the <c>sqlitegate.write_queue.depth</c> metric.
    /// </summary>
    int PendingWriters { get; }

    /// <summary>
    /// Whether the underlying database is in-memory, in which case reads and writes share a single
    /// connection and are fully serialized. See the package README for why.
    /// </summary>
    bool IsInMemory { get; }

    /// <summary>
    /// Applies the pragma bootstrap (WAL, <c>synchronous</c>, <c>busy_timeout</c>, foreign keys) if
    /// it has not run yet. Every other method does this for you; call it explicitly at startup to
    /// pay the cost — and surface any configuration error — before the first request arrives.
    /// </summary>
    /// <param name="cancellationToken">Token used to abort the bootstrap.</param>
    /// <returns>A task that completes once the database is ready for use.</returns>
    /// <exception cref="SqliteGateException">
    /// Write-ahead logging could not be enabled and <see cref="SqliteGateOptions.RequireWriteAheadLog"/> is set.
    /// </exception>
    Task EnsureInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous counterpart of <see cref="EnsureInitializedAsync"/>, for callbacks that have no
    /// asynchronous path — EF Core's synchronous connection interceptor, for one. Genuinely
    /// synchronous rather than an asynchronous bootstrap waited on inline.
    /// </summary>
    /// <param name="cancellationToken">Token used to abort the bootstrap.</param>
    void EnsureInitialized(CancellationToken cancellationToken = default);

    /// <summary>Runs a read against a pooled reader connection, without ever waiting on the write queue.</summary>
    /// <typeparam name="T">Type produced by the read.</typeparam>
    /// <param name="operation">The read to run. The connection is open and must not be disposed by the delegate.</param>
    /// <param name="cancellationToken">Token observed while waiting for a pooled connection and during the read.</param>
    /// <returns>The value the delegate produced.</returns>
    Task<T> ReadAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);

    /// <summary>Runs a read against a pooled reader connection, without ever waiting on the write queue.</summary>
    /// <param name="operation">The read to run. The connection is open and must not be disposed by the delegate.</param>
    /// <param name="cancellationToken">Token observed while waiting for a pooled connection and during the read.</param>
    /// <returns>A task that completes when the read completes.</returns>
    Task ReadAsync(Func<SqliteConnection, CancellationToken, Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a write and runs it inside an implicit transaction once the write slot is free.
    /// </summary>
    /// <typeparam name="T">Type produced by the write.</typeparam>
    /// <param name="operation">The write to run. Commands created from the supplied connection join the transaction automatically.</param>
    /// <param name="cancellationToken">Token observed while queued and during the write.</param>
    /// <returns>The value the delegate produced, once the transaction has committed.</returns>
    /// <remarks>
    /// The transaction is what makes retrying safe: if SQLite still reports contention part-way
    /// through, the attempt is rolled back in full before the next one starts, so a retried write
    /// cannot apply the same row twice. Use <see cref="WriteWithoutTransactionAsync{T}"/> for the
    /// few statements SQLite refuses to run inside a transaction.
    /// </remarks>
    Task<T> WriteAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);

    /// <summary>Queues a write and runs it inside an implicit transaction once the write slot is free.</summary>
    /// <param name="operation">The write to run. Commands created from the supplied connection join the transaction automatically.</param>
    /// <param name="cancellationToken">Token observed while queued and during the write.</param>
    /// <returns>A task that completes once the transaction has committed.</returns>
    Task WriteAsync(Func<SqliteConnection, CancellationToken, Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a write and hands the delegate the transaction as well as the connection, for libraries
    /// that need it passed explicitly. Committing is still the gate's job.
    /// </summary>
    /// <typeparam name="T">Type produced by the write.</typeparam>
    /// <param name="operation">The write to run.</param>
    /// <param name="cancellationToken">Token observed while queued and during the write.</param>
    /// <returns>The value the delegate produced, once the transaction has committed.</returns>
    Task<T> WriteTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);

    /// <summary>Queues a write and hands the delegate the transaction as well as the connection.</summary>
    /// <param name="operation">The write to run.</param>
    /// <param name="cancellationToken">Token observed while queued and during the write.</param>
    /// <returns>A task that completes once the transaction has committed.</returns>
    Task WriteTransactionAsync(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues a write and runs it with no transaction wrapped around it, for statements SQLite
    /// cannot run inside one — <c>VACUUM</c>, <c>PRAGMA journal_mode</c>, and similar.
    /// </summary>
    /// <typeparam name="T">Type produced by the write.</typeparam>
    /// <param name="operation">The write to run.</param>
    /// <param name="cancellationToken">Token observed while queued and during the write.</param>
    /// <returns>The value the delegate produced.</returns>
    /// <remarks>
    /// Without a transaction there is nothing to roll back, so a retry re-runs the delegate from the
    /// top over whatever the failed attempt already committed. Keep these operations idempotent, or
    /// set <see cref="SqliteGateOptions.MaxRetryAttempts"/> to <c>0</c>.
    /// </remarks>
    Task<T> WriteWithoutTransactionAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);

    /// <summary>Queues a write and runs it with no transaction wrapped around it.</summary>
    /// <param name="operation">The write to run.</param>
    /// <param name="cancellationToken">Token observed while queued and during the write.</param>
    /// <returns>A task that completes when the write completes.</returns>
    Task WriteWithoutTransactionAsync(Func<SqliteConnection, CancellationToken, Task> operation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the write slot and returns it as a lease, for callers that own their own connection and
    /// therefore cannot use the delegate-based write methods. The EF Core adapter uses this to put
    /// <c>SaveChanges</c> in the queue.
    /// </summary>
    /// <param name="cancellationToken">Token observed while queued.</param>
    /// <returns>A lease that releases the write slot when disposed.</returns>
    /// <remarks>
    /// The caller holds the process-wide write slot for the entire lifetime of the lease, so dispose
    /// it promptly and never await unrelated work while holding it.
    /// </remarks>
    ValueTask<ISqliteWriteLease> AcquireWriteLeaseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous counterpart of <see cref="AcquireWriteLeaseAsync"/>, for the synchronous
    /// <c>SaveChanges</c> path EF Core still exposes. This is a genuine blocking wait on the write
    /// slot, not a synchronously-awaited asynchronous one.
    /// </summary>
    /// <param name="cancellationToken">Token observed while queued.</param>
    /// <returns>A lease that releases the write slot when disposed.</returns>
    ISqliteWriteLease AcquireWriteLease(CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a fresh connection with the gate's pragmas already applied, handing ownership to the
    /// caller. Nothing about it is gated — it is an escape hatch for tooling and diagnostics, not a
    /// way to run application writes.
    /// </summary>
    /// <param name="cancellationToken">Token used to abort opening the connection.</param>
    /// <returns>An open connection the caller is responsible for disposing.</returns>
    Task<SqliteConnection> OpenUngatedConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A held write slot. Disposing it lets the next queued writer through.
/// </summary>
public interface ISqliteWriteLease : IDisposable, IAsyncDisposable
{
    /// <summary>How long this lease spent queued before it was granted.</summary>
    TimeSpan WaitDuration { get; }
}
