using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.Tests.Support;

namespace PepperX.SqliteGate.Tests;

/// <summary>
/// The instruments an operator needs when a service is slow and nobody can tell whether SQLite is
/// the reason. Contention is invisible in a stack trace, so it is measured instead.
/// </summary>
public sealed class MetricsTests
{
    private const string MeterName = "PepperX.SqliteGate";

    [Fact]
    public async Task Writes_and_reads_are_measured_and_tagged_with_their_database()
    {
        await using var database = TestDatabase.Create();
        var databaseName = Path.GetFileName(database.Path);

        using var recorder = new Recorder(databaseName);

        var gate = SqliteGate.For(database.Path);
        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));
        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(
            connection,
            "INSERT INTO Todos (Title, Ordinal) VALUES ('measured', 1);",
            token));
        await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token));

        recorder.Doubles("sqlitegate.write.duration").Should().HaveCount(2);
        recorder.Doubles("sqlitegate.write.wait.duration").Should().HaveCount(2);
        recorder.Doubles("sqlitegate.read.duration").Should().ContainSingle();

        recorder.Longs("sqlitegate.operation.count").Sum().Should().Be(3);
        recorder.Modes("sqlitegate.operation.count").Should().BeEquivalentTo(["write", "write", "read"]);
        recorder.Outcomes("sqlitegate.operation.count").Should().OnlyContain(outcome => outcome == "success");
    }

    [Fact]
    public async Task A_failed_operation_is_counted_as_a_failure()
    {
        await using var database = TestDatabase.Create();
        var databaseName = Path.GetFileName(database.Path);

        using var recorder = new Recorder(databaseName);

        var gate = SqliteGate.For(database.Path, options => options.MaxRetryAttempts = 0);

        var act = async () => await gate.WriteAsync(
            (connection, token) => Sql.ExecuteAsync(connection, "INSERT INTO Missing (X) VALUES (1);", token));

        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();

        recorder.Outcomes("sqlitegate.operation.count").Should().Contain("failure");
    }

    [Fact]
    public async Task Queue_depth_returns_to_zero_once_the_writes_are_done()
    {
        await using var database = TestDatabase.Create();
        var databaseName = Path.GetFileName(database.Path);

        using var recorder = new Recorder(databaseName);

        var gate = SqliteGate.For(database.Path);
        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var failures = await ConcurrencyHarness.RunConcurrentlyAsync(
            40,
            (ordinal, cancellationToken) => gate.WriteAsync(
                (connection, token) => Sql.ExecuteAsync(
                    connection,
                    "INSERT INTO Todos (Title, Ordinal) VALUES ('q', $ordinal);",
                    token,
                    ("$ordinal", ordinal)),
                cancellationToken));

        failures.Should().BeEmpty(ConcurrencyHarness.Describe(failures));

        var deltas = recorder.Ints("sqlitegate.write_queue.depth");
        deltas.Should().NotBeEmpty();
        deltas.Sum().Should().Be(0, "every writer that joined the queue also left it");
        deltas.Count(delta => delta > 0).Should().Be(41, "forty inserts plus the schema write");
    }

    [Fact]
    public async Task Retries_are_counted()
    {
        await using var database = TestDatabase.Create();
        var databaseName = Path.GetFileName(database.Path);

        var gate = SqliteGate.For(database.Path, options =>
        {
            options.BusyTimeout = TimeSpan.Zero;
            options.MaxRetryAttempts = 2;
            options.InitialRetryDelay = TimeSpan.FromMilliseconds(10);
        });

        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        using var recorder = new Recorder(databaseName);
        await using var blocker = await WriteLockBlocker.HoldAsync(database.Path);

        var act = async () => await gate.WriteAsync(
            (connection, token) => Sql.ExecuteAsync(connection, "INSERT INTO Todos (Title, Ordinal) VALUES ('x', 1);", token));

        await act.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();

        recorder.Longs("sqlitegate.retry.count").Sum().Should().Be(2);
    }

    [Fact]
    public async Task Metrics_can_be_switched_off()
    {
        await using var database = TestDatabase.Create();
        var databaseName = Path.GetFileName(database.Path);

        using var recorder = new Recorder(databaseName);

        var gate = SqliteGate.For(database.Path, options => options.EnableMetrics = false);
        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));
        await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT 1;", token));

        recorder.Doubles("sqlitegate.write.duration").Should().BeEmpty();
        recorder.Longs("sqlitegate.operation.count").Should().BeEmpty();
    }

    /// <summary>
    /// Listens to the gate's meter, keeping only the measurements tagged with one database. The
    /// meter is process-wide and the suite runs in parallel, so filtering is what makes these
    /// assertions about this test rather than about whatever else is running.
    /// </summary>
    private sealed class Recorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentBag<(string Instrument, double Value)> _doubles = [];
        private readonly ConcurrentBag<(string Instrument, long Value)> _longs = [];
        private readonly ConcurrentBag<(string Instrument, int Value)> _ints = [];
        private readonly ConcurrentBag<(string Instrument, string? Mode, string? Outcome)> _tags = [];

        internal Recorder(string databaseName)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };

            _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            {
                if (Matches(tags, databaseName))
                {
                    _doubles.Add((instrument.Name, measurement));
                }
            });

            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                if (Matches(tags, databaseName))
                {
                    _longs.Add((instrument.Name, measurement));
                    _tags.Add((instrument.Name, Tag(tags, "sqlitegate.mode"), Tag(tags, "sqlitegate.outcome")));
                }
            });

            _listener.SetMeasurementEventCallback<int>((instrument, measurement, tags, _) =>
            {
                if (Matches(tags, databaseName))
                {
                    _ints.Add((instrument.Name, measurement));
                }
            });

            _listener.Start();
        }

        internal IReadOnlyList<double> Doubles(string instrument) =>
            [.. _doubles.Where(entry => entry.Instrument == instrument).Select(entry => entry.Value)];

        internal IReadOnlyList<long> Longs(string instrument) =>
            [.. _longs.Where(entry => entry.Instrument == instrument).Select(entry => entry.Value)];

        internal IReadOnlyList<int> Ints(string instrument) =>
            [.. _ints.Where(entry => entry.Instrument == instrument).Select(entry => entry.Value)];

        internal IReadOnlyList<string?> Modes(string instrument) =>
            [.. _tags.Where(entry => entry.Instrument == instrument).Select(entry => entry.Mode)];

        internal IReadOnlyList<string?> Outcomes(string instrument) =>
            [.. _tags.Where(entry => entry.Instrument == instrument).Select(entry => entry.Outcome)];

        public void Dispose() => _listener.Dispose();

        private static bool Matches(ReadOnlySpan<KeyValuePair<string, object?>> tags, string databaseName)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "db.name" && tag.Value as string == databaseName)
                {
                    return true;
                }
            }

            return false;
        }

        private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == key)
                {
                    return tag.Value as string;
                }
            }

            return null;
        }
    }
}
