using System.Text.Json.Serialization;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Configuration;

/// <summary>What one pillar is worth, as an administrator sets it.</summary>
/// <remarks>
/// The pillar travels as its name, like every other enum on this API — declared once on
/// <see cref="ScorePillar"/> rather than restated here (W11 slice 0b). It was written out per
/// property until then, and the workflow's step type shipped as a 400 for every name because
/// somebody did not.
/// </remarks>
public sealed record ScoreWeightRequest(
    ScorePillar Pillar,
    decimal Percentage);

/// <summary>A draft weighting, as an administrator sets it.</summary>
public sealed record ScoreWeightSetRequest(IReadOnlyList<ScoreWeightRequest> Weights);

public sealed record ScoreWeightResponse(string Pillar, decimal Percentage);

/// <summary>A weighting version, as stored.</summary>
/// <remarks>
/// <see cref="Version"/> is what an audit records (<c>BR-AUD-8</c>) and what a person says out loud;
/// the id is the identity. Both are returned because a screen needs one and a support conversation
/// needs the other.
/// </remarks>
public sealed record ScoreWeightSetResponse(
    Guid Id,
    int Version,
    bool IsPublished,
    DateTimeOffset? PublishedAtUtc,
    IReadOnlyList<ScoreWeightResponse> Weights);

/// <summary>
/// The tenant's perfect-store weighting, by version (<c>AUD-06</c>, <c>AUD-07</c>, <c>BR-AUD-4/8</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Versions rather than a single editable set</b>, and the reason is `BR-AUD-8`: the server
/// recomputes a pushed audit with the weights that audit was scored against. That is a sentence
/// about a fixed set of numbers, so publishing is one-way and re-weighting drafts a new version —
/// the decision made in W10 slice 0 and argued in
/// [audits §5](../../docs/product/22-merchandising-and-audits.md).
/// </para>
/// <para>
/// <b>POST for a draft, PUT while it is a draft, POST to publish it</b> — the same shape a journey
/// plan has. Deliberately not a single PUT that publishes when it validates: an administrator moving
/// a slider and an administrator freezing a version are different intentions, and collapsing them
/// would make every edit irreversible by accident.
/// </para>
/// </remarks>
internal static class ScoreWeightEndpoints
{
    public static void MapScoreWeightEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var weights = endpoints.MapGroup("/api/config/score-weights").WithTags("Configuration");

        // Newest first: an administrator opening this screen is nearly always looking at the version
        // in force or the draft they are about to publish, not at the history.
        weights.MapGet("/", async (ConfigurationDbContext db, CancellationToken ct) =>
        {
            var sets = await db.ScoreWeightSets
                .Include(set => set.Weights)
                .OrderByDescending(set => set.Version)
                .ToListAsync(ct);

            return sets.Select(Respond).ToList();
        }).RequirePermission(ConfigurationPermissions.Read);

        weights.MapGet("/{version:int}", async (
            int version, ConfigurationDbContext db, CancellationToken ct) =>
        {
            var set = await db.ScoreWeightSets
                .Include(candidate => candidate.Weights)
                .SingleOrDefaultAsync(candidate => candidate.Version == version, ct);

            return set is null ? Results.NotFound() : Results.Ok(Respond(set));
        }).RequirePermission(ConfigurationPermissions.Read);

        weights.MapPost("/", async (
            ScoreWeightSetRequest request, ConfigurationDbContext db, CancellationToken ct) =>
        {
            /*
             * The next version number, read rather than counted.
             *
             * `Max + 1` rather than `Count + 1`: nothing deletes a version — sealed audits point at
             * them — but a count would be wrong the first time anything ever did, and would produce
             * a number that collides with a version an audit already names. The unique index is what
             * catches two administrators drafting at the same moment; this is what makes that rare.
             */
            var next = await db.ScoreWeightSets.MaxAsync(set => (int?)set.Version, ct) ?? 0;

            var (drafted, refusal) = ScoreWeightSet.Draft(next + 1, Weights(request));
            if (refusal is not WeightSetRefusal.None) return Refuse(refusal);

            db.ScoreWeightSets.Add(drafted!);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/config/score-weights/{drafted!.Version}", Respond(drafted));
        }).RequirePermission(ConfigurationPermissions.Write);

        weights.MapPut("/{version:int}", async (
            int version, ScoreWeightSetRequest request, ConfigurationDbContext db, IClock clock,
            CancellationToken ct) =>
        {
            var set = await db.ScoreWeightSets
                .Include(candidate => candidate.Weights)
                .SingleOrDefaultAsync(candidate => candidate.Version == version, ct);

            if (set is null) return Results.NotFound();

            var refusal = set.Set(Weights(request), clock);
            if (refusal is not WeightSetRefusal.None) return Refuse(refusal);

            await db.SaveChangesAsync(ct);

            return Results.Ok(Respond(set));
        }).RequirePermission(ConfigurationPermissions.Write);

        /*
         * Freezing a version. One-way, and the endpoint that makes `BR-AUD-8` true.
         *
         * A second attempt answers `config.weights.alreadyPublished` rather than succeeding quietly:
         * an administrator who thinks they are publishing an edit needs to be told the edit was
         * never in this version, which a 200 would hide.
         */
        weights.MapPost("/{version:int}/publish", async (
            int version, ConfigurationDbContext db, IClock clock, CancellationToken ct) =>
        {
            var set = await db.ScoreWeightSets
                .Include(candidate => candidate.Weights)
                .SingleOrDefaultAsync(candidate => candidate.Version == version, ct);

            if (set is null) return Results.NotFound();

            var refusal = set.Publish(clock);
            if (refusal is not WeightSetRefusal.None) return Refuse(refusal);

            await db.SaveChangesAsync(ct);

            return Results.Ok(Respond(set));
        }).RequirePermission(ConfigurationPermissions.Write);
    }

    private static IReadOnlyList<(ScorePillar Pillar, decimal Percentage)> Weights(
        ScoreWeightSetRequest request) =>
        [.. request.Weights.Select(weight => (weight.Pillar, weight.Percentage))];

    /// <summary>Maps the aggregate's refusal onto an <c>ADR-0012</c> code a screen can branch on.</summary>
    private static IResult Refuse(WeightSetRefusal refusal) => refusal switch
    {
        WeightSetRefusal.DoesNotSumToOneHundred => Problems.BadRequest(
            "weights",
            "The weights have to add up to exactly 100%.",
            "config.weights.doesNotSumToOneHundred"),

        WeightSetRefusal.DuplicatePillar => Problems.BadRequest(
            "weights", "Each pillar can be weighted once.", "config.weights.duplicatePillar"),

        WeightSetRefusal.PercentageOutOfRange => Problems.BadRequest(
            "weights", "A weight is a percentage between 0 and 100.", "config.weights.outOfRange"),

        WeightSetRefusal.Empty => Problems.BadRequest(
            "weights", "Weight at least one pillar. A score of nothing is not a score.",
            "config.weights.empty"),

        // A conflict rather than a bad request: the body was fine, the *state* refused it — the same
        // distinction `journey.plan.alreadyPublished` draws.
        WeightSetRefusal.AlreadyPublished => Problems.Conflict(
            null,
            "That version is published. Publishing is one-way — draft a new version to change the weights.",
            "config.weights.alreadyPublished"),

        _ => Problems.BadRequest("weights", "That weighting was refused.", "config.weights.refused"),
    };

    private static ScoreWeightSetResponse Respond(ScoreWeightSet set) => new(
        set.Id,
        set.Version,
        set.IsPublished,
        set.PublishedAtUtc,
        [.. set.Weights
            .OrderBy(weight => weight.Pillar)
            .Select(weight => new ScoreWeightResponse(weight.Pillar.ToString(), weight.Percentage))]);
}
