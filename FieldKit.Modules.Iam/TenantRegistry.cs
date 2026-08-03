using FieldKit.Modules.Iam.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Iam;

/// <summary>
/// The tenant list, for issuer resolution. Internal — consumers bind to
/// <see cref="ITenantRegistry"/> (AT-2).
/// </summary>
/// <remarks>
/// This is the one read in the platform that is legitimately cross-tenant, and it works without any
/// bypass: <see cref="Tenant"/> is not <c>ITenantOwned</c>, so no query filter applies to it. That
/// is why the exemption lives in the model rather than in a call to <c>IgnoreQueryFilters</c> —
/// which is banned outright (AT-9), and would have been the obvious wrong way to write this.
/// </remarks>
internal sealed class TenantRegistry(IamDbContext db) : ITenantRegistry
{
    public async Task<IReadOnlyList<TenantRealm>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await db.Tenants
            .OrderBy(tenant => tenant.Realm)
            .Select(tenant => new TenantRealm(tenant.Id, tenant.Realm, tenant.IsActive))
            .ToListAsync(cancellationToken);
}
