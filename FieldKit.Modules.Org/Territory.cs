using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Org;

/// <summary>
/// A bounded slice of the market a rep is responsible for (<c>ORG-03</c>).
/// </summary>
/// <remarks>
/// <para>
/// Membership-based: a territory <i>is</i> the set of outlets in it. Nothing about geography is
/// stored here — geo/postal rules that materialize membership are <c>ORG-07</c>, and they produce
/// the same rows this does rather than replacing them.
/// </para>
/// <para>
/// It hangs off an org unit, and that is required rather than optional. BR-ORG-4 says a supervisor
/// sees the territories under their branch; without a unit, a territory is under no branch and
/// therefore visible to nobody by that rule. Making it optional would mean inventing a second
/// visibility rule for the territories that had skipped the first.
/// </para>
/// </remarks>
public sealed class Territory : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    /// <summary>Unique within the tenant — two territories of one name is a data-entry accident.</summary>
    public string Name { get; private set; } = null!;

    /// <summary>The org unit this territory belongs to. See the remarks for why it is required.</summary>
    public Guid OrgUnitId { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Territory() { } // EF

    public static Territory Create(string name, Guid orgUnitId) =>
        new() { Id = Guid.CreateVersion7(), Name = name, OrgUnitId = orgUnitId };

    public void Update(string name, Guid orgUnitId, IClock clock)
    {
        Name = name;
        OrgUnitId = orgUnitId;
        ModifiedAtUtc = clock.UtcNow;
    }
}

/// <summary>
/// An outlet's membership of a territory (<c>ORG-03</c>, <c>ORG-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// The mapping lives in Organization, not on the outlet, because Organization owns territories. It
/// is keyed by outlet id across a schema boundary, so there is no foreign key; the endpoint validates
/// through <c>IOutletCatalog</c> instead.
/// </para>
/// <para>
/// <b>This used to say the dependency runs one way and that Outlets must not depend on Organization.
/// The first half still holds; the second was wrong.</b> Outlets now asks
/// <see cref="Contracts.ITerritoryDirectory"/> which territory covers a shop, because <c>BR-OUT-1</c>
/// says an outlet <i>has</i> one — so answering it completes Outlets' own model rather than borrowing
/// Organization's.
/// </para>
/// <para>
/// What that original note was protecting against does not apply. A mutual reference between two
/// modules' <i>contracts</i> cannot produce a build cycle: every <c>.Contracts</c> assembly is a leaf,
/// so the assembly graph stays acyclic however many modules point at each other, and AT-1 still
/// forbids the coupling that would actually break (implementation to implementation). Insisting on a
/// one-way arrow would have invented a hierarchy the domain does not have — Outlets owns what a shop
/// is, Organization owns who covers it, and neither sits above the other.
/// </para>
/// <para>
/// The real risk is one AT-1 cannot see: two contract implementations calling each other, which is
/// mutual recursion wearing two sets of legal references. <b>AT-10</b> is the gate for that, and the
/// alternatives it lets us avoid are worse — a client-side join puts a domain relationship in the
/// browser for every future consumer to re-implement, and copying the territory onto the outlet buys
/// a second source of truth plus an event to keep it aligned.
/// </para>
/// <para>
/// <b>BR-ORG-1 / <c>ORG-05</c> are enforced by a unique index on the outlet</b>, not by a check in
/// code. One row per outlet means "exactly one primary territory" is a fact about the table rather
/// than a rule someone has to remember on every write path — including the bulk ones that do not
/// exist yet.
/// </para>
/// </remarks>
public sealed class TerritoryOutlet : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    public Guid TerritoryId { get; private set; }

    /// <summary>The outlet, owned by the Outlets module. Not a foreign key — different schema.</summary>
    public Guid OutletId { get; private set; }

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private TerritoryOutlet() { } // EF

    public static TerritoryOutlet Create(Guid territoryId, Guid outletId) =>
        new() { Id = Guid.CreateVersion7(), TerritoryId = territoryId, OutletId = outletId };
}
