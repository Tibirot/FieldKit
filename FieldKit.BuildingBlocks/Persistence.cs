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
