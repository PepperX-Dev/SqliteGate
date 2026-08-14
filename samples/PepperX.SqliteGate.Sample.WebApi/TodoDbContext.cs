using Microsoft.EntityFrameworkCore;
using PepperX.SqliteGate.Dapper;

namespace PepperX.SqliteGate.Sample.WebApi;

/// <summary>A todo row, as EF Core sees it.</summary>
public sealed class Todo
{
    /// <summary>Primary key.</summary>
    public long Id { get; set; }

    /// <summary>What to do.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Which package wrote this row: <c>ado</c>, <c>dapper</c>, or <c>efcore</c>.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>A named counter, for the read-modify-write endpoint.</summary>
public sealed class Counter
{
    /// <summary>Primary key.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Current value.</summary>
    public long Value { get; set; }
}

/// <summary>The sample's EF Core context, mapped onto the same tables the other two packages use.</summary>
public class TodoDbContext : DbContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="options">Options configured with <c>UseSqliteGate</c>.</param>
    public TodoDbContext(DbContextOptions<TodoDbContext> options)
        : base(options)
    {
    }

    /// <summary>The todos.</summary>
    public DbSet<Todo> Todos => Set<Todo>();

    /// <summary>The counters.</summary>
    public DbSet<Counter> Counters => Set<Counter>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Todo>(entity =>
        {
            entity.ToTable("Todos");
            entity.HasKey(todo => todo.Id);
            entity.Property(todo => todo.Title).IsRequired();
            entity.Property(todo => todo.Source).IsRequired();
        });

        modelBuilder.Entity<Counter>(entity =>
        {
            entity.ToTable("Counters");
            entity.HasKey(counter => counter.Name);
        });
    }
}

/// <summary>Creates the sample schema at startup.</summary>
public static class SampleDatabase
{
    /// <summary>
    /// Builds the tables the sample needs, through the gate like any other write.
    /// </summary>
    /// <param name="services">The application's service provider.</param>
    /// <returns>A task that completes once the schema exists.</returns>
    public static async Task InitializeAsync(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetRequiredService<ISqliteGate>();

        await gate.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS Todos (
                Id     INTEGER PRIMARY KEY AUTOINCREMENT,
                Title  TEXT    NOT NULL,
                Source TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Counters (
                Name  TEXT    PRIMARY KEY,
                Value INTEGER NOT NULL
            );

            INSERT OR IGNORE INTO Counters (Name, Value) VALUES ('hits', 0);
            """);
    }
}
