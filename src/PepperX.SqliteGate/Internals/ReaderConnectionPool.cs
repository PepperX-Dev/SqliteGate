using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PepperX.SqliteGate.Internals;

/// <summary>
/// A bounded pool of open reader connections. Reads borrow from here and never touch the write queue.
/// </summary>
/// <remarks>
/// <para>
/// Under WAL a reader works from a snapshot taken when its transaction starts, so it neither waits
/// for the writer nor makes the writer wait. The pool exists for resource control rather than
/// correctness: without a bound, a traffic spike opens a file handle per concurrent request.
/// </para>
/// <para>
/// Connections are kept open and reused, with the connection-scoped pragmas applied once at open
/// time. A connection that comes back broken is discarded rather than returned to the bag, so one
/// failure cannot poison the pool.
/// </para>
/// </remarks>
internal sealed class ReaderConnectionPool : IAsyncDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentBag<SqliteConnection> _idle = [];
    private readonly string _connectionString;
    private readonly SqliteGateOptions _options;
    private readonly ILogger _logger;
    private readonly string _database;
    private readonly int _capacity;
    private int _opened;
    private volatile bool _disposed;

    internal ReaderConnectionPool(string connectionString, SqliteGateOptions options, ILogger logger, string database)
    {
        _connectionString = connectionString;
        _options = options;
        _logger = logger;
        _database = database;
        _capacity = options.MaxReaderConnections;
        _slots = new SemaphoreSlim(_capacity, _capacity);
    }

    /// <summary>Connections currently checked out.</summary>
    internal int InUse => _capacity - _slots.CurrentCount;

    internal async ValueTask<Lease> RentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_idle.TryTake(out var connection))
            {
                connection = await ConnectionBootstrapper
                    .OpenAsync(_connectionString, _options, cancellationToken)
                    .ConfigureAwait(false);

                Log.ReaderConnectionOpened(_logger, _database, Interlocked.Increment(ref _opened), _capacity);
            }

            return new Lease(this, connection);
        }
        catch
        {
            _slots.Release();
            throw;
        }
    }

    private void Return(SqliteConnection connection, bool healthy)
    {
        if (healthy && !_disposed && connection.State == System.Data.ConnectionState.Open)
        {
            _idle.Add(connection);
        }
        else
        {
            Interlocked.Decrement(ref _opened);
            connection.Dispose();
        }

        _slots.Release();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        while (_idle.TryTake(out var connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _slots.Dispose();
    }

    /// <summary>A borrowed reader connection, returned to the pool when disposed.</summary>
    internal readonly struct Lease : IAsyncDisposable
    {
        private readonly ReaderConnectionPool _pool;

        internal Lease(ReaderConnectionPool pool, SqliteConnection connection)
        {
            _pool = pool;
            Connection = connection;
        }

        /// <summary>The open connection to read from.</summary>
        internal SqliteConnection Connection { get; }

        /// <summary>Returns the connection to the pool, discarding it if it is no longer usable.</summary>
        public ValueTask DisposeAsync()
        {
            var healthy = Connection.State == System.Data.ConnectionState.Open;

            if (!healthy)
            {
                Log.ReaderConnectionDiscarded(
                    _pool._logger,
                    _pool._database,
                    new SqliteGateException($"Reader connection was in state '{Connection.State}' when returned."));
            }

            _pool.Return(Connection, healthy);
            return ValueTask.CompletedTask;
        }
    }
}
