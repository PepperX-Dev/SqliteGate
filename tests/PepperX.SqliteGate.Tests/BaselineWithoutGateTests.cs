using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.Tests.Support;

namespace PepperX.SqliteGate.Tests;

/// <summary>
/// The problem, reproduced. These tests use plain <c>Microsoft.Data.Sqlite</c> with no gate at all,
/// so that the thing the library fixes is written down and verified rather than asserted in a README.
/// </summary>
/// <remarks>
/// <para>
/// The failure is made prompt rather than manufactured. Two things stand between concurrent writers
/// and a <c>SQLITE_BUSY</c> the caller can see: SQLite's own busy handler, switched off here with
/// <c>PRAGMA busy_timeout=0</c>, and a retry loop <c>Microsoft.Data.Sqlite</c> runs in managed code
/// for <c>CommandTimeout</c> seconds — 30 by default — which <c>busy_timeout</c> does not affect.
/// Shrinking that budget to one second (<c>Default Timeout=1</c>) means the wall is hit in seconds
/// instead of under sustained production load. Note that zero would mean <em>wait forever</em>, not
/// "do not wait".
/// </para>
/// <para>
/// So the writers here are given one second of the provider's patience and asked to do more work
/// than fits in it. A real application gets thirty seconds of patience and eventually asks for more
/// than fits in that.
/// </para>
/// </remarks>
public sealed class BaselineWithoutGateTests
{
    private const int Writers = 48;

    /// <summary>How long each ungated writer holds the write lock, to make the collision window real.</summary>
    private static readonly TimeSpan LockHold = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task Plain_ado_net_reports_database_is_locked_under_concurrent_writes()
    {
        await using var database = TestDatabase.Create();
        await CreateSchemaWithoutGateAsync(database.Path);

        var contention = new ConcurrentBag<SqliteException>();

        // Up to three rounds so a fluke of scheduling cannot fail the build. In practice the first
        // round is enough — that is the point of the test.
        for (var round = 0; round < 3 && contention.IsEmpty; round++)
        {
            var failures = await ConcurrencyHarness.RunConcurrentlyAsync(
                Writers,
                (ordinal, cancellationToken) => InsertWithoutGateAsync(database.Path, ordinal, cancellationToken),
                TimeSpan.FromSeconds(60));

            foreach (var error in ConcurrencyHarness.ContentionErrors(failures))
            {
                contention.Add(error);
            }
        }

        contention.Should().NotBeEmpty(
            "unmanaged concurrent writes against one SQLite file are expected to produce SQLITE_BUSY — " +
            "if this ever stops happening, the baseline no longer demonstrates the problem SqliteGate solves");

        contention.Should().OnlyContain(
            exception => exception.SqliteErrorCode == 5 || exception.SqliteErrorCode == 6,
            "the failure mode is specifically SQLITE_BUSY (5) or SQLITE_LOCKED (6)");
    }

    [Fact]
    public async Task The_same_load_through_the_gate_is_clean()
    {
        await using var database = TestDatabase.Create();

        var gate = SqliteGate.For(database.Path);
        await gate.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));

        var failures = await ConcurrencyHarness.RunConcurrentlyAsync(
            Writers,
            (ordinal, cancellationToken) => gate.WriteAsync(
                (connection, token) => Sql.ExecuteAsync(
                    connection,
                    "INSERT INTO Todos (Title, Ordinal) VALUES ($title, $ordinal);",
                    token,
                    ("$title", $"Todo {ordinal}"),
                    ("$ordinal", ordinal)),
                cancellationToken));

        failures.Should().BeEmpty(
            "same load, same file, same machine — the only difference is the gate. Failures:{0}{1}",
            Environment.NewLine,
            ConcurrencyHarness.Describe(failures));

        var count = await gate.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token));
        count.Should().Be(Writers);
    }

    private static async Task CreateSchemaWithoutGateAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        await Sql.ExecuteAsync(connection, Sql.Schema, CancellationToken.None);
    }

    private static async Task InsertWithoutGateAsync(string path, int ordinal, CancellationToken cancellationToken)
    {
        // One second of provider patience instead of thirty, so the collision surfaces while the
        // test is still running. Zero would mean "wait forever" and hang the suite.
        await using var connection = new SqliteConnection($"Data Source={path};Default Timeout=1");
        await connection.OpenAsync(cancellationToken);

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout=0;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }

        // No gate: every task begins its own write transaction against the same file and hopes.
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Todos (Title, Ordinal) VALUES ($title, $ordinal);";
        command.Parameters.AddWithValue("$title", $"Todo {ordinal}");
        command.Parameters.AddWithValue("$ordinal", ordinal);
        await command.ExecuteNonQueryAsync(cancellationToken);

        // Holding the write lock widens the window the other writers collide with, and makes the
        // total work exceed the one second of retrying they are each allowed.
        await Task.Delay(LockHold, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }
}
