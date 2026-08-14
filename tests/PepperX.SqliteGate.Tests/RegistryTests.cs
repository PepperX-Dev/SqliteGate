using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.DependencyInjection;
using PepperX.SqliteGate.Tests.Support;

namespace PepperX.SqliteGate.Tests;

/// <summary>
/// Guards the one-gate-per-file rule.
/// </summary>
/// <remarks>
/// If these ever fail, nothing else in the suite means anything: two gates for one file is two
/// perfectly-behaved write queues feeding a database that only accepts one writer. The symptom is
/// <c>SQLITE_BUSY</c> in production while every concurrency test still passes, because each suite
/// only ever creates one gate.
/// </remarks>
public sealed class RegistryTests
{
    [Fact]
    public async Task Different_spellings_of_one_path_share_a_single_gate()
    {
        await using var database = TestDatabase.Create();

        var directory = Path.GetDirectoryName(database.Path)!;
        var fileName = Path.GetFileName(database.Path);

        var byAbsolutePath = SqliteGate.For(database.Path);
        var byRedundantPath = SqliteGate.For(Path.Combine(directory, "nested", "..", fileName));
        var byConnectionString = SqliteGate.For($"Data Source={database.Path}");

        byRedundantPath.Should().BeSameAs(byAbsolutePath, "'a/../b' and 'b' are the same file");
        byConnectionString.Should().BeSameAs(byAbsolutePath, "a connection string naming the file is still that file");
    }

    [Fact]
    public async Task A_relative_path_and_its_absolute_form_share_a_single_gate()
    {
        var relative = $"registry-relative-{Guid.NewGuid():N}.db";
        var absolute = Path.GetFullPath(relative);

        try
        {
            var byRelativePath = SqliteGate.For(relative);
            var byAbsolutePath = SqliteGate.For(absolute);

            byRelativePath.Should().BeSameAs(byAbsolutePath);
            byRelativePath.DatabasePath.Should().Be(absolute, "the gate reports the canonical path, not the input");
        }
        finally
        {
            await SqliteGateRegistry.RemoveAsync(absolute);
            File.Delete(absolute);
        }
    }

    [Fact]
    public async Task Different_files_get_different_gates()
    {
        await using var first = TestDatabase.Create();
        await using var second = TestDatabase.Create();

        SqliteGate.For(first.Path).Should().NotBeSameAs(SqliteGate.For(second.Path));
    }

    [Fact]
    public async Task The_container_hands_out_the_same_gate_as_the_static_entry_point()
    {
        await using var database = TestDatabase.Create();

        var services = new ServiceCollection();
        services.AddSqliteGate(database.Path);

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISqliteGate>()
            .Should().BeSameAs(
                SqliteGate.For(database.Path),
                "an app that injects the gate and a library that reaches for the static entry point " +
                "must end up in the same write queue");
    }

    [Fact]
    public async Task Keyed_registrations_resolve_the_gate_for_their_own_database()
    {
        await using var orders = TestDatabase.Create();
        await using var audit = TestDatabase.Create();

        var services = new ServiceCollection();
        services.AddKeyedSqliteGate("orders", orders.Path);
        services.AddKeyedSqliteGate("audit", audit.Path);

        await using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<ISqliteGate>("orders").DatabasePath.Should().Be(orders.Path);
        provider.GetRequiredKeyedService<ISqliteGate>("audit").DatabasePath.Should().Be(audit.Path);
        provider.GetRequiredKeyedService<ISqliteGate>("orders")
            .Should().NotBeSameAs(provider.GetRequiredKeyedService<ISqliteGate>("audit"));
    }

    [Fact]
    public async Task A_disposed_gate_is_replaced_rather_than_handed_back()
    {
        await using var database = TestDatabase.Create();

        var original = SqliteGate.For(database.Path);
        await original.DisposeAsync();

        var replacement = SqliteGate.For(database.Path);

        replacement.Should().NotBeSameAs(original, "a host shutting down must not leave the registry holding a closed gate");

        // And the replacement genuinely works.
        await replacement.WriteAsync((connection, token) => Sql.ExecuteAsync(connection, Sql.Schema, token));
        (await replacement.ReadAsync((connection, token) => Sql.ScalarAsync(connection, "SELECT COUNT(*) FROM Todos;", token)))
            .Should().Be(0);
    }

    [Fact]
    public async Task Configuration_from_the_first_caller_wins()
    {
        await using var database = TestDatabase.Create();

        var first = SqliteGate.For(database.Path, options => options.MaxRetryAttempts = 9);
        var second = SqliteGate.For(database.Path, options => options.MaxRetryAttempts = 1);

        second.Should().BeSameAs(first);
        second.Options.MaxRetryAttempts.Should().Be(9, "one database cannot have two sets of concurrency settings");
    }

    [Fact]
    public async Task Removing_a_gate_takes_it_out_of_the_registry()
    {
        await using var database = TestDatabase.Create();

        _ = SqliteGate.For(database.Path);
        SqliteGateRegistry.DatabasePaths.Should().Contain(database.Path);

        (await SqliteGateRegistry.RemoveAsync(database.Path)).Should().BeTrue();
        SqliteGateRegistry.DatabasePaths.Should().NotContain(database.Path);

        (await SqliteGateRegistry.RemoveAsync(database.Path)).Should().BeFalse("it is already gone");
    }
}
