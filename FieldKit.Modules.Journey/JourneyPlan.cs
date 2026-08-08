using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Journey;

/// <summary>Whether a plan is still being looked at, or is the rep's actual work.</summary>
public enum JourneyPlanStatus
{
    /// <summary>Generated and reviewable. Nothing outside this module can see it.</summary>
    Draft,

    /// <summary>Announced. This is what the rep is working, and it no longer changes.</summary>
    Published,
}

/// <summary>One call on a plan, on one day (<c>JRN-04</c>).</summary>
/// <remarks>
/// The stored form of <see cref="GeneratedVisit"/>. It has an identity because things will later
/// happen *to* it — a rep marks it not-visited with a reason (<c>JRN-06</c>), or it becomes an
/// actual Visit — and none of those can name a row that has no id.
/// </remarks>
public sealed class PlannedVisit : ITenantOwned
{
    public Guid Id { get; private set; }

    public Guid JourneyPlanId { get; private set; }

    public Guid OutletId { get; private set; }

    /// <summary>The day it is planned for. A date, in no timezone — see <see cref="JourneyPlan"/>.</summary>
    public DateOnly Date { get; private set; }

    public TenantId TenantId { get; set; }

    private PlannedVisit() { } // EF

    internal static PlannedVisit Create(Guid planId, DateOnly date, Guid outletId) =>
        new() { Id = Guid.CreateVersion7(), JourneyPlanId = planId, Date = date, OutletId = outletId };
}

/// <summary>
/// An outlet the plan could not call on as often as its frequency asks (<c>JRN-03</c>).
/// </summary>
/// <remarks>
/// <b>Stored, and the exclusions are not.</b> The difference is whether the fact survives its
/// inputs. A shortfall is a statement about *this* plan against the capacity it had — nothing else
/// records it, and re-deriving it later would need the frequencies and calendar exactly as they were
/// at generation, which nobody keeps. An exclusion is recoverable at any time: whether an outlet is
/// closed is Outlets' to answer, and whether it has a frequency is one screen away. So the plan
/// carries the fact only it knows.
/// </remarks>
public sealed class PlanShortfall : ITenantOwned
{
    public Guid Id { get; private set; }

    public Guid JourneyPlanId { get; private set; }

    public Guid OutletId { get; private set; }

    /// <summary>What the frequency asked for over this window.</summary>
    public int Required { get; private set; }

    /// <summary>What fitted. Always fewer than <see cref="Required"/>, or this row would not exist.</summary>
    public int Planned { get; private set; }

    public TenantId TenantId { get; set; }

    private PlanShortfall() { } // EF

    internal static PlanShortfall Create(Guid planId, Guid outletId, int required, int planned) =>
        new() { Id = Guid.CreateVersion7(), JourneyPlanId = planId, OutletId = outletId, Required = required, Planned = planned };
}

/// <summary>
/// A rep's plan for a window: the calls, and what the plan could not do (<c>JRN-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Draft until it is published.</b> Generation is cheap and repeatable — a supervisor runs it,
/// changes a frequency, runs it again — so a generated plan is not yet anybody's work. Publishing is
/// the moment it becomes the thing a rep is holding, and it is the moment
/// <see cref="JourneyPublished"/> announces it. Without the two states, every experiment a
/// supervisor ran would be a plan somebody's device tried to download.
/// </para>
/// <para>
/// <b>Sealed once published.</b> A published plan is not regenerated in place: the rep may already
/// have walked half of it, and rewriting the rows underneath them would silently discard visits they
/// have completed. Replanning produces a *new* plan; what happens to the old one when they overlap
/// is <c>JRN-08</c>'s question, and this slice deliberately does not answer it — it refuses the
/// second publish instead of guessing.
/// </para>
/// <para>
/// <b>Dates, not instants</b>, the same reasoning <c>RepAssignment</c> carries: a plan covers days,
/// and storing a timestamp would invite a conversion that moves the window by a few hours.
/// </para>
/// </remarks>
public sealed class JourneyPlan : AggregateRoot, ITenantOwned, IAuditable
{
    private readonly List<PlannedVisit> _visits = [];
    private readonly List<PlanShortfall> _shortfalls = [];

    public Guid Id { get; private set; }

    /// <summary>The rep whose plan this is — the Keycloak subject.</summary>
    public string UserId { get; private set; } = null!;

    public DateOnly FromDate { get; private set; }

    public DateOnly ToDate { get; private set; }

    public JourneyPlanStatus Status { get; private set; }

    public DateTimeOffset GeneratedAtUtc { get; private set; }

    /// <summary>When it became the rep's work. Null while it is a draft.</summary>
    public DateTimeOffset? PublishedAtUtc { get; private set; }

    public IReadOnlyList<PlannedVisit> Visits => _visits;

    public IReadOnlyList<PlanShortfall> Shortfalls => _shortfalls;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private JourneyPlan() { } // EF

    public static JourneyPlan Draft(
        string userId, DateOnly from, DateOnly to, GeneratedPlan generated, IClock clock)
    {
        var plan = new JourneyPlan
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            FromDate = from,
            ToDate = to,
            Status = JourneyPlanStatus.Draft,
            GeneratedAtUtc = clock.UtcNow,
        };

        plan._visits.AddRange(
            generated.Visits.Select(visit => PlannedVisit.Create(plan.Id, visit.Date, visit.OutletId)));

        plan._shortfalls.AddRange(
            generated.Shortfalls.Select(shortfall =>
                PlanShortfall.Create(plan.Id, shortfall.OutletId, shortfall.Required, shortfall.Planned)));

        return plan;
    }

    /// <summary>
    /// Makes this the rep's work, and says so.
    /// </summary>
    /// <returns>False when it is already published — see the remarks on sealing.</returns>
    public bool TryPublish(IClock clock)
    {
        if (Status == JourneyPlanStatus.Published) return false;

        Status = JourneyPlanStatus.Published;
        PublishedAtUtc = clock.UtcNow;
        ModifiedAtUtc = clock.UtcNow;

        Raise(new JourneyPublished(
            Guid.CreateVersion7(),
            clock.UtcNow,
            Id,
            UserId,
            FromDate,
            ToDate,
            _visits.Count));

        return true;
    }
}

/// <summary>
/// A rep's journey plan was published (<c>JRN-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// Delivered through the outbox. <b>Sync is the consumer and does not exist yet</b> (W8), which is
/// fine and is the established shape here: <c>PriceListPublished</c> and <c>PromotionActivated</c>
/// were both emitted before anything listened. An event is a statement about something that
/// happened, and it is true whether or not anyone is currently reading it.
/// </para>
/// <para>
/// That is deliberately <i>not</i> the rule for contracts. <c>IJourneyQuery</c> is specified and
/// still unbuilt, because an interface designed before its consumer is a guess the consumer has to
/// live with — Visit arrives later in this week and will shape it. An event has no such problem: a
/// consumer that wants more can ask for it at the point of use.
/// </para>
/// <para>
/// <b>It does not carry the visits.</b> A plan is hundreds of rows, an outbox message is a row in a
/// table, and a device that needs the plan is going to pull it through Sync anyway — where it can be
/// paged, filtered to the rep's territory scope (A4) and resumed. What this carries is enough to
/// decide *whether* to pull: whose plan, which window, and how big.
/// </para>
/// </remarks>
/// <param name="VisitCount">How many calls it holds — enough to size the pull, not to perform it.</param>
public sealed record JourneyPublished(
    Guid Id,
    DateTimeOffset OccurredOn,
    Guid JourneyPlanId,
    string UserId,
    DateOnly From,
    DateOnly To,
    int VisitCount) : IIntegrationEvent;
