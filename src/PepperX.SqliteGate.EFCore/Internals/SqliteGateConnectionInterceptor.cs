using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PepperX.SqliteGate.EFCore.Internals;

/// <summary>
/// Applies the gate's connection-scoped pragmas to the connections EF Core opens for itself.
/// </summary>
/// <remarks>
/// <para>
/// EF Core manages its own connections from the connection string, which means it never passes
/// through the gate's reader pool and never inherits the pragmas applied there. Since
/// <c>busy_timeout</c>, <c>synchronous</c> and <c>foreign_keys</c> all reset on every new
/// connection, without this an EF Core query would run with SQLite's bare defaults while the rest of
/// the application ran with the configured ones.
/// </para>
/// <para>
/// This is also where the gate is bootstrapped if EF Core happens to open the first connection to
/// the file, so write-ahead logging is on before anything reads.
/// </para>
/// </remarks>
internal sealed class SqliteGateConnectionInterceptor : IDbConnectionInterceptor
{
    private readonly ISqliteGate _gate;

    internal SqliteGateConnectionInterceptor(ISqliteGate gate) => _gate = gate;

    /// <inheritdoc />
    public InterceptionResult ConnectionOpening(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        _gate.EnsureInitialized();
        return result;
    }

    /// <inheritdoc />
    public async ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        await _gate.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is not SqliteConnection sqlite)
        {
            return;
        }

        var options = _gate.Options;
        var busyTimeoutMs = (int)Math.Clamp(options.BusyTimeout.TotalMilliseconds, 0, int.MaxValue);

        Execute(sqlite, $"PRAGMA busy_timeout={busyTimeoutMs}; PRAGMA synchronous={(int)options.Synchronous}; PRAGMA foreign_keys={(options.EnableForeignKeys ? "ON" : "OFF")};");

        foreach (var pragma in options.AdditionalPragmas)
        {
            if (!string.IsNullOrWhiteSpace(pragma))
            {
                Execute(sqlite, pragma);
            }
        }

        static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    /// <inheritdoc />
    public async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is not SqliteConnection sqlite)
        {
            return;
        }

        var options = _gate.Options;
        var busyTimeoutMs = (int)Math.Clamp(options.BusyTimeout.TotalMilliseconds, 0, int.MaxValue);

        await ExecuteAsync(
            sqlite,
            $"PRAGMA busy_timeout={busyTimeoutMs}; PRAGMA synchronous={(int)options.Synchronous}; PRAGMA foreign_keys={(options.EnableForeignKeys ? "ON" : "OFF")};",
            cancellationToken).ConfigureAwait(false);

        foreach (var pragma in options.AdditionalPragmas)
        {
            if (!string.IsNullOrWhiteSpace(pragma))
            {
                await ExecuteAsync(sqlite, pragma, cancellationToken).ConfigureAwait(false);
            }
        }

        static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
