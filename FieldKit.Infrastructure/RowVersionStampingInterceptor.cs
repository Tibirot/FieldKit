using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FieldKit.Infrastructure;

/// <summary>
/// Stamps <see cref="ISyncTracked.RowVersion"/> on everything saved in a transaction, from this
/// module's per-tenant counter (ADR-0013).
/// </summary>
/// <remarks>
/// <para>
/// The counter is read and written <b>through the change tracker</b>, so it commits in the same
/// transaction as the rows it numbers. That is the whole mechanism: the counter's UPDATE carries a
/// concurrency token, so a second transaction racing for the next number fails instead of reusing
/// it, and a rollback takes the number back rather than burning it.
/// </para>
/// <para>
/// The alternative — issuing <c>nextval()</c> from a sequence — allocates outside the transaction
/// and cannot be undone, so a slower transaction with a lower number can commit after a device has
/// already moved its watermark past it. The row is then skipped for good. ADR-0013 has the full
/// argument; the short version is that this interceptor exists because sequences order allocation
/// and delta sync needs commit order.
/// </para>
/// <para>
/// <b>One version per transaction, not per row.</b> Fifty visits pushed together share a number, and
/// the counter reads as "the Nth committed change set for this tenant".
/// </para>
/// </remarks>
public sealed class RowVersionStampingInterceptor(ITenantContext tenantContext) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext context)
    {
        var pending = context.ChangeTracker.Entries<ISyncTracked>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .ToList();

        // Nothing syncable changed: do not touch the counter. A save that only writes, say, an
        // outbox row must not consume a version, or the feed grows numbers that name no change.
        if (pending.Count == 0) return;

        var tenantId = tenantContext.TenantId;
        var sequence = NextVersion(context, tenantId);

        foreach (var entry in pending)
            entry.Entity.RowVersion = sequence;
    }

    private static long NextVersion(DbContext context, TenantId tenantId)
    {
        // Tracked-first, so several saves on one context stay consistent, and a synchronous read
        // only happens the first time a tenant is seen on this context.
        var sequence = context.ChangeTracker.Entries<ChangeSequence>()
            .Select(entry => entry.Entity)
            .FirstOrDefault(candidate => candidate.TenantId == tenantId);

        if (sequence is null)
        {
            sequence = context.Set<ChangeSequence>()
                .FirstOrDefault(candidate => candidate.TenantId == tenantId);

            if (sequence is null)
            {
                // First change this tenant has ever made in this module. Versions start at 1, so a
                // device's initial cursor of 0 is below every real change rather than equal to one.
                sequence = new ChangeSequence { TenantId = tenantId, Value = 0 };
                context.Add(sequence);
            }
        }

        return ++sequence.Value;
    }
}
