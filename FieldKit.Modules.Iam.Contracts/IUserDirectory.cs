using FieldKit.SharedKernel;

namespace FieldKit.Modules.Iam.Contracts;

/// <summary>
/// Enough about a user to attribute work to them — the display half of a user, without the
/// authorization half.
/// </summary>
/// <param name="UserId">The Keycloak subject (<c>sub</c>), stable for the life of the account.</param>
/// <param name="DisplayName">Rendered next to a visit, order or audit.</param>
/// <param name="Email">Contact address; also how an admin recognises the account.</param>
/// <param name="IsActive">
/// False once deactivated. Callers should still resolve deactivated users: work they did last month
/// must keep its author, and blanking it would rewrite history rather than protect anything.
/// </param>
public sealed record UserSummary(string UserId, string DisplayName, string Email, bool IsActive);

/// <summary>
/// Resolves display information for users (IAM module contract).
/// </summary>
/// <remarks>
/// <para>
/// Consumed by Visit, Order and Audit for actor attribution — "who checked in", "who submitted this
/// order". Those modules must not read IAM's tables to answer that; this interface is the seam that
/// makes the schema-per-module rule survivable in practice (ADR-0005).
/// </para>
/// <para>
/// Deliberately read-only and deliberately narrow. It exposes no roles and no permissions:
/// authorization is answered by <c>ITenantContext.Has</c> from the token, never by looking a user up.
/// A module that could ask IAM "what may this user do?" would be a module that could get a different
/// answer than the request's own token.
/// </para>
/// <para>
/// All lookups are implicitly scoped to the current tenant by the global query filter — there is no
/// tenant parameter, because a caller able to pass one is a caller able to pass the wrong one.
/// </para>
/// </remarks>
public interface IUserDirectory
{
    /// <summary>Resolves one user, or <c>null</c> if no such user exists in the current tenant.</summary>
    Task<UserSummary?> FindAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves several users at once. Ids with no match are simply absent from the result rather
    /// than returned as nulls — callers are attributing a list of work items and want the ones they
    /// can name.
    /// </summary>
    Task<IReadOnlyList<UserSummary>> FindManyAsync(
        IReadOnlyCollection<string> userIds, CancellationToken cancellationToken = default);
}

/// <summary>
/// Identifies the tenant a realm's tokens belong to, for issuer validation.
/// </summary>
/// <param name="TenantId">The FieldKit tenant id carried in the token's <c>tenant</c> claim.</param>
/// <param name="Realm">The Keycloak realm name backing this tenant (realm-per-tenant, ADR-0008).</param>
/// <param name="IsActive">False for a suspended tenant; its tokens should stop being accepted.</param>
public sealed record TenantRealm(TenantId TenantId, string Realm, bool IsActive);

/// <summary>
/// The tenant registry, exposed so token validation can resolve an issuer per request.
/// </summary>
/// <remarks>
/// This is the contract ADR-0008's multi-issuer validation needs: realm-per-tenant means each
/// tenant's tokens arrive from a different issuer and JWKS endpoint, so the API must know which
/// realms exist before it can decide whether an issuer is one of them. The API currently validates a
/// single realm; this interface is what lets that become a lookup without reshaping the host.
/// </remarks>
public interface ITenantRegistry
{
    /// <summary>Every tenant known to the platform. Not tenant-scoped — this *is* the tenant list.</summary>
    Task<IReadOnlyList<TenantRealm>> GetAllAsync(CancellationToken cancellationToken = default);
}
