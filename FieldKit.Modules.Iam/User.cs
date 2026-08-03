using FieldKit.BuildingBlocks;
using FieldKit.Modules.Iam.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Iam;

/// <summary>
/// A person within a tenant — the FieldKit-side profile of a Keycloak account.
/// </summary>
/// <remarks>
/// <para>
/// <b>No credentials here, ever.</b> Keycloak owns authentication (ADR-0008); this holds only what
/// FieldKit needs and Keycloak does not model — locale, timezone, and which roles the user has. The
/// link between the two is <see cref="SubjectId"/>, the token's <c>sub</c>.
/// </para>
/// <para>
/// Locale and timezone are mandatory (BR-IAM-5) because every money amount, quantity and timestamp
/// in the product renders through them (A3). A nullable default would mean a rep somewhere seeing a
/// visit time in the wrong zone, which in a field app is a missed appointment rather than a cosmetic
/// bug.
/// </para>
/// </remarks>
public sealed class User : AggregateRoot, ITenantOwned, IAuditable
{
    private readonly List<UserRole> _roles = [];

    public Guid Id { get; private set; }

    /// <summary>
    /// The Keycloak subject (<c>sub</c>). Unique within the tenant and stable for the life of the
    /// account — email is not, and using it as the key would break attribution on a rename.
    /// </summary>
    public string SubjectId { get; private set; } = null!;

    public string Email { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;

    /// <summary>BCP-47, e.g. <c>ro-RO</c>. Drives formatting, not just translation (ADR-0010).</summary>
    public string Locale { get; private set; } = null!;

    /// <summary>IANA zone, e.g. <c>Europe/Bucharest</c>. Stored UTC, displayed here.</summary>
    public string TimeZone { get; private set; } = null!;

    public bool IsActive { get; private set; }

    public IReadOnlyList<UserRole> Roles => _roles;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private User() { } // EF

    public static User Create(
        string subjectId, string email, string displayName, string locale, string timeZone) => new()
        {
            Id = Guid.CreateVersion7(),
            SubjectId = subjectId,
            Email = email,
            DisplayName = displayName,
            Locale = locale,
            TimeZone = timeZone,
            IsActive = true,
        };

    /// <summary>
    /// Updates the editable profile. <see cref="SubjectId"/> is deliberately not among them: it is
    /// the link to the Keycloak account, and repointing it would silently reattribute every visit,
    /// order and audit this user has ever recorded.
    /// </summary>
    public void UpdateProfile(string email, string displayName, string locale, string timeZone, IClock clock)
    {
        Email = email;
        DisplayName = displayName;
        Locale = locale;
        TimeZone = timeZone;
        Touch(clock);
    }

    /// <summary>
    /// Replaces the user's roles.
    /// </summary>
    /// <remarks>
    /// Enforces BR-IAM-3 — a user must hold at least one role. Removing the last one is expressed as
    /// <see cref="Deactivate"/> rather than allowed silently, because a user with no roles is not a
    /// restricted user: they can authenticate and then do nothing, which reads as a broken account
    /// rather than a disabled one.
    /// </remarks>
    public void SetRoles(IEnumerable<Guid> roleIds, IClock clock)
    {
        var distinct = roleIds.Distinct().ToList();

        if (distinct.Count == 0)
        {
            throw new InvalidOperationException(
                "A user must hold at least one role (BR-IAM-3). Deactivate the user instead.");
        }

        _roles.Clear();
        _roles.AddRange(distinct.Select(roleId => new UserRole { UserId = Id, RoleId = roleId }));
        Touch(clock);
    }

    /// <summary>
    /// Deactivates the user and announces it, so Sync can release the bound device (A8).
    /// </summary>
    /// <remarks>
    /// Existing access tokens keep working until they expire (BR-IAM-4). That is a deliberate trade:
    /// checking a database on every request would put IAM in the path of every call in the platform.
    /// Short token lifetimes plus refresh revocation bound the window instead.
    /// </remarks>
    public void Deactivate(IClock clock)
    {
        if (!IsActive) return; // idempotent — deactivating twice must not publish twice

        IsActive = false;
        Touch(clock);
        Raise(new UserDeactivated(Guid.CreateVersion7(), clock.UtcNow, SubjectId));
    }

    public void Reactivate(IClock clock)
    {
        if (IsActive) return;

        IsActive = true;
        Touch(clock);
    }

    // The auditing interceptor stamps ModifiedAtUtc on save; this exists so a change that only
    // touches a collection still registers as a change to the aggregate.
    private void Touch(IClock clock) => ModifiedAtUtc = clock.UtcNow;
}

/// <summary>
/// Join between <see cref="User"/> and <see cref="Role"/>.
/// </summary>
/// <remarks>
/// A plain join rather than a skip-navigation so the pair stays addressable — role assignment is an
/// audited administrative act, and "who gave this rep order:submit, and when" is a question the
/// platform should be able to answer.
/// </remarks>
public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
}
