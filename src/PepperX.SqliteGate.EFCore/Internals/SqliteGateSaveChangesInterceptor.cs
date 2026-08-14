using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace PepperX.SqliteGate.EFCore.Internals;

/// <summary>
/// Puts <c>SaveChanges</c> in the write queue.
/// </summary>
/// <remarks>
/// <para>
/// The write slot is taken before EF Core sends anything and released once the save has finished,
/// one way or another. Interception happens above the change tracker, so nothing here touches
/// tracked state, and the interceptor holds no per-context state of its own — which is what keeps it
/// safe to share across a pooled <see cref="DbContext"/>.
/// </para>
/// <para>
/// Both the asynchronous and synchronous paths are implemented. Leaving the synchronous one out
/// would be tidier, and would mean a single <c>SaveChanges()</c> call anywhere in the application
/// bypassing the queue entirely — the guarantee has to hold for the whole API surface or it is not a
/// guarantee. The synchronous path takes the slot with a genuine blocking wait rather than by
/// waiting on the asynchronous one inline.
/// </para>
/// </remarks>
internal sealed class SqliteGateSaveChangesInterceptor : ISaveChangesInterceptor
{
    private readonly ISqliteGate _gate;
    private readonly WriteLeaseRegistry _leases;

    internal SqliteGateSaveChangesInterceptor(ISqliteGate gate, WriteLeaseRegistry leases)
    {
        _gate = gate;
        _leases = leases;
    }

    /// <inheritdoc />
    public InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context && !_leases.IsHeldBy(context))
        {
            _leases.Register(context, _gate.AcquireWriteLease(), LeaseOwner.SaveChanges);
        }

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is { } context && !_leases.IsHeldBy(context))
        {
            var lease = await _gate.AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
            _leases.Register(context, lease, LeaseOwner.SaveChanges);
        }

        return result;
    }

    /// <inheritdoc />
    public int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ReleaseFor(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ReleaseFor(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ReleaseFor(eventData.Context);
    }

    /// <inheritdoc />
    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ReleaseFor(eventData.Context);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void SaveChangesCanceled(DbContextEventData eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ReleaseFor(eventData.Context);
    }

    /// <inheritdoc />
    public Task SaveChangesCanceledAsync(DbContextEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        ReleaseFor(eventData.Context);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Every way a save can end — committed, thrown, cancelled — funnels through here. A path that
    /// forgot to release would hold the process-wide write slot forever.
    /// </summary>
    private void ReleaseFor(DbContext? context)
    {
        if (context is not null)
        {
            _leases.Release(context, LeaseOwner.SaveChanges);
        }
    }
}
