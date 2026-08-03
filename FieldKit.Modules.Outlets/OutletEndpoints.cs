using System.Text.Json.Serialization;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Outlets;

/// <summary>An outlet as the back office sees it.</summary>
/// <remarks>
/// <paramref name="Status"/> travels as its name rather than its ordinal. The converter is on the
/// property instead of the host's JSON options because it is this contract's decision, not a
/// platform-wide one — and a number on the wire would make the API's meaning depend on the order
/// members happen to sit in an enum.
/// </remarks>
public sealed record OutletResponse(
    Guid Id,
    string Code,
    string Name,
    Guid ChannelId,
    string ChannelName,
    string? Segment,
    string? Banner,
    [property: JsonConverter(typeof(JsonStringEnumConverter<OutletStatus>))] OutletStatus Status);

/// <summary>Create an outlet. <paramref name="Code"/> is the tenant's own identifier.</summary>
public sealed record CreateOutletRequest(
    string Code, string Name, Guid ChannelId, string? Segment, string? Banner);

/// <summary>Update the details. The code is not editable — see <see cref="Outlet.Update"/>.</summary>
public sealed record UpdateOutletRequest(string Name, Guid ChannelId, string? Segment, string? Banner);

/// <summary>Move an outlet through its lifecycle (<c>OUT-04</c>). Accepts the status by name.</summary>
public sealed record OutletStatusRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter<OutletStatus>))] OutletStatus Status);

/// <summary>
/// The outlet base (<c>OUT-01</c>, <c>OUT-04</c>).
/// </summary>
internal static class OutletEndpoints
{
    public static void MapOutletEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var outlets = endpoints.MapGroup("/api/outlets").WithTags("Outlets");

        // Filters rather than one list: an outlet base is the biggest table a tenant has, and the
        // two questions a back office actually asks of it are "who is in this channel" and "what is
        // still open". Both are indexed.
        outlets.MapGet("/", async (
                Guid? channelId, OutletStatus? status, OutletsDbContext db, CancellationToken ct) =>
            await Project(
                    db,
                    db.Outlets
                        .Where(outlet => channelId == null || outlet.ChannelId == channelId)
                        .Where(outlet => status == null || outlet.Status == status))
                .ToListAsync(ct))
            .RequirePermission(OutletsPermissions.OutletRead);

        outlets.MapGet("/{id:guid}", async (Guid id, OutletsDbContext db, CancellationToken ct) =>
                await Single(db, id, ct) is { } outlet ? Results.Ok(outlet) : Results.NotFound())
            .RequirePermission(OutletsPermissions.OutletRead);

        outlets.MapPost("/", async (
            CreateOutletRequest request, OutletsDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code))
            {
                return Results.BadRequest(new { error = "An outlet needs a code." });
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "An outlet needs a name." });
            }

            if (await ChannelProblem(db, request.ChannelId, ct) is { } channelProblem) return channelProblem;

            if (await db.Outlets.AnyAsync(outlet => outlet.Code == request.Code, ct))
            {
                return Results.Conflict(new { error = $"An outlet with code '{request.Code}' already exists." });
            }

            var created = Outlet.Create(
                request.Code, request.Name, request.ChannelId, request.Segment, request.Banner);

            db.Outlets.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/outlets/{created.Id}", await Single(db, created.Id, ct));
        }).RequirePermission(OutletsPermissions.OutletWrite);

        outlets.MapPut("/{id:guid}", async (
            Guid id, UpdateOutletRequest request, OutletsDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "An outlet needs a name." });
            }

            var outlet = await db.Outlets.SingleOrDefaultAsync(o => o.Id == id, ct);
            if (outlet is null) return Results.NotFound();

            if (await ChannelProblem(db, request.ChannelId, ct) is { } channelProblem) return channelProblem;

            outlet.Update(request.Name, request.ChannelId, request.Segment, request.Banner, clock);
            await db.SaveChangesAsync(ct);

            return Results.Ok(await Single(db, id, ct));
        }).RequirePermission(OutletsPermissions.OutletWrite);

        // Its own endpoint, not a field on the update. "This store is shut" is a different decision
        // from "the name was spelled wrong", and merging them lets a careless edit close an outlet
        // as a side effect of fixing a typo.
        outlets.MapPost("/{id:guid}/status", async (
            Guid id, OutletStatusRequest request, OutletsDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(request.Status))
            {
                return Results.BadRequest(new { error = "Unknown outlet status." });
            }

            var outlet = await db.Outlets.SingleOrDefaultAsync(o => o.Id == id, ct);
            if (outlet is null) return Results.NotFound();

            if (outlet.ChangeStatus(request.Status, clock) is { } refusal)
            {
                return Results.Conflict(new { error = refusal });
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(await Single(db, id, ct));
        }).RequirePermission(OutletsPermissions.OutletWrite);
    }

    /// <summary>
    /// Rejects a channel this tenant does not have.
    /// </summary>
    /// <remarks>
    /// BR-OUT-1 in the one place it can be enforced with a useful message. The foreign key would
    /// also refuse, but as a constraint violation rather than "that channel does not exist" — and
    /// the query filter means another tenant's channel id is simply unknown here, which is the
    /// right answer rather than one that confirms it exists elsewhere.
    /// </remarks>
    private static async Task<IResult?> ChannelProblem(
        OutletsDbContext db, Guid channelId, CancellationToken ct) =>
        await db.Channels.AnyAsync(channel => channel.Id == channelId, ct)
            ? null
            : Results.BadRequest(new { error = "The channel does not exist." });

    /// <summary>
    /// Joins the channel name in, so a list of outlets is readable without a second call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both ids and the name are returned: the id is what a client edits with, the name is what it
    /// renders. Returning only the id would make every screen fetch the channel list to caption a
    /// table it already has.
    /// </para>
    /// <para>
    /// <b>Every filter and the ordering are applied to <paramref name="source"/>, before the join.</b>
    /// Composing them onto the projection instead does not translate — EF cannot push a predicate or
    /// an <c>OrderBy</c> back through a manual join into a constructor projection, and the query
    /// fails at runtime rather than at compile time. That is how this was found.
    /// </para>
    /// </remarks>
    private static IQueryable<OutletResponse> Project(OutletsDbContext db, IQueryable<Outlet> source) =>
        from outlet in source.OrderBy(outlet => outlet.Code)
        join channel in db.Channels on outlet.ChannelId equals channel.Id
        select new OutletResponse(
            outlet.Id,
            outlet.Code,
            outlet.Name,
            channel.Id,
            channel.Name,
            outlet.Segment,
            // The enum, not `.ToString()` — that is not translatable to SQL, and the column already
            // holds the name anyway. Rendering it as text is the JSON layer's job.
            outlet.Banner,
            outlet.Status);

    private static async Task<OutletResponse?> Single(OutletsDbContext db, Guid id, CancellationToken ct) =>
        await Project(db, db.Outlets.Where(outlet => outlet.Id == id)).SingleOrDefaultAsync(ct);
}
