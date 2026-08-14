using System.Diagnostics;
using FluentAssertions;

namespace PepperX.SqliteGate.Conformance;

/// <summary>
/// The concurrency guarantee, written once and inherited by every package.
/// </summary>
/// <remarks>
/// <para>
/// Raw ADO.NET, Dapper and EF Core are three very different ways to phrase an <c>INSERT</c>, and all
/// three are supposed to end up in the same write queue with the same outcome. Expressing that as a
/// shared suite means a regression shows up on every package at once, instead of one adapter quietly
/// drifting out of the guarantee while its own tests stay green.
/// </para>
/// <para>
/// Every test here runs against a real file on disk. That is the only way file locking — the thing
/// the library exists to manage — is present at all.
/// </para>
/// </remarks>
public abstract class ConcurrencyConformanceTests : IAsyncLifetime
{
    /// <summary>The throwaway database for the current test.</summary>
    protected TestDatabase Database { get; private set; } = null!;

    /// <summary>Full path of the current test's database file.</summary>
    protected string DatabasePath => Database.Path;

    /// <summary>Creates the schema the suite expects: a <c>Todos</c> table and a <c>Counters</c> row.</summary>
    /// <param name="databasePath">Database to build the schema in.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>A task that completes when the schema exists.</returns>
    protected abstract Task CreateSchemaAsync(string databasePath, CancellationToken cancellationToken);

    /// <summary>Inserts one row through the package under test, as a gated write.</summary>
    /// <param name="databasePath">Target database.</param>
    /// <param name="ordinal">Value to store in the <c>Ordinal</c> column.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>A task that completes when the row is committed.</returns>
    protected abstract Task InsertTodoAsync(string databasePath, int ordinal, CancellationToken cancellationToken);

    /// <summary>Counts the rows in <c>Todos</c>, as a gated read.</summary>
    /// <param name="databasePath">Target database.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>The row count.</returns>
    protected abstract Task<long> CountTodosAsync(string databasePath, CancellationToken cancellationToken);

    /// <summary>Sums the <c>Ordinal</c> column, as a gated read.</summary>
    /// <param name="databasePath">Target database.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>The sum, or zero when the table is empty.</returns>
    protected abstract Task<long> SumOrdinalsAsync(string databasePath, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the counter and writes back one more than it read, as a single gated write. This is a
    /// read-modify-write on purpose: SQL cannot make it atomic for us, so it fails loudly if writes
    /// are not actually serialized.
    /// </summary>
    /// <param name="databasePath">Target database.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>A task that completes when the new value is committed.</returns>
    protected abstract Task IncrementCounterAsync(string databasePath, CancellationToken cancellationToken);

    /// <summary>Reads the counter value, as a gated read.</summary>
    /// <param name="databasePath">Target database.</param>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>The current counter value.</returns>
    protected abstract Task<long> ReadCounterAsync(string databasePath, CancellationToken cancellationToken);

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        Database = TestDatabase.Create(GetType().Name);
        await CreateSchemaAsync(DatabasePath, CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await Database.DisposeAsync();

    [Theory]
    [InlineData(50)]
    [InlineData(ConcurrencyHarness.DefaultConcurrency)]
    public async Task Concurrent_writers_commit_every_row_and_none_report_contention(int writers)
    {
        var stopwatch = Stopwatch.StartNew();

        var failures = await ConcurrencyHarness.RunConcurrentlyAsync(
            writers,
            (ordinal, cancellationToken) => InsertTodoAsync(DatabasePath, ordinal, cancellationToken));

        stopwatch.Stop();

        ConcurrencyHarness.ContentionErrors(failures).Should().BeEmpty(
            "the write queue exists so SQLITE_BUSY never reaches the caller, but got:{0}{1}",
            Environment.NewLine,
            ConcurrencyHarness.Describe(failures));

        failures.Should().BeEmpty(
            "no write should fail at all. Failures:{0}{1}",
            Environment.NewLine,
            ConcurrencyHarness.Describe(failures));

        (await CountTodosAsync(DatabasePath, CancellationToken.None))
            .Should().Be(writers, "every queued write must land exactly once");

        // 0 + 1 + … + (writers - 1): proves the rows are the ones that were asked for, not just the
        // right number of rows.
        (await SumOrdinalsAsync(DatabasePath, CancellationToken.None))
            .Should().Be((long)writers * (writers - 1) / 2);

        stopwatch.Elapsed.Should().BeLessThan(
            ConcurrencyHarness.DefaultTimeout,
            "a run that only finishes on the deadlock guard has not really passed");
    }

    [Fact]
    public async Task Concurrent_read_modify_write_never_loses_an_update()
    {
        const int writers = ConcurrencyHarness.DefaultConcurrency;

        var failures = await ConcurrencyHarness.RunConcurrentlyAsync(
            writers,
            (_, cancellationToken) => IncrementCounterAsync(DatabasePath, cancellationToken));

        failures.Should().BeEmpty(
            "increments must not fail. Failures:{0}{1}",
            Environment.NewLine,
            ConcurrencyHarness.Describe(failures));

        (await ReadCounterAsync(DatabasePath, CancellationToken.None)).Should().Be(
            writers,
            "each increment reads then writes, so any overlap between two writers would lose one");
    }

    [Fact]
    public async Task Concurrent_reads_and_writes_run_together_without_contention_errors()
    {
        const int writers = ConcurrencyHarness.DefaultConcurrency;
        const int readers = ConcurrencyHarness.DefaultConcurrency;

        var observed = new System.Collections.Concurrent.ConcurrentBag<long>();

        var failures = await ConcurrencyHarness.RunConcurrentlyAsync(
            writers + readers,
            async (ordinal, cancellationToken) =>
            {
                if (ordinal < writers)
                {
                    await InsertTodoAsync(DatabasePath, ordinal, cancellationToken);
                }
                else
                {
                    observed.Add(await CountTodosAsync(DatabasePath, cancellationToken));
                }
            });

        ConcurrencyHarness.ContentionErrors(failures).Should().BeEmpty(
            "readers must never collide with the writer under WAL, but got:{0}{1}",
            Environment.NewLine,
            ConcurrencyHarness.Describe(failures));

        failures.Should().BeEmpty(
            "nothing should fail in a mixed workload. Failures:{0}{1}",
            Environment.NewLine,
            ConcurrencyHarness.Describe(failures));

        (await CountTodosAsync(DatabasePath, CancellationToken.None)).Should().Be(writers);

        observed.Should().HaveCount(readers);
        observed.Should().OnlyContain(count => count >= 0 && count <= writers,
            "a reader can only ever see a committed snapshot, never a partial or impossible one");
    }
}
