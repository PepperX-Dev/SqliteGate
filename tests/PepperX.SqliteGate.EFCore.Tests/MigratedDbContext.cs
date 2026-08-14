using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PepperX.SqliteGate.EFCore.Tests;

/// <summary>A widget, so the migration has something to create.</summary>
public sealed class Widget
{
    /// <summary>Primary key.</summary>
    public long Id { get; set; }

    /// <summary>Free text.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// A context with a real migration attached, so <c>Database.Migrate()</c> can be exercised rather
/// than only <c>EnsureCreated()</c>.
/// </summary>
public class MigratedDbContext : DbContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="options">Configured options, normally from <c>UseSqliteGate</c>.</param>
    public MigratedDbContext(DbContextOptions<MigratedDbContext> options)
        : base(options)
    {
    }

    /// <summary>The widgets.</summary>
    public DbSet<Widget> Widgets => Set<Widget>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Widget>(entity =>
        {
            entity.ToTable("Widgets");
            entity.HasKey(widget => widget.Id);
            entity.Property(widget => widget.Name).IsRequired();
        });
    }
}

/// <summary>
/// Hand-written rather than scaffolded, because the test only needs one migration to exist and
/// checking in a model snapshot for it would be noise.
/// </summary>
[DbContext(typeof(MigratedDbContext))]
[Migration("20260101000000_CreateWidgets")]
public sealed class CreateWidgets : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.CreateTable(
            name: "Widgets",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_Widgets", widget => widget.Id));
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.DropTable("Widgets");
    }
}
