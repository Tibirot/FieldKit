using FieldKit.Modules.Iam.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Journey;

/// <summary>A rep's working pattern, as it is set.</summary>
/// <remarks>
/// The days travel as their names — <c>["Monday","Wednesday"]</c> — for the reason every other enum
/// on this API does: an ordinal on the wire makes the meaning depend on the order members happen to
/// sit in, and <see cref="DayOfWeek"/>'s ordinals start the week on Sunday, which is exactly the
/// off-by-one a reader would not question.
/// </remarks>
public sealed record WorkingCalendarRequest(
    IReadOnlyList<DayOfWeek> WorkingDays,
    int VisitsPerDay);

/// <summary>A rep's working pattern, as it is stored.</summary>
public sealed record WorkingCalendarResponse(
    string UserId,
    string? DisplayName,
    IReadOnlyList<string> WorkingDays,
    int VisitsPerDay);

/// <summary>A date nobody works.</summary>
public sealed record HolidayRequest(DateOnly Date, string Name);

/// <summary>A date nobody works, as stored.</summary>
public sealed record HolidayResponse(Guid Id, DateOnly Date, string Name);

/// <summary>One day a rep can be sent out, and how many calls it holds.</summary>
public sealed record WorkingDayResponse(DateOnly Date, int Capacity);

/// <summary>
/// The working calendar: a rep's pattern, and the tenant's holidays (<c>JRN-02</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>PUT keyed by the rep</b>, like a segment frequency is keyed by its segment: a rep has at most
/// one calendar, so the natural identifier is the person rather than a generated id, and setting one
/// twice has set it once.
/// </para>
/// <para>
/// Holidays are POSTed rather than PUT, because a date is not a name the caller chooses — they are a
/// list a tenant adds to, and the unique index is what makes adding Christmas twice a refusal rather
/// than a duplicate.
/// </para>
/// </remarks>
internal static class CalendarEndpoints
{
    public static void MapCalendarEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var calendars = endpoints.MapGroup("/api/journey/calendars").WithTags("Journey");

        calendars.MapGet("/", async (
            JourneyDbContext db, IUserDirectory users, CancellationToken ct) =>
        {
            var rows = await db.WorkingCalendars.OrderBy(row => row.UserId).ToListAsync(ct);

            return await WithDisplayNamesAsync(rows, users, ct);
        }).RequirePermission(JourneyPermissions.Read);

        calendars.MapGet("/{userId}", async (
            string userId, JourneyDbContext db, IUserDirectory users, CancellationToken ct) =>
        {
            var calendar = await db.WorkingCalendars.SingleOrDefaultAsync(row => row.UserId == userId, ct);

            return calendar is null
                ? Results.NotFound()
                : Results.Ok((await WithDisplayNamesAsync([calendar], users, ct)).Single());
        }).RequirePermission(JourneyPermissions.Read);

        calendars.MapPut("/{userId}", async (
            string userId, WorkingCalendarRequest request, JourneyDbContext db, IUserDirectory users,
            IClock clock, CancellationToken ct) =>
        {
            // The rep is IAM's, so a calendar cannot name somebody this tenant does not have — the
            // same check a rep assignment makes, and for the same reason: the user id is a string
            // and nothing about it says which tenant it belongs to.
            if (await users.FindAsync(userId, ct) is null)
            {
                return Problems.BadRequest(
                    "userId", "No such user in this tenant.", "journey.calendar.unknownUser");
            }

            var existing = await db.WorkingCalendars.SingleOrDefaultAsync(row => row.UserId == userId, ct);

            if (existing is null)
            {
                if (!WorkingCalendar.TryCreate(userId, request.WorkingDays, request.VisitsPerDay, out var created))
                {
                    return CalendarProblem(request);
                }

                db.WorkingCalendars.Add(created);
                existing = created;
            }
            else if (!existing.TrySet(request.WorkingDays, request.VisitsPerDay, clock))
            {
                return CalendarProblem(request);
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok((await WithDisplayNamesAsync([existing], users, ct)).Single());
        }).RequirePermission(JourneyPermissions.Write);

        calendars.MapDelete("/{userId}", async (
            string userId, JourneyDbContext db, CancellationToken ct) =>
        {
            var existing = await db.WorkingCalendars.SingleOrDefaultAsync(row => row.UserId == userId, ct);
            if (existing is null) return Results.NotFound();

            // Removing a calendar makes the rep unconfigured rather than un-plannable-on-purpose.
            // There is no "works no days" state to fall back to — see WorkingCalendar.TryCreate.
            db.WorkingCalendars.Remove(existing);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(JourneyPermissions.Write);

        /*
         * The pattern and the holidays, resolved into the days generation can actually use.
         *
         * Exposed for the same reason frequency resolution is: the screen that reviews a plan has to
         * show which days it was built on, and re-deriving "Monday minus Christmas" in TypeScript
         * would be a second implementation of a rule this module owns.
         */
        calendars.MapGet("/{userId}/working-days", async (
            string userId, DateOnly from, DateOnly to, CalendarReader reader, CancellationToken ct) =>
        {
            if (to < from)
            {
                return Problems.BadRequest(
                    "to", "A period ends on or after it starts.", "journey.calendar.periodBackwards");
            }

            if (to.DayNumber - from.DayNumber + 1 > CalendarReader.MaximumSpanDays)
            {
                return Problems.BadRequest(
                    "to",
                    $"Ask for at most {CalendarReader.MaximumSpanDays} days at a time.",
                    "journey.calendar.periodTooLong",
                    new Dictionary<string, string>
                    {
                        ["max"] = CalendarReader.MaximumSpanDays.ToString(),
                    });
            }

            var days = await reader.ForRepAsync(userId, from, to, ct);

            // Empty for a rep with no calendar, which is "unconfigured" rather than "works nothing".
            // A 404 would have conflated the two, and the caller is asking about days, not about
            // whether a calendar exists.
            return Results.Ok(days.Select(day => new WorkingDayResponse(day.Date, day.Capacity)).ToList());
        }).RequirePermission(JourneyPermissions.Read);

        var holidays = endpoints.MapGroup("/api/journey/holidays").WithTags("Journey");

        holidays.MapGet("/", async (
                DateOnly? from, DateOnly? to, JourneyDbContext db, CancellationToken ct) =>
            await db.Holidays
                .Where(holiday => (from == null || holiday.Date >= from) && (to == null || holiday.Date <= to))
                .OrderBy(holiday => holiday.Date)
                .Select(holiday => new HolidayResponse(holiday.Id, holiday.Date, holiday.Name))
                .ToListAsync(ct))
            .RequirePermission(JourneyPermissions.Read);

        holidays.MapPost("/", async (
            HolidayRequest request, JourneyDbContext db, CancellationToken ct) =>
        {
            if (HolidayProblem(request.Name) is { } problem) return problem;

            var taken = await db.Holidays.AnyAsync(holiday => holiday.Date == request.Date, ct);

            if (taken)
            {
                return Problems.Conflict(
                    "date",
                    $"{request.Date:yyyy-MM-dd} is already a holiday.",
                    "journey.holiday.duplicate",
                    new Dictionary<string, string> { ["date"] = request.Date.ToString("O") });
            }

            var created = Holiday.Create(request.Date, request.Name);

            db.Holidays.Add(created);
            await db.SaveChangesAsync(ct);

            return Results.Created(
                $"/api/journey/holidays/{created.Id}",
                new HolidayResponse(created.Id, created.Date, created.Name));
        }).RequirePermission(JourneyPermissions.Write);

        holidays.MapDelete("/{id:guid}", async (Guid id, JourneyDbContext db, CancellationToken ct) =>
        {
            var existing = await db.Holidays.SingleOrDefaultAsync(holiday => holiday.Id == id, ct);
            if (existing is null) return Results.NotFound();

            db.Holidays.Remove(existing);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(JourneyPermissions.Write);
    }

    /// <summary>
    /// Names what is wrong with a pattern the domain refused.
    /// </summary>
    /// <remarks>
    /// Rebuilt here rather than returned from <c>TryCreate</c>, because the domain's answer is
    /// yes-or-no and the API's job is to say which of the two numbers the admin typed is the
    /// problem. The alternative — a domain that returns messages — is a domain that has to know
    /// which language the caller reads.
    /// </remarks>
    private static IResult CalendarProblem(WorkingCalendarRequest request)
    {
        var problems = new List<FieldProblem>();

        if (request.WorkingDays.Count(Enum.IsDefined) == 0)
        {
            problems.Add(new FieldProblem(
                "workingDays",
                "A rep works at least one day a week. To stop planning for them, remove the calendar.",
                "journey.calendar.noWorkingDays"));
        }

        if (request.VisitsPerDay is < 1 or > WorkingCalendar.MaximumVisitsPerDay)
        {
            problems.Add(new FieldProblem(
                "visitsPerDay",
                $"A day holds between 1 and {WorkingCalendar.MaximumVisitsPerDay} calls.",
                "journey.calendar.capacityOutOfRange",
                new Dictionary<string, string>
                {
                    ["max"] = WorkingCalendar.MaximumVisitsPerDay.ToString(),
                }));
        }

        return Problems.BadRequest(problems);
    }

    private static IResult? HolidayProblem(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? Problems.BadRequest(
                "name", "A holiday needs a name.", "journey.holiday.nameRequired")
            : TextLimits.TooLong(
                "name", name.Trim(), Holiday.MaximumNameLength, "journey.holiday.nameTooLong") is { } tooLong
                ? Problems.BadRequest([tooLong])
                : null;

    /// <summary>
    /// Labels each calendar with whose it is.
    /// </summary>
    /// <remarks>
    /// Null when the directory no longer resolves the subject — the calendar still stands, the same
    /// way a rep assignment to a since-removed user does. A screen showing an id alone cannot tell
    /// an admin whose pattern they are editing.
    /// </remarks>
    private static async Task<List<WorkingCalendarResponse>> WithDisplayNamesAsync(
        IReadOnlyList<WorkingCalendar> calendars, IUserDirectory users, CancellationToken ct)
    {
        if (calendars.Count == 0) return [];

        var known = (await users.FindManyAsync([.. calendars.Select(row => row.UserId).Distinct()], ct))
            .ToDictionary(user => user.UserId, user => user.DisplayName);

        return
        [
            .. calendars.Select(row => new WorkingCalendarResponse(
                row.UserId,
                known.GetValueOrDefault(row.UserId),
                [.. row.WorkingDays.Select(day => day.ToString())],
                row.VisitsPerDay)),
        ];
    }
}
