using System.Text.Json;
using System.Text.Json.Serialization;
using FieldKit.Modules.Configuration.Contracts;
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
    [property: JsonConverter(typeof(JsonStringEnumConverter<OutletStatus>))] OutletStatus Status,
    string TimeZoneId,
    Address? Address,
    // The DTO, not the domain value: `GeoPoint` is a struct with read-only properties, so a .NET
    // client deserializing it would get the implicit parameterless constructor and two zeros. The
    // wire shape is identical either way — this just keeps a serialization concern out of a shared
    // domain type.
    Coordinates? Location,
    IReadOnlyList<OutletContact> Contacts,
    IReadOnlyDictionary<string, JsonElement> CustomFields);

/// <summary>Create an outlet. <paramref name="Code"/> is the tenant's own identifier.</summary>
public sealed record CreateOutletRequest(
    string Code,
    string Name,
    Guid ChannelId,
    string? Segment,
    string? Banner,
    string TimeZoneId,
    Address? Address = null,
    Coordinates? Location = null,
    IReadOnlyList<OutletContact>? Contacts = null,
    IReadOnlyDictionary<string, JsonElement>? CustomFields = null);

/// <summary>
/// Update the details. The code is not editable — see <see cref="Outlet.Update"/>.
/// </summary>
/// <remarks>
/// <paramref name="Contacts"/> replaces the list wholesale rather than patching it: a delta needs the
/// caller to know the current state, and two people editing one outlet would interleave silently.
/// Sending an empty list removes every contact, which is also how erasure works today.
/// </remarks>
public sealed record UpdateOutletRequest(
    string Name,
    Guid ChannelId,
    string? Segment,
    string? Banner,
    string TimeZoneId,
    Address? Address = null,
    Coordinates? Location = null,
    IReadOnlyList<OutletContact>? Contacts = null,
    IReadOnlyDictionary<string, JsonElement>? CustomFields = null);


/// <summary>Move an outlet through its lifecycle (<c>OUT-04</c>). Accepts the status by name.</summary>
/// <param name="Reason">Required when closing — see the endpoint for why only then.</param>
public sealed record OutletStatusRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter<OutletStatus>))] OutletStatus Status,
    string? Reason = null);

/// <summary>One transition in an outlet's life, as recorded (<c>OUT-04</c>).</summary>
public sealed record OutletStatusChangeResponse(
    [property: JsonConverter(typeof(JsonStringEnumConverter<OutletStatus>))] OutletStatus? From,
    [property: JsonConverter(typeof(JsonStringEnumConverter<OutletStatus>))] OutletStatus To,
    string? Reason,
    DateTimeOffset ChangedAtUtc,
    string? ChangedBy);

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
            CreateOutletRequest request, OutletsDbContext db, IFieldDefinitionCatalog fields,
            CancellationToken ct) =>
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
            if (LocationProblem(request.TimeZoneId, request.Location, out var location) is { } locationProblem)
            {
                return locationProblem;
            }

            if (await CustomFieldProblem(request.CustomFields, fields, ct) is { } fieldProblem)
            {
                return fieldProblem;
            }

            if (await db.Outlets.AnyAsync(outlet => outlet.Code == request.Code, ct))
            {
                return Results.Conflict(new { error = $"An outlet with code '{request.Code}' already exists." });
            }

            var created = Outlet.Create(
                request.Code,
                request.Name,
                request.ChannelId,
                request.Segment,
                request.Banner,
                request.TimeZoneId,
                request.Address,
                location,
                request.Contacts,
                request.CustomFields);

            db.Outlets.Add(created);

            // The trail starts at birth, with `from` null. Without this entry the history of an
            // outlet that was never deactivated is empty, and "no history" reads the same as
            // "history was lost".
            db.OutletStatusChanges.Add(
                OutletStatusChange.Record(created.Id, from: null, created.Status, reason: null));

            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/outlets/{created.Id}", await Single(db, created.Id, ct));
        }).RequirePermission(OutletsPermissions.OutletWrite);

        outlets.MapPut("/{id:guid}", async (
            Guid id, UpdateOutletRequest request, OutletsDbContext db, IFieldDefinitionCatalog fields,
            IClock clock, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "An outlet needs a name." });
            }

            var outlet = await db.Outlets.SingleOrDefaultAsync(o => o.Id == id, ct);
            if (outlet is null) return Results.NotFound();

            if (await ChannelProblem(db, request.ChannelId, ct) is { } channelProblem) return channelProblem;
            if (LocationProblem(request.TimeZoneId, request.Location, out var location) is { } locationProblem)
            {
                return locationProblem;
            }

            if (await CustomFieldProblem(request.CustomFields, fields, ct) is { } fieldProblem)
            {
                return fieldProblem;
            }

            outlet.Update(
                request.Name,
                request.ChannelId,
                request.Segment,
                request.Banner,
                request.TimeZoneId,
                request.Address,
                location,
                request.Contacts,
                request.CustomFields,
                clock);

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

            // A reason is required to close and optional otherwise. Closing is irreversible and
            // removes the outlet from every future journey, so "why" is the question an auditor will
            // ask about it — and the person who knows the answer is the one doing it, now.
            // Demanding one for a routine Active↔Inactive toggle would buy a field full of "." .
            if (request.Status == OutletStatus.Closed && string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { error = "Closing an outlet permanently requires a reason." });
            }

            var outlet = await db.Outlets.SingleOrDefaultAsync(o => o.Id == id, ct);
            if (outlet is null) return Results.NotFound();

            var previous = outlet.Status;

            if (outlet.ChangeStatus(request.Status, clock) is { } refusal)
            {
                return Results.Conflict(new { error = refusal });
            }

            // Only a real transition is recorded. A no-op request is accepted (it is idempotent),
            // but writing a row for it would fill the trail with entries where nothing happened.
            if (previous != outlet.Status)
            {
                db.OutletStatusChanges.Add(
                    OutletStatusChange.Record(outlet.Id, previous, outlet.Status, request.Reason));
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(await Single(db, id, ct));
        }).RequirePermission(OutletsPermissions.OutletWrite);

        // Read-only, and there is deliberately no endpoint to add, edit or remove an entry. The
        // trail is written as a side effect of the transitions above or not at all — an audit log
        // with a write API is a log that can be arranged after the fact.
        outlets.MapGet("/{id:guid}/status-history", async (
            Guid id, OutletsDbContext db, CancellationToken ct) =>
        {
            if (!await db.Outlets.AnyAsync(outlet => outlet.Id == id, ct)) return Results.NotFound();

            return Results.Ok(await db.OutletStatusChanges
                .Where(change => change.OutletId == id)
                // Id breaks ties: it is a v7 GUID, so it orders by creation time at a finer
                // resolution than the timestamp column. Two transitions in the same instant would
                // otherwise come back in whatever order Postgres felt like, and an audit trail whose
                // order is not deterministic is a set of facts with the sequence removed.
                .OrderByDescending(change => change.CreatedAtUtc)
                .ThenByDescending(change => change.Id)
                .Select(change => new OutletStatusChangeResponse(
                    change.From, change.To, change.Reason, change.CreatedAtUtc, change.CreatedBy))
                .ToListAsync(ct));
        }).RequirePermission(OutletsPermissions.OutletRead);
    }

    /// <summary>
    /// Rejects custom-field values the tenant's catalogue does not describe (<c>CFG-02</c>).
    /// </summary>
    /// <remarks>
    /// Outlets asks Configuration for the definitions and validates its own values, rather than
    /// handing them over to be judged. The rule belongs where the data is written: this module knows
    /// it is an outlet being saved and can say so, where a shared validator would have to say
    /// "entity".
    /// </remarks>
    private static async Task<IResult?> CustomFieldProblem(
        IReadOnlyDictionary<string, JsonElement>? values,
        IFieldDefinitionCatalog catalog,
        CancellationToken ct)
    {
        var definitions = await catalog.ForAsync(CustomFieldEntity.Outlet, ct);

        // Skipped entirely when the tenant has defined nothing and sent nothing — the common case,
        // and not worth a round trip's worth of ceremony to conclude there is nothing to check.
        if (definitions.Count == 0 && (values is null || values.Count == 0)) return null;

        var problems = CustomFieldValidator.Validate(values, definitions);

        return problems.Count == 0 ? null : Results.BadRequest(new { errors = problems });
    }

    /// <summary>
    /// Rejects an unknown time zone, or coordinates that are not a place on the earth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The time zone is always required: a visit's business day and a promotion's validity resolve in
    /// it, so an unknown zone is a wrong answer waiting to be given rather than a cosmetic problem.
    /// </para>
    /// <para>
    /// Coordinates are optional, and when supplied they are <b>always</b> checked. That is not a
    /// policy a tenant chooses — latitude 91 is not meaningful for any kind of outlet or any kind of
    /// visit. What <i>is</i> a policy, and lives elsewhere, is whether a rep must be standing at the
    /// outlet: BR-VIS-2 already says an out-of-geofence check-in is allowed with a recorded override
    /// reason, and remote-capable visit types are a Visit/Configuration concern.
    /// </para>
    /// </remarks>
    private static IResult? LocationProblem(string timeZoneId, Coordinates? location, out GeoPoint? point)
    {
        point = null;

        if (string.IsNullOrWhiteSpace(timeZoneId) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
        {
            return Results.BadRequest(new { error = $"'{timeZoneId}' is not a known IANA time zone." });
        }

        if (location is null) return null;

        // TryCreate rather than the constructor: this is untrusted input, and the caller deserves a
        // 400 naming the range instead of an exception from inside a value object.
        if (!GeoPoint.TryCreate(location.Latitude, location.Longitude, out var created))
        {
            return Results.BadRequest(new
            {
                error = "Latitude must be between -90 and 90, and longitude between -180 and 180.",
            });
        }

        point = created;
        return null;
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
        // AsNoTracking is load-bearing, not an optimisation: EF refuses to project an owned entity
        // (address, location, contacts) out of a *tracking* query, because a tracked owned instance
        // without its owner has no identity to be tracked by. Every use of this is a read.
        from outlet in source.AsNoTracking().OrderBy(outlet => outlet.Code)
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
            outlet.Status,
            outlet.TimeZoneId,
            outlet.Address,
            outlet.Latitude != null && outlet.Longitude != null
                ? new Coordinates(outlet.Latitude.Value, outlet.Longitude.Value)
                : null,
            outlet.Contacts,
            outlet.CustomFields);

    private static async Task<OutletResponse?> Single(OutletsDbContext db, Guid id, CancellationToken ct) =>
        await Project(db, db.Outlets.Where(outlet => outlet.Id == id)).SingleOrDefaultAsync(ct);
}
