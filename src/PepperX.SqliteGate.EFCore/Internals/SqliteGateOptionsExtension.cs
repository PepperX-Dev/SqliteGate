using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace PepperX.SqliteGate.EFCore.Internals;

/// <summary>
/// Carries the gate — and the lease bookkeeping that goes with it — inside the
/// <c>DbContextOptions</c>, where EF Core's own services can reach it.
/// </summary>
/// <remarks>
/// An options extension rather than a captured variable, for two reasons. It is the only way the
/// decorated migration services can find the gate, since EF Core constructs those from its internal
/// service provider. And it lets <see cref="DbContextOptionsExtensionInfo.GetServiceProviderHashCode"/>
/// account for which database is in play, so two contexts pointed at different files do not share
/// one cached internal service provider and end up writing through each other's queue.
/// </remarks>
internal sealed class SqliteGateOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;

    internal SqliteGateOptionsExtension(ISqliteGate gate) => Gate = gate;

    /// <summary>The gate this context writes through.</summary>
    internal ISqliteGate Gate { get; }

    /// <summary>Shared record of which contexts currently hold the write slot.</summary>
    internal WriteLeaseRegistry Leases => WriteLeaseRegistry.For(Gate);

    /// <inheritdoc />
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);

    /// <inheritdoc />
    public void ApplyServices(IServiceCollection services)
    {
        var gate = Gate;

        // Two seams, because schema changes arrive by two routes. Migrations come through IMigrator,
        // which is far enough out to cover the migration lock EF Core takes before running anything;
        // EnsureCreated never touches a migrator and reaches the database through the command
        // executor instead.
        ServiceDecorator.Decorate<IMigrator>(
            services,
            (provider, inner) => new SqliteGateMigrator(inner, gate, provider.GetRequiredService<ICurrentDbContext>()));

        ServiceDecorator.Decorate<IMigrationCommandExecutor>(
            services,
            (provider, inner) => new SqliteGateMigrationCommandExecutor(inner, gate, provider.GetRequiredService<ICurrentDbContext>()));
    }

    /// <inheritdoc />
    public void Validate(IDbContextOptions options)
    {
        // Nothing to reject. The gate resolves and validates its own options when it is created,
        // long before a DbContext is built.
    }

    private sealed class ExtensionInfo(SqliteGateOptionsExtension extension)
        : DbContextOptionsExtensionInfo(extension)
    {
        private new SqliteGateOptionsExtension Extension => (SqliteGateOptionsExtension)base.Extension;

        public override bool IsDatabaseProvider => false;

        public override string LogFragment => $"using SqliteGate ({Extension.Gate.DatabasePath}) ";

        public override int GetServiceProviderHashCode() => Extension.Gate.GetHashCode();

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) =>
            other is ExtensionInfo info && ReferenceEquals(info.Extension.Gate, Extension.Gate);

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo)
        {
            ArgumentNullException.ThrowIfNull(debugInfo);

            debugInfo["SqliteGate:DatabasePath"] = Extension.Gate.DatabasePath;
            debugInfo["SqliteGate:BusyTimeout"] = Extension.Gate.Options.BusyTimeout.ToString();
            debugInfo["SqliteGate:MaxRetryAttempts"] = Extension.Gate.Options.MaxRetryAttempts.ToString();
        }
    }
}
