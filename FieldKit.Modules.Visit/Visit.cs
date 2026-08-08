using FieldKit.BuildingBlocks;
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
    /// Takes the assessment rather than making it, so the rule stays in
    /// <see cref="Geofencing"/> where it can be tested without a database — and so this cannot
    /// quietly disagree with what the endpoint told the rep.
    /// </remarks>
    public static Visit CheckIn(
        Guid outletId,
        string userId,
        Guid? plannedVisitId,
        GeoPoint? at,
        GeofenceAssessment assessment,
        string? overrideReason,
        IClock clock) => new()
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
}
