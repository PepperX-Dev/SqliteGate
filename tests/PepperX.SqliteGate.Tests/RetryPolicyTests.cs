using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.Internals;
using PepperX.SqliteGate.Tests.Support;

namespace PepperX.SqliteGate.Tests;

/// <summary>
/// Covers the contention the queue cannot remove: another connection — in production, another
/// process — holding the file while a gated write wants it.
/// </summary>
public sealed class RetryPolicyTests
{
    /// <summary>
    /// Each attempt costs at least a second, because the provider's own busy loop rounds up to whole
    /// seconds. The budgets here are sized around that floor rather than around the backoff.
    /// </summary>
    private static readonly TimeSpan ProviderAttemptFloor = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_write_that_meets_a_held_lock_retries_and_then_succeeds()
    {
        await using var database = TestDatabase.Create();
        var logs = new CapturingLoggerProvider();

        var gate = SqliteGate.For(database.Path, options =>
        {
            options.BusyTimeout = TimeSpan.Zero;
            options.MaxRetryAttempts = 10;
            options.InitialRetryDelay = TimeSpan.FromMilliseconds(20);
            options.MaxRetryDelay = TimeSpan.FromMilliseconds(100);
            options.LoggerFactory = logs;
        });

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var blocker = await WriteLockBlocker.HoldAsync(database.Path);
        var release = blocker.ReleaseAfterAsync(ProviderAttemptFloor * 1.5);

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(
            connection,
            "INSERT INTO Todos (Title, Ordinal) VALUES ('retried', 1);",
            token));

        await release;

        logs.Count(GateEvents.RetryingAfterContention).Should().BeGreaterThan(0, "the lock was genuinely held");
        logs.Count(GateEvents.RetriesExhausted).Should().Be(0);

        (await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token)))
            .Should().Be(1, "the write eventually landed, exactly once");
    }

    [Fact]
    public async Task The_original_sqlite_exception_survives_the_retries()
    {
        await using var database = TestDatabase.Create();
        var logs = new CapturingLoggerProvider();

        var gate = SqliteGate.For(database.Path, options =>
        {
            options.BusyTimeout = TimeSpan.Zero;
            options.MaxRetryAttempts = 2;
            options.InitialRetryDelay = TimeSpan.FromMilliseconds(10);
            options.LoggerFactory = logs;
        });

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        await using var blocker = await WriteLockBlocker.HoldAsync(database.Path);

        var act = async () => await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(
            connection,
            "INSERT INTO Todos (Title, Ordinal) VALUES ('never', 1);",
            token));

        var thrown = await act.Should().ThrowAsync<SqliteException>(
            "callers that already handle SqliteException must keep working — the library does not wrap it");

        ConcurrencyHarness.IsContention(thrown.Which).Should().BeTrue();

        logs.Count(GateEvents.RetryingAfterContention).Should().Be(2, "two retries were configured, so three attempts in total");
        logs.Count(GateEvents.RetriesExhausted).Should().Be(1);
    }

    [Fact]
    public async Task Errors_that_are_not_contention_are_never_retried()
    {
        await using var database = TestDatabase.Create();
        var logs = new CapturingLoggerProvider();

        var gate = SqliteGate.For(database.Path, options =>
        {
            options.MaxRetryAttempts = 5;
            options.LoggerFactory = logs;
        });

        var stopwatch = Stopwatch.StartNew();

        var act = async () => await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(
            connection,
            "INSERT INTO NoSuchTable (X) VALUES (1);",
            token));

        var thrown = await act.Should().ThrowAsync<SqliteException>();
        stopwatch.Stop();

        thrown.Which.SqliteErrorCode.Should().Be(1, "a missing table is SQLITE_ERROR, a real answer rather than a busy signal");
        logs.Count(GateEvents.RetryingAfterContention).Should().Be(0, "retrying a broken statement only wastes time");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Retrying_stops_when_the_token_is_cancelled()
    {
        await using var database = TestDatabase.Create();

        var gate = SqliteGate.For(database.Path, options =>
        {
            options.BusyTimeout = TimeSpan.Zero;
            options.MaxRetryAttempts = 100;
            options.InitialRetryDelay = TimeSpan.FromSeconds(30);
            options.MaxRetryDelay = TimeSpan.FromSeconds(30);
            options.MaxTotalRetryDuration = TimeSpan.FromMinutes(10);
        });

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        await using var blocker = await WriteLockBlocker.HoldAsync(database.Path);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var stopwatch = Stopwatch.StartNew();

        var act = async () => await gate.WriteAsync(
            (connection, token) => Sql.ExecuteAsync(connection, "INSERT INTO Todos (Title, Ordinal) VALUES ('x', 1);", token),
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        stopwatch.Stop();

        // The configured backoff is thirty seconds, so anything under that could not have waited it
        // out. The margin is for a loaded CI runner, not for the behaviour under test.
        stopwatch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(20),
            "a cancelled operation must not sit out a thirty-second backoff");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(8)]
    public void Backoff_doubles_each_attempt_and_stays_inside_the_cap(int attempt)
    {
        var options = new SqliteGateOptions
        {
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(400),
            RetryJitter = 0.25,
        };

        var executor = new RetryExecutor(options, "test", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, []);

        var expected = Math.Min(50 * Math.Pow(2, attempt - 1), 400);
        var delays = Enumerable.Range(0, 200).Select(_ => executor.ComputeDelay(attempt).TotalMilliseconds).ToArray();

        delays.Should().OnlyContain(delay => delay >= expected * 0.75 - 0.001 && delay <= expected * 1.25 + 0.001,
            "jitter spreads the delay around the computed value by the configured proportion");

        delays.Should().Contain(delay => Math.Abs(delay - expected) > 0.001, "jitter means the delays are not all identical");
    }

    [Fact]
    public void Backoff_without_jitter_is_exactly_the_doubling_series()
    {
        var options = new SqliteGateOptions
        {
            InitialRetryDelay = TimeSpan.FromMilliseconds(10),
            MaxRetryDelay = TimeSpan.FromMilliseconds(1000),
            RetryJitter = 0,
        };

        var executor = new RetryExecutor(options, "test", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, []);

        executor.ComputeDelay(1).Should().Be(TimeSpan.FromMilliseconds(10));
        executor.ComputeDelay(2).Should().Be(TimeSpan.FromMilliseconds(20));
        executor.ComputeDelay(3).Should().Be(TimeSpan.FromMilliseconds(40));
        executor.ComputeDelay(9).Should().Be(TimeSpan.FromMilliseconds(1000), "capped");
        executor.ComputeDelay(40).Should().Be(TimeSpan.FromMilliseconds(1000), "still capped, and no overflow");
    }

    [Fact]
    public void Retrying_can_be_switched_off_entirely()
    {
        var options = new SqliteGateOptions { MaxRetryAttempts = 0 };
        options.Validate();
        options.MaxRetryAttempts.Should().Be(0);
    }
}
