using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PepperX.SqliteGate.EFCore.Internals;

/// <summary>
/// Holds the write slot for the whole of <c>Database.Migrate()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Gating the commands a migration runs is not enough, and the difference is easy to miss. Before
/// EF Core executes anything it takes a <em>migration lock</em> on the database through the history
/// repository, so that two processes cannot migrate at once. That happens above
/// <see cref="IMigrationCommandExecutor"/>, which means it reaches SQLite outside the queue — and
/// against a database another writer is holding, it fails immediately with <c>SQLITE_BUSY</c>
/// rather than waiting its turn.
/// </para>
/// <para>
/// <see cref="IMigrator"/> is the outermost seam, so taking the slot here covers the lock, the
/// history table, and every migration command as one unit. The command-executor decorator stays in
/// place for <c>EnsureCreated()</c>, which never goes through a migrator; when both are involved the
/// inner one sees the slot is already held and leaves it alone.
/// </para>
/// </remarks>
internal sealed class SqliteGateMigrator : IMigrator
{
    private readonly IMigrator _inner;
    private readonly ISqliteGate _gate;
    private readonly WriteLeaseRegistry _leases;
    private readonly ICurrentDbContext _currentContext;

    internal SqliteGateMigrator(IMigrator inner, ISqliteGate gate, ICurrentDbContext currentContext)
    {
        _inner = inner;
        _gate = gate;
        _currentContext = currentContext;
        _leases = WriteLeaseRegistry.For(gate);
    }

    /// <inheritdoc />
    public void Migrate(string? targetMigration = null)
    {
        var context = _currentContext.Context;

        if (_leases.IsHeldBy(context))
        {
            _inner.Migrate(targetMigration);
            return;
        }

        _leases.Register(context, _gate.AcquireWriteLease(), LeaseOwner.Migration);

        try
        {
            _inner.Migrate(targetMigration);
        }
        finally
        {
            _leases.Release(context, LeaseOwner.Migration);
        }
    }

    /// <inheritdoc />
    public async Task MigrateAsync(string? targetMigration = null, CancellationToken cancellationToken = default)
    {
        var context = _currentContext.Context;

        if (_leases.IsHeldBy(context))
        {
            await _inner.MigrateAsync(targetMigration, cancellationToken).ConfigureAwait(false);
            return;
        }

        var lease = await _gate.AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        _leases.Register(context, lease, LeaseOwner.Migration);

        try
        {
            await _inner.MigrateAsync(targetMigration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _leases.Release(context, LeaseOwner.Migration);
        }
    }

    /// <inheritdoc />
    public string GenerateScript(
        string? fromMigration = null,
        string? toMigration = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
        // Generating a script produces text and touches nothing, so it stays out of the queue.
        => _inner.GenerateScript(fromMigration, toMigration, options);

    /// <inheritdoc />
    public bool HasPendingModelChanges() => _inner.HasPendingModelChanges();
}
