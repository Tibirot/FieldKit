using FieldKit.SharedKernel;

namespace FieldKit.BuildingBlocks;

/// <summary>
/// The ambient tenant/user context for the current request, resolved from the auth token (ADR-0008).
/// Modules read the tenant and check permissions through this — never role names, never a tenant id
/// from the request body.
/// </summary>
public interface ITenantContext
{
    TenantId TenantId { get; }

    /// <summary>The authenticated subject (Keycloak <c>sub</c>).</summary>
    string UserId { get; }

    /// <summary>Fine-grained permission strings (e.g. "order:submit").</summary>
    IReadOnlySet<string> Permissions { get; }

    /// <summary>True if the current user holds the given <paramref name="permission"/>.</summary>
    bool Has(string permission);
}
