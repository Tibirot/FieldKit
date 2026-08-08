using FieldKit.BuildingBlocks;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.Modules.Outlets.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Visit;

/// <summary>Starting a visit at an outlet.</summary>
/// <param name="Latitude">
/// Where the device says the rep is. Null when it had no fix — which is a real state a rep can be
/// in, not a client bug, and one the visit records rather than refuses.
/// </param>
/// <param name="PlannedVisitId">The planned call this fulfils, when there was one.</param>
/// <param name="OverrideReason">Why the rep is not at the outlet, when they are not.</param>
public sealed record CheckInRequest(
    Guid OutletId,
    double? Latitude = null,
    double? Longitude = null,
    Guid? PlannedVisitId = null,
    string? OverrideReason = null);

/// <summary>A visit, as it is stored.</summary>
public sealed record VisitResponse(
    Guid Id,
    Guid OutletId,
    string UserId,
    Guid? PlannedVisitId,
    string Status,
    DateTimeOffset CheckedInAtUtc,
    double? CheckInLatitude,
    double? CheckInLongitude,
    double? CheckInDistanceMetres,
    bool WasInsideGeofence,
    string? GeofenceOverrideReason);

/// <summary>
/// Check-in (<c>VIS-01</c>, <c>VIS-02</c>, <c>BR-VIS-2</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rep is taken from the token, never from the body.</b> A visit is a statement about who was
/// where, and a caller able to name somebody else is a caller able to name the wrong person — which
/// is the whole of the audit trail this module exists to produce.
/// </para>
/// <para>
/// <b>Never blocks.</b> <c>BR-VIS-2</c> is emphatic that a rep outside the geofence still gets to
/// work: the 400 below asks for the sentence a supervisor will read, and once it is supplied the
/// visit starts exactly as it would have inside. Nothing here can turn a rep away from a shop.
/// </para>
/// </remarks>
internal static class VisitEndpoints
{
    public static void MapVisitEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var visits = endpoints.MapGroup("/api/visits").WithTags("Visit");

        visits.MapGet("/", async (
                Guid? outletId, string? userId, VisitDbContext db, CancellationToken ct) =>
            await db.Visits
                .Where(visit => (outletId == null || visit.OutletId == outletId)
                    && (userId == null || visit.UserId == userId))
                .OrderByDescending(visit => visit.CheckedInAtUtc)
                .Select(visit => Respond(visit))
                .ToListAsync(ct))
            .RequirePermission(VisitPermissions.Read);

        visits.MapGet("/{id:guid}", async (Guid id, VisitDbContext db, CancellationToken ct) =>
                await db.Visits.SingleOrDefaultAsync(visit => visit.Id == id, ct) is { } visit
                    ? Results.Ok(Respond(visit))
                    : Results.NotFound())
            .RequirePermission(VisitPermissions.Read);

        visits.MapPost("/check-in", async (
            CheckInRequest request,
            VisitDbContext db,
            IOutletGeofence geofences,
            IOutletClassification classification,
            IVisitWorkflow workflows,
            ITenantContext tenant,
            IClock clock,
            CancellationToken ct) =>
        {
            if (PointProblem(request) is { } pointProblem) return pointProblem;

            var geofence = await geofences.ForOutletAsync(request.OutletId, ct);

            if (geofence is null)
            {
                return Problems.BadRequest(
                    "outletId", "No such outlet in this tenant.", "visit.checkIn.unknownOutlet");
            }

            // The channel decides whether presence is expected at all — BR-VIS-2's assumption, and
            // the reason IVisitWorkflow was built a slice before this one. An outlet whose channel
            // has vanished falls to the workflow default, which expects presence: the safe way round.
            var classified = (await classification.ClassifyManyAsync([request.OutletId], ct))
                .SingleOrDefault();

            var workflow = classified is null
                ? null
                : await workflows.ForChannelAsync(classified.ChannelId, ct);

            var at = request.Latitude is { } latitude && request.Longitude is { } longitude
                ? new GeoPoint(latitude, longitude)
                : (GeoPoint?)null;

            var outletAt = geofence.Latitude is { } outletLat && geofence.Longitude is { } outletLon
                ? new GeoPoint(outletLat, outletLon)
                : (GeoPoint?)null;

            var assessment = Geofencing.Assess(
                at, outletAt, geofence.RadiusMetres, workflow?.PresenceExpected ?? true);

            if (assessment.ReasonRequired && string.IsNullOrWhiteSpace(request.OverrideReason))
            {
                // Asking, not refusing. The visit is allowed the moment a reason arrives — see the
                // remarks on this class, and BR-VIS-2, which is emphatic about the difference.
                return Problems.BadRequest(
                    "overrideReason",
                    "You are not at this outlet. Say why, and the visit will go ahead.",
                    "visit.checkIn.overrideReasonRequired",
                    new Dictionary<string, string>
                    {
                        ["distanceMetres"] = assessment.DistanceMetres is { } distance
                            ? Math.Round(distance).ToString("F0")
                            : "unknown",
                        ["radiusMetres"] = geofence.RadiusMetres.ToString(),
                    });
            }

            if (ReasonProblem(request.OverrideReason) is { } reasonProblem) return reasonProblem;

            var visit = Visit.CheckIn(
                request.OutletId,
                tenant.UserId,
                request.PlannedVisitId,
                at,
                assessment,
                request.OverrideReason,
                clock);

            db.Visits.Add(visit);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/visits/{visit.Id}", Respond(visit));
        }).RequirePermission(VisitPermissions.Write);
    }

    /// <summary>
    /// Refuses half a position, and one that is not on the earth.
    /// </summary>
    /// <remarks>
    /// The bounds are the same ones <see cref="GeoPoint"/> enforces for an outlet's own coordinates.
    /// Checked here rather than left to the constructor because a rep's device sending nonsense is a
    /// caller's mistake and deserves a 400 naming the field, not an exception from inside a value
    /// object.
    /// </remarks>
    private static IResult? PointProblem(CheckInRequest request)
    {
        var problems = new List<FieldProblem>();

        if (request.Latitude is null != request.Longitude is null)
        {
            problems.Add(new FieldProblem(
                request.Latitude is null ? "latitude" : "longitude",
                "A position needs both a latitude and a longitude.",
                "visit.checkIn.halfPosition"));
        }

        if (request.Latitude is { } latitude and (< -90 or > 90))
        {
            problems.Add(new FieldProblem(
                "latitude", "Latitude is between -90 and 90.", "visit.checkIn.latitudeOutOfRange"));
        }

        if (request.Longitude is { } longitude and (< -180 or > 180))
        {
            problems.Add(new FieldProblem(
                "longitude", "Longitude is between -180 and 180.", "visit.checkIn.longitudeOutOfRange"));
        }

        return problems.Count == 0 ? null : Problems.BadRequest(problems);
    }

    private static IResult? ReasonProblem(string? reason) =>
        reason is null
            ? null
            : TextLimits.TooLong(
                "overrideReason", reason.Trim(), Visit.MaximumOverrideReasonLength,
                "visit.checkIn.reasonTooLong") is { } tooLong
                ? Problems.BadRequest([tooLong])
                : null;

    private static VisitResponse Respond(Visit visit) => new(
        visit.Id,
        visit.OutletId,
        visit.UserId,
        visit.PlannedVisitId,
        visit.Status.ToString(),
        visit.CheckedInAtUtc,
        visit.CheckInLatitude,
        visit.CheckInLongitude,
        visit.CheckInDistanceMetres,
        visit.WasInsideGeofence,
        visit.GeofenceOverrideReason);
}
