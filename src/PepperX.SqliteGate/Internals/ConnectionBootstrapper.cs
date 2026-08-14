using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace PepperX.SqliteGate.Internals;

/// <summary>
/// Applies the pragmas that make SQLite behave, so callers never have to remember to.
/// </summary>
/// <remarks>
/// <para>
/// The distinction that matters here is scope. <c>journal_mode=WAL</c> is a property of the
/// <em>database file</em>: it is written into the header once and every later connection inherits
/// it. <c>synchronous</c>, <c>busy_timeout</c> and <c>foreign_keys</c> are properties of a
/// <em>connection</em> and reset to SQLite's defaults on every new one — which is exactly why
/// applications that set them "at startup" quietly lose them the moment the connection pool grows.
/// </para>
/// <para>
/// So: WAL is negotiated once per file during bootstrap, and the connection-scoped pragmas are
/// re-applied to every single connection the gate opens.
/// </para>
/// </remarks>
internal static class ConnectionBootstrapper
{
    /// <summary>Applies the connection-scoped pragmas. Runs on every connection the gate opens.</summary>
    internal static async Task ApplyConnectionPragmasAsync(
        SqliteConnection connection,
        SqliteGateOptions options,
        CancellationToken cancellationToken)
    {
        var busyTimeoutMs = (int)Math.Clamp(options.BusyTimeout.TotalMilliseconds, 0, int.MaxValue);

        await ExecuteAsync(
            connection,
            string.Create(
                CultureInfo.InvariantCulture,
                $"PRAGMA busy_timeout={busyTimeoutMs}; PRAGMA synchronous={(int)options.Synchronous}; PRAGMA foreign_keys={(options.EnableForeignKeys ? "ON" : "OFF")};"),
            cancellationToken).ConfigureAwait(false);

        foreach (var pragma in options.AdditionalPragmas)
        {
            if (!string.IsNullOrWhiteSpace(pragma))
            {
                await ExecuteAsync(connection, pragma, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Negotiates write-ahead logging for the file and reports the mode SQLite settled on.
    /// </summary>
    /// <returns>The resulting <c>journal_mode</c>, lower-cased.</returns>
    internal static async Task<string> EnableWriteAheadLoggingAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        // PRAGMA journal_mode returns the mode actually in force, which is how we find out that a
        // filesystem refused WAL rather than assuming it worked.
        command.CommandText = "PRAGMA journal_mode=WAL;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return result?.ToString()?.ToLowerInvariant() ?? "unknown";
    }

    /// <summary>Opens a connection and applies the connection-scoped pragmas to it.</summary>
    internal static async Task<SqliteConnection> OpenAsync(
        string connectionString,
        SqliteGateOptions options,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ApplyConnectionPragmasAsync(connection, options, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static void LogBootstrap(
        ILogger logger,
        string database,
        string journalMode,
        SqliteGateOptions options)
    {
        Log.Bootstrapped(
            logger,
            database,
            journalMode,
            options.Synchronous.ToString(),
            (int)options.BusyTimeout.TotalMilliseconds,
            options.EnableForeignKeys);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
