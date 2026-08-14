using System.Diagnostics;
using FluentAssertions;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.Tests.Support;

namespace PepperX.SqliteGate.Tests;

/// <summary>
/// Proves the registry is keyed per database rather than being one global lock wearing a per-file
/// costume: two files must be able to take writes at the same time.
/// </summary>
public sealed class MultiFileIsolationTests
{
    [Fact]
    public async Task A_write_held_on_one_database_does_not_delay_another()
    {
        await using var first = TestDatabase.Create();
        await using var second = TestDatabase.Create();

        var firstGate = SqliteGate.For(first.Path);
        var secondGate = SqliteGate.For(second.Path);

        firstGate.Should().NotBeSameAs(secondGate);

        await firstGate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));
        await secondGate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Occupy the first database's single write slot and keep it.
        var heldWrite = firstGate.WriteAsync(async (connection, token) =>
        {
            await Sql.ExecuteAsync(connection, "INSERT INTO Todos (Title, Ordinal) VALUES ('held', 0);", token);
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        var stopwatch = Stopwatch.StartNew();

        var failures = await ConcurrencyHarness.RunConcurrentlyAsync(
            50,
            (ordinal, cancellationToken) => secondGate.WriteAsync(
                (connection, token) => Sql.ExecuteAsync(
                    connection,
                    "INSERT INTO Todos (Title, Ordinal) VALUES ($title, $ordinal);",
                    token,
                    ("$title", $"Todo {ordinal}"),
                    ("$ordinal", ordinal)),
                cancellationToken),
            TimeSpan.FromSeconds(30));

        stopwatch.Stop();

        failures.Should().BeEmpty(
            "the second database has its own queue. Failures:{0}{1}",
            Environment.NewLine,
            ConcurrencyHarness.Describe(failures));

        // The decisive assertion: the first database's write is still in flight, so all fifty writes
        // to the second database completed while the first one was locked.
        firstGate.PendingWriters.Should().Be(1, "the held write has not been released yet");

        release.SetResult();
        await heldWrite;

        (await secondGate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token)))
            .Should().Be(50);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Each_database_reports_its_own_queue_depth()
    {
        await using var first = TestDatabase.Create();
        await using var second = TestDatabase.Create();

        var firstGate = SqliteGate.For(first.Path);
        var secondGate = SqliteGate.For(second.Path);

        await firstGate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var heldWrite = firstGate.WriteAsync(async (_, _) =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        firstGate.PendingWriters.Should().Be(1);
        secondGate.PendingWriters.Should().Be(0, "an unrelated database is not waiting for anything");

        release.SetResult();
        await heldWrite;

        firstGate.PendingWriters.Should().Be(0);
    }
}
