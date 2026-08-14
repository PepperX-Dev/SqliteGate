using Dapper;
using FluentAssertions;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.Dapper;

namespace PepperX.SqliteGate.Dapper.Tests;

/// <summary>
/// The routing rule in action: <c>Query*</c> reads, <c>Execute*</c> writes, decided by the method
/// you called and never by what the SQL looks like.
/// </summary>
public sealed class DapperRoutingTests
{
    [Fact]
    public async Task Queries_run_while_a_write_is_still_in_flight()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.ExecuteAsync(TestSchema.Sql);
        await gate.ExecuteAsync("INSERT INTO Todos (Title, Ordinal) VALUES ('seed', 0);");

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var heldWrite = gate.ExecuteBatchAsync(async (_, _, _) =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        var rows = await gate.QueryAsync<TodoRow>("SELECT Id, Title, Ordinal FROM Todos;");

        rows.Should().ContainSingle();
        gate.PendingWriters.Should().Be(1, "the query never joined the write queue");

        release.SetResult();
        await heldWrite;
    }

    [Fact]
    public async Task Execute_scalar_is_a_write_so_insert_returning_works()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.ExecuteAsync(TestSchema.Sql);

        var id = await gate.ExecuteScalarAsync<long>(
            "INSERT INTO Todos (Title, Ordinal) VALUES (@Title, @Ordinal) RETURNING Id;",
            new { Title = "returned", Ordinal = 7 });

        id.Should().Be(1);

        (await gate.QuerySingleAsync<long>("SELECT COUNT(*) FROM Todos;")).Should().Be(1);
    }

    [Fact]
    public async Task Execute_returning_hands_back_the_rows_it_wrote()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.ExecuteAsync(TestSchema.Sql);

        var written = await gate.ExecuteReturningAsync<TodoRow>(
            "INSERT INTO Todos (Title, Ordinal) VALUES (@Title, @Ordinal) RETURNING Id, Title, Ordinal;",
            new { Title = "round trip", Ordinal = 3 });

        var row = written.Should().ContainSingle().Subject;
        row.Title.Should().Be("round trip");
        row.Ordinal.Should().Be(3);
    }

    [Fact]
    public async Task A_batch_commits_together_or_not_at_all()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.ExecuteAsync(TestSchema.Sql);

        var act = async () => await gate.ExecuteBatchAsync(async (connection, transaction, token) =>
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO Todos (Title, Ordinal) VALUES ('first', 1);",
                transaction: transaction,
                cancellationToken: token));

            throw new InvalidOperationException("halfway");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();

        (await gate.QuerySingleAsync<long>("SELECT COUNT(*) FROM Todos;")).Should().Be(0);
    }

    [Fact]
    public async Task A_batch_takes_the_write_slot_once_for_all_of_its_statements()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.ExecuteAsync(TestSchema.Sql);

        await gate.ExecuteBatchAsync(async (connection, transaction, token) =>
        {
            for (var ordinal = 0; ordinal < 25; ordinal++)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO Todos (Title, Ordinal) VALUES (@Title, @Ordinal);",
                    new { Title = "batched", Ordinal = ordinal },
                    transaction,
                    cancellationToken: token));
            }
        });

        (await gate.QuerySingleAsync<long>("SELECT COUNT(*) FROM Todos;")).Should().Be(25);
        gate.PendingWriters.Should().Be(0);
    }

    [Fact]
    public async Task Dapper_parameter_lists_still_expand_into_repeated_statements()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.ExecuteAsync(TestSchema.Sql);

        var affected = await gate.ExecuteAsync(
            "INSERT INTO Todos (Title, Ordinal) VALUES (@Title, @Ordinal);",
            Enumerable.Range(0, 10).Select(ordinal => new { Title = $"Todo {ordinal}", Ordinal = ordinal }).ToArray());

        affected.Should().Be(10);
        (await gate.QuerySingleAsync<long>("SELECT COUNT(*) FROM Todos;")).Should().Be(10);
    }

    [Fact]
    public async Task The_query_family_maps_rows_the_way_dapper_always_did()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.ExecuteAsync(TestSchema.Sql);
        await gate.ExecuteAsync(
            "INSERT INTO Todos (Title, Ordinal) VALUES (@Title, @Ordinal);",
            new { Title = "only", Ordinal = 42 });

        (await gate.QueryFirstAsync<TodoRow>("SELECT * FROM Todos;")).Title.Should().Be("only");
        (await gate.QuerySingleAsync<TodoRow>("SELECT * FROM Todos;")).Ordinal.Should().Be(42);
        (await gate.QueryFirstOrDefaultAsync<TodoRow>("SELECT * FROM Todos WHERE Ordinal = 999;")).Should().BeNull();
        (await gate.QuerySingleOrDefaultAsync<TodoRow>("SELECT * FROM Todos WHERE Ordinal = @Ordinal;", new { Ordinal = 42 }))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task A_read_batch_sees_one_consistent_snapshot()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.ExecuteAsync(TestSchema.Sql);
        await gate.ExecuteAsync(
            "INSERT INTO Todos (Title, Ordinal) VALUES (@Title, @Ordinal);",
            Enumerable.Range(0, 5).Select(ordinal => new { Title = "t", Ordinal = ordinal }).ToArray());

        var (count, sum) = await gate.QueryBatchAsync(async (connection, token) =>
        {
            var rows = await connection.QuerySingleAsync<long>(new CommandDefinition("SELECT COUNT(*) FROM Todos;", cancellationToken: token));
            var total = await connection.QuerySingleAsync<long>(new CommandDefinition("SELECT SUM(Ordinal) FROM Todos;", cancellationToken: token));
            return (rows, total);
        });

        count.Should().Be(5);
        sum.Should().Be(10);
    }

    /// <summary>A row of the shared <c>Todos</c> table, mapped by Dapper.</summary>
    private sealed class TodoRow
    {
        public long Id { get; init; }

        public string Title { get; init; } = string.Empty;

        public long Ordinal { get; init; }
    }
}
