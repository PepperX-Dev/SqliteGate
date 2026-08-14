using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.Tests.Support;

namespace PepperX.SqliteGate.Tests;

/// <summary>
/// Runs the shared concurrency suite through the core package's raw ADO.NET API.
/// </summary>
public sealed class AdoNetConcurrencyTests : ConcurrencyConformanceTests
{
    /// <inheritdoc />
    protected override Task CreateSchemaAsync(string databasePath, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).WriteAsync(
            (connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token),
            cancellationToken);

    /// <inheritdoc />
    protected override Task InsertTodoAsync(string databasePath, int ordinal, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).WriteAsync(
            (connection, token) => Sql.ExecuteAsync(
                connection,
                "INSERT INTO Todos (Title, Ordinal) VALUES ($title, $ordinal);",
                token,
                ("$title", $"Todo {ordinal}"),
                ("$ordinal", ordinal)),
            cancellationToken);

    /// <inheritdoc />
    protected override Task<long> CountTodosAsync(string databasePath, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).ReadAsync(
            (connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token),
            cancellationToken);

    /// <inheritdoc />
    protected override Task<long> SumOrdinalsAsync(string databasePath, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).ReadAsync(
            (connection, token) => Sql.ScalarAsync(connection, "SELECT COALESCE(SUM(Ordinal), 0) FROM Todos;", token),
            cancellationToken);

    /// <inheritdoc />
    protected override Task IncrementCounterAsync(string databasePath, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).WriteAsync(
            async (connection, token) =>
            {
                // Deliberately not "SET Value = Value + 1". Reading and then writing is what an
                // application does when the new value depends on the old one, and it is precisely
                // what goes wrong when two writers overlap.
                var current = await Sql.ScalarAsync(connection, "SELECT Value FROM Counters WHERE Name = 'hits';", token);

                await Sql.ExecuteAsync(
                    connection,
                    "UPDATE Counters SET Value = $value WHERE Name = 'hits';",
                    token,
                    ("$value", current + 1));
            },
            cancellationToken);

    /// <inheritdoc />
    protected override Task<long> ReadCounterAsync(string databasePath, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).ReadAsync(
            (connection, token) => Sql.ScalarAsync(connection, "SELECT Value FROM Counters WHERE Name = 'hits';", token),
            cancellationToken);
}
