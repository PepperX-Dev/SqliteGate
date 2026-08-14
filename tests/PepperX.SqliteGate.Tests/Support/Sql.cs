using Microsoft.Data.Sqlite;

namespace PepperX.SqliteGate.Tests.Support;

/// <summary>Small ADO.NET helpers so the tests read as tests rather than as command plumbing.</summary>
internal static class Sql
{
    /// <summary>The schema shared with every other package's suite.</summary>
    internal const string Schema = Conformance.TestSchema.Sql;

    internal static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<long> ScalarAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }

    internal static async Task<string> PragmaAsync(SqliteConnection connection, string pragma, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result?.ToString()?.ToLowerInvariant() ?? string.Empty;
    }
}
