// Placed before the namespace declaration on purpose: inside "PepperX.SqliteGate.Dapper.Tests" the
// name "Dapper" would bind to this repository's own namespace rather than to Dapper itself.
using Dapper;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.Dapper;

namespace PepperX.SqliteGate.Dapper.Tests;

/// <summary>
/// Runs the shared concurrency suite through Dapper. Same guarantee, same assertions, different API.
/// </summary>
public sealed class DapperConcurrencyTests : ConcurrencyConformanceTests
{
    /// <inheritdoc />
    protected override Task CreateSchemaAsync(string databasePath, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).ExecuteAsync(TestSchema.Sql, cancellationToken: cancellationToken);

    /// <inheritdoc />
    protected override Task InsertTodoAsync(string databasePath, int ordinal, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).ExecuteAsync(
            "INSERT INTO Todos (Title, Ordinal) VALUES (@Title, @Ordinal);",
            new { Title = $"Todo {ordinal}", Ordinal = ordinal },
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    protected override Task<long> CountTodosAsync(string databasePath, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).QuerySingleAsync<long>(
            "SELECT COUNT(*) FROM Todos;",
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    protected override Task<long> SumOrdinalsAsync(string databasePath, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).QuerySingleAsync<long>(
            "SELECT COALESCE(SUM(Ordinal), 0) FROM Todos;",
            cancellationToken: cancellationToken);

    /// <inheritdoc />
    protected override Task IncrementCounterAsync(string databasePath, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).ExecuteBatchAsync(
            async (connection, transaction, token) =>
            {
                // Read then write, both inside the gate's transaction, so an overlap between two
                // writers would show up as a lost update.
                var current = await connection.QuerySingleAsync<long>(new CommandDefinition(
                    "SELECT Value FROM Counters WHERE Name = 'hits';",
                    transaction: transaction,
                    cancellationToken: token));

                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE Counters SET Value = @Value WHERE Name = 'hits';",
                    new { Value = current + 1 },
                    transaction,
                    cancellationToken: token));
            },
            cancellationToken);

    /// <inheritdoc />
    protected override Task<long> ReadCounterAsync(string databasePath, CancellationToken cancellationToken) =>
        SqliteGate.For(databasePath).QuerySingleAsync<long>(
            "SELECT Value FROM Counters WHERE Name = 'hits';",
            cancellationToken: cancellationToken);
}
