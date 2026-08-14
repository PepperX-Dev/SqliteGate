using System.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace PepperX.SqliteGate.EFCore.Internals;

/// <summary>
/// Puts schema changes that never reach a migrator into the write queue — <c>EnsureCreated()</c>,
/// principally.
/// </summary>
/// <remarks>
/// <para>
/// A schema change is a write, usually the most disruptive one an application makes, and it does not
/// go through <c>SaveChanges</c>, so the save interceptor never sees it.
/// <c>Database.EnsureCreated()</c> reaches the database here, by way of
/// <c>RelationalDatabaseCreator.CreateTables</c>.
/// </para>
/// <para>
/// <c>Database.Migrate()</c> also passes through this type, but it will already be holding the write
/// slot by then: <see cref="SqliteGateMigrator"/> takes it further out, where it can also cover the
/// migration lock EF Core acquires before any command runs. The re-entrancy check below is what lets
/// the two compose instead of deadlocking on each other.
/// </para>
/// </remarks>
internal sealed class SqliteGateMigrationCommandExecutor : IMigrationCommandExecutor
{
    private readonly IMigrationCommandExecutor _inner;
    private readonly ISqliteGate _gate;
    private readonly WriteLeaseRegistry _leases;
    private readonly ICurrentDbContext _currentContext;

    internal SqliteGateMigrationCommandExecutor(
        IMigrationCommandExecutor inner,
        ISqliteGate gate,
        ICurrentDbContext currentContext)
    {
        _inner = inner;
        _gate = gate;
        _currentContext = currentContext;
        _leases = WriteLeaseRegistry.For(gate);
    }

    /// <inheritdoc />
    public void ExecuteNonQuery(IEnumerable<MigrationCommand> migrationCommands, IRelationalConnection connection)
    {
        using var hold = AcquireHold();
        _inner.ExecuteNonQuery(migrationCommands, connection);
    }

    /// <inheritdoc />
    public int ExecuteNonQuery(
        IReadOnlyList<MigrationCommand> migrationCommands,
        IRelationalConnection connection,
        MigrationExecutionState executionState,
        bool commitTransaction,
        IsolationLevel? isolationLevel = null)
    {
        using var hold = AcquireHold();
        return _inner.ExecuteNonQuery(migrationCommands, connection, executionState, commitTransaction, isolationLevel);
    }

    /// <inheritdoc />
    public async Task ExecuteNonQueryAsync(
        IEnumerable<MigrationCommand> migrationCommands,
        IRelationalConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var hold = await AcquireHoldAsync(cancellationToken).ConfigureAwait(false);
        await _inner.ExecuteNonQueryAsync(migrationCommands, connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> ExecuteNonQueryAsync(
        IReadOnlyList<MigrationCommand> migrationCommands,
        IRelationalConnection connection,
        MigrationExecutionState executionState,
        bool commitTransaction,
        IsolationLevel? isolationLevel = null,
        CancellationToken cancellationToken = default)
    {
        await using var hold = await AcquireHoldAsync(cancellationToken).ConfigureAwait(false);
        return await _inner
            .ExecuteNonQueryAsync(migrationCommands, connection, executionState, commitTransaction, isolationLevel, cancellationToken)
            .ConfigureAwait(false);
    }

    private LeaseHold AcquireHold()
    {
        var context = _currentContext.Context;

        if (_leases.IsHeldBy(context))
        {
            return new LeaseHold(null, context);
        }

        _leases.Register(context, _gate.AcquireWriteLease(), LeaseOwner.Migration);
        return new LeaseHold(_leases, context);
    }

    private async ValueTask<LeaseHold> AcquireHoldAsync(CancellationToken cancellationToken)
    {
        var context = _currentContext.Context;

        if (_leases.IsHeldBy(context))
        {
            return new LeaseHold(null, context);
        }

        var lease = await _gate.AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        _leases.Register(context, lease, LeaseOwner.Migration);
        return new LeaseHold(_leases, context);
    }

    /// <summary>
    /// Releases the write slot only when this hold is the one that took it, so a nested call leaves
    /// the outer holder in charge.
    /// </summary>
    private readonly struct LeaseHold(WriteLeaseRegistry? owned, Microsoft.EntityFrameworkCore.DbContext context)
        : IDisposable, IAsyncDisposable
    {
        public void Dispose() => owned?.Release(context, LeaseOwner.Migration);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
