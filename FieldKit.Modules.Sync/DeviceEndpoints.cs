using FieldKit.BuildingBlocks;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

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
            await db.SaveChangesAsync(ct);

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
/// </param>
public sealed record RevokeDeviceRequest(DeactivationReason Reason);

public sealed record DeviceResponse(
    Guid Id,
    string UserId,
    string Name,
    DateTimeOffset BoundAtUtc,
    bool IsActive,
    string? DeactivatedBecause,
    DateTimeOffset? DeactivatedAtUtc);
