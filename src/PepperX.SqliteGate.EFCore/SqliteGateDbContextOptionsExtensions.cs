using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PepperX.SqliteGate.EFCore.Internals;

namespace PepperX.SqliteGate.EFCore;

/// <summary>
/// Wires EF Core's SQLite provider to the gate.
/// </summary>
public static class SqliteGateDbContextOptionsExtensions
{
    /// <summary>
    /// Configures the context to use SQLite through the gate for <paramref name="databasePathOrConnectionString"/>.
    /// </summary>
    /// <param name="optionsBuilder">The options builder being configured.</param>
    /// <param name="databasePathOrConnectionString">File path or <c>Microsoft.Data.Sqlite</c> connection string.</param>
    /// <param name="configureGate">
    /// Applied only if this call is what creates the gate for that file. A gate that already exists —
    /// because the ADO.NET or Dapper API reached it first — is used as it stands.
    /// </param>
    /// <param name="sqliteOptionsAction">Optional EF Core SQLite provider configuration.</param>
    /// <returns>The same options builder, for chaining.</returns>
    /// <example>
    /// <code>
    /// services.AddDbContext&lt;AppDbContext&gt;(options =&gt;
    ///     options.UseSqliteGate("app.db", gate =&gt; gate.MaxRetryAttempts = 5));
    /// </code>
    /// </example>
    public static DbContextOptionsBuilder UseSqliteGate(
        this DbContextOptionsBuilder optionsBuilder,
        string databasePathOrConnectionString,
        Action<SqliteGateOptions>? configureGate = null,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePathOrConnectionString);

        return optionsBuilder.UseSqliteGate(
            SqliteGate.For(databasePathOrConnectionString, configureGate),
            sqliteOptionsAction);
    }

    /// <summary>
    /// Configures the context to use an existing gate — the overload to reach for when the gate came
    /// from dependency injection and is already shared with the rest of the application.
    /// </summary>
    /// <param name="optionsBuilder">The options builder being configured.</param>
    /// <param name="gate">The gate to write through.</param>
    /// <param name="sqliteOptionsAction">Optional EF Core SQLite provider configuration.</param>
    /// <returns>The same options builder, for chaining.</returns>
    public static DbContextOptionsBuilder UseSqliteGate(
        this DbContextOptionsBuilder optionsBuilder,
        ISqliteGate gate,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        ArgumentNullException.ThrowIfNull(gate);

        // EF Core keeps its own connections; handing it the gate's connection string is what makes
        // its pragmas, its command timeout and its file all match the rest of the application.
        optionsBuilder.UseSqlite(gate.ConnectionString, sqliteOptionsAction);

        optionsBuilder.AddInterceptors(
            new SqliteGateSaveChangesInterceptor(gate, WriteLeaseRegistry.For(gate)),
            new SqliteGateConnectionInterceptor(gate));

        // Schema changes bypass SaveChanges entirely; the extension's ApplyServices puts them back
        // in the queue by decorating EF Core's own migration services.
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder)
            .AddOrUpdateExtension(new SqliteGateOptionsExtension(gate));

        return optionsBuilder;
    }

    /// <summary>
    /// Strongly-typed counterpart of
    /// <see cref="UseSqliteGate(DbContextOptionsBuilder, string, Action{SqliteGateOptions}?, Action{SqliteDbContextOptionsBuilder}?)"/>.
    /// </summary>
    /// <typeparam name="TContext">The context type.</typeparam>
    /// <param name="optionsBuilder">The options builder being configured.</param>
    /// <param name="databasePathOrConnectionString">File path or <c>Microsoft.Data.Sqlite</c> connection string.</param>
    /// <param name="configureGate">Applied only if this call is what creates the gate.</param>
    /// <param name="sqliteOptionsAction">Optional EF Core SQLite provider configuration.</param>
    /// <returns>The same options builder, for chaining.</returns>
    public static DbContextOptionsBuilder<TContext> UseSqliteGate<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder,
        string databasePathOrConnectionString,
        Action<SqliteGateOptions>? configureGate = null,
        Action<SqliteDbContextOptionsBuilder>? sqliteOptionsAction = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        UseSqliteGate((DbContextOptionsBuilder)optionsBuilder, databasePathOrConnectionString, configureGate, sqliteOptionsAction);
        return optionsBuilder;
    }

    /// <summary>
    /// Returns the gate a configured context writes through.
    /// </summary>
    /// <param name="database">The context's <see cref="DatabaseFacade"/>.</param>
    /// <returns>The gate.</returns>
    /// <exception cref="SqliteGateException">The context was not configured with <c>UseSqliteGate</c>.</exception>
    public static ISqliteGate GetSqliteGate(this DatabaseFacade database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var extension = database.GetService<IDbContextOptions>().FindExtension<SqliteGateOptionsExtension>()
            ?? throw new SqliteGateException("This DbContext was not configured with UseSqliteGate().");

        return extension.Gate;
    }
}
