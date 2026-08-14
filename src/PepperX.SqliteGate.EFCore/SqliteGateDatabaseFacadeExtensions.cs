using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using PepperX.SqliteGate.EFCore.Internals;

namespace PepperX.SqliteGate.EFCore;

/// <summary>
/// Transactions that span several <c>SaveChanges</c> calls, held inside the write queue for their
/// whole lifetime.
/// </summary>
public static class SqliteGateDatabaseFacadeExtensions
{
    /// <summary>
    /// Takes the write slot and opens an EF Core transaction on top of it, keeping both until the
    /// returned object is disposed.
    /// </summary>
    /// <param name="database">The context's <see cref="DatabaseFacade"/>.</param>
    /// <param name="cancellationToken">Observed while queued for the write slot.</param>
    /// <returns>The transaction, which releases the write slot when disposed.</returns>
    /// <remarks>
    /// <para>
    /// Reach for this when several saves have to commit together. A plain
    /// <c>Database.BeginTransactionAsync()</c> would work, but the slot would then be taken and
    /// released around each individual <c>SaveChanges</c> while the SQLite write lock stayed held by
    /// the open transaction — so other writers would be let out of the queue only to meet a locked
    /// database and fall back on retrying. Holding the slot for the whole transaction keeps the
    /// queue honest.
    /// </para>
    /// <para>
    /// It is also what makes a read-modify-write safe: <c>SaveChanges</c> alone holds the slot for
    /// the save, but the read that decided the new value happened before that.
    /// </para>
    /// <para>
    /// While it is held, <c>SaveChangesAsync</c> on the same context will not try to take the slot
    /// again — that re-entrancy would deadlock, and is handled for you.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// await using var transaction = await db.Database.BeginGatedTransactionAsync();
    ///
    /// db.Orders.Add(order);
    /// await db.SaveChangesAsync();
    ///
    /// db.AuditEntries.Add(entry);
    /// await db.SaveChangesAsync();
    ///
    /// await transaction.CommitAsync();
    /// </code>
    /// </example>
    public static async Task<IGatedTransaction> BeginGatedTransactionAsync(
        this DatabaseFacade database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);

        var extension = database.GetService<IDbContextOptions>().FindExtension<SqliteGateOptionsExtension>()
            ?? throw new SqliteGateException("This DbContext was not configured with UseSqliteGate().");

        var context = database.GetService<ICurrentDbContext>().Context;

        if (extension.Leases.IsHeldBy(context))
        {
            throw new SqliteGateException(
                "This DbContext already holds the write slot. Gated transactions cannot be nested — " +
                "commit or dispose the outer one first.");
        }

        var lease = await extension.Gate.AcquireWriteLeaseAsync(cancellationToken).ConfigureAwait(false);
        extension.Leases.Register(context, lease, LeaseOwner.ExplicitTransaction);

        try
        {
            var transaction = await database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return new GatedTransaction(transaction, extension.Leases, context);
        }
        catch
        {
            // Never hold the process-wide write slot because opening a transaction failed.
            extension.Leases.Release(context, LeaseOwner.ExplicitTransaction);
            throw;
        }
    }

    private sealed class GatedTransaction(
        IDbContextTransaction transaction,
        WriteLeaseRegistry leases,
        DbContext context) : IGatedTransaction
    {
        public Guid TransactionId => transaction.TransactionId;

        public Task CommitAsync(CancellationToken cancellationToken = default) =>
            transaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken = default) =>
            transaction.RollbackAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Disposing an uncommitted transaction rolls it back, which must happen before the
                // slot is handed to the next writer.
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                leases.Release(context, LeaseOwner.ExplicitTransaction);
            }
        }
    }
}

/// <summary>
/// An EF Core transaction that also holds the gate's write slot for as long as it is open.
/// </summary>
public interface IGatedTransaction : IAsyncDisposable
{
    /// <summary>EF Core's identifier for the underlying transaction.</summary>
    Guid TransactionId { get; }

    /// <summary>Commits the transaction. The write slot is released when this object is disposed.</summary>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>A task that completes once the transaction has committed.</returns>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Rolls the transaction back. The write slot is released when this object is disposed.</summary>
    /// <param name="cancellationToken">Abort token.</param>
    /// <returns>A task that completes once the transaction has rolled back.</returns>
    Task RollbackAsync(CancellationToken cancellationToken = default);
}
