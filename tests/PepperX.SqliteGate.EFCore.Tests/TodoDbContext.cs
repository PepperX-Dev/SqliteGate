using Microsoft.EntityFrameworkCore;

namespace PepperX.SqliteGate.EFCore.Tests;

/// <summary>A todo, mapped onto the shared conformance schema.</summary>
public sealed class Todo
{
    /// <summary>Primary key.</summary>
    public long Id { get; set; }

    /// <summary>Free text.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The value the concurrency suite checks the arithmetic of.</summary>
    public long Ordinal { get; set; }
}

/// <summary>A named counter, used for the read-modify-write scenario.</summary>
public sealed class Counter
{
    /// <summary>Primary key.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Current value.</summary>
    public long Value { get; set; }
}

/// <summary>The context the EF Core suites run against.</summary>
public class TodoDbContext : DbContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="options">Configured options, normally from <c>UseSqliteGate</c>.</param>
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
        });

        modelBuilder.Entity<Counter>(entity =>
        {
            entity.ToTable("Counters");
            entity.HasKey(counter => counter.Name);
        });
    }
}
