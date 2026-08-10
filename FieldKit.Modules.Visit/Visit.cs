using FieldKit.BuildingBlocks;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Visit.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Visit;

/// <summary>Where a visit has got to.</summary>
public enum VisitStatus
{
    /// <summary>Checked in, being worked.</summary>
    InProgress,

    /// <summary>Checked out and sealed (<c>VIS-05</c>, <c>BR-VIS-4</c>).</summary>
    CheckedOut,
}

/// <summary>
/// What the visit came to (<c>VIS-05</c>).
/// </summary>
/// <remarks>
/// Two values and not a list of outcome codes, deliberately. "Productive or not" is the question
/// every strike-rate report asks, and it is the same question in every tenant; <i>why</i> a call was
/// unproductive is a sentence the rep writes, not a vocabulary a back office maintains. A code list
/// here would be a third classification to configure and the first one nobody could compare across
/// tenants.
/// </remarks>
public enum VisitOutcome
{
    /// <summary>Something came of it — an order, an audit, the job.</summary>
    Productive,

    /// <summary>Nothing did, and the rep says why.</summary>
    NonProductive,
}

/// <summary>
/// How a visit reached this server (<c>VIS-05</c>, W9 slice 0).
/// </summary>
/// <remarks>
/// <para>
/// <b>Stored, not derived.</b> The obvious shortcut is to compare <see cref="Visit.CreatedAtUtc"/>
/// with <see cref="Visit.CheckedOutAtUtc"/> and call a gap "offline" — and it is wrong in the case
/// that matters. A rep who checks out in a shop with signal drains within seconds, so an ingested
/// visit and a live one produce identical timestamps. The gap tells you a visit was drained *late*;
/// only this says it came from a phone at all.
/// </para>
/// <para>
/// <b>Nullable, and null means "recorded before this was tracked".</b> Every visit stored before
/// this column existed genuinely has no answer: nothing was written down at the time, and inferring
/// one from timestamps would manufacture a fact with the same shape as a real one and no way to tell
/// them apart. A column that admits a gap is worth more than a column that fills it in.
/// </para>
/// </remarks>
public enum VisitSource
{
    /// <summary>Worked online, through <c>/api/visits/check-in</c>.</summary>
    Live,

    /// <summary>Captured on a device and drained through <c>/sync/push</c> (<c>OFF-04</c>).</summary>
    Device,
}

/// <summary>
/// One in-store engagement: a rep, an outlet, and what happened between check-in and check-out
/// (<c>VIS-01</c>, <c>BR-VIS-1</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One outlet and one rep, fixed at check-in.</b> <c>BR-VIS-1</c>, and it is why neither is
/// settable afterwards: the visit's children — audits, orders, notes — all belong to it by way of
/// those two, and moving either would silently re-attribute work that was already done.
/// </para>
/// <para>
/// <b>The geo-stamp is what was captured, not what was concluded.</b> The position, the distance and
/// whether it counted as inside are all stored, because a supervisor reviewing an override needs to
/// see the number rather than a verdict — "eighty metres" is a different conversation from "two
/// kilometres", and a boolean flattens both into "flagged".
/// </para>
/// <para>
/// <b>A visit may exist without a planned one.</b> An unplanned call is ordinary
/// (<c>JRN-06</c>), so <see cref="PlannedVisitId"/> is nullable — and it is a bare id rather than a
/// foreign key, because the plan lives in Journey's schema (AT-1).
/// </para>
/// </remarks>
public sealed class Visit : AggregateRoot, ITenantOwned, IAuditable
{
    /// <summary>The column width for an out-of-geofence override reason.</summary>
    public const int MaximumOverrideReasonLength = 500;

    /// <summary>The column width for why a visit came to nothing.</summary>
    public const int MaximumOutcomeReasonLength = 500;

    public Guid Id { get; private set; }

    public Guid OutletId { get; private set; }

    /// <summary>The rep — the Keycloak subject, the same identifier a plan uses.</summary>
    public string UserId { get; private set; } = null!;

    /// <summary>The planned call this fulfils, when there was one (<c>JRN-04</c>).</summary>
    public Guid? PlannedVisitId { get; private set; }

    public VisitStatus Status { get; private set; }

    public DateTimeOffset CheckedInAtUtc { get; private set; }

    /// <summary>Where the device said the rep was. Null when it had no fix.</summary>
    public double? CheckInLatitude { get; private set; }

    public double? CheckInLongitude { get; private set; }

    /// <summary>How far that was from the outlet, when both positions were known.</summary>
    public double? CheckInDistanceMetres { get; private set; }

    /// <summary>Whether the rep was within the outlet's geofence at check-in.</summary>
    public bool WasInsideGeofence { get; private set; }

    /// <summary>
    /// Why the rep was not at the outlet. Null when they were, or when nobody expected them to be.
    /// </summary>
    public string? GeofenceOverrideReason { get; private set; }

    /// <summary>
    /// The steps this visit was started with (<c>VIS-03</c>), in order.
    /// </summary>
    /// <remarks>
    /// Fixed at check-in and never added to: see <see cref="VisitStep"/> for why the workflow is
    /// copied rather than consulted. A visit whose channel has no configured workflow has none, which
    /// is a real visit — check in, check out — and not a misconfiguration.
    /// </remarks>
    public IReadOnlyCollection<VisitStep> Steps => _steps;

    private readonly List<VisitStep> _steps = [];

    /// <summary>When the rep left. Null while the visit is still being worked.</summary>
    public DateTimeOffset? CheckedOutAtUtc { get; private set; }

    /// <summary>Where they were when they left (<c>VIS-05</c>). Null when the device had no fix.</summary>
    public double? CheckOutLatitude { get; private set; }

    public double? CheckOutLongitude { get; private set; }

    /// <summary>What the visit came to. Null until it ends.</summary>
    public VisitOutcome? Outcome { get; private set; }

    /// <summary>
    /// Why nothing came of it. Required for a non-productive visit and absent for a productive one.
    /// </summary>
    public string? OutcomeReason { get; private set; }

    /// <summary>
    /// How long the rep was in the shop (<c>BR-VIS-5</c>), or null while they still are.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored: it is exactly check-out minus check-in, and a stored copy is a
    /// second answer to the same question that can disagree with the first. <b>Nothing here flags an
    /// abnormally short or long visit</b> — <c>BR-VIS-5</c> is explicit that those are reporting
    /// facts and never blocks, and the threshold that decides "abnormal" is a reporting decision
    /// (<c>VIS-10</c>, Phase 3) made against a population this system does not have yet.
    /// </remarks>
    public TimeSpan? TimeOnSite =>
        CheckedOutAtUtc is { } left ? left - CheckedInAtUtc : null;

    /// <summary>
    /// How this visit reached the server (<c>VIS-05</c>). Null for visits stored before it was
    /// recorded — see <see cref="VisitSource"/>.
    /// </summary>
    public VisitSource? Source { get; private set; }

    public TenantId TenantId { get; set; }

    /// <summary>
    /// <b>When this server first stored the visit</b>, stamped from <c>IClock</c> by
    /// <c>EntityStampingInterceptor</c> — which makes it the visit's provenance timestamp, and the
    /// reason there is no second column for one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the audit field every entity carries, and on this one it happens to answer a domain
    /// question: an ingested visit holds the <i>device's</i> <see cref="CheckedInAtUtc"/> and
    /// <see cref="CheckedOutAtUtc"/>, so the distance between those and this is how long the work sat
    /// on a phone. W9 slice 0 was planned as a new <c>RecordedAtUtc</c> beside them; the interceptor
    /// had been writing exactly that value on both paths since W1, and a second column would have
    /// been the "second answer to the same question that can disagree with the first" that
    /// <see cref="TimeOnSite"/> is derived to avoid.
    /// </para>
    /// <para>
    /// It also means the value exists for <b>every</b> visit already stored, with no backfill and no
    /// guess — unlike <see cref="Source"/>, which nobody was writing down.
    /// </para>
    /// <para>
    /// A device claiming a <see cref="CheckedOutAtUtc"/> later than this is claiming the future,
    /// which no correct device does. Nothing acts on that: it is a signal for <c>VIS-10</c>/W13
    /// reporting to weigh against a real population, not a rule, and <c>BR-VIS-2</c>'s "never block
    /// the rep, always record" is the same answer one layer down.
    /// </para>
    /// </remarks>
    public DateTimeOffset CreatedAtUtc { get; set; }

    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Visit() { } // EF

    /// <summary>
    /// Starts a visit (<c>VIS-01</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the assessment rather than making it, so the rule stays in
    /// <see cref="Geofencing"/> where it can be tested without a database — and so this cannot
    /// quietly disagree with what the endpoint told the rep.
    /// </para>
    /// <para>
    /// Takes the workflow's steps for the same reason it takes the assessment, and copies them: from
    /// here on the visit carries its own list, and Configuration can be edited underneath it without
    /// changing what this rep was asked to do (<see cref="VisitStep"/>, <c>BR-VIS-6</c>).
    /// </para>
    /// </remarks>
    public static Visit CheckIn(
        Guid outletId,
        string userId,
        Guid? plannedVisitId,
        GeoPoint? at,
        GeofenceAssessment assessment,
        string? overrideReason,
        IReadOnlyList<VisitStepDescriptor> steps,
        IClock clock)
    {
        var visit = new Visit
        {
            Id = Guid.CreateVersion7(),
            OutletId = outletId,
            UserId = userId,
            PlannedVisitId = plannedVisitId,
            Status = VisitStatus.InProgress,
            Source = VisitSource.Live,
            CheckedInAtUtc = clock.UtcNow,
            CheckInLatitude = at?.Latitude,
            CheckInLongitude = at?.Longitude,
            CheckInDistanceMetres = assessment.DistanceMetres,
            WasInsideGeofence = assessment.Inside,

            // Kept only when it was actually needed. A reason volunteered for a check-in that was
            // inside the geofence is noise on a supervisor's screen, and it would make "how many
            // overrides this month" a count of typing rather than of exceptions.
            GeofenceOverrideReason = assessment.ReasonRequired ? overrideReason?.Trim() : null,
        };

        visit._steps.AddRange(
            steps.OrderBy(step => step.Order).Select(step => VisitStep.From(visit.Id, step)));

        return visit;
    }

    /// <summary>
    /// A visit that already happened offline, arriving through <c>IVisitIngest</c> (W8 slice 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sealed on arrival: it is created checked-out, with the device's timestamps rather than the
    /// server's clock. A visit worked yesterday and pushed today is a record of yesterday, and
    /// stamping it with now would make every offline day look like it happened at reconnection.
    /// </para>
    /// <para>
    /// It does not call <see cref="CheckIn"/> and then <see cref="TryCheckOut"/>, and that is
    /// deliberate rather than lazy. Those two enforce rules about a visit in progress — a geofence
    /// assessed against the outlet's current radius, mandatory steps from the channel's current
    /// workflow — and re-running them against *today's* configuration would re-judge a completed
    /// visit under rules that may have been republished since. The rules that still mean something
    /// are enforced by the caller (<c>VisitIngestService</c>), where they can produce a refusal a
    /// device can act on.
    /// </para>
    /// </remarks>
    internal static Visit Ingest(
        CapturedVisit captured, string userId, VisitOutcome outcome, IReadOnlyList<VisitStep> steps)
    {
        var visit = new Visit
        {
            Id = captured.VisitId,
            OutletId = captured.OutletId,
            UserId = userId,
            PlannedVisitId = captured.PlannedVisitId,
            Status = VisitStatus.CheckedOut,

            // The one fact about this visit that only the arrival path knows. Everything else here
            // is the device's; this is the server saying where it got it.
            Source = VisitSource.Device,
            CheckedInAtUtc = captured.CheckedInAtUtc,
            CheckInLatitude = captured.CheckInLatitude,
            CheckInLongitude = captured.CheckInLongitude,
            CheckInDistanceMetres = captured.CheckInDistanceMetres,

            // The device's verdict, copied rather than recomputed. It knew where the phone was and
            // what the radius was at the time; this server knows neither (`CapturedVisit`).
            WasInsideGeofence = captured.WasInsideGeofence,
            GeofenceOverrideReason = string.IsNullOrWhiteSpace(captured.OverrideReason)
                ? null
                : captured.OverrideReason.Trim(),
            CheckedOutAtUtc = captured.CheckedOutAtUtc,
            CheckOutLatitude = captured.CheckOutLatitude,
            CheckOutLongitude = captured.CheckOutLongitude,
            Outcome = outcome,
            OutcomeReason = outcome == VisitOutcome.NonProductive
                ? captured.OutcomeReason?.Trim()
                : null,
        };

        visit._steps.AddRange(steps);

        // The same event the online path raises, so everything downstream of a completed visit is
        // downstream of an ingested one too — a subscriber must not have to know how it arrived.
        visit.Raise(new VisitCompleted(
            Guid.CreateVersion7(),
            captured.CheckedOutAtUtc,
            visit.Id,
            visit.OutletId,
            visit.UserId,
            visit.PlannedVisitId,
            captured.CheckedInAtUtc,
            captured.CheckedOutAtUtc,
            outcome.ToString(),
            visit._steps.Count,
            visit._steps.Count(step => step.Status == VisitStepStatus.Completed)));

        return visit;
    }

    /// <summary>Why a step could not be completed.</summary>
    public enum StepRefusal
    {
        None,

        /// <summary>No such step on this visit.</summary>
        NoSuchStep,

        /// <summary>
        /// It is already done.
        /// </summary>
        /// <remarks>
        /// Refused rather than overwritten, for the reason a not-visited reason is
        /// (<c>JRN-06</c>): the first completion's timestamp is a fact about the rep's day, and
        /// silently replacing it would make time-on-step a measure of the last edit.
        /// </remarks>
        AlreadyCompleted,

        /// <summary>A <see cref="VisitStepType.Note"/> step completed with nothing written.</summary>
        NoteRequired,

        /// <summary>The visit is checked out, and nothing about it changes again (<c>BR-VIS-4</c>).</summary>
        VisitSealed,
    }

    /// <summary>
    /// Records that the rep did a step (<c>VIS-03</c>).
    /// </summary>
    /// <remarks>
    /// <b>Completing is an assertion by the rep, not a consequence of anything yet.</b> An audit or
    /// order step will eventually be completed <i>by</i> its child work — that lands with the Audit
    /// and Order modules in Phase 3 — and this route is what a checklist, a note or a task step
    /// needs today. It is deliberately not "mark anything done": the step must exist on this visit,
    /// and a note step with no note is not a completed note.
    /// </remarks>
    public StepRefusal TryCompleteStep(Guid stepId, string? notes, IClock clock)
    {
        // BR-VIS-4, first: a sealed visit answers the same way whether or not the step exists, so a
        // late write cannot use this route to discover what was on somebody's visit.
        if (Status == VisitStatus.CheckedOut) return StepRefusal.VisitSealed;

        if (_steps.SingleOrDefault(step => step.Id == stepId) is not { } target)
        {
            return StepRefusal.NoSuchStep;
        }

        if (target.Status == VisitStepStatus.Completed) return StepRefusal.AlreadyCompleted;

        if (target.Type == VisitStepType.Note && string.IsNullOrWhiteSpace(notes))
        {
            return StepRefusal.NoteRequired;
        }

        target.Complete(notes, clock);

        return StepRefusal.None;
    }

    /// <summary>
    /// The mandatory steps still open — <c>BR-VIS-3</c>, as a question rather than a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule is "all mandatory steps complete before check-out", and <see cref="TryCheckOut"/> is
    /// what enforces it. It is answered here as well, on every response that returns a visit, because
    /// a rep needs to see what is outstanding <i>while they work</i> — being told at the door that the
    /// visit cannot end is the version of this rule that wastes a trip back into the shop.
    /// </para>
    /// <para>
    /// Returns the steps rather than a boolean for the same reason the geofence carries a distance:
    /// "you cannot check out" is not actionable, and "the audit and the order are still open" is.
    /// </para>
    /// </remarks>
    public IReadOnlyList<VisitStep> OpenMandatorySteps() =>
        [.. _steps.Where(step => step.IsOpenAndMandatory).OrderBy(step => step.Order)];

    /// <summary>Why a visit could not be ended.</summary>
    public enum CheckOutRefusal
    {
        None,

        /// <summary>It has already ended, and is sealed (<c>BR-VIS-4</c>).</summary>
        AlreadyCheckedOut,

        /// <summary>A mandatory step is still open (<c>BR-VIS-3</c>).</summary>
        MandatoryStepsOpen,

        /// <summary>A non-productive visit with no reason given.</summary>
        ReasonRequired,
    }

    /// <summary>
    /// Ends the visit and seals it (<c>VIS-05</c>, <c>BR-VIS-3</c>, <c>BR-VIS-4</c>, <c>BR-VIS-5</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This one does block, and check-in does not.</b> The two ends of a visit are opposite in
    /// temperament on purpose: <c>BR-VIS-2</c> refuses to keep a rep out of a shop, while
    /// <c>BR-VIS-3</c> refuses to let a visit be filed as done when the work it was configured for is
    /// not. Nothing is lost by refusing here — the rep is still in the shop, still checked in, and
    /// the refusal names the steps.
    /// </para>
    /// <para>
    /// <b>No geofence check on the way out.</b> The position is captured (<c>VIS-05</c>) because two
    /// points are a cheap counter against a visit that was never really worked, but nothing is
    /// refused or flagged on it: a rep who has done the job and walked to the car has not done
    /// anything wrong, and a second override prompt at the door would be the flag that fires on
    /// ordinary work.
    /// </para>
    /// <para>
    /// <b>Sealed, not locked.</b> Once this returns <see cref="CheckOutRefusal.None"/>, every write
    /// path on this aggregate refuses — steps included. That is what makes the visit safe to push
    /// through Sync without a conflict story (<c>B7</c>): the device owns it until it is done, and
    /// after that nobody owns it.
    /// </para>
    /// </remarks>
    public CheckOutRefusal TryCheckOut(
        VisitOutcome outcome, string? reason, GeoPoint? at, IClock clock)
    {
        if (Status == VisitStatus.CheckedOut) return CheckOutRefusal.AlreadyCheckedOut;

        if (OpenMandatorySteps().Count > 0) return CheckOutRefusal.MandatoryStepsOpen;

        if (outcome == VisitOutcome.NonProductive && string.IsNullOrWhiteSpace(reason))
        {
            return CheckOutRefusal.ReasonRequired;
        }

        Status = VisitStatus.CheckedOut;
        CheckedOutAtUtc = clock.UtcNow;
        CheckOutLatitude = at?.Latitude;
        CheckOutLongitude = at?.Longitude;
        Outcome = outcome;

        // Kept only where it means something, like the geofence override reason: "why was nothing
        // sold" is the reporting fact, and a sentence attached to a productive call would put noise
        // in the same column a supervisor reads that answer from.
        OutcomeReason = outcome == VisitOutcome.NonProductive ? reason!.Trim() : null;

        Raise(new VisitCompleted(
            Guid.CreateVersion7(),
            clock.UtcNow,
            Id,
            OutletId,
            UserId,
            PlannedVisitId,
            CheckedInAtUtc,
            CheckedOutAtUtc.Value,
            outcome.ToString(),
            _steps.Count,
            _steps.Count(step => step.Status == VisitStepStatus.Completed)));

        return CheckOutRefusal.None;
    }
}

/// <summary>
/// A visit was worked and sealed (<c>VIS-05</c>).
/// </summary>
/// <remarks>
/// <para>
/// Delivered through the outbox, to consumers that do not exist yet: reporting reads it for
/// strike rate and time-on-site, Journey will read it to mark a planned call done, and Sync (W8)
/// carries it. That is the established shape here — <c>PriceListPublished</c> has been emitted into
/// an empty room since W6 — and it is the right shape: an event is a statement about something that
/// happened, true whether or not anyone is listening.
/// </para>
/// <para>
/// <b>It carries a summary, not the visit.</b> Step notes, positions and override reasons stay in
/// the module that owns them; what travels is what a consumer needs to decide whether the visit
/// interests it — whose, where, when, what it came to, and how much of the configured work was
/// done. <c>VIS-05</c>'s "children summary" is the two step counts today; audits and orders add
/// their own when those modules exist, and adding a field to an event is a thing a consumer can
/// ignore.
/// </para>
/// <para>
/// <b>Time-on-site is not a field.</b> It is check-out minus check-in, both of which are here, and
/// a computed duplicate is a second answer that can disagree with the first.
/// </para>
/// </remarks>
/// <param name="Outcome">Productive or not, by name — see <see cref="VisitOutcome"/>.</param>
/// <param name="StepsCompleted">
/// How many of <paramref name="StepCount"/> were done. Mandatory ones are necessarily all of them
/// (<c>BR-VIS-3</c>), so a gap here is optional work the rep chose to skip — which is the reporting
/// question this pair exists to answer.
/// </param>
public sealed record VisitCompleted(
    Guid Id,
    DateTimeOffset OccurredOn,
    Guid VisitId,
    Guid OutletId,
    string UserId,
    Guid? PlannedVisitId,
    DateTimeOffset CheckedInAtUtc,
    DateTimeOffset CheckedOutAtUtc,
    string Outcome,
    int StepCount,
    int StepsCompleted) : IIntegrationEvent;
