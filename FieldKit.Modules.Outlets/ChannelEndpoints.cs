using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Outlets;

/// <summary>A trade classification.</summary>
public sealed record ChannelResponse(Guid Id, string Name);

/// <summary>Create or rename a channel.</summary>
public sealed record ChannelRequest(string Name);

/// <summary>
/// The trade classifications a tenant works with (<c>OUT-01</c>).
/// </summary>
internal static class ChannelEndpoints
{
    public static void MapChannelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var channels = endpoints.MapGroup("/api/outlets/channels").WithTags("Outlets");

        channels.MapGet("/", async (OutletsDbContext db, CancellationToken ct) =>
                await db.Channels
                    .OrderBy(channel => channel.Name)
                    .Select(channel => new ChannelResponse(channel.Id, channel.Name))
                    .ToListAsync(ct))
            .RequirePermission(OutletsPermissions.ChannelRead);

        channels.MapPost("/", async (ChannelRequest request, OutletsDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Problems.BadRequest("name", "A channel needs a name.");
            }

            if (await db.Channels.AnyAsync(channel => channel.Name.ToLower() == request.Name.ToLower(), ct))
            {
                return Problems.Conflict("name", $"A channel named '{request.Name}' already exists.");
            }

            var created = Channel.Create(request.Name);
            db.Channels.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/outlets/channels/{created.Id}", new ChannelResponse(created.Id, created.Name));
        }).RequirePermission(OutletsPermissions.ChannelWrite);

        channels.MapPut("/{id:guid}", async (
            Guid id, ChannelRequest request, OutletsDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Problems.BadRequest("name", "A channel needs a name.");
            }

            var channel = await db.Channels.SingleOrDefaultAsync(c => c.Id == id, ct);
            if (channel is null) return Results.NotFound();

            if (await db.Channels.AnyAsync(c => c.Name.ToLower() == request.Name.ToLower() && c.Id != id, ct))
            {
                return Problems.Conflict("name", $"A channel named '{request.Name}' already exists.");
            }

            // Renaming is safe in a way deleting is not: everything that keys off a channel keys off
            // its id, so the label can change without any rule silently stopping matching.
            channel.Rename(request.Name, clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new ChannelResponse(channel.Id, channel.Name));
        }).RequirePermission(OutletsPermissions.ChannelWrite);

        channels.MapDelete("/{id:guid}", async (Guid id, OutletsDbContext db, CancellationToken ct) =>
        {
            var channel = await db.Channels.SingleOrDefaultAsync(c => c.Id == id, ct);
            if (channel is null) return Results.NotFound();

            // BR-OUT-1 says every outlet has a channel, so there is no such thing as removing one
            // from underneath the outlets that use it. The foreign key would refuse anyway; this is
            // what turns that into an answer an admin can act on.
            var inUse = await db.Outlets.CountAsync(outlet => outlet.ChannelId == id, ct);
            if (inUse > 0)
            {
                return Problems.Conflict(
                    $"{inUse} outlet(s) are classified as '{channel.Name}'. Reclassify them first.");
            }

            db.Channels.Remove(channel);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(OutletsPermissions.ChannelWrite);
    }
}
