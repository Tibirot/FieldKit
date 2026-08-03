using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Org;

/// <summary>
/// A user occupying a place in the sales hierarchy — "Andrei, Area North" (<c>ORG-02</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is what turns a tree of units into a management line: the units say how the organization is
/// shaped, the positions say who is in it, and everything derived — roll-up reporting, a supervisor's
/// visibility scope (BR-ORG-4) — falls out of walking the tree from someone's units.
/// </para>
/// <para>
/// <b>Current state, not history.</b> A row means "is attached now"; removing it detaches. The
/// management line is a question about the present, and reassignment-with-history is explicitly
/// Phase 2 (<c>ORG-08</c>). BR-ORG-5 is unaffected either way — a visit or an order records the user
/// who made it, so its attribution survives any change here. Adding validity dates later is an
/// additive migration.
/// </para>
/// </remarks>
public sealed class Position : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    /// <summary>
    /// The Keycloak subject (<c>sub</c>) — the same identifier the token carries and that visits and
    /// orders attribute work to. Not IAM's row id: this has to mean the same thing everywhere.
    /// </summary>
    public string UserId { get; private set; } = null!;

    public Guid OrgUnitId { get; private set; }

    /// <summary>
    /// What this person is called here — "Supervisor", "Key Account Manager".
    /// </summary>
    /// <remarks>
    /// <b>Display only. Never an authorization input.</b> It is free text an admin types, so it can
    /// say "Supervisor" for someone holding no supervisory permission at all, and it can be renamed
    /// by anyone with <c>position:write</c>. What a user may do is answered by
    /// <see cref="ITenantContext.Has"/> from their token and nowhere else (BR-IAM-2) — the moment
    /// something branches on this string, the permission model has a second, editable, unenforced
    /// copy.
    /// </remarks>
    public string Title { get; private set; } = null!;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Position() { } // EF

    public static Position Create(string userId, Guid orgUnitId, string title) =>
        new() { Id = Guid.CreateVersion7(), UserId = userId, OrgUnitId = orgUnitId, Title = title };

    public void Retitle(string title, IClock clock)
    {
        Title = title;
        ModifiedAtUtc = clock.UtcNow;
    }
}
