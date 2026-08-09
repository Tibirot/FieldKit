using FieldKit.BuildingBlocks;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Visit;

/// <summary>Where a visit has got to.</summary>
public enum VisitStatus
{
    /// <summary>Checked in, being worked. The only state this slice can produce.</summary>
    InProgress,

    /// <summary>Checked out and sealed (<c>VIS-05</c>, <c>BR-VIS-4</c>) — slice 9.</summary>
    CheckedOut,
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

    public TenantId TenantId { get; set; }
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
        // BR-VIS-4's seal is not checked here because no visit can be checked out yet. It lands with
        // check-out, in the slice that creates the state — see VisitStatus.CheckedOut.
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
    /// The rule is "all mandatory steps complete before check-out", and check-out is the next slice.
    /// It is expressed here, now, because a rep needs to see what is outstanding <i>while they work</i>
    /// — being told at the door that the visit cannot end is the version of this rule that wastes a
    /// trip back into the shop.
    /// </para>
    /// <para>
    /// Returns the steps rather than a boolean for the same reason the geofence carries a distance:
    /// "you cannot check out" is not actionable, and "the audit and the order are still open" is.
    /// </para>
    /// </remarks>
    public IReadOnlyList<VisitStep> OpenMandatorySteps() =>
        [.. _steps.Where(step => step.IsOpenAndMandatory).OrderBy(step => step.Order)];
}
