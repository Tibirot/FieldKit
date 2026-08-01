using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FieldKit.Infrastructure;

/// <summary>
/// Stamps <see cref="ITenantOwned.TenantId"/> on insert and audit fields on insert/update, from the
/// ambient tenant context and the clock — so those are never written by hand (data &amp; persistence §5).
/// </summary>
public sealed class EntityStampingInterceptor(IClock clock, ITenantContext tenantContext)
    : SaveChangesInterceptor
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
        var now = clock.UtcNow;
        var actor = tenantContext.UserId;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry is { Entity: ITenantOwned owned, State: EntityState.Added } && owned.TenantId == default)
                owned.TenantId = tenantContext.TenantId;

            if (entry.Entity is not IAuditable auditable) continue;

            switch (entry.State)
            {
                case EntityState.Added:
                    auditable.CreatedAtUtc = now;
                    auditable.CreatedBy = actor;
                    break;
                case EntityState.Modified:
                    auditable.ModifiedAtUtc = now;
                    auditable.ModifiedBy = actor;
                    break;
            }
        }
    }
}
