using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Server;

/// <summary>
/// TEMPORARY tenant context for Phase 0 — a fixed dev tenant, optionally overridden by an
/// <c>X-Tenant-Id</c> header, granting all permissions. Replaced by the token-derived context once
/// IAM / Keycloak lands (Phase 1, ADR-0008). It exists only so the tenant filter and stamping have
/// something to resolve while there is no authentication yet.
/// </summary>
public sealed class DevTenantContext : ITenantContext
{
    private static readonly TenantId DefaultDevTenant = TenantId.Parse("00000000-0000-0000-0000-000000000001");

    public DevTenantContext(IHttpContextAccessor httpContextAccessor)
    {
        var header = httpContextAccessor.HttpContext?.Request.Headers["X-Tenant-Id"].ToString();
        TenantId = !string.IsNullOrWhiteSpace(header) ? TenantId.Parse(header) : DefaultDevTenant;
    }

    public TenantId TenantId { get; }
    public string UserId => "dev-user";
    public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();

    public bool Has(string permission) => true; // dev only — no authorization until IAM
}
