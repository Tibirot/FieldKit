using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Journey;
using FieldKit.Modules.Journey.Contracts;
using FieldKit.Modules.Order.Contracts;
using FieldKit.Modules.Org.Contracts;
using FieldKit.Modules.Visit;
using FieldKit.Modules.Visit.Contracts;
using FieldKit.SharedKernel;
using FieldKit.Web;

namespace FieldKit.Server;

/// <summary>Coverage over a scope and a window, as a supervisor reads it.</summary>
/// <param name="Planned">Calls the published rounds promised — the denominator.</param>
/// <param name="NotVisited">Of those, the ones a rep declined and said why (<c>JRN-06</c>).</param>
/// <param name="Made">Distinct planned calls a visit claimed — the numerator.</param>
/// <param name="Percentage">
/// <c>Made ÷ Planned</c> as a percentage, or null when nothing was planned. Null rather than zero,
/// because a scope with no round has no coverage — 0% would say a team failed every call it was
/// never given.
/// </param>
public sealed record CoverageSummaryResponse(
    int Planned, int NotVisited, int Made, decimal? Percentage);

/// <summary>Visits over the same scope and window.</summary>
/// <param name="StrikeRate">
/// Productive ÷ finished, as a percentage, or null when nothing has finished — carried through from
/// <c>VisitOutcomeCounts</c> rather than re-derived here.
/// </param>
public sealed record VisitSummaryResponse(
    int Productive, int NonProductive, int Open, decimal? StrikeRate);

/// <summary>One pillar's average across the audits that measured it.</summary>
public sealed record PillarSummaryResponse(string Pillar, decimal? Average, int Measured, int Skipped);

/// <summary>Perfect store over the same scope and window.</summary>
/// <param name="Comparable">
/// False when the window mixes weight-set versions, in which case the average is a mean of two
/// rulers (<c>BR-AUD-8</c>). Surfaced rather than hidden — see <c>PerfectStoreSummary</c>.
/// </param>
public sealed record AuditSummaryResponse(
    int Audits,
    int Scored,
    decimal? AverageScore,
    bool Comparable,
    IReadOnlyList<int> WeightSetVersions,
    IReadOnlyList<PillarSummaryResponse> Pillars);

/// <summary>Order value in one currency.</summary>
public sealed record OrderValueResponse(
    string CurrencyCode, decimal Net, decimal Tax, decimal Gross, int Orders);

/// <summary>Order capture over the same scope and window.</summary>
public sealed record OrderSummaryResponse(
    int Orders,
    int Lines,
    decimal? LinesPerOrder,
    int Rejected,
    int Cancelled,
    int PriceDisagreements,
    IReadOnlyList<OrderValueResponse> Value);

/// <summary>
/// The dashboard's four KPIs over one scope and one period (<c>AUD-09</c>, <c>JRN-04</c>,
/// <c>ORD-09</c>, <c>VIS-10</c>).
/// </summary>
/// <param name="From">First day counted, inclusive.</param>
/// <param name="To">Last day counted, inclusive.</param>
/// <param name="TerritoryId">The territory asked about, or null for every territory.</param>
/// <param name="Outlets">
/// How many shops the figures are totalled over. Reported because every number below is meaningless
/// without it: "0% coverage" over four hundred shops and over none are different emergencies.
/// </param>
public sealed record ReportingSummaryResponse(
    DateOnly From,
    DateOnly To,
    Guid? TerritoryId,
    int Outlets,
    CoverageSummaryResponse Coverage,
    VisitSummaryResponse Visits,
    AuditSummaryResponse PerfectStore,
    OrderSummaryResponse Orders);

/// <summary>
/// The supervisor dashboard's read (<c>AUD-09</c>, <c>JRN-04</c>, <c>ORD-09</c>, <c>VIS-10</c>) —
/// W12 slice 3.
/// </summary>
/// <remarks>
/// <para>
/// <b>This lives in the host because it belongs to no module.</b> Reporting is not a write-model and
/// has no schema of its own; it is four modules' aggregates read side by side, and every attempt to
/// give one of them the job would have that module reaching into the other three. The product
/// overview has said so since W1 — "composed from the query contracts each module exposes" — and
/// this is the first place that sentence has been executable.
/// </para>
/// <para>
/// <b>What the module boundaries buy is visible here.</b> Nothing below touches a table. The host
/// resolves a scope through <see cref="ITerritoryDirectory"/> and asks four contracts the same
/// question about the same shops and the same days; each module keeps the judgement that only it
/// can make — what counts as productive, what a skipped pillar is worth, which orders are money.
/// </para>
/// <para>
/// <b>Scoping is tenant plus an optional territory, and not yet per supervisor.</b> The W12
/// decomposition said <c>IRepScope</c> would decide what a supervisor may total. Building this found
/// that it cannot: <c>IRepScope</c> answers about a <i>rep's</i> assignments on one day, so an
/// administrator or a supervisor who is not assigned as a rep resolves to no shops at all — an empty
/// dashboard for the persona it exists for. The org-hierarchy visibility scope that would answer it
/// (<c>BR-ORG-4</c>) is returned as <i>data</i> by <c>/api/org/users/{id}/scope</c> and explicitly
/// not enforced: its own note says enforcement lands with <c>ORG-09</c>, which is unbuilt.
/// </para>
/// <para>
/// So this endpoint is scoped exactly as every other back-office read is — tenant-isolated and
/// permission-gated — and no more. That is a <b>deliberate limit, not an oversight</b>: a caller
/// holding the two read permissions can total any territory in their tenant. Making reporting the
/// one enforced read in the system would be inconsistent as well as incomplete, and the enforcement
/// belongs in one place for every read at once.
/// </para>
/// </remarks>
internal static class ReportingEndpoints
{
    /// <summary>The longest window this will total, in days.</summary>
    /// <remarks>
    /// A year, and it is a guard rather than a product decision. The four aggregates are indexed and
    /// grouped in the database, so a wide window is not expensive by the row — but an unbounded one
    /// invites a caller to ask for a decade and makes the cost a function of how long the tenant has
    /// existed. The dashboard asks for a cycle or a month (W12 slice 4).
    /// </remarks>
    public const int MaximumWindowDays = 366;

    public static void MapReportingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/reporting/summary", async (
            DateOnly? from,
            DateOnly? to,
            Guid? territoryId,
            ITerritoryDirectory territories,
            IJourneyQuery journeys,
            IVisitQuery visits,
            IAuditQuery audits,
            IOrderQuery orders,
            IClock clock,
            CancellationToken ct) =>
        {
            // Defaulted rather than required, so the dashboard's first load is one bare GET. The
            // month containing today, in UTC — the same instant every aggregate below dates by.
            var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
            var start = from ?? new DateOnly(today.Year, today.Month, 1);
            var end = to ?? today;

            if (end < start)
            {
                return Problems.BadRequest("to", "A period cannot end before it starts.");
            }

            if (end.DayNumber - start.DayNumber >= MaximumWindowDays)
            {
                return Problems.BadRequest(
                    "to", $"A period cannot be longer than {MaximumWindowDays} days.");
            }

            var outletIds = await territories.OutletsInAsync(territoryId, ct);

            /*
             * Four modules, asked concurrently — but only four, and the boundary is not arbitrary.
             *
             * Each module answers from its own `DbContext` over its own schema, with no writes and
             * no ordering between them, so waiting for each in turn would make the dashboard's
             * latency the sum of four queries rather than the slowest one.
             *
             * <b>Visit's two questions are asked in sequence, and that is a bug fixed rather than a
             * style.</b> They share one `VisitDbContext` — one scoped instance per request — and EF
             * Core refuses a second operation on a context while the first is running. Started
             * together they threw, intermittently: the endpoint's own tests passed one at a time and
             * failed the moment the class ran as a class. Concurrency here is per *module*, because
             * the thing that makes it safe is the schema-per-module boundary and nothing else.
             *
             * The scope is resolved first and passed as a value for a related reason: the four must
             * be totalled over exactly the same shops, and re-resolving inside each would let a
             * membership change land between them.
             */
            async Task<(VisitOutcomeCounts Outcomes, int Made)> VisitsAsync() =>
                (await visits.CountByOutcomeAsync(outletIds, start, end, ct),
                 await visits.CountFulfilledCallsAsync(outletIds, start, end, ct));

            var coverage = journeys.CountPlannedAsync(outletIds, start, end, ct);
            var visited = VisitsAsync();
            var perfectStore = audits.SummariseAsync(outletIds, start, end, ct);
            var ordered = orders.SummariseAsync(outletIds, start, end, ct);

            await Task.WhenAll(coverage, visited, perfectStore, ordered);

            return Results.Ok(new ReportingSummaryResponse(
                start,
                end,
                territoryId,
                outletIds.Count,
                Coverage(coverage.Result, visited.Result.Made),
                Visits(visited.Result.Outcomes),
                PerfectStore(perfectStore.Result),
                Orders(ordered.Result)));
        })
        .WithTags("Reporting")
        .RequirePermission(VisitPermissions.Read)

        // Both, because the response carries both modules' answers. `visit:read` already covers
        // audits and orders — neither declares a read permission of its own, and `OrderEndpoints`
        // says why it borrows this one — but coverage's denominator is Journey's, and a caller
        // holding only `visit:read` would otherwise learn planned-call counts that
        // `/api/journey/plans` refuses them. Two calls rather than one policy with two claims: this
        // is the shape every other endpoint uses, and ASP.NET requires all of them to pass.
        .RequirePermission(JourneyPermissions.Read);
    }

    /// <summary>
    /// The one figure this endpoint computes rather than passes through, and the reason it is here.
    /// </summary>
    /// <remarks>
    /// Neither module can produce it. Journey knows what was promised and never learns a call was
    /// made; Visit knows which calls its visits claimed and knows nothing about the round. The
    /// division is the composition's whole job, and it is done once here rather than in the browser.
    /// </remarks>
    private static CoverageSummaryResponse Coverage(PlannedCallCounts planned, int made) => new(
        planned.Total,
        planned.NotVisited,
        made,

        // Half-up to two places, the policy every other percentage in this response already carries
        // (`BR-PRD-9`). Null when nothing was planned — see the record.
        planned.Total == 0
            ? null
            : Math.Round(100m * made / planned.Total, 2, MidpointRounding.AwayFromZero));

    private static VisitSummaryResponse Visits(VisitOutcomeCounts counts) => new(
        counts.Productive,
        counts.NonProductive,
        counts.Open,

        // As a percentage, because every other rate in this response is one and a dashboard that
        // mixed 0.75 with 75.00 would be read wrong once.
        counts.StrikeRate is { } rate
            ? Math.Round(100m * rate, 2, MidpointRounding.AwayFromZero)
            : null);

    private static AuditSummaryResponse PerfectStore(PerfectStoreSummary summary) => new(
        summary.Audits,
        summary.Scored,
        summary.AverageScore,
        summary.Comparable,
        summary.WeightSetVersions,
        [.. summary.Pillars.Select(pillar => new PillarSummaryResponse(
            pillar.Pillar, pillar.Average, pillar.Measured, pillar.Skipped))]);

    private static OrderSummaryResponse Orders(OrderSummary summary) => new(
        summary.Orders,
        summary.Lines,
        summary.LinesPerOrder,
        summary.Rejected,
        summary.Cancelled,
        summary.PriceDisagreements,
        [.. summary.Value.Select(value => new OrderValueResponse(
            value.CurrencyCode, value.Net, value.Tax, value.Gross, value.Orders))]);
}
