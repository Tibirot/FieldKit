using FieldKit.SharedKernel;

namespace FieldKit.BuildingBlocks;

/// <summary>
/// The tenant startup code is acting within, when there is no request to read one from — W12.
/// </summary>
/// <remarks>
/// <para>
/// <b>Seeding and migration run outside a request, and tenant-owned data does not.</b> Every query
/// filter, every audit stamp and every row-version counter reads <see cref="ITenantContext"/>, so
/// startup work that touches a tenant's tables has to say which tenant — and the app's
/// implementation reads an authenticated principal, which at boot does not exist.
/// </para>
/// <para>
/// <b>Three seeders had already answered this privately</b>, each with its own
/// <c>SeedingIdentity : ITenantContext</c> and its own hand-built <c>DbContext</c>. That works while
/// a seeder only touches its own module's tables. It stops working the moment one needs another
/// module's data through a contract — the contract's implementation resolves the *registered*
/// tenant context, which is the request-bound one, and throws. This is that seam made once and
/// properly.
/// </para>
/// <para>
/// <b>It grants nothing.</b> The scope names a tenant and a subject; the permission set is empty and
/// <see cref="ITenantContext.Has"/> is always false, exactly as the three private copies were. Code
/// running under a seeding scope can therefore do no more than an administrator could — it can see
/// one tenant's rows, and every endpoint-level permission check refuses it.
/// </para>
/// <para>
/// <b>`AsyncLocal`, so it follows the work rather than the thread.</b> A hosted service awaits its
/// way through several scopes and a thread-static would be lost at the first continuation. The value
/// is restored on dispose rather than cleared, so nesting is safe and an inner scope cannot leave an
/// outer one broken.
/// </para>
/// </remarks>
public static class TenantScope
{
    private static readonly AsyncLocal<SeedingIdentity?> Current = new();

    /// <summary>The identity to act as, or null when this is ordinary request work.</summary>
    public static ITenantContext? Ambient => Current.Value;

    /// <summary>
    /// Acts as <paramref name="userId"/> within <paramref name="tenantId"/> until the returned
    /// handle is disposed.
    /// </summary>
    /// <param name="userId">
    /// What the audit stamp will record. <c>"system"</c> for startup work, and it should stay
    /// recognisable as not-a-person: a row created by seeding and a row created by an administrator
    /// are different facts, and the only place that difference survives is here.
    /// </param>
    public static IDisposable For(TenantId tenantId, string userId)
    {
        var previous = Current.Value;

        Current.Value = new SeedingIdentity(tenantId, userId);

        return new Restore(previous);
    }

    private sealed class Restore(SeedingIdentity? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    private sealed class SeedingIdentity(TenantId tenantId, string userId) : ITenantContext
    {
        public TenantId TenantId => tenantId;

        public string UserId => userId;

        /// <summary>Empty, always. See the class remarks — this seam names a tenant, never a right.</summary>
        public IReadOnlySet<string> Permissions { get; } = new HashSet<string>();

        public bool Has(string permission) => false;
    }
}
