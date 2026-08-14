using System.Collections.Concurrent;
using PepperX.SqliteGate.Internals;

namespace PepperX.SqliteGate;

/// <summary>
/// The process-wide map of database file to gate. One file, one gate, one write queue — no matter
/// which package asked for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the detail the entire library rests on.</b> If raw ADO.NET, Dapper and EF Core each
/// built their own gate for <c>app.db</c>, every one of them would serialize its own writes
/// perfectly and they would still collide with each other — three orderly queues feeding one file
/// that only accepts one writer. Nothing would look wrong: the queue metrics would be healthy, the
/// tests for each package would pass, and production would return <c>SQLITE_BUSY</c> under load.
/// </para>
/// <para>
/// Which is why lookup is keyed on the <em>canonical full path</em> rather than the string the
/// caller happened to pass. <c>"app.db"</c>, <c>"./app.db"</c>, <c>"/srv/app/app.db"</c> and
/// <c>"Data Source=app.db"</c> all resolve to one entry.
/// </para>
/// </remarks>
public static class SqliteGateRegistry
{
    private static readonly ConcurrentDictionary<string, DefaultSqliteGate> Gates =
        new(DatabaseIdentity.KeyComparer);

    /// <summary>Number of gates currently alive in this process.</summary>
    public static int Count => Gates.Count;

    /// <summary>Canonical paths of every database currently gated.</summary>
    public static IReadOnlyCollection<string> DatabasePaths =>
        Gates.Values.Select(gate => gate.DatabasePath).ToArray();

    /// <summary>
    /// Returns the gate for a database, creating it on first use.
    /// </summary>
    /// <param name="databasePathOrConnectionString">
    /// A file path (relative or absolute), or a full <c>Microsoft.Data.Sqlite</c> connection string.
    /// A value containing <c>'='</c> is treated as a connection string.
    /// </param>
    /// <param name="configure">
    /// Applied only when this call is the one that creates the gate. A later caller naming the same
    /// file receives the existing gate and their configuration is ignored — one database cannot have
    /// two sets of concurrency settings.
    /// </param>
    /// <returns>The shared gate for that database.</returns>
    public static ISqliteGate For(string databasePathOrConnectionString, Action<SqliteGateOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePathOrConnectionString);

        var options = new SqliteGateOptions();
        configure?.Invoke(options);
        options.Validate();

        var identity = DatabaseIdentity.Resolve(databasePathOrConnectionString, options);

        while (true)
        {
            var gate = Gates.GetOrAdd(
                identity.Key,
                static (_, state) => new DefaultSqliteGate(state.Identity, state.Options.Clone(), Remove),
                (Identity: identity, Options: options));

            if (!gate.IsDisposed)
            {
                return gate;
            }

            // A disposed gate is a tombstone — a host shut down and took its singleton with it while
            // other code kept a reference to the path. Drop it and build a fresh one rather than
            // handing back something unusable.
            Gates.TryRemove(new KeyValuePair<string, DefaultSqliteGate>(identity.Key, gate));
        }
    }

    /// <summary>
    /// Removes a gate from the registry and disposes it, closing its connections. Mostly useful in
    /// tests, and when an application is finished with a database for good.
    /// </summary>
    /// <param name="databasePathOrConnectionString">The database whose gate should be released.</param>
    /// <returns><see langword="true"/> if a gate was found and disposed.</returns>
    public static async ValueTask<bool> RemoveAsync(string databasePathOrConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePathOrConnectionString);

        var identity = DatabaseIdentity.Resolve(databasePathOrConnectionString, new SqliteGateOptions());

        if (!Gates.TryRemove(identity.Key, out var gate))
        {
            return false;
        }

        await gate.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Disposes every gate in the process and empties the registry.</summary>
    /// <returns>A task that completes once all gates are closed.</returns>
    public static async ValueTask ClearAsync()
    {
        foreach (var key in Gates.Keys.ToArray())
        {
            if (Gates.TryRemove(key, out var gate))
            {
                await gate.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void Remove(DefaultSqliteGate gate) =>
        Gates.TryRemove(new KeyValuePair<string, DefaultSqliteGate>(gate.Key, gate));
}
