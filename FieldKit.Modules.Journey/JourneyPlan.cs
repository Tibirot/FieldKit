using System.Text.Json.Serialization;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Journey;

/// <summary>Whether a plan is still being looked at, or is the rep's actual work.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<JourneyPlanStatus>))]
public enum JourneyPlanStatus
{
    /// <summary>Generated and reviewable. Nothing outside this module can see it.</summary>
    Draft,

    /// <summary>Announced. This is what the rep is working, and it no longer changes.</summary>
    Published,
}

/// <summary>How a call came to be on the plan.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<VisitSource>))]
public enum VisitSource
{
    /// <summary>Generation put it there (<c>JRN-03</c>).</summary>
    Generated,

    /// <summary>The rep added it in the field — a shop that was not on the plan (<c>JRN-06</c>).</summary>
    Unplanned,
}

/// <summary>Where a call has got to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PlannedVisitStatus>))]
public enum PlannedVisitStatus
{
    /// <summary>Still to do.</summary>
    Planned,

    /// <summary>
    /// The rep was not able to make it, and said why (<c>JRN-06</c>, <c>VIS-07</c>).
    /// </summary>
    /// <remarks>
    /// <b>There is no <c>Deleted</c>.</b> <c>BR-JRN-2</c> is explicit that a rep cannot remove a
    /// planned call — a shop that was skipped is a fact about the round, and letting it disappear
    /// would make coverage look complete and make <c>BR-JRN-6</c>'s compliance metric a measure of
    /// what was left on the plan rather than of what was done.
    /// </remarks>
    NotVisited,
}

/// <summary>One call on a plan, on one day (<c>JRN-04</c>).</summary>
/// <remarks>
/// The stored form of <see cref="GeneratedVisit"/>. It has an identity because things happen *to*
/// it — a rep marks it not-visited with a reason (<c>JRN-06</c>), moves it within its cycle, or it
/// becomes an actual Visit — and none of those can name a row that has no id.
/// </remarks>
public sealed class PlannedVisit : ITenantOwned, ISyncTracked
{
    /// <summary>
    /// Set by the row-version interceptor, never here (ADR-0013).
    /// </summary>
    /// <remarks>
    /// On the call rather than on the <see cref="JourneyPlan"/>, because the call is what the device
    /// holds. Stamping the plan would make one rep marking one shop not-visited look, to every
    /// device, like the whole round had changed.
    /// </remarks>
    public long RowVersion { get; set; }

    /// <summary>The column width for a not-visited reason.</summary>
    public const int MaximumReasonLength = 500;

    public Guid Id { get; private set; }

    public Guid JourneyPlanId { get; private set; }

    public Guid OutletId { get; private set; }

    /// <summary>The day it is planned for. A date, in no timezone — see <see cref="JourneyPlan"/>.</summary>
    public DateOnly Date { get; private set; }

    /// <summary>
    /// The length of the cycle this call belongs to, from the frequency that generated it.
    /// </summary>
    /// <remarks>
    /// Zero for an unplanned call, which belongs to no cycle — see <see cref="AddUnplanned"/>.
    /// </remarks>
    public int CycleLengthDays { get; private set; }

    public VisitSource Source { get; private set; }

    public PlannedVisitStatus Status { get; private set; }

    /// <summary>Why the rep could not make it. Null until they say so, and required when they do.</summary>
    public string? NotVisitedReason { get; private set; }

    /// <summary>The day it was originally planned for, once it has been moved. Null if it never was.</summary>
    /// <remarks>
    /// Kept because a moved call and a call that was always on Thursday are different things to
    /// anybody reviewing the round, and the original date is the only evidence of the first.
    /// </remarks>
    public DateOnly? RescheduledFrom { get; private set; }

    public TenantId TenantId { get; set; }

    private PlannedVisit() { } // EF

    internal static PlannedVisit Create(Guid planId, GeneratedVisit visit) => new()
    {
        Id = Guid.CreateVersion7(),
        JourneyPlanId = planId,
        Date = visit.Date,
        OutletId = visit.OutletId,
        CycleLengthDays = visit.CycleLengthDays,
        Source = VisitSource.Generated,
        Status = PlannedVisitStatus.Planned,
    };

    /// <summary>
    /// A call the rep added in the field.
    /// </summary>
    /// <remarks>
    /// <b>It belongs to no cycle</b>, so <see cref="CycleLengthDays"/> is zero and it cannot be
    /// rescheduled. That is not an omission: <c>BR-JRN-4</c>'s rule is about moving a call *within
    /// the cycle its frequency put it in*, and a call nobody planned was never in one. A rep who
    /// wants it on a different day adds it on that day — the plan is theirs to add to.
    /// </remarks>
    internal static PlannedVisit AddUnplanned(Guid planId, DateOnly date, Guid outletId) => new()
    {
        Id = Guid.CreateVersion7(),
        JourneyPlanId = planId,
        Date = date,
        OutletId = outletId,
        CycleLengthDays = 0,
        Source = VisitSource.Unplanned,
        Status = PlannedVisitStatus.Planned,
    };

    /// <summary>Records that the rep could not make it, and why. False if it is already recorded.</summary>
    internal bool TryMarkNotVisited(string reason)
    {
        if (Status == PlannedVisitStatus.NotVisited) return false;

        Status = PlannedVisitStatus.NotVisited;
        NotVisitedReason = reason.Trim();

        return true;
    }

    /// <summary>
    /// The days this call may be moved to, or null if it may not be moved (<c>BR-JRN-4</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cycles tile forward from the plan's first day, so the cycle a call sits in is how many whole
    /// cycles have passed since then, and its window is that cycle's own span. Moving inside one is
    /// the rep's own call; moving across a boundary changes which cycle the outlet was covered in,
    /// which changes <c>BR-JRN-6</c> compliance for two cycles at once — and that is a supervisor's
    /// decision, not a rep's.
    /// </para>
    /// <para>
    /// <b>Clipped to the plan's window</b>, because a plan whose last cycle is cut short by its end
    /// date has no days beyond it to offer. <see cref="JourneyPlan.TryReschedule"/> checks that
    /// bound separately so it can name it in the refusal, and the two agree by construction: this
    /// returns the range that method accepts.
    /// </para>
    /// <para>
    /// <b>A range rather than a predicate</b>, since W12. The predicate answered one question — *may
    /// it move here* — and the device needs the other one: *where may it move at all*. Deriving the
    /// second from the first means trying every date, and deriving it on the phone means a second
    /// implementation of this rule.
    /// </para>
    /// </remarks>
    internal (DateOnly From, DateOnly To)? MovableWithin(DateOnly planStart, DateOnly planEnd)
    {
        // Zero for an unplanned call, which was never in a cycle and so has none to move inside.
        if (CycleLengthDays < 1) return null;

        // A call outside its own plan's window is not a state this module can produce; returning
        // nothing rather than arithmetic on it means a corrupt row offers no days instead of wrong
        // ones. It also keeps the integer division below on non-negative input, where truncation
        // toward zero and flooring agree.
        if (Date < planStart || Date > planEnd) return null;

        var cycle = (Date.DayNumber - planStart.DayNumber) / CycleLengthDays;
        var from = planStart.AddDays(cycle * CycleLengthDays);
        var to = from.AddDays(CycleLengthDays - 1);

        return (from, to > planEnd ? planEnd : to);
    }

    internal void MoveTo(DateOnly date)
    {
        RescheduledFrom ??= Date;
        Date = date;
    }
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
            generated.Visits.Select(visit => PlannedVisit.Create(plan.Id, visit)));

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

    /// <summary>Why an annotation was refused. <see cref="None"/> means it was not.</summary>
    public enum AnnotationRefusal
    {
        None,

        /// <summary>The plan is still a draft — there is nothing for a rep to be annotating.</summary>
        NotPublished,

        /// <summary>Already recorded as not-visited.</summary>
        AlreadyNotVisited,

        /// <summary>The new date is outside the window this plan covers.</summary>
        OutsideWindow,

        /// <summary>The new date is in a different cycle (<c>BR-JRN-4</c>).</summary>
        OutsideCycle,
    }

    /// <summary>
    /// Records that a rep could not make a call, and announces it (<c>JRN-06</c>, <c>VIS-07</c>).
    /// </summary>
    /// <remarks>
    /// <b>Only on a published plan.</b> A draft is a supervisor's experiment; there is no round for a
    /// rep to be reporting on, and allowing it would let an annotation vanish when the plan it was
    /// against was superseded by the next generation run.
    /// </remarks>
    public AnnotationRefusal TryMarkNotVisited(PlannedVisit visit, string reason, IClock clock)
    {
        if (Status != JourneyPlanStatus.Published) return AnnotationRefusal.NotPublished;
        if (!visit.TryMarkNotVisited(reason)) return AnnotationRefusal.AlreadyNotVisited;

        ModifiedAtUtc = clock.UtcNow;

        Raise(new PlannedVisitMarkedNotVisited(
            Guid.CreateVersion7(), clock.UtcNow, Id, visit.Id, visit.OutletId, UserId, visit.Date, reason.Trim()));

        return AnnotationRefusal.None;
    }

    /// <summary>
    /// Moves a call to another day inside its own cycle (<c>JRN-06</c>, <c>BR-JRN-4</c>).
    /// </summary>
    /// <remarks>
    /// No event. A reschedule inside a cycle changes nothing anybody outside this module reasons
    /// about — the outlet still gets its call in the cycle its frequency asked for, so compliance is
    /// unchanged and Sync is already carrying the plan. Announcing every day a rep shuffles would be
    /// a stream of messages with no consumer and no decision behind them.
    /// </remarks>
    public AnnotationRefusal TryReschedule(PlannedVisit visit, DateOnly date, IClock clock)
    {
        if (Status != JourneyPlanStatus.Published) return AnnotationRefusal.NotPublished;
        if (date < FromDate || date > ToDate) return AnnotationRefusal.OutsideWindow;

        /*
         * The same window the device was sent (`PlannedVisitSnapshot.MovableFrom`), asked as a
         * question rather than published as an answer — W12, regression F2.
         *
         * Written this way round on purpose. Before, this method held the rule and the device held
         * nothing; the obvious fix was to compute a window *beside* it for the feed, and then two
         * expressions of `BR-JRN-4` would sit ten lines apart, agreeing until one of them was
         * edited. One of them is the rule now, and this is the reader.
         */
        if (visit.MovableWithin(FromDate, ToDate) is not { } window
            || date < window.From
            || date > window.To)
        {
            return AnnotationRefusal.OutsideCycle;
        }

        visit.MoveTo(date);
        ModifiedAtUtc = clock.UtcNow;

        return AnnotationRefusal.None;
    }

    /// <summary>Adds a call the rep made that nobody planned (<c>JRN-06</c>).</summary>
    public AnnotationRefusal TryAddUnplanned(
        Guid outletId, DateOnly date, IClock clock, out PlannedVisit? added)
    {
        added = null;

        if (Status != JourneyPlanStatus.Published) return AnnotationRefusal.NotPublished;
        if (date < FromDate || date > ToDate) return AnnotationRefusal.OutsideWindow;

        added = PlannedVisit.AddUnplanned(Id, date, outletId);
        _visits.Add(added);
        ModifiedAtUtc = clock.UtcNow;

        return AnnotationRefusal.None;
    }
}

/// <summary>
/// A rep reported that a planned call did not happen, and why (<c>JRN-06</c>, <c>VIS-07</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is where "not visited" lives, and it is deliberately not a Visit.</b> `VIS-07` is the
/// same requirement seen from the other side: capturing the reason against the *planned* call means
/// the Visit module never grows a state for a visit that did not happen — no half-created visit with
/// no check-in, no outcome and a reason field that is null for every real one.
/// </para>
/// <para>
/// Announced because it is the one rep-side annotation another module reasons about: `BR-JRN-6`
/// measures whether an outlet got its calls, and reporting will want the reasons in aggregate —
/// "forty per cent of misses this month were 'closed on arrival'" is a fact about the territory, not
/// about one round. A reschedule inside a cycle raises nothing, because nothing outside changes.
/// </para>
/// </remarks>
public sealed record PlannedVisitMarkedNotVisited(
    Guid Id,
    DateTimeOffset OccurredOn,
    Guid JourneyPlanId,
    Guid PlannedVisitId,
    Guid OutletId,
    string UserId,
    DateOnly Date,
    string Reason) : IIntegrationEvent;

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
