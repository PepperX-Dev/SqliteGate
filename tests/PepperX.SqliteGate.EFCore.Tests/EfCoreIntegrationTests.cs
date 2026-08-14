using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.EFCore;

namespace PepperX.SqliteGate.EFCore.Tests;

/// <summary>
/// The EF Core-specific promises: pooling still works, change tracking is untouched, schema changes
/// are gated, and reads are not.
/// </summary>
public sealed class EfCoreIntegrationTests
{
    [Fact]
    public async Task The_context_shares_the_gate_with_every_other_api()
    {
        await using var database = TestDatabase.Create();

        await using var context = EfCoreConcurrencyTests.CreateContext(database.Path);

        context.Database.GetSqliteGate()
            .Should().BeSameAs(
                SqliteGate.For(database.Path),
                "EF Core writes must queue behind the same slot as raw ADO.NET and Dapper writes, not beside it");
    }

    [Fact]
    public async Task Save_changes_waits_for_the_write_slot()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await using (var setup = EfCoreConcurrencyTests.CreateContext(database.Path))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Occupy the slot from the raw ADO.NET side.
        var heldWrite = gate.WriteAsync(async (_, _) =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        await using var context = EfCoreConcurrencyTests.CreateContext(database.Path);
        context.Todos.Add(new Todo { Title = "queued behind ADO.NET", Ordinal = 1 });
        var save = context.SaveChangesAsync();

        await Task.Delay(500);
        save.IsCompleted.Should().BeFalse("the save is queued behind a write started through a different API");

        release.SetResult();
        await heldWrite;
        await save;

        (await context.Todos.LongCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Queries_read_freely_while_a_write_is_in_flight()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await using (var setup = EfCoreConcurrencyTests.CreateContext(database.Path))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Todos.Add(new Todo { Title = "seed", Ordinal = 0 });
            await setup.SaveChangesAsync();
        }

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var heldWrite = gate.WriteAsync(async (_, _) =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        await using var context = EfCoreConcurrencyTests.CreateContext(database.Path);
        var todos = await context.Todos.ToListAsync();

        todos.Should().ContainSingle();
        gate.PendingWriters.Should().Be(1, "the query never joined the write queue");

        release.SetResult();
        await heldWrite;
    }

    [Fact]
    public async Task Ensure_created_goes_through_the_queue()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var heldWrite = gate.WriteAsync(async (_, _) =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        await using var context = EfCoreConcurrencyTests.CreateContext(database.Path);
        var create = context.Database.EnsureCreatedAsync();

        await Task.Delay(500);
        create.IsCompleted.Should().BeFalse("schema changes are writes and belong in the queue like any other");

        release.SetResult();
        await heldWrite;
        await create;

        (await context.Todos.LongCountAsync()).Should().Be(0, "the schema exists once the queue let it through");
    }

    [Fact]
    public async Task Migrations_go_through_the_queue()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var heldWrite = gate.WriteAsync(async (_, _) =>
        {
            holding.SetResult();
            await release.Task;
        });

        await holding.Task;

        await using var context = new MigratedDbContext(
            new DbContextOptionsBuilder<MigratedDbContext>().UseSqliteGate(database.Path).Options);

        var migrate = context.Database.MigrateAsync();

        await Task.Delay(500);

        // This is the assertion that caught the migration lock: EF Core takes it before running any
        // command, so gating only the command executor left it reaching SQLite outside the queue —
        // where it failed outright with SQLITE_BUSY rather than waiting.
        migrate.IsCompleted.Should().BeFalse("a migration alters the schema, which is the most disruptive write there is");

        release.SetResult();
        await heldWrite;
        await migrate;

        (await context.Widgets.LongCountAsync()).Should().Be(0, "the migration applied once it reached the front of the queue");
    }

    [Fact]
    public async Task Context_pooling_keeps_working_and_leaks_no_leases()
    {
        await using var database = TestDatabase.Create();

        var services = new ServiceCollection();
        services.AddDbContextPool<TodoDbContext>(options => options.UseSqliteGate(database.Path));

        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<TodoDbContext>().Database.EnsureCreatedAsync();
        }

        var failures = await ConcurrencyHarness.RunConcurrentlyAsync(
            100,
            async (ordinal, cancellationToken) =>
            {
                await using var scope = provider.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TodoDbContext>();

                context.Todos.Add(new Todo { Title = $"Pooled {ordinal}", Ordinal = ordinal });
                await context.SaveChangesAsync(cancellationToken);
            });

        failures.Should().BeEmpty(
            "pooled contexts are reused, so a leaked lease would show up here as a deadlock. Failures:{0}{1}",
            Environment.NewLine,
            ConcurrencyHarness.Describe(failures));

        await using var verification = provider.CreateAsyncScope();
        (await verification.ServiceProvider.GetRequiredService<TodoDbContext>().Todos.LongCountAsync()).Should().Be(100);

        SqliteGate.For(database.Path).PendingWriters.Should().Be(0, "every lease taken was given back");
    }

    [Fact]
    public async Task Change_tracking_still_behaves_normally()
    {
        await using var database = TestDatabase.Create();

        await using var context = EfCoreConcurrencyTests.CreateContext(database.Path);
        await context.Database.EnsureCreatedAsync();

        var todo = new Todo { Title = "tracked", Ordinal = 1 };
        context.Todos.Add(todo);

        context.Entry(todo).State.Should().Be(EntityState.Added);
        await context.SaveChangesAsync();
        context.Entry(todo).State.Should().Be(EntityState.Unchanged, "the interceptor sits above the change tracker");

        todo.Id.Should().BeGreaterThan(0, "the generated key was read back");

        todo.Title = "modified";
        context.Entry(todo).State.Should().Be(EntityState.Modified);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();

        // Re-read after clearing, and delete the instance the tracker now knows about — attaching the
        // stale one would be an identity conflict, and that would be this test's bug rather than the
        // library's.
        var reloaded = await context.Todos.SingleAsync();
        reloaded.Title.Should().Be("modified");

        context.Todos.Remove(reloaded);
        await context.SaveChangesAsync();
        (await context.Todos.LongCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_failed_save_releases_the_write_slot()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await using var context = EfCoreConcurrencyTests.CreateContext(database.Path);
        await context.Database.EnsureCreatedAsync();

        context.Counters.Add(new Counter { Name = "duplicate", Value = 1 });
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        context.Counters.Add(new Counter { Name = "duplicate", Value = 2 });

        var act = async () => await context.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();

        gate.PendingWriters.Should().Be(0, "a save that throws must still hand the slot back");

        // And the gate is still usable afterwards.
        context.ChangeTracker.Clear();
        context.Todos.Add(new Todo { Title = "after failure", Ordinal = 1 });
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task A_gated_transaction_holds_the_slot_across_several_saves()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await using var context = EfCoreConcurrencyTests.CreateContext(database.Path);
        await context.Database.EnsureCreatedAsync();

        await using (var transaction = await context.Database.BeginGatedTransactionAsync())
        {
            gate.PendingWriters.Should().Be(1, "the transaction holds the slot");

            context.Todos.Add(new Todo { Title = "first", Ordinal = 1 });
            await context.SaveChangesAsync();

            // The nested save must not try to take the slot a second time — that would deadlock on a
            // non-reentrant semaphore.
            gate.PendingWriters.Should().Be(1);

            context.Todos.Add(new Todo { Title = "second", Ordinal = 2 });
            await context.SaveChangesAsync();

            await transaction.CommitAsync();
        }

        gate.PendingWriters.Should().Be(0);
        (await context.Todos.LongCountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task An_abandoned_gated_transaction_rolls_back_and_frees_the_slot()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await using var context = EfCoreConcurrencyTests.CreateContext(database.Path);
        await context.Database.EnsureCreatedAsync();

        await using (var transaction = await context.Database.BeginGatedTransactionAsync())
        {
            context.Todos.Add(new Todo { Title = "never committed", Ordinal = 1 });
            await context.SaveChangesAsync();
        }

        gate.PendingWriters.Should().Be(0);

        context.ChangeTracker.Clear();
        (await context.Todos.LongCountAsync()).Should().Be(0, "no commit, no rows");
    }

    [Fact]
    public async Task Nesting_gated_transactions_is_refused_rather_than_deadlocking()
    {
        await using var database = TestDatabase.Create();

        await using var context = EfCoreConcurrencyTests.CreateContext(database.Path);
        await context.Database.EnsureCreatedAsync();

        await using var outer = await context.Database.BeginGatedTransactionAsync();

        var act = async () => await context.Database.BeginGatedTransactionAsync();
        await act.Should().ThrowAsync<SqliteGateException>();
    }

    [Fact]
    public async Task Bulk_operations_can_be_wrapped_in_a_gated_transaction()
    {
        await using var database = TestDatabase.Create();
        var gate = SqliteGate.For(database.Path);

        await using var context = EfCoreConcurrencyTests.CreateContext(database.Path);
        await context.Database.EnsureCreatedAsync();

        context.Todos.AddRange(
            new Todo { Title = "keep", Ordinal = 1 },
            new Todo { Title = "drop", Ordinal = 2 },
            new Todo { Title = "drop", Ordinal = 3 });
        await context.SaveChangesAsync();

        // ExecuteDelete bypasses the change tracker, so no save interceptor sees it. This is the
        // documented way to put it back in the queue.
        await using (var transaction = await context.Database.BeginGatedTransactionAsync())
        {
            gate.PendingWriters.Should().Be(1);
            await context.Todos.Where(todo => todo.Title == "drop").ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }

        gate.PendingWriters.Should().Be(0);

        context.ChangeTracker.Clear();
        (await context.Todos.LongCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_connection_pragmas_reach_ef_cores_own_connections()
    {
        await using var database = TestDatabase.Create();

        await using var context = EfCoreConcurrencyTests.CreateContext(database.Path);
        await context.Database.EnsureCreatedAsync();

        var journalMode = await ScalarAsync(context, "PRAGMA journal_mode;");
        var foreignKeys = await ScalarAsync(context, "PRAGMA foreign_keys;");
        var synchronous = await ScalarAsync(context, "PRAGMA synchronous;");

        journalMode.Should().Be("wal");
        foreignKeys.Should().Be("1", "EF Core opens its own connections, which reset pragmas to SQLite's defaults");
        synchronous.Should().Be("1");
    }

    private static async Task<string> ScalarAsync(DbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await context.Database.OpenConnectionAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())?.ToString()?.ToLowerInvariant() ?? string.Empty;
    }
}
