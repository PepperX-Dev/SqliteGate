using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace PepperX.SqliteGate.DependencyInjection;

/// <summary>
/// Registers gates with the dependency injection container.
/// </summary>
public static class SqliteGateServiceCollectionExtensions
{
    /// <summary>
    /// Registers the gate for <paramref name="databasePathOrConnectionString"/> as a singleton
    /// <see cref="ISqliteGate"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="databasePathOrConnectionString">File path or <c>Microsoft.Data.Sqlite</c> connection string.</param>
    /// <param name="configure">Applied only if this registration is what creates the gate.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The registration resolves through <see cref="SqliteGateRegistry"/>, so the instance the
    /// container hands out is the very same object <see cref="SqliteGate.For(string, Action{SqliteGateOptions}?)"/>
    /// returns for that file. Code that reaches for the static entry point and code that takes a
    /// constructor dependency end up in the same write queue, which is the whole point.
    /// </para>
    /// <para>
    /// The container disposes the gate when the host shuts down. If some other part of the process
    /// asks for the same database afterwards, the registry notices the tombstone and builds a fresh
    /// gate rather than handing back a closed one.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSqliteGate(
        this IServiceCollection services,
        string databasePathOrConnectionString,
        Action<SqliteGateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePathOrConnectionString);

        services.TryAddSingleton<ISqliteGate>(provider =>
            SqliteGateRegistry.For(
                databasePathOrConnectionString,
                options => ApplyConfiguration(options, provider, configure)));

        return services;
    }

    /// <summary>
    /// Registers the gate for <paramref name="databasePathOrConnectionString"/> under a service key,
    /// for applications that talk to more than one SQLite database.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="serviceKey">Key to resolve the gate with, for example via <c>[FromKeyedServices]</c>.</param>
    /// <param name="databasePathOrConnectionString">File path or <c>Microsoft.Data.Sqlite</c> connection string.</param>
    /// <param name="configure">Applied only if this registration is what creates the gate.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddKeyedSqliteGate(
        this IServiceCollection services,
        object serviceKey,
        string databasePathOrConnectionString,
        Action<SqliteGateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(serviceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePathOrConnectionString);

        services.TryAddKeyedSingleton<ISqliteGate>(serviceKey, (provider, _) =>
            SqliteGateRegistry.For(
                databasePathOrConnectionString,
                options => ApplyConfiguration(options, provider, configure)));

        return services;
    }

    private static void ApplyConfiguration(
        SqliteGateOptions options,
        IServiceProvider provider,
        Action<SqliteGateOptions>? configure)
    {
        // Take the host's logging by default so queue waits and retries show up in the application's
        // own logs without anyone having to wire it, while still letting configure() override it.
        options.LoggerFactory = provider.GetService<ILoggerFactory>();
        configure?.Invoke(options);
    }
}
