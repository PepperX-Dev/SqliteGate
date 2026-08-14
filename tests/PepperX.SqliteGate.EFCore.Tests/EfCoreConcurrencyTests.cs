using Microsoft.EntityFrameworkCore;
using PepperX.SqliteGate.Conformance;
using PepperX.SqliteGate.EFCore;

namespace PepperX.SqliteGate.EFCore.Tests;

/// <summary>
/// Runs the shared concurrency suite through EF Core. Same guarantee as the ADO.NET and Dapper
/// suites, reached through a change tracker and <c>SaveChangesAsync</c>.
/// </summary>
/// <remarks>
/// A context per operation, which is how a web application behaves: one scope, one context, one
/// unit of work.
/// </remarks>
public sealed class EfCoreConcurrencyTests : ConcurrencyConformanceTests
{
    internal static TodoDbContext CreateContext(string databasePath) =>
        new(new DbContextOptionsBuilder<TodoDbContext>().UseSqliteGate(databasePath).Options);

    /// <inheritdoc />
    protected override async Task CreateSchemaAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(databasePath);

        // EnsureCreated goes through the gate as well: it reaches the database through the same
        // IMigrationCommandExecutor that migrations do.
        await context.Database.EnsureCreatedAsync(cancellationToken);

        context.Counters.Add(new Counter { Name = "hits", Value = 0 });
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task InsertTodoAsync(string databasePath, int ordinal, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(databasePath);

        context.Todos.Add(new Todo { Title = $"Todo {ordinal}", Ordinal = ordinal });
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task<long> CountTodosAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(databasePath);
        return await context.Todos.LongCountAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task<long> SumOrdinalsAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(databasePath);
        return await context.Todos.SumAsync(todo => todo.Ordinal, cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task IncrementCounterAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(databasePath);

        // A gated transaction, not a bare SaveChanges, and the distinction matters. SaveChanges
        // takes the write slot for the duration of the save — but the read that decides the new
        // value happens before that, so two contexts could both read 5 and both save 6. Holding the
        // slot across the read as well is what makes the increment safe, and it is the same shape as
        // the ADO.NET and Dapper suites, which do their read inside the gated write too.
        await using var transaction = await context.Database.BeginGatedTransactionAsync(cancellationToken);

        var counter = await context.Counters.SingleAsync(entity => entity.Name == "hits", cancellationToken);
        counter.Value += 1;

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task<long> ReadCounterAsync(string databasePath, CancellationToken cancellationToken)
    {
        await using var context = CreateContext(databasePath);
        return await context.Counters.Where(entity => entity.Name == "hits").Select(entity => entity.Value).SingleAsync(cancellationToken);
    }
}
