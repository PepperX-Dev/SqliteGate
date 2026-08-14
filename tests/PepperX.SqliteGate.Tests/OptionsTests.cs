using FluentAssertions;

namespace PepperX.SqliteGate.Tests;

/// <summary>
/// Option validation — the errors a caller should get at startup rather than at 3am under load.
/// </summary>
public sealed class OptionsTests
{
    [Fact]
    public void The_defaults_are_valid_and_sensible()
    {
        var options = new SqliteGateOptions();

        options.Invoking(o => o.Validate()).Should().NotThrow();

        options.BusyTimeout.Should().Be(TimeSpan.FromSeconds(5));
        options.Synchronous.Should().Be(SqliteSynchronousMode.Normal);
        options.EnableForeignKeys.Should().BeTrue();
        options.RequireWriteAheadLog.Should().BeTrue();
        options.MaxRetryAttempts.Should().Be(5);
        options.MaxReaderConnections.Should().BeInRange(4, 16);
        options.WriteQueueTimeout.Should().BeNull("waiting forever, but cancellably, is the right default");
        options.CommandTimeout.Should().BeNull("it follows BusyTimeout unless told otherwise");
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Impossible_configurations_are_rejected(string because, Action<SqliteGateOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SqliteGateOptions();
        configure(options);

        options.Invoking(o => o.Validate())
            .Should().Throw<ArgumentOutOfRangeException>(because);
    }

    public static TheoryData<string, Action<SqliteGateOptions>> InvalidOptions() => new()
    {
        { "a pool of zero readers can never serve a read", options => options.MaxReaderConnections = 0 },
        { "negative retry counts are meaningless", options => options.MaxRetryAttempts = -1 },
        { "a maximum below the initial delay cannot be honoured", options =>
            {
                options.InitialRetryDelay = TimeSpan.FromSeconds(2);
                options.MaxRetryDelay = TimeSpan.FromSeconds(1);
            }
        },
        { "jitter is a proportion", options => options.RetryJitter = 1.5 },
        { "jitter cannot be negative", options => options.RetryJitter = -0.1 },
        { "a queue timeout of zero would fail every write instantly", options => options.WriteQueueTimeout = TimeSpan.Zero },
        { "a negative busy timeout is not a thing", options => options.BusyTimeout = TimeSpan.FromSeconds(-1) },
        { "a negative command timeout is not a thing", options => options.CommandTimeout = TimeSpan.FromSeconds(-1) },
    };

    [Fact]
    public void Options_are_snapshotted_so_later_edits_cannot_change_a_running_gate()
    {
        var options = new SqliteGateOptions { MaxRetryAttempts = 3 };
        var clone = options.Clone();

        options.MaxRetryAttempts = 99;

        clone.MaxRetryAttempts.Should().Be(3);
    }

    [Fact]
    public void Additional_pragmas_survive_the_snapshot()
    {
        var options = new SqliteGateOptions();
        options.AdditionalPragmas.Add("PRAGMA temp_store=MEMORY;");

        options.Clone().AdditionalPragmas.Should().ContainSingle().Which.Should().Be("PRAGMA temp_store=MEMORY;");
    }

    [Fact]
    public void A_blank_database_name_is_rejected_immediately()
    {
        var act = () => SqliteGate.For("   ");
        act.Should().Throw<ArgumentException>();
    }
}
