using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Iam;

/// <summary>
/// A customer of the platform, backed by a Keycloak realm (realm-per-tenant, ADR-0008).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not <see cref="ITenantOwned"/>.</b> Every other entity in this schema carries a
/// <c>TenantId</c> and is hidden by the global query filter; this one *is* the tenant. Marking it
/// tenant-owned would make the tenant list visible only to a tenant that could already be resolved —
/// circular, and it would break the one legitimate cross-tenant read the platform needs: token
/// validation asking "which realms exist?" before it knows whose token this is.
/// </para>
/// <para>
/// That exemption is the reason <see cref="Contracts.ITenantRegistry"/> is a narrow, read-only
/// contract rather than a general repository. Cross-tenant reads are not forbidden here because they
/// are impossible — they are forbidden everywhere else, so the one place they are allowed is worth
/// keeping small and obvious.
/// </para>
/// </remarks>
public sealed class Tenant : AggregateRoot, IAuditable
{
    public TenantId Id { get; private set; }

    /// <summary>Display name, e.g. "Veridian Beverages".</summary>
    public string Name { get; private set; } = null!;

    /// <summary>
    /// The Keycloak realm backing this tenant. Unique across the platform: two tenants sharing a
    /// realm would mean two tenants sharing an identity provider and a token audience.
    /// </summary>
    public string Realm { get; private set; } = null!;

    /// <summary>A suspended tenant keeps its data; its users simply stop being able to reach it.</summary>
    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Tenant() { } // EF

    public static Tenant Create(TenantId id, string name, string realm) => new()
    {
        Id = id,
        Name = name,
        Realm = realm,
        IsActive = true,
    };

    public void Suspend() => IsActive = false;

    public void Reinstate() => IsActive = true;
}
