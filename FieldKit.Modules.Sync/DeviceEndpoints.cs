using System.Text.Json.Serialization;
using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FieldKit.Modules.Sync;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var devices = endpoints.MapGroup("/api/sync/devices").WithTags("Sync");

        /*
         * Bind this device to the caller (OFF-12, sync engine §7).
         *
         * Binding is what a rep does on a new phone, so it is authenticated but not permissioned —
         * see SyncPermissions. The device belongs to the subject in the token and to nobody else;
         * there is no user id in the request precisely so that one cannot be supplied.
         *
         * Registering deactivates the rep's previous device as `Swapped`, which keeps its right to
         * one final drain-push. A device that is gone rather than replaced is revoked below, as
         * `Compromised`, which does not.
         */
        devices.MapPost("/", async (
            BindDeviceRequest request,
            SyncDbContext db,
            ITenantContext tenant,
            IClock clock,
            CancellationToken ct) =>
        {
            var name = request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Problems.BadRequest(
                    "name", "Give the device a name you will recognise in a list.", "device.bind.nameRequired");
            }

            var previous = await db.Devices
                .Where(device => device.UserId == tenant.UserId && device.IsActive)
                .ToListAsync(ct);

            // Plural on purpose. The unique index makes two actives impossible going forward, and
            // this code is what has to be true for the index to have been added — sweeping every
            // active row rather than assuming there is one keeps the two consistent.
            foreach (var device in previous)
                device.Deactivate(DeactivationReason.Swapped, clock.UtcNow);

            var bound = Device.Bind(tenant.UserId, name, clock.UtcNow);
            db.Devices.Add(bound);

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException failure) when (IsOneActivePerUserViolation(failure))
            {
                /*
                 * Two binds for one rep, at once — and the index is the only thing that can catch it.
                 *
                 * The read above and the insert below are separate statements, so two requests can
                 * both find no active device and both insert one. A pre-check cannot close that; the
                 * unique index can, and this is where its verdict becomes an answer rather than a 500.
                 *
                 * <b>Refused rather than resolved.</b> Returning the winner's id would be tidier and
                 * wrong: the caller would be handed a device id belonging to a *different phone*,
                 * and every push it made would be attributed there. A rep who really is setting up
                 * two devices has done something the model says is one at a time, and the honest
                 * answer is to say so and let them read `/mine`.
                 *
                 * Found in the browser during W9 slice 1: React's development double-invocation made
                 * one component bind twice, and the second attempt was a 500. The client now
                 * de-duplicates in flight, which fixed the symptom; this is the server half.
                 */
                return Problems.Conflict(
                    null,
                    "Another device was registered for you at the same moment. Check your devices and try again.",
                    "device.bind.raced");
            }

            return Results.Created($"/api/sync/devices/{bound.Id}", Describe(bound));
        }).RequireAuthorization();

        /// The rep's own devices. No permission: seeing which of your phones is active is not
        /// oversight of anybody.
        devices.MapGet("/mine", async (SyncDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var mine = await db.Devices
                .Where(device => device.UserId == tenant.UserId)
                .OrderByDescending(device => device.BoundAtUtc)
                .ToListAsync(ct);

            return Results.Ok(mine.Select(Describe));
        }).RequireAuthorization();

        // Everyone's devices — the administrator's view, and the one that needs a permission.
        devices.MapGet("/", async (SyncDbContext db, CancellationToken ct) =>
        {
            var all = await db.Devices
                .OrderBy(device => device.UserId)
                .ThenByDescending(device => device.BoundAtUtc)
                .ToListAsync(ct);

            return Results.Ok(all.Select(Describe));
        }).RequirePermission(SyncPermissions.DeviceRead);

        /*
         * Revoke a device the rep no longer holds.
         *
         * Separate from binding because the two differ on the thing that matters: a swap leaves the
         * old device able to drain one last push, and a compromised device must not push at all
         * (security §5). Nothing infers which happened — an administrator says so, because the
         * difference is about a phone in the world, not about a row.
         */
        devices.MapPost("/{id:guid}/revoke", async (
            Guid id,
            RevokeDeviceRequest request,
            SyncDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var device = await db.Devices.SingleOrDefaultAsync(candidate => candidate.Id == id, ct);
            if (device is null) return Results.NotFound();

            if (!device.IsActive)
            {
                return Problems.Conflict(
                    field: null,
                    "That device is already inactive.",
                    "device.revoke.alreadyInactive");
            }

            device.Deactivate(request.Reason, clock.UtcNow);
            await db.SaveChangesAsync(ct);

            return Results.Ok(Describe(device));
        }).RequirePermission(SyncPermissions.DeviceRevoke);
    }

    /// <summary>
    /// Whether a failed save was <c>UX_device_one_active_per_user</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Named, not merely "a unique violation".</b> A <c>catch</c> that turned every 23505 in this
    /// statement into <c>device.bind.raced</c> would one day answer that for a constraint added later
    /// and unrelated, and the refusal would be a confident lie. Matching the index by name means a
    /// different violation still surfaces as a 500 — which is the right answer for a bug nobody has
    /// designed a response to yet.
    /// </para>
    /// <para>
    /// The SQLSTATE and the index name both come from Postgres, which ADR-0005 already commits to;
    /// this is not a portability seam.
    /// </para>
    /// </remarks>
    private static bool IsOneActivePerUserViolation(DbUpdateException failure) =>
        failure.InnerException is PostgresException { SqlState: "23505" } violation
        && violation.ConstraintName == "UX_device_one_active_per_user";

    private static DeviceResponse Describe(Device device) => new(
        device.Id,
        device.UserId,
        device.Name,
        device.BoundAtUtc,
        device.IsActive,
        device.DeactivatedBecause?.ToString(),
        device.DeactivatedAtUtc);
}

public sealed record BindDeviceRequest(string? Name);

/// <param name="Reason">
/// Whether the device was replaced or lost. It decides whether the device may still drain captured
/// work, so it is required rather than defaulted — a default here would quietly pick a security
/// posture on an administrator's behalf.
/// <para>
/// By name, which is not what it did: without the converter this took the ordinal, so an
/// administrator revoking a stolen phone as <c>"Compromised"</c> got a 400 and <c>2</c> worked. On
/// a field that decides whether a suspect device may still push, a spelling nobody can guess from
/// the response — <see cref="DeviceResponse.DeactivatedBecause"/> is already a name — is worse than
/// merely inconsistent.
/// </para>
/// </param>
public sealed record RevokeDeviceRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter<DeactivationReason>))]
    DeactivationReason Reason);

public sealed record DeviceResponse(
    Guid Id,
    string UserId,
    string Name,
    DateTimeOffset BoundAtUtc,
    bool IsActive,
    string? DeactivatedBecause,
    DateTimeOffset? DeactivatedAtUtc);
