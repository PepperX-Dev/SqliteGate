using FluentAssertions;
using PepperX.SqliteGate.Tests.Support;

namespace PepperX.SqliteGate.Tests;

/// <summary>
/// In-memory databases, where the gate deliberately does something different.
/// </summary>
/// <remarks>
/// A <c>:memory:</c> database lives inside its connection, so a reader pool would hand out empty
/// databases rather than snapshots of a shared one. The gate detects this and serializes reads
/// through the write slot on a single connection. Correct, and a reminder that concurrency itself
/// has to be proven on disk — there are no file locks here to get wrong.
/// </remarks>
public sealed class InMemoryDatabaseTests
{
    private static string UniqueMemoryDatabase() =>
        $"Data Source=sqlitegate-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

    [Fact]
    public async Task An_in_memory_gate_reads_back_what_it_wrote()
    {
        var connectionString = UniqueMemoryDatabase();

        try
        {
            var gate = SqliteGate.For(connectionString);

            gate.IsInMemory.Should().BeTrue();

            await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));
            await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(
                connection,
                "INSERT INTO Todos (Title, Ordinal) VALUES ('in memory', 1);",
                token));

            (await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token)))
                .Should().Be(1, "reads and writes share one connection, so they see one database");
        }
        finally
        {
            await SqliteGateRegistry.RemoveAsync(connectionString);
        }
    }

    [Fact]
    public async Task Write_ahead_logging_is_not_demanded_of_a_database_that_has_no_file()
    {
        var connectionString = UniqueMemoryDatabase();

        try
        {
            // RequireWriteAheadLog defaults to true; an in-memory database cannot honour it and must
            // not be failed for that.
            var gate = SqliteGate.For(connectionString, options => options.RequireWriteAheadLog = true);

            var act = async () => await gate.EnsureInitializedAsync();

            await act.Should().NotThrowAsync();
        }
        finally
        {
            await SqliteGateRegistry.RemoveAsync(connectionString);
        }
    }

    [Fact]
    public async Task Concurrent_writes_to_an_in_memory_database_are_still_serialized()
    {
        var connectionString = UniqueMemoryDatabase();

        try
        {
            var gate = SqliteGate.For(connectionString);
            await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

            var failures = await Conformance.ConcurrencyHarness.RunConcurrentlyAsync(
                100,
                (ordinal, cancellationToken) => gate.WriteAsync(
                    (connection, token) => Sql.ExecuteAsync(
                        connection,
                        "INSERT INTO Todos (Title, Ordinal) VALUES ('m', $ordinal);",
                        token,
                        ("$ordinal", ordinal)),
                    cancellationToken));

            failures.Should().BeEmpty(Conformance.ConcurrencyHarness.Describe(failures));

            (await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token)))
                .Should().Be(100);
        }
        finally
        {
            await SqliteGateRegistry.RemoveAsync(connectionString);
        }
    }

    [Fact]
    public async Task Two_differently_named_memory_databases_get_their_own_gates()
    {
        var first = UniqueMemoryDatabase();
        var second = UniqueMemoryDatabase();

        try
        {
            SqliteGate.For(first).Should().NotBeSameAs(SqliteGate.For(second));
        }
        finally
        {
            await SqliteGateRegistry.RemoveAsync(first);
            await SqliteGateRegistry.RemoveAsync(second);
        }
    }

    [Fact]
    public async Task The_same_memory_database_resolves_to_one_gate_whatever_the_options()
    {
        var connectionString = UniqueMemoryDatabase();

        try
        {
            var first = SqliteGate.For(connectionString, options => options.BusyTimeout = TimeSpan.FromSeconds(9));
            var second = SqliteGate.For(connectionString, options => options.BusyTimeout = TimeSpan.FromSeconds(2));

            second.Should().BeSameAs(first, "tuning options must not be part of the identity of a database");
        }
        finally
        {
            await SqliteGateRegistry.RemoveAsync(connectionString);
        }
    }
}
