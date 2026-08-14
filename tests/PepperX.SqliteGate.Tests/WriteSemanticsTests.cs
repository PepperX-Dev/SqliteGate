using FluentAssertions;
using Microsoft.Data.Sqlite;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.Tests.Support;

namespace PepperX.SqliteGate.Tests;

/// <summary>
/// The transactional contract of the write methods — the part that makes retrying safe rather than
/// merely persistent.
/// </summary>
public sealed class WriteSemanticsTests
{
    [Fact]
    public async Task A_failed_write_leaves_nothing_behind()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var act = async () => await gate.WriteAsync(async (connection, token) =>
        {
            await Sql.ExecuteAsync(connection, "INSERT INTO Todos (Title, Ordinal) VALUES ('first', 1);", token);
            await Sql.ExecuteAsync(connection, "INSERT INTO Todos (Title, Ordinal) VALUES ('second', 2);", token);
            throw new InvalidOperationException("something went wrong halfway through");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();

        (await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token)))
            .Should().Be(0, "the implicit transaction is what lets a retry start from a clean slate");
    }

    [Fact]
    public async Task Commands_created_inside_a_write_join_its_transaction_automatically()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        // Nothing here mentions a transaction, yet both statements must commit together.
        await gate.WriteAsync(async (connection, token) =>
        {
            await Sql.ExecuteAsync(connection, "INSERT INTO Todos (Title, Ordinal) VALUES ('a', 1);", token);
            await Sql.ExecuteAsync(connection, "INSERT INTO Todos (Title, Ordinal) VALUES ('b', 2);", token);
        });

        (await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token)))
            .Should().Be(2);
    }

    [Fact]
    public async Task The_transaction_can_be_taken_explicitly_when_a_library_needs_it_passed()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var inserted = await gate.WriteTransactionAsync(async (connection, transaction, token) =>
        {
            transaction.Should().NotBeNull();

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO Todos (Title, Ordinal) VALUES ('explicit', 1); SELECT last_insert_rowid();";
            return (long)(await command.ExecuteScalarAsync(token))!;
        });

        inserted.Should().BeGreaterThan(0);

        (await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token)))
            .Should().Be(1);
    }

    [Fact]
    public async Task Statements_that_cannot_run_in_a_transaction_have_their_own_path()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        // VACUUM is rejected inside a transaction, which is exactly why this overload exists.
        var act = async () => await gate.WriteWithoutTransactionAsync(
            (connection, token) => Sql.ExecuteAsync(connection, "VACUUM;", token));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_write_returning_a_value_returns_it_after_the_commit()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var id = await gate.WriteAsync(async (connection, token) =>
        {
            await Sql.ExecuteAsync(connection, "INSERT INTO Todos (Title, Ordinal) VALUES ('with id', 1);", token);
            return await Sql.ScalarAsync(connection, "SELECT last_insert_rowid();", token);
        });

        id.Should().Be(1);
    }

    [Fact]
    public async Task Foreign_key_violations_still_surface_as_sqlite_errors()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(
            connection,
            """
            CREATE TABLE Parent (Id INTEGER PRIMARY KEY);
            CREATE TABLE Child (Id INTEGER PRIMARY KEY, ParentId INTEGER NOT NULL REFERENCES Parent(Id));
            """,
            token));

        var act = async () => await gate.WriteAsync(
            (connection, token) => Sql.ExecuteAsync(connection, "INSERT INTO Child (ParentId) VALUES (999);", token));

        var thrown = await act.Should().ThrowAsync<SqliteException>();
        thrown.Which.SqliteErrorCode.Should().Be(19, "foreign_keys=ON is applied for you, so this is a constraint failure");
    }

    [Fact]
    public async Task A_disposed_gate_refuses_further_work()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));
        await gate.DisposeAsync();

        var write = async () => await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, "SELECT 1;", token));
        var read = async () => await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT 1;", token));

        await write.Should().ThrowAsync<ObjectDisposedException>();
        await read.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task An_ungated_connection_is_available_for_tooling()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        await using var connection = await gate.OpenUngatedConnectionAsync();

        connection.State.Should().Be(System.Data.ConnectionState.Open);
        (await Sql.PragmaAsync(connection, "journal_mode")).Should().Be("wal", "it still gets the gate's pragmas");
        (await Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", CancellationToken.None)).Should().Be(0);
    }
}
