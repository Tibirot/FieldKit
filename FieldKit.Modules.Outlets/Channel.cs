using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// A trade classification — Modern Trade, Traditional Trade, HoReCa (<c>OUT-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// Reference data with a stable id rather than a string on the outlet, because channel is the one
/// classification other modules make decisions with: it drives assortment, pricing, the visit
/// workflow and audit forms. A free-text channel makes those rules match on spelling, so
/// "HoReCa" and "Horeca" become two channels and one of them silently has no assortment.
/// </para>
/// <para>
/// Tenant-owned, because the vocabulary is a tenant's own: a distributor's channels are not a
/// brand's. Segment and banner stay plain strings on the outlet for now — nothing branches on them
/// yet, and promoting a string to reference data later is an additive migration plus a backfill.
/// </para>
/// </remarks>
public sealed class Channel : AggregateRoot, ITenantOwned, IAuditable, ISyncTracked
{
    public Guid Id { get; private set; }

    /// <summary>Unique within the tenant — two channels with one name are a data-entry accident.</summary>
    public string Name { get; private set; } = null!;

    /// <summary>Set by the row-version interceptor (ADR-0013). A deleted channel leaves a tombstone.</summary>
    public long RowVersion { get; set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Channel() { } // EF

    public static Channel Create(string name) => new() { Id = Guid.CreateVersion7(), Name = name };

    public void Rename(string name, IClock clock)
    {
        Name = name;
        ModifiedAtUtc = clock.UtcNow;
    }
}
