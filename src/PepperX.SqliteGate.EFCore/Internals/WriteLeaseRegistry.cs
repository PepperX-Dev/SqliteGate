using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace PepperX.SqliteGate.EFCore.Internals;

/// <summary>
/// Remembers which <see cref="DbContext"/> instances currently hold the write slot, and who took it.
/// </summary>
/// <remarks>
/// <para>
/// The write slot is a <c>SemaphoreSlim(1, 1)</c>, and semaphores are not re-entrant. That makes
/// this bookkeeping load-bearing rather than tidy: if a caller opens a gated transaction (holding
/// the slot) and then calls <c>SaveChangesAsync</c>, an interceptor that reached for the slot again
/// would wait for a lease it is already holding, and the application would deadlock on its own
/// first write.
/// </para>
/// <para>
/// So every component that can take the slot asks here first, and only the component that actually
/// took it ever gives it back.
/// </para>
/// </remarks>
internal sealed class WriteLeaseRegistry
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISqliteGate, WriteLeaseRegistry> Registries = [];

    private readonly ConcurrentDictionary<DbContext, Entry> _entries = [];

    /// <summary>
    /// The registry for a gate. Keyed on the gate rather than created per <c>UseSqliteGate</c> call,
    /// because EF Core caches one internal service provider per gate and shares it between every
    /// context configured against that database — components resolved from that cache have to see
    /// the same bookkeeping as the interceptors attached to the options, or a gated transaction
    /// opened through one would be invisible to the other.
    /// </summary>
    /// <param name="gate">The gate whose leases are being tracked.</param>
    /// <returns>The registry for that gate.</returns>
    internal static WriteLeaseRegistry For(ISqliteGate gate) =>
        Registries.GetValue(gate, static _ => new WriteLeaseRegistry());

    /// <summary>Whether this context already holds the write slot, by any route.</summary>
    internal bool IsHeldBy(DbContext context) => _entries.ContainsKey(context);

    /// <summary>Records a lease taken for a context.</summary>
    /// <param name="context">The context that now holds the slot.</param>
    /// <param name="lease">The lease itself.</param>
    /// <param name="owner">Which component took it, so only that component releases it.</param>
    internal void Register(DbContext context, ISqliteWriteLease lease, LeaseOwner owner) =>
        _entries[context] = new Entry(lease, owner);

    /// <summary>
    /// Releases the lease for a context, but only when the caller is the component that took it.
    /// </summary>
    /// <param name="context">The context to release.</param>
    /// <param name="owner">The component asking to release.</param>
    internal void Release(DbContext context, LeaseOwner owner)
    {
        if (!_entries.TryGetValue(context, out var entry) || entry.Owner != owner)
        {
            return;
        }

        if (_entries.TryRemove(new KeyValuePair<DbContext, Entry>(context, entry)))
        {
            entry.Lease.Dispose();
        }
    }

    private readonly record struct Entry(ISqliteWriteLease Lease, LeaseOwner Owner);
}

/// <summary>Which part of the adapter took the write slot.</summary>
internal enum LeaseOwner
{
    /// <summary>Taken by the <c>SaveChanges</c> interceptor, for the duration of one save.</summary>
    SaveChanges,

    /// <summary>Taken by an explicit gated transaction, for as long as the caller holds it.</summary>
    ExplicitTransaction,

    /// <summary>Taken by the migration pipeline, for the duration of the schema change.</summary>
    Migration,
}
