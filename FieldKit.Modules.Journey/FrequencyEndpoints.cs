using FieldKit.Modules.Outlets.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Journey;

/// <summary>A call frequency as it is set: how many visits, over how many days.</summary>
public sealed record FrequencyRequest(int VisitsPerCycle, int CycleLengthDays);

/// <summary>A segment's default frequency.</summary>
public sealed record SegmentFrequencyResponse(string Segment, int VisitsPerCycle, int CycleLengthDays);

/// <summary>One outlet's override.</summary>
public sealed record OutletFrequencyResponse(Guid OutletId, int VisitsPerCycle, int CycleLengthDays);

/// <summary>
/// What an outlet is actually due, and which rule decided it.
/// </summary>
/// <param name="Source">
/// <c>Outlet</c> or <c>Segment</c>. Returned because "why is this shop planned four times a month?"
/// is the question an admin asks, and a number alone cannot answer it.
/// </param>
public sealed record ResolvedFrequencyResponse(
    Guid OutletId, int VisitsPerCycle, int CycleLengthDays, string Source);

/// <summary>
/// Call frequency: the default per segment, the override per outlet (<c>JRN-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>PUT rather than POST, keyed by the thing the rule is about.</b> A segment has at most one
/// frequency and an outlet has at most one override, so the natural identifier is the segment label
/// or the outlet id rather than a generated one — and that makes setting a rule idempotent. An admin
/// who saves twice has set it once, which is the behaviour the unique index enforces anyway; POST
/// would have made the second save a 409 about a row the caller never asked to create.
/// </para>
/// <para>
/// Guarded by <c>journey:read</c> / <c>journey:write</c>. Frequency is not outlet master data even
/// though it is keyed by an outlet — it is the input to planning, and the person who decides how
/// often a shop is called on is not necessarily the person who may rename it.
/// </para>
/// </remarks>
internal static class FrequencyEndpoints
{
    public static void MapFrequencyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var frequencies = endpoints.MapGroup("/api/journey/frequencies").WithTags("Journey");

        frequencies.MapGet("/segments", async (JourneyDbContext db, CancellationToken ct) =>
                await db.SegmentFrequencies
                    .OrderBy(rule => rule.Segment)
                    .Select(rule => new SegmentFrequencyResponse(
                        rule.Segment, rule.VisitsPerCycle, rule.CycleLengthDays))
                    .ToListAsync(ct))
            .RequirePermission(JourneyPermissions.Read);

        frequencies.MapPut("/segments/{segment}", async (
            string segment, FrequencyRequest request, JourneyDbContext db, IClock clock,
            CancellationToken ct) =>
        {
            if (SegmentProblem(segment) is { } segmentProblem) return segmentProblem;
            if (!TryRead(request, out var frequency, out var problem)) return problem;

            var key = SegmentFrequency.Normalise(segment);

            // Case-insensitively, so "A" and "a" edit one rule rather than creating a second the
            // resolver would then have to choose between. The stored casing is whatever created it.
            var existing = await db.SegmentFrequencies
                .SingleOrDefaultAsync(rule => rule.Segment.ToLower() == key.ToLower(), ct);

            if (existing is null)
            {
                db.SegmentFrequencies.Add(SegmentFrequency.Create(key, frequency));
            }
            else
            {
                existing.Set(frequency, clock);
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(new SegmentFrequencyResponse(
                existing?.Segment ?? key, frequency.VisitsPerCycle, frequency.CycleLengthDays));
        }).RequirePermission(JourneyPermissions.Write);

        frequencies.MapDelete("/segments/{segment}", async (
            string segment, JourneyDbContext db, CancellationToken ct) =>
        {
            var key = SegmentFrequency.Normalise(segment);

            var existing = await db.SegmentFrequencies
                .SingleOrDefaultAsync(rule => rule.Segment.ToLower() == key.ToLower(), ct);

            if (existing is null) return Results.NotFound();

            // Deleting a default does not delete the outlets in that segment — they simply stop
            // having a frequency, and generation reports them as unconfigured rather than planning
            // them zero times. See FrequencyResolver for why that is not the same as "never".
            db.SegmentFrequencies.Remove(existing);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(JourneyPermissions.Write);

        frequencies.MapGet("/outlets", async (JourneyDbContext db, CancellationToken ct) =>
                await db.OutletFrequencies
                    .OrderBy(rule => rule.OutletId)
                    .Select(rule => new OutletFrequencyResponse(
                        rule.OutletId, rule.VisitsPerCycle, rule.CycleLengthDays))
                    .ToListAsync(ct))
            .RequirePermission(JourneyPermissions.Read);

        frequencies.MapPut("/outlets/{outletId:guid}", async (
            Guid outletId, FrequencyRequest request, JourneyDbContext db, IOutletCatalog outlets,
            IClock clock, CancellationToken ct) =>
        {
            if (!TryRead(request, out var frequency, out var problem)) return problem;

            // The outlet id is not a foreign key — Outlets is another schema (AT-1) — so this is
            // where a rule about a shop that does not exist is refused. Without it a typo becomes a
            // row that resolves against nothing, forever, silently.
            if ((await outlets.FindManyAsync([outletId], ct)).Count == 0)
            {
                return Problems.BadRequest(
                    "outletId", "No such outlet in this tenant.", "journey.frequency.unknownOutlet");
            }

            var existing = await db.OutletFrequencies
                .SingleOrDefaultAsync(rule => rule.OutletId == outletId, ct);

            if (existing is null)
            {
                db.OutletFrequencies.Add(OutletFrequency.Create(outletId, frequency));
            }
            else
            {
                existing.Set(frequency, clock);
            }

            await db.SaveChangesAsync(ct);

            return Results.Ok(new OutletFrequencyResponse(
                outletId, frequency.VisitsPerCycle, frequency.CycleLengthDays));
        }).RequirePermission(JourneyPermissions.Write);

        frequencies.MapDelete("/outlets/{outletId:guid}", async (
            Guid outletId, JourneyDbContext db, CancellationToken ct) =>
        {
            var existing = await db.OutletFrequencies
                .SingleOrDefaultAsync(rule => rule.OutletId == outletId, ct);

            if (existing is null) return Results.NotFound();

            // Removing an override falls back to the segment default rather than to nothing, which
            // is the point of a ladder — and is why this is a delete rather than "set it to zero".
            db.OutletFrequencies.Remove(existing);
            await db.SaveChangesAsync(ct);

            return Results.NoContent();
        }).RequirePermission(JourneyPermissions.Write);

        /*
         * Resolution, exposed because generation is not the only thing that needs it.
         *
         * The back-office screen has to show an admin what a shop is *actually* due before they
         * publish a plan built on it, and re-deriving the ladder in TypeScript would be a second
         * implementation of a rule this module owns — the mistake `PRD-04` exists to avoid.
         */
        frequencies.MapGet("/resolve", async (
            Guid[] outletId, FrequencyResolver resolver, CancellationToken ct) =>
        {
            var resolved = await resolver.ForOutletsAsync(outletId, ct);

            // Outlets with no rule are absent, not zero — the caller asked which of these are
            // configured, and a shop nobody has decided about is exactly what it needs to see.
            return Results.Ok(resolved
                .Select(row => new ResolvedFrequencyResponse(
                    row.OutletId,
                    row.Frequency.VisitsPerCycle,
                    row.Frequency.CycleLengthDays,
                    row.Source.ToString()))
                .ToList());
        }).RequirePermission(JourneyPermissions.Read);
    }

    /// <summary>Reads the request into a <see cref="CallFrequency"/>, or says what is wrong with it.</summary>
    private static bool TryRead(
        FrequencyRequest request, out CallFrequency frequency, out IResult problem)
    {
        if (CallFrequency.TryCreate(request.VisitsPerCycle, request.CycleLengthDays, out frequency))
        {
            problem = Results.Empty;
            return true;
        }

        var problems = new List<FieldProblem>();

        if (request.VisitsPerCycle < 1)
        {
            // Named rather than folded into one message about "the frequency", because the admin
            // typed two numbers and only one of them is wrong.
            problems.Add(new FieldProblem(
                "visitsPerCycle",
                "An outlet is visited at least once per cycle. To stop visiting it, remove the rule.",
                "journey.frequency.visitsTooFew"));
        }

        if (request.CycleLengthDays is < 1 or > CallFrequency.MaximumCycleLengthDays)
        {
            problems.Add(new FieldProblem(
                "cycleLengthDays",
                $"A cycle is between 1 and {CallFrequency.MaximumCycleLengthDays} days.",
                "journey.frequency.cycleOutOfRange",
                new Dictionary<string, string>
                {
                    ["max"] = CallFrequency.MaximumCycleLengthDays.ToString(),
                }));
        }

        problem = Problems.BadRequest(problems);
        return false;
    }

    private static IResult? SegmentProblem(string segment) =>
        string.IsNullOrWhiteSpace(segment)
            ? Problems.BadRequest("segment", "A rule needs a segment.", "journey.frequency.segmentRequired")
            : TextLimits.TooLong(
                "segment", segment.Trim(), SegmentFrequency.MaximumSegmentLength,
                "journey.frequency.segmentTooLong") is { } tooLong
                ? Problems.BadRequest([tooLong])
                : null;
}
