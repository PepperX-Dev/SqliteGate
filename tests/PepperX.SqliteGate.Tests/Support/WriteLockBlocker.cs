using Microsoft.Data.Sqlite;

namespace PepperX.SqliteGate.Tests.Support;

/// <summary>
/// Holds the database's write lock from outside the gate, so contention can be produced on demand
/// instead of hoped for.
/// </summary>
/// <remarks>
/// This is the situation the retry policy exists for: the gate serializes writers inside the
/// process, and something it does not control — here a stray connection, in production another
/// process — is holding the file anyway. A separate connection running <c>BEGIN IMMEDIATE</c> is
/// exactly that, and it is deterministic.
/// </remarks>
internal sealed class WriteLockBlocker : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;

    private WriteLockBlocker(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    /// <summary>Opens a connection and takes the write lock, holding it until disposal.</summary>
    /// <param name="databasePath">Database to lock.</param>
    /// <returns>The blocker, which releases the lock when disposed.</returns>
    internal static async Task<WriteLockBlocker> HoldAsync(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Default Timeout=1");
        await connection.OpenAsync();

        await Sql.ExecuteAsync(connection, "PRAGMA busy_timeout=0;", CancellationToken.None);

        // BEGIN IMMEDIATE takes the write lock straight away and keeps it for the transaction's life.
        var transaction = connection.BeginTransaction(deferred: false);

        return new WriteLockBlocker(connection, transaction);
    }

    /// <summary>Releases the lock after a delay, on a background task.</summary>
    /// <param name="delay">How long to keep holding.</param>
    /// <returns>A task that completes once the lock has been released.</returns>
    internal Task ReleaseAfterAsync(TimeSpan delay) => Task.Run(async () =>
    {
        await Task.Delay(delay);
        await DisposeAsync();
    });

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _transaction.RollbackAsync();
        }
        catch (InvalidOperationException)
        {
            // Already completed by an earlier disposal; releasing twice is not an error here.
        }

        await _transaction.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
