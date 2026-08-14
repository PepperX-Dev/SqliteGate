using System.Diagnostics;
using FluentAssertions;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.Tests.Support;

namespace PepperX.SqliteGate.Tests;

/// <summary>
/// Behaviour of the write queue itself: exclusion, ordering, cancellation while queued, the queue
/// timeout, and the fact that reads never join the queue at all.
/// </summary>
public sealed class WriteQueueTests
{
    [Fact]
    public async Task Only_one_write_is_ever_in_flight()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var concurrent = 0;
        var peak = 0;

        var failures = await ConcurrencyHarness.RunConcurrentlyAsync(
            100,
            (ordinal, cancellationToken) => gate.WriteAsync(
                async (connection, token) =>
                {
                    var current = Interlocked.Increment(ref concurrent);
                    InterlockedMax(ref peak, current);

                    await Sql.ExecuteAsync(
                        connection,
                        "INSERT INTO Todos (Title, Ordinal) VALUES ($title, $ordinal);",
                        token,
                        ("$title", "t"),
                        ("$ordinal", ordinal));

                    Interlocked.Decrement(ref concurrent);
                },
                cancellationToken));

        failures.Should().BeEmpty(ConcurrencyHarness.Describe(failures));
        peak.Should().Be(1, "the whole point is that two writes are never inside the database at once");
    }

    [Fact]
    public async Task Reads_are_not_blocked_by_a_write_in_flight()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var heldWrite = gate.WriteAsync(async (connection, token) =>
        {
            await Sql.ExecuteAsync(connection, "INSERT INTO Todos (Title, Ordinal) VALUES ('held', 0);", token);
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        var stopwatch = Stopwatch.StartNew();
        var failures = await ConcurrencyHarness.RunConcurrentlyAsync(
            50,
            (_, cancellationToken) => gate.ReadAsync(
                (connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token),
                cancellationToken),
            TimeSpan.FromSeconds(30));
        stopwatch.Stop();

        failures.Should().BeEmpty(
            "readers work from a WAL snapshot and never wait for the writer. Failures:{0}{1}",
            Environment.NewLine,
            ConcurrencyHarness.Describe(failures));

        gate.PendingWriters.Should().Be(1, "all fifty reads completed while the write was still open");

        release.SetResult();
        await heldWrite;
    }

    [Fact]
    public async Task A_queued_write_observes_its_cancellation_token()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var heldWrite = gate.WriteAsync(async (_, _) =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        using var cancellation = new CancellationTokenSource();

        var queuedWrite = gate.WriteAsync(
            (connection, token) => Sql.ExecuteAsync(connection, "INSERT INTO Todos (Title, Ordinal) VALUES ('never', 1);", token),
            cancellation.Token);

        // Wait for it to actually reach the queue rather than guessing with a sleep.
        await WaitUntilAsync(() => gate.PendingWriters == 2);

        cancellation.Cancel();

        // The assertion is about ordering, not wall time: the writer must leave the queue while the
        // holder is still holding it. A stopwatch here would be measuring the CI scheduler instead.
        var act = async () => await queuedWrite.WaitAsync(TimeSpan.FromSeconds(30));
        await act.Should().ThrowAsync<OperationCanceledException>();

        gate.PendingWriters.Should().Be(1, "the cancelled writer released its queue slot, and the holder still has the other");

        release.SetResult();
        await heldWrite;

        gate.PendingWriters.Should().Be(0);

        (await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token)))
            .Should().Be(0, "the cancelled write never reached the database");
    }

    [Fact]
    public async Task A_saturated_queue_times_out_rather_than_waiting_forever()
    {
        await using var database = TestDatabase.Create();

        var gate = SqliteGate.For(database.Path, options => options.WriteQueueTimeout = TimeSpan.FromMilliseconds(200));

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var heldWrite = gate.WriteAsync(async (_, _) =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        var act = async () => await gate.WriteAsync(
            (connection, token) => Sql.ExecuteAsync(connection, "INSERT INTO Todos (Title, Ordinal) VALUES ('never', 1);", token));

        var thrown = await act.Should().ThrowAsync<SqliteGateTimeoutException>();
        thrown.Which.Timeout.Should().Be(TimeSpan.FromMilliseconds(200));
        thrown.Which.DatabasePath.Should().Be(database.Path);

        release.SetResult();
        await heldWrite;

        gate.PendingWriters.Should().Be(0, "a timed-out waiter must not leak a queue slot either");
    }

    [Fact]
    public async Task Queue_depth_reflects_the_writers_that_are_waiting()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var heldWrite = gate.WriteAsync(async (_, _) =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        var queued = Enumerable.Range(0, 5)
            .Select(ordinal => gate.WriteAsync((connection, token) => Sql.ExecuteAsync(
                connection,
                "INSERT INTO Todos (Title, Ordinal) VALUES ('queued', $ordinal);",
                token,
                ("$ordinal", ordinal))))
            .ToArray();

        await WaitUntilAsync(() => gate.PendingWriters == 6);

        gate.PendingWriters.Should().Be(6, "one in flight plus five waiting");

        release.SetResult();
        await heldWrite;
        await Task.WhenAll(queued);

        gate.PendingWriters.Should().Be(0);
    }

    /// <summary>
    /// Polls until a condition holds, so tests wait for the state they need instead of sleeping for
    /// a guessed interval. Returns quietly on timeout and lets the following assertion do the
    /// reporting.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();

        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(10);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}
