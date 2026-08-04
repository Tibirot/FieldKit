using FieldKit.BuildingBlocks;
using FieldKit.Modules.Org.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Org;

/// <summary>
/// Which rep covers a territory, over a period (<c>ORG-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// The first time-bounded relationship in the system, and the input to two things that matter: a
/// rep's offline data scope (BR-ORG-3) and journey generation. Changing one changes what a device
/// downloads, which is why every change publishes <see cref="RepAssignmentChanged"/>.
/// </para>
/// <para>
/// <b>Dates, not instants.</b> "From 1 March" is a statement about days; storing a timestamp would
/// invite a conversion that moves the boundary by a few hours. Which timezone decides what "today"
/// is belongs to whoever asks — see <see cref="RepAssignmentEndpoints"/>.
/// </para>
/// <para>
/// <b>Editable in place</b>, subject to BR-ORG-2. Correcting a mistyped start date should not need a
/// cancellation and a replacement; the audit columns record who last changed it, and the outbox
/// carries what changed. History as a first-class concern is <c>ORG-08</c>, Phase 2.
/// </para>
/// </remarks>
public sealed class RepAssignment : AggregateRoot, ITenantOwned, IAuditable
{
    public Guid Id { get; private set; }

    public Guid TerritoryId { get; private set; }

    /// <summary>The Keycloak subject — the same identifier positions, visits and orders use.</summary>
    public string UserId { get; private set; } = null!;

    /// <summary>First day covered. Stored as a column; see <see cref="Period"/>.</summary>
    public DateOnly FromDate { get; private set; }

    /// <summary>Last day covered, or null for "until further notice".</summary>
    public DateOnly? ToDate { get; private set; }

    /// <summary>
    /// The period as a value, for the overlap rule.
    /// </summary>
    /// <remarks>
    /// Composed from two columns rather than mapped as an owned type, for the same reason the
    /// outlet's location is: EF owns only reference types, and a struct value object is worth more
    /// than the mapping convenience. The invariant it carries — an end that is not before its start —
    /// is also a check constraint, so it holds against anything that writes the table.
    /// </remarks>
    public DateRange Period => new(FromDate, ToDate);

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private RepAssignment() { } // EF

    public static RepAssignment Create(Guid territoryId, string userId, DateRange period, IClock clock)
    {
        var assignment = new RepAssignment
        {
            Id = Guid.CreateVersion7(),
            TerritoryId = territoryId,
            UserId = userId,
            FromDate = period.From,
            ToDate = period.To,
        };

        // No outgoing rep on a first assignment; the caller supplies one when it is a handover.
        assignment.Announce(clock, outgoingUserId: null);
        return assignment;
    }

    /// <summary>
    /// Changes who covers the territory, or when.
    /// </summary>
    /// <remarks>
    /// Publishes the previous holder as the outgoing rep whenever the person changes, so a consumer
    /// re-scoping devices does not have to have been watching. Editing only the dates still
    /// publishes — the period is what decides when a device should hold the territory's outlets.
    /// </remarks>
    public void Update(string userId, DateRange period, IClock clock)
    {
        var previousUserId = UserId;

        UserId = userId;
        FromDate = period.From;
        ToDate = period.To;
        ModifiedAtUtc = clock.UtcNow;

        Announce(clock, outgoingUserId: previousUserId == userId ? null : previousUserId);
    }

    /// <summary>Announces that this territory now has nobody assigned for this period.</summary>
    public void AnnounceRemoval(IClock clock) =>
        Raise(new RepAssignmentChanged(
            Guid.CreateVersion7(),
            clock.UtcNow,
            TerritoryId,
            IncomingUserId: null,
            OutgoingUserId: UserId,
            From: null,
            To: null));

    private void Announce(IClock clock, string? outgoingUserId) =>
        Raise(new RepAssignmentChanged(
            Guid.CreateVersion7(),
            clock.UtcNow,
            TerritoryId,
            IncomingUserId: UserId,
            OutgoingUserId: outgoingUserId,
            FromDate,
            ToDate));
}
