using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PepperX.SqliteGate.Internals;

/// <summary>
/// The single-writer / many-reader gate for one database file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a <see cref="SemaphoreSlim"/> rather than a channel of write requests.</b> Both give
/// mutual exclusion; the semaphore keeps the caller on their own execution path. A channel would
/// need a pump task, and every write would then be marshalled through a
/// <see cref="TaskCompletionSource"/> — which costs a thread hop per write, rewrites exception stack
/// traces, and loses the ambient context (activity, culture, async-local state) the caller expects
/// to still be there inside their delegate. <see cref="SemaphoreSlim.WaitAsync(CancellationToken)"/>
/// already supports cancellation and timeout natively, and its asynchronous waiters are queued in a
/// linked list that is drained in arrival order, so ordering is first-in-first-out in practice.
/// (That ordering is an implementation property rather than a documented guarantee. If you need
/// strict, contractual FIFO — a fair scheduler rather than a fast one — a channel-with-pump is the
/// alternative worth taking the overhead for.)
/// </para>
/// </remarks>
internal sealed class DefaultSqliteGate : ISqliteGate
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly DatabaseIdentity _identity;
    private readonly SqliteGateOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly RetryExecutor _retry;
    private readonly ReaderConnectionPool? _readers;
    private readonly KeyValuePair<string, object?>[] _metricTags;
    private readonly bool _metricsEnabled;
    private readonly Action<DefaultSqliteGate>? _onDisposed;

    private SqliteConnection? _writeConnection;
    private int _pendingWriters;
    private volatile bool _initialized;
    private volatile bool _disposed;

    internal DefaultSqliteGate(DatabaseIdentity identity, SqliteGateOptions options, Action<DefaultSqliteGate>? onDisposed = null)
    {
        _identity = identity;
        _options = options;
        _timeProvider = options.TimeProvider;
        _onDisposed = onDisposed;
        _logger = options.LoggerFactory?.CreateLogger("PepperX.SqliteGate") ?? NullLogger.Instance;
        _metricsEnabled = options.EnableMetrics;
        _metricTags = [new KeyValuePair<string, object?>("db.name", identity.MetricName)];
        _retry = new RetryExecutor(options, identity.DisplayPath, _logger, _metricTags);

        // An in-memory database lives inside its connection: a second connection is a second, empty
        // database. So there is no reader pool to build — reads and writes share the one connection
        // and are fully serialized. Correct, and the reason concurrency should be exercised on disk.
        _readers = identity.IsInMemory
            ? null
            : new ReaderConnectionPool(identity.ConnectionString, options, _logger, identity.DisplayPath);
    }

    /// <inheritdoc />
    public string DatabasePath => _identity.DisplayPath;

    /// <inheritdoc />
    public string ConnectionString => _identity.ConnectionString;

    /// <inheritdoc />
    public SqliteGateOptions Options => _options;

    /// <inheritdoc />
    public int PendingWriters => Volatile.Read(ref _pendingWriters);

    /// <inheritdoc />
    public bool IsInMemory => _identity.IsInMemory;

    internal bool IsDisposed => _disposed;

    internal string Key => _identity.Key;

    internal int ReadersInUse => _readers?.InUse ?? 0;

    // ---------------------------------------------------------------------------------------------
    // Bootstrap
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            EnsureDirectoryExists();

            var connection = await ConnectionBootstrapper
                .OpenAsync(_identity.ConnectionString, _options, cancellationToken)
                .ConfigureAwait(false);

            var journalMode = _identity.IsInMemory
                ? "memory"
                : await ConnectionBootstrapper.EnableWriteAheadLoggingAsync(connection, cancellationToken).ConfigureAwait(false);

            VerifyJournalMode(journalMode);

            // The bootstrap connection becomes the writer: it is already open, already configured,
            // and keeping exactly one writer connection alive is what lets WAL do its job.
            _writeConnection = connection;
            _initialized = true;

            ConnectionBootstrapper.LogBootstrap(_logger, _identity.DisplayPath, journalMode, _options);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Synchronous bootstrap, used by the synchronous lease path. Genuinely synchronous ADO calls —
    /// not an asynchronous bootstrap waited on with <c>.Result</c>.
    /// </summary>
    /// <inheritdoc />
    public void EnsureInitialized(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_initialized)
        {
            return;
        }

        _initLock.Wait(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            EnsureDirectoryExists();

            var connection = new SqliteConnection(_identity.ConnectionString);
            try
            {
                connection.Open();

                var busyTimeoutMs = (int)Math.Clamp(_options.BusyTimeout.TotalMilliseconds, 0, int.MaxValue);
                Execute(connection, $"PRAGMA busy_timeout={busyTimeoutMs}; PRAGMA synchronous={(int)_options.Synchronous}; PRAGMA foreign_keys={(_options.EnableForeignKeys ? "ON" : "OFF")};");

                foreach (var pragma in _options.AdditionalPragmas)
                {
                    if (!string.IsNullOrWhiteSpace(pragma))
                    {
                        Execute(connection, pragma);
                    }
                }

                string journalMode;
                if (_identity.IsInMemory)
                {
                    journalMode = "memory";
                }
                else
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "PRAGMA journal_mode=WAL;";
                    journalMode = command.ExecuteScalar()?.ToString()?.ToLowerInvariant() ?? "unknown";
                }

                VerifyJournalMode(journalMode);

                _writeConnection = connection;
                _initialized = true;

                ConnectionBootstrapper.LogBootstrap(_logger, _identity.DisplayPath, journalMode, _options);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
        finally
        {
            _initLock.Release();
        }

        static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private void EnsureDirectoryExists()
    {
        if (_identity.IsInMemory)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_identity.DisplayPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private void VerifyJournalMode(string journalMode)
    {
        if (_identity.IsInMemory || journalMode == "wal")
        {
            return;
        }

        Log.WriteAheadLoggingUnavailable(_logger, _identity.DisplayPath, journalMode);

        if (_options.RequireWriteAheadLog)
        {
            throw new SqliteGateException(
                $"Write-ahead logging could not be enabled on '{_identity.DisplayPath}' — SQLite reports journal_mode='{journalMode}'. " +
                "Without WAL a writer blocks every reader, so reads would queue behind writes and SqliteGate could not keep its promise. " +
                "This usually means the file lives on a filesystem that does not support WAL, such as some network shares. " +
                $"Set {nameof(SqliteGateOptions)}.{nameof(SqliteGateOptions.RequireWriteAheadLog)} to false to proceed anyway with blocking reads.");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Reads
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<T> ReadAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var startedAt = _timeProvider.GetTimestamp();
        var succeeded = false;

        try
        {
            T result;

            if (_readers is null)
            {
                // In-memory: the one connection is the database, so a read takes the write slot too.
                using var lease = await AcquireWriteLeaseCoreAsync(cancellationToken).ConfigureAwait(false);
                var connection = RequireWriteConnection();
                result = await _retry.ExecuteAsync((_, ct) => operation(connection, ct), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await using var lease = await _readers.RentAsync(cancellationToken).ConfigureAwait(false);
                var connection = lease.Connection;
                result = await _retry.ExecuteAsync((_, ct) => operation(connection, ct), cancellationToken).ConfigureAwait(false);
            }

            succeeded = true;
            return result;
        }
        finally
        {
            RecordOperation(SqliteGateMetrics.ReadDuration, startedAt, "read", succeeded);
        }
    }

    /// <inheritdoc />
    public async Task ReadAsync(Func<SqliteConnection, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await ReadAsync<object?>(
            async (connection, ct) =>
            {
                await operation(connection, ct).ConfigureAwait(false);
                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------------
    // Writes
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public Task<T> WriteAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunWriteAsync((connection, _, ct) => operation(connection, ct), useTransaction: true, cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteAsync(Func<SqliteConnection, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return RunWriteAsync<object?>(
            async (connection, _, ct) =>
            {
                await operation(connection, ct).ConfigureAwait(false);
                return null;
            },
            useTransaction: true,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<T> WriteTransactionAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunWriteAsync((connection, transaction, ct) => operation(connection, transaction!, ct), useTransaction: true, cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteTransactionAsync(Func<SqliteConnection, SqliteTransaction, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return RunWriteAsync<object?>(
            async (connection, transaction, ct) =>
            {
                await operation(connection, transaction!, ct).ConfigureAwait(false);
                return null;
            },
            useTransaction: true,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<T> WriteWithoutTransactionAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return RunWriteAsync((connection, _, ct) => operation(connection, ct), useTransaction: false, cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteWithoutTransactionAsync(Func<SqliteConnection, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return RunWriteAsync<object?>(
            async (connection, _, ct) =>
            {
                await operation(connection, ct).ConfigureAwait(false);
                return null;
            },
            useTransaction: false,
            cancellationToken);
    }

    private async Task<T> RunWriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction?, CancellationToken, Task<T>> operation,
        bool useTransaction,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        using var lease = await AcquireWriteLeaseCoreAsync(cancellationToken).ConfigureAwait(false);

        var startedAt = _timeProvider.GetTimestamp();
        var succeeded = false;

        try
        {
            var connection = RequireWriteConnection();

            var result = await _retry.ExecuteAsync(
                async (_, ct) =>
                {
                    if (!useTransaction)
                    {
                        return await operation(connection, null, ct).ConfigureAwait(false);
                    }

                    // IMMEDIATE rather than DEFERRED. A deferred transaction only takes the database
                    // write lock at its first write statement, so contention from another *process*
                    // surfaces halfway through — Microsoft.Data.Sqlite's own documentation warns
                    // that such a transaction "can fail if [it is] upgraded from a read transaction
                    // to a write transaction but the database is locked", leaving the application to
                    // retry the whole thing. Taking the lock at BEGIN turns that into a clean
                    // rollback-and-retry instead. There is no asynchronous overload carrying the
                    // deferred flag, and BEGIN is a local operation, so this call is synchronous.
                    await using var transaction = connection.BeginTransaction(deferred: false);

                    var value = await operation(connection, transaction, ct).ConfigureAwait(false);
                    await transaction.CommitAsync(ct).ConfigureAwait(false);
                    return value;
                },
                cancellationToken).ConfigureAwait(false);

            succeeded = true;
            return result;
        }
        finally
        {
            RecordOperation(SqliteGateMetrics.WriteDuration, startedAt, "write", succeeded);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Write leases
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async ValueTask<ISqliteWriteLease> AcquireWriteLeaseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await AcquireWriteLeaseCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ISqliteWriteLease AcquireWriteLease(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureInitialized(cancellationToken);

        var depth = Interlocked.Increment(ref _pendingWriters);
        if (_metricsEnabled)
        {
            SqliteGateMetrics.WriteQueueDepth.Add(1, _metricTags);
        }

        var startedAt = _timeProvider.GetTimestamp();
        var acquired = false;

        try
        {
            if (_options.WriteQueueTimeout is { } timeout)
            {
                acquired = _writeLock.Wait(timeout, cancellationToken);
                if (!acquired)
                {
                    throw new SqliteGateTimeoutException(_identity.DisplayPath, timeout);
                }
            }
            else
            {
                _writeLock.Wait(cancellationToken);
                acquired = true;
            }
        }
        finally
        {
            if (!acquired)
            {
                ReleaseQueueSlot();
            }
        }

        return CompleteLease(startedAt, depth);
    }

    private async ValueTask<WriteLease> AcquireWriteLeaseCoreAsync(CancellationToken cancellationToken)
    {
        var depth = Interlocked.Increment(ref _pendingWriters);
        if (_metricsEnabled)
        {
            SqliteGateMetrics.WriteQueueDepth.Add(1, _metricTags);
        }

        var startedAt = _timeProvider.GetTimestamp();
        var acquired = false;

        try
        {
            if (_options.WriteQueueTimeout is { } timeout)
            {
                acquired = await _writeLock.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                if (!acquired)
                {
                    throw new SqliteGateTimeoutException(_identity.DisplayPath, timeout);
                }
            }
            else
            {
                await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
            }
        }
        finally
        {
            if (!acquired)
            {
                ReleaseQueueSlot();
            }
        }

        return CompleteLease(startedAt, depth);
    }

    private WriteLease CompleteLease(long startedAt, int depth)
    {
        var waited = _timeProvider.GetElapsedTime(startedAt);

        if (_metricsEnabled)
        {
            SqliteGateMetrics.WriteWaitDuration.Record(waited.TotalMilliseconds, _metricTags);
        }

        if (depth > 1)
        {
            Log.WriteQueued(_logger, _identity.DisplayPath, (long)waited.TotalMilliseconds, depth - 1);
        }

        return new WriteLease(this, waited);
    }

    private void ReleaseQueueSlot()
    {
        Interlocked.Decrement(ref _pendingWriters);
        if (_metricsEnabled)
        {
            SqliteGateMetrics.WriteQueueDepth.Add(-1, _metricTags);
        }
    }

    private void ReleaseWriteLease()
    {
        ReleaseQueueSlot();
        _writeLock.Release();
    }

    // ---------------------------------------------------------------------------------------------
    // Connections
    // ---------------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<SqliteConnection> OpenUngatedConnectionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await ConnectionBootstrapper
            .OpenAsync(_identity.ConnectionString, _options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Only ever called while the write slot is held, so no synchronisation is needed here.</summary>
    private SqliteConnection RequireWriteConnection()
    {
        var connection = _writeConnection
            ?? throw new SqliteGateException($"The gate for '{_identity.DisplayPath}' has no write connection; initialization did not complete.");

        return connection;
    }

    // ---------------------------------------------------------------------------------------------
    // Bookkeeping
    // ---------------------------------------------------------------------------------------------

    private void RecordOperation(System.Diagnostics.Metrics.Histogram<double> histogram, long startedAt, string mode, bool succeeded)
    {
        if (!_metricsEnabled)
        {
            return;
        }

        var elapsed = _timeProvider.GetElapsedTime(startedAt);
        histogram.Record(elapsed.TotalMilliseconds, _metricTags);

        SqliteGateMetrics.Operations.Add(
            1,
            _metricTags[0],
            new KeyValuePair<string, object?>("sqlitegate.mode", mode),
            new KeyValuePair<string, object?>("sqlitegate.outcome", succeeded ? "success" : "failure"));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, typeof(ISqliteGate));

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _onDisposed?.Invoke(this);

        if (_readers is not null)
        {
            await _readers.DisposeAsync().ConfigureAwait(false);
        }

        if (_writeConnection is not null)
        {
            await _writeConnection.DisposeAsync().ConfigureAwait(false);
            _writeConnection = null;
        }

        _writeLock.Dispose();
        _initLock.Dispose();
    }

    private sealed class WriteLease : ISqliteWriteLease
    {
        private DefaultSqliteGate? _gate;

        internal WriteLease(DefaultSqliteGate gate, TimeSpan waitDuration)
        {
            _gate = gate;
            WaitDuration = waitDuration;
        }

        public TimeSpan WaitDuration { get; }

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.ReleaseWriteLease();

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
