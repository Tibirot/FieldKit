using FieldKit.Modules.Outlets.Contracts;
using FieldKit.Modules.Iam.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Journey;

/// <summary>Generate a plan for a rep over a window.</summary>
public sealed record GeneratePlanRequest(string UserId, DateOnly From, DateOnly To);

/// <summary>One call on a plan.</summary>
public sealed record PlannedVisitResponse(
    Guid Id,
    DateOnly Date,
    Guid OutletId,
    string Status,
    string Source,
    string? NotVisitedReason,
    DateOnly? RescheduledFrom);

/// <summary>Why a rep could not make a call.</summary>
public sealed record NotVisitedRequest(string Reason);

/// <summary>Where a rep is moving a call to.</summary>
public sealed record RescheduleRequest(DateOnly Date);

/// <summary>A call the rep made that nobody planned.</summary>
public sealed record UnplannedVisitRequest(Guid OutletId, DateOnly Date);

/// <summary>An outlet the plan could not call on as often as its frequency asks.</summary>
public sealed record ShortfallResponse(Guid OutletId, int Required, int Planned);

/// <summary>A plan, as a list shows it.</summary>
public sealed record JourneyPlanResponse(
    Guid Id,
    string UserId,
    string? DisplayName,
    DateOnly From,
    DateOnly To,
    string Status,
    int VisitCount,
    int ShortfallCount,
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset? PublishedAtUtc);

/// <summary>A plan with everything on it.</summary>
public sealed record JourneyPlanDetailResponse(
    JourneyPlanResponse Plan,
    IReadOnlyList<PlannedVisitResponse> Visits,
    IReadOnlyList<ShortfallResponse> Shortfalls);

/// <summary>
/// Generating, reviewing and publishing a rep's plan (<c>JRN-04</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Generating is a POST that writes, not a GET that computes.</b> A plan is the artefact a
/// supervisor reviews, edits the inputs of, and regenerates — so each run is a thing with an id they
/// can come back to and compare, rather than a number that vanishes when the tab closes.
/// </para>
/// <para>
/// <b>Publishing is a separate act</b>, and that separation is the point of the slice: until it
/// happens, a generated plan is an experiment. See <see cref="JourneyPlan"/>.
/// </para>
/// </remarks>
internal static class PlanEndpoints
{
    public static void MapPlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var plans = endpoints.MapGroup("/api/journey/plans").WithTags("Journey");

        plans.MapGet("/", async (
            string? userId, JourneyDbContext db, IUserDirectory users, CancellationToken ct) =>
        {
            // Counted in the database rather than by loading the children. A plan is hundreds of
            // visits and a list shows tens of plans — reading every row to call `.Count` on it is how
            // a list screen becomes the slowest thing in the app.
            var rows = await db.JourneyPlans
                .Where(plan => userId == null || plan.UserId == userId)
                .OrderByDescending(plan => plan.FromDate)
                .Select(plan => new PlanCounts(plan, plan.Visits.Count, plan.Shortfalls.Count))
                .ToListAsync(ct);

            return await SummariesAsync(rows, users, ct);
        }).RequirePermission(JourneyPermissions.Read);

        plans.MapGet("/{id:guid}", async (
            Guid id, JourneyDbContext db, IUserDirectory users, CancellationToken ct) =>
        {
            // Included, unlike the list: this is the one request that is *about* the contents, and
            // without them the response is a plan that reads back as empty — which is exactly what
            // the first version of this endpoint did.
            var plan = await db.JourneyPlans
                .Include(row => row.Visits)
                .Include(row => row.Shortfalls)
                .SingleOrDefaultAsync(row => row.Id == id, ct);

            if (plan is null) return Results.NotFound();

            var summary = (await SummariesAsync(
                [new PlanCounts(plan, plan.Visits.Count, plan.Shortfalls.Count)], users, ct)).Single();

            return Results.Ok(new JourneyPlanDetailResponse(
                summary,
                [.. plan.Visits
                    .OrderBy(visit => visit.Date)
                    .Select(visit => Respond(visit))],
                [.. plan.Shortfalls.Select(shortfall =>
                    new ShortfallResponse(shortfall.OutletId, shortfall.Required, shortfall.Planned))]));
        }).RequirePermission(JourneyPermissions.Read);

        plans.MapPost("/", async (
            GeneratePlanRequest request, JourneyDbContext db, JourneyPlanner planner,
            IUserDirectory users, IClock clock, CancellationToken ct) =>
        {
            if (WindowProblem(request.From, request.To) is { } problem) return problem;

            // The rep is IAM's, the same check a calendar makes. Generating for somebody this tenant
            // does not have would produce an empty plan that looks like a coverage problem.
            if (await users.FindAsync(request.UserId, ct) is null)
            {
                return Problems.BadRequest(
                    "userId", "No such user in this tenant.", "journey.plan.unknownUser");
            }

            var generated = await planner.GenerateAsync(request.UserId, request.From, request.To, ct);
            var plan = JourneyPlan.Draft(request.UserId, request.From, request.To, generated, clock);

            db.JourneyPlans.Add(plan);
            await db.SaveChangesAsync(ct);

            var summary = (await SummariesAsync([new PlanCounts(plan, plan.Visits.Count, plan.Shortfalls.Count)], users, ct)).Single();

            // The exclusions are returned here and stored nowhere — see PlanShortfall for the split.
            // This is the one moment they can be acted on: the shops are on screen, and the reason is
            // either "close it properly" or "give it a frequency".
            return Results.Created(
                $"/api/journey/plans/{plan.Id}",
                new
                {
                    plan = summary,
                    visits = plan.Visits
                        .OrderBy(visit => visit.Date)
                        .Select(visit => Respond(visit)),
                    shortfalls = plan.Shortfalls.Select(shortfall =>
                        new ShortfallResponse(shortfall.OutletId, shortfall.Required, shortfall.Planned)),
                    excluded = generated.Excluded.Select(row => new
                    {
                        outletId = row.OutletId,
                        reason = row.Reason.ToString(),
                    }),
                });
        }).RequirePermission(JourneyPermissions.Write);

        plans.MapPost("/{id:guid}/publish", async (
            Guid id, JourneyDbContext db, IUserDirectory users, IClock clock, CancellationToken ct) =>
        {
            // The children are loaded because the *event* counts them: `JourneyPublished` carries a
            // visit count so Sync can size a pull, and an un-included collection would have made
            // that a confident zero on every plan ever published.
            var plan = await db.JourneyPlans
                .Include(row => row.Visits)
                .Include(row => row.Shortfalls)
                .SingleOrDefaultAsync(row => row.Id == id, ct);

            if (plan is null) return Results.NotFound();

            if (!plan.TryPublish(clock))
            {
                // Refused rather than treated as a no-op. A second publish is either a double-click
                // or somebody expecting it to re-announce a changed plan — and the second is a
                // misunderstanding worth correcting, because a published plan does not change.
                return Problems.Conflict(
                    field: null,
                    "This plan is already published. Generate a new one instead.",
                    "journey.plan.alreadyPublished");
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok((await SummariesAsync([new PlanCounts(plan, plan.Visits.Count, plan.Shortfalls.Count)], users, ct)).Single());
        }).RequirePermission(JourneyPermissions.Write);

        /*
         * The rep's own three acts, and they are held to `journey:annotate` rather than
         * `journey:write`.
         *
         * A rep reports on the round they walked; they do not decide what the round is. Giving them
         * `journey:write` to record a closed shop would also let them generate and publish plans —
         * for anyone — which is the difference between a permission model and a tier list.
         *
         * **There is no delete.** `BR-JRN-2` is explicit, and the absence is the enforcement: a
         * skipped shop is a fact about the round, and letting it disappear would make coverage look
         * complete and turn `BR-JRN-6` into a measure of what was left on the plan.
         */
        plans.MapPost("/{id:guid}/visits/{visitId:guid}/not-visited", async (
            Guid id, Guid visitId, NotVisitedRequest request, JourneyDbContext db, IClock clock,
            CancellationToken ct) =>
        {
            if (ReasonProblem(request.Reason) is { } problem) return problem;

            var (plan, visit, missing) = await FindVisitAsync(db, id, visitId, ct);
            if (missing is not null) return missing;

            var refusal = plan!.TryMarkNotVisited(visit!, request.Reason, clock);
            if (refusal is not JourneyPlan.AnnotationRefusal.None) return Refuse(refusal);

            await db.SaveChangesAsync(ct);

            return Results.Ok(Respond(visit!));
        }).RequirePermission(JourneyPermissions.Annotate);

        plans.MapPost("/{id:guid}/visits/{visitId:guid}/reschedule", async (
            Guid id, Guid visitId, RescheduleRequest request, JourneyDbContext db, IClock clock,
            CancellationToken ct) =>
        {
            var (plan, visit, missing) = await FindVisitAsync(db, id, visitId, ct);
            if (missing is not null) return missing;

            var refusal = plan!.TryReschedule(visit!, request.Date, clock);
            if (refusal is not JourneyPlan.AnnotationRefusal.None) return Refuse(refusal);

            await db.SaveChangesAsync(ct);

            return Results.Ok(Respond(visit!));
        }).RequirePermission(JourneyPermissions.Annotate);

        plans.MapPost("/{id:guid}/visits", async (
            Guid id, UnplannedVisitRequest request, JourneyDbContext db, IOutletCatalog outlets,
            IClock clock, CancellationToken ct) =>
        {
            var plan = await db.JourneyPlans
                .Include(row => row.Visits)
                .SingleOrDefaultAsync(row => row.Id == id, ct);

            if (plan is null) return Results.NotFound();

            // The outlet is Outlets' to confirm. A rep adding a shop that does not exist in this
            // tenant is a client bug, and storing it would put an unresolvable id on a plan that
            // reporting later has to explain.
            if ((await outlets.FindManyAsync([request.OutletId], ct)).Count == 0)
            {
                return Problems.BadRequest(
                    "outletId", "No such outlet in this tenant.", "journey.visit.unknownOutlet");
            }

            var refusal = plan.TryAddUnplanned(request.OutletId, request.Date, clock, out var added);
            if (refusal is not JourneyPlan.AnnotationRefusal.None) return Refuse(refusal);

            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/journey/plans/{plan.Id}", Respond(added!));
        }).RequirePermission(JourneyPermissions.Annotate);
    }

    /// <summary>Loads a plan and one of its calls, or the 404 that says which is missing.</summary>
    private static async Task<(JourneyPlan? Plan, PlannedVisit? Visit, IResult? Missing)> FindVisitAsync(
        JourneyDbContext db, Guid planId, Guid visitId, CancellationToken ct)
    {
        var plan = await db.JourneyPlans
            .Include(row => row.Visits)
            .SingleOrDefaultAsync(row => row.Id == planId, ct);

        if (plan is null) return (null, null, Results.NotFound());

        // Found through the plan rather than by id alone, so a call can only be annotated by way of
        // the plan it is on — a visit id from another plan is simply not here.
        var visit = plan.Visits.SingleOrDefault(row => row.Id == visitId);

        return visit is null ? (null, null, Results.NotFound()) : (plan, visit, null);
    }

    /// <summary>Turns the domain's refusal into the API's.</summary>
    private static IResult Refuse(JourneyPlan.AnnotationRefusal refusal) => refusal switch
    {
        JourneyPlan.AnnotationRefusal.NotPublished => Problems.Conflict(
            field: null,
            "A draft plan is not a rep's round yet. Publish it first.",
            "journey.visit.planNotPublished"),

        JourneyPlan.AnnotationRefusal.AlreadyNotVisited => Problems.Conflict(
            field: null,
            "This call is already recorded as not visited.",
            "journey.visit.alreadyNotVisited"),

        JourneyPlan.AnnotationRefusal.OutsideWindow => Problems.BadRequest(
            "date", "That day is outside the plan's window.", "journey.visit.outsideWindow"),

        JourneyPlan.AnnotationRefusal.OutsideCycle => Problems.BadRequest(
            "date",
            "A call can move within its cycle. Moving it outside needs a supervisor.",
            "journey.visit.outsideCycle"),

        _ => Results.Ok(),
    };

    private static IResult? ReasonProblem(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? Problems.BadRequest(
                "reason",
                "Say why the call did not happen. A skipped shop with no reason is a gap nobody can act on.",
                "journey.visit.reasonRequired")
            : TextLimits.TooLong(
                "reason", reason.Trim(), PlannedVisit.MaximumReasonLength,
                "journey.visit.reasonTooLong") is { } tooLong
                ? Problems.BadRequest([tooLong])
                : null;

    private static PlannedVisitResponse Respond(PlannedVisit visit) => new(
        visit.Id,
        visit.Date,
        visit.OutletId,
        visit.Status.ToString(),
        visit.Source.ToString(),
        visit.NotVisitedReason,
        visit.RescheduledFrom);

    /// <summary>
    /// The window a plan may be generated for.
    /// </summary>
    /// <remarks>
    /// Bounded by the same span the calendar reader accepts, and for the same reason: the range comes
    /// from a request body and generation walks it. A backwards window is refused rather than
    /// quietly producing an empty plan, because an empty plan reads as "this rep covers nothing".
    /// </remarks>
    private static IResult? WindowProblem(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return Problems.BadRequest(
                "to", "A window ends on or after it starts.", "journey.plan.windowBackwards");
        }

        return to.DayNumber - from.DayNumber + 1 > CalendarReader.MaximumSpanDays
            ? Problems.BadRequest(
                "to",
                $"Plan at most {CalendarReader.MaximumSpanDays} days at a time.",
                "journey.plan.windowTooLong",
                new Dictionary<string, string> { ["max"] = CalendarReader.MaximumSpanDays.ToString() })
            : null;
    }

    /// <summary>A plan and its child counts, so a list never loads the children to count them.</summary>
    private sealed record PlanCounts(JourneyPlan Plan, int Visits, int Shortfalls);

    private static async Task<List<JourneyPlanResponse>> SummariesAsync(
        IReadOnlyList<PlanCounts> plans, IUserDirectory users, CancellationToken ct)
    {
        if (plans.Count == 0) return [];

        var known = (await users.FindManyAsync([.. plans.Select(row => row.Plan.UserId).Distinct()], ct))
            .ToDictionary(user => user.UserId, user => user.DisplayName);

        return
        [
            .. plans.Select(row => new JourneyPlanResponse(
                row.Plan.Id,
                row.Plan.UserId,
                known.GetValueOrDefault(row.Plan.UserId),
                row.Plan.FromDate,
                row.Plan.ToDate,
                row.Plan.Status.ToString(),
                row.Visits,
                row.Shortfalls,
                row.Plan.GeneratedAtUtc,
                row.Plan.PublishedAtUtc)),
        ];
    }
}
