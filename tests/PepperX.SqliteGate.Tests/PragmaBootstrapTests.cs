using FluentAssertions;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.Tests.Support;

namespace PepperX.SqliteGate.Tests;

/// <summary>
/// Verifies the pragma setup the caller is promised they never have to do by hand — including on
/// connections opened long after startup, which is where hand-rolled setup usually goes wrong.
/// </summary>
public sealed class PragmaBootstrapTests
{
    [Fact]
    public async Task Write_ahead_logging_is_enabled_on_first_use()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await gate.EnsureInitializedAsync();

        var journalMode = await gate.ReadAsync((connection, token) => Sql.PragmaAsync(connection, "journal_mode", token));
        journalMode.Should().Be("wal", "free concurrent reads are a consequence of WAL, not a bonus on top of it");
    }

    [Fact]
    public async Task The_connection_scoped_pragmas_are_applied_to_readers_and_the_writer_alike()
    {
        await using var database = TestDatabase.Create();

        var gate = SqliteGate.For(database.Path, options =>
        {
            options.BusyTimeout = TimeSpan.FromMilliseconds(7500);
            options.Synchronous = SqliteSynchronousMode.Normal;
            options.EnableForeignKeys = true;
        });

        var onTheWriter = await gate.WriteWithoutTransactionAsync(async (connection, token) => new
        {
            BusyTimeout = await Sql.PragmaAsync(connection, "busy_timeout", token),
            Synchronous = await Sql.PragmaAsync(connection, "synchronous", token),
            ForeignKeys = await Sql.PragmaAsync(connection, "foreign_keys", token),
        });

        var onAReader = await gate.ReadAsync(async (connection, token) => new
        {
            BusyTimeout = await Sql.PragmaAsync(connection, "busy_timeout", token),
            Synchronous = await Sql.PragmaAsync(connection, "synchronous", token),
            ForeignKeys = await Sql.PragmaAsync(connection, "foreign_keys", token),
        });

        onTheWriter.BusyTimeout.Should().Be("7500");
        onTheWriter.Synchronous.Should().Be("1", "PRAGMA synchronous reports NORMAL as 1");
        onTheWriter.ForeignKeys.Should().Be("1");

        // The reader is a different connection, opened later, from a pool. Connection-scoped pragmas
        // reset on every new connection, so this is the assertion that catches "we set it at startup".
        onAReader.BusyTimeout.Should().Be("7500");
        onAReader.Synchronous.Should().Be("1");
        onAReader.ForeignKeys.Should().Be("1");
    }

    [Fact]
    public async Task Foreign_keys_can_be_turned_off()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path, options => options.EnableForeignKeys = false);

        (await gate.ReadAsync((connection, token) => Sql.PragmaAsync(connection, "foreign_keys", token)))
            .Should().Be("0");
    }

    [Fact]
    public async Task Additional_pragmas_are_applied_to_every_connection()
    {
        await using var database = TestDatabase.Create();

        var gate = SqliteGate.For(database.Path, options => options.AdditionalPragmas.Add("PRAGMA cache_size=-8000;"));

        (await gate.ReadAsync((connection, token) => Sql.PragmaAsync(connection, "cache_size", token)))
            .Should().Be("-8000");
    }

    [Fact]
    public async Task The_bootstrap_is_logged_once_with_what_it_settled_on()
    {
        await using var database = TestDatabase.Create();
        var logs = new CapturingLoggerProvider();

        var gate = SqliteGate.For(database.Path, options => options.LoggerFactory = logs);

        await gate.EnsureInitializedAsync();
        await gate.EnsureInitializedAsync();
        await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT 1;", token));

        logs.Count(GateEvents.Bootstrapped).Should().Be(1, "the bootstrap runs once per database, not once per call");
        logs.Entries.Should().Contain(entry => entry.Message.Contains("journal_mode=wal"));
        logs.Count(GateEvents.WriteAheadLoggingUnavailable).Should().Be(0);
    }

    [Fact]
    public async Task The_database_directory_is_created_if_it_is_missing()
    {
        await using var database = TestDatabase.Create();

        var nested = Path.Combine(Path.GetDirectoryName(database.Path)!, "does", "not", "exist", "app.db");

        try
        {
            var gate = SqliteGate.For(nested);
            await gate.EnsureInitializedAsync();

            File.Exists(nested).Should().BeTrue("'nothing to configure by hand' includes not pre-creating directories");
        }
        finally
        {
            await SqliteGateRegistry.RemoveAsync(nested);
        }
    }

    [Fact]
    public async Task The_connection_string_bounds_the_providers_own_busy_retry_loop()
    {
        await using var database = TestDatabase.Create();

        var gate = SqliteGate.For(database.Path, options => options.BusyTimeout = TimeSpan.FromSeconds(4));

        // Microsoft.Data.Sqlite retries locked statements itself for CommandTimeout seconds, and its
        // default of 30 would sit in front of everything this library configures.
        gate.ConnectionString.Should().Contain("Default Timeout=4");
    }

    [Fact]
    public async Task Sub_second_busy_timeouts_round_up_rather_than_becoming_infinite()
    {
        await using var database = TestDatabase.Create();

        var gate = SqliteGate.For(database.Path, options => options.BusyTimeout = TimeSpan.FromMilliseconds(200));

        // Zero would mean "wait forever" in the provider, which is the opposite of what a 200 ms
        // busy timeout is asking for.
        gate.ConnectionString.Should().Contain("Default Timeout=1");
    }
}
