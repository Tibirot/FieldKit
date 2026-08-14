using System.Collections.Frozen;
using System.Security.Claims;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using FieldKit.Web;

namespace FieldKit.Server;

/// <summary>
/// The ambient tenant/user context, derived from the validated access token (ADR-0008,
/// <c>IAM-02</c>). Replaces the temporary <c>DevTenantContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// The tenant comes from the token's <c>tenant</c> claim and <b>nowhere else</b> — not a header, not
/// a route value, not the request body. That is the whole point: the EF global query filter reads
/// <see cref="TenantId"/>, so anything able to influence it can cross tenants. The previous
/// implementation honoured an <c>X-Tenant-Id</c> header, which was fine while nothing authenticated
/// and is exactly the vector this removes.
/// </para>
/// <para>
/// <b>Resolved lazily, deliberately.</b> This is registered scoped, and a scope also exists outside
/// any request — <c>ModuleMigrator</c> creates one at startup to run migrations, where there is no
/// user and no token. Reading claims in the constructor would fail startup. Migrations never touch a
/// tenant-owned entity, so nothing asks for the tenant there; if something ever does, it throws
/// rather than silently inventing one.
/// </para>
/// <para>
/// The throws below should be unreachable for an authenticated request: token validation rejects a
/// token without a usable <c>tenant</c> claim before any endpoint runs
/// (<see cref="AuthenticationExtensions"/>). They are here because "unreachable" and "cannot happen"
/// are different, and the safe failure for a tenant that cannot be determined is a loud one.
/// </para>
/// <para>
/// <b>A seeding scope wins, and it can only exist where no request does</b>
/// (<see cref="TenantScope"/>, W12). Startup work that touches tenant-owned data has to name a
/// tenant, and there is no principal to read one from — so <c>TenantScope.For</c> pushes one for
/// the duration of a scope and this defers to it.
/// </para>
/// <para>
/// <b>It is not a way in.</b> The ambient identity is set by in-process startup code and by nothing
/// a request can reach: no header, no claim, no body. It carries an empty permission set, so
/// everything behind <c>RequirePermission</c> refuses it — the same posture the three private
/// <c>SeedingIdentity</c> copies it replaces already had. The ordering is nevertheless the nervous
/// line to read: a request arriving while a seeding scope was open <i>on the same async flow</i>
/// would take the seed's tenant. No such flow exists — a hosted service is not a request — and the
/// only way to set it is to be running inside this process already.
/// </para>
/// </remarks>
public sealed class KeycloakTenantContext(IHttpContextAccessor httpContextAccessor) : ITenantContext
{
    private readonly Lazy<TenantId> _tenantId = new(() =>
        TenantScope.Ambient?.TenantId ?? TenantId.Parse(Claim(httpContextAccessor, "tenant")));

    private readonly Lazy<string> _userId = new(() =>
        TenantScope.Ambient?.UserId ?? Claim(httpContextAccessor, "sub"));

    private readonly Lazy<FrozenSet<string>> _permissions = new(() =>
        Principal(httpContextAccessor)
            .FindAll(PermissionExtensions.PermissionsClaim)
            .Select(claim => claim.Value)
            .ToFrozenSet(StringComparer.Ordinal));

    public TenantId TenantId => _tenantId.Value;

    public string UserId => _userId.Value;

    public IReadOnlySet<string> Permissions => _permissions.Value;

    /// <summary>
    /// Exact, case-sensitive match. Permissions are identifiers, not prose — treating
    /// <c>Product:Read</c> as <c>product:read</c> would make a typo in a role name silently grant
    /// access.
    /// </summary>
    public bool Has(string permission) => Permissions.Contains(permission);

    private static ClaimsPrincipal Principal(IHttpContextAccessor accessor) =>
        accessor.HttpContext?.User?.Identity?.IsAuthenticated == true
            ? accessor.HttpContext.User
            : throw new InvalidOperationException(
                "No authenticated user on the current request — the tenant context was resolved "
                + "outside an authenticated request. Endpoints that touch tenant-owned data must "
                + "require authorization.");

    private static string Claim(IHttpContextAccessor accessor, string type) =>
        Principal(accessor).FindFirstValue(type)
        ?? throw new InvalidOperationException($"The access token carries no '{type}' claim.");
}
