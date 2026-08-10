using FieldKit.SharedKernel;

namespace FieldKit.BuildingBlocks;

/// <summary>
/// Marks an entity as belonging to a tenant. The global query filter and the stamping interceptor
/// use <see cref="TenantId"/> to enforce isolation automatically (ADR-0008) — a developer never
/// writes the tenant predicate by hand.
/// </summary>
public interface ITenantOwned
{
    TenantId TenantId { get; set; }
}

/// <summary>
/// Marks an entity a device can hold a copy of, and whose changes a delta pull must order.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RowVersion"/> is stamped by the row-version interceptor, never assigned by hand — it
/// is allocated once per transaction from the module's per-tenant counter, so every entity saved
/// together shares one value (ADR-0013).
/// </para>
/// <para>
/// The property a device's correctness rests on: <b>once a version N has been served, nothing with a
/// version ≤ N may become visible afterwards.</b> A row that breaks it is skipped permanently rather
/// than late, because a watermark exists precisely so that what is already known is never re-read.
/// ADR-0013 explains why that rules out a Postgres sequence.
/// </para>
/// <para>
/// Marking an entity with this does not put it on the wire. What a device receives is decided by the
/// sync module's change feed and the device's scope; this only makes the ordering available.
/// </para>
/// </remarks>
public interface ISyncTracked
{
    /// <summary>Monotonic per tenant within the owning module. Zero until first saved.</summary>
    long RowVersion { get; set; }
}

/// <summary>
/// Marks an entity that records who/when it was created and last modified. Stamped by the auditing
/// interceptor via <c>IClock</c> + <c>ITenantContext</c> (data &amp; persistence §5). Times are UTC.
/// </summary>
public interface IAuditable
{
    DateTimeOffset CreatedAtUtc { get; set; }
    string? CreatedBy { get; set; }
    DateTimeOffset? ModifiedAtUtc { get; set; }
    string? ModifiedBy { get; set; }
}
