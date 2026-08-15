using FieldKit.Modules.Audit.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Audit;

/// <summary>Answers <see cref="IAuditQuery"/> from the audit schema (<c>AUD-09</c>).</summary>
/// <remarks>
/// Both reads <c>Include</c> all three sections, because an audit without its lines answers nothing a
/// reader asked — and the alternative is one query per section per audit. It reads only Audit's own
/// schema (AT-1); resolving a product id to a name is the caller's job, through Products' contracts.
/// </remarks>
/// <remarks>
/// The clock is here because one thing a reader sees is not stored: whether a photograph that has
/// not been confirmed is still expected or has stopped coming (W11 slice 13a). Injected rather than
/// read statically, so a test can age an audit past the threshold without waiting a week.
/// </remarks>
internal sealed class AuditQueryService(AuditDbContext db, IClock clock) : IAuditQuery
{
    /// <summary>The most audits one outlet read will return, however large a limit is asked for.</summary>
    /// <remarks>
    /// A ceiling rather than a page size. The question this read answers is "how has this shop been
    /// trending lately"; a caller asking for ten thousand is either paging — which this deliberately
    /// does not do — or has a bug, and either way the database should not wear it.
    /// </remarks>
    public const int MaximumOutletAudits = 100;

    public async Task<AuditRecord?> ForVisitAsync(
        Guid visitId, CancellationToken cancellationToken = default)
    {
        var audit = await Query().SingleOrDefaultAsync(row => row.VisitId == visitId, cancellationToken);

        return audit?.Describe(clock.UtcNow);
    }

    public async Task<IReadOnlyList<AuditRecord>> ForOutletAsync(
        Guid outletId, int limit, CancellationToken cancellationToken = default)
    {
        var audits = await Query()
            .Where(row => row.OutletId == outletId)

            // By when the rep measured, not by when the server stored it. A day of offline audits
            // drained at once shares a `CreatedAtUtc` to the second, and ordering by that would put
            // the shop's history in whatever order the outbox happened to flush.
            .OrderByDescending(row => row.CapturedAtUtc)
            .Take(Math.Clamp(limit, 1, MaximumOutletAudits))
            .ToListAsync(cancellationToken);

        return [.. audits.Select(audit => audit.Describe(clock.UtcNow))];
    }

    public async Task<PerfectStoreSummary> SummariseAsync(
        IReadOnlyCollection<Guid> outletIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        // Nothing in scope is not the same as no filter — the decision `VisitQueryService` and
        // `JourneyQueries` both make, said here rather than left to a provider's translation.
        if (outletIds.Count == 0) return Empty;

        // `CapturedAtUtc` is when the rep measured, so the window is half-open over instants for the
        // same reason the visit side is: a function on the column cannot use an index, and this is a
        // query a dashboard runs on every load.
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var audits = db.Audits
            .AsNoTracking()
            .Where(audit => outletIds.Contains(audit.OutletId))
            .Where(audit => audit.CapturedAtUtc >= start && audit.CapturedAtUtc < end);

        /*
         * Three aggregates rather than one, and none of them ships a row.
         *
         * `Average` over a nullable column ignores nulls in SQL, which is exactly the rule this needs
         * — an unscored audit must not average in as zero — but it is a coincidence of SQL semantics
         * rather than something a reader can see, so `Scored` is counted separately and the two are
         * asserted against each other in the tests.
         */
        var scores = await audits
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Audits = group.Count(),
                Scored = group.Count(audit => audit.Score != null),
                Average = group.Average(audit => audit.Score),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (scores is null) return Empty;

        var versions = await audits
            .Select(audit => audit.WeightSetVersion)
            .Distinct()
            .OrderBy(version => version)
            .ToListAsync(cancellationToken);

        // A pillar's average is over the audits that *measured* it, and the skipped ones are counted
        // rather than dropped: `BR-AUD-2` renormalises a skipped pillar away instead of scoring it
        // zero, so an average with no count beside it cannot be read safely.
        var pillars = await audits
            .SelectMany(audit => audit.ScoredPillars)
            .GroupBy(pillar => pillar.Pillar)
            .Select(group => new
            {
                Pillar = group.Key,
                Average = group.Average(pillar => pillar.Percentage),
                Measured = group.Count(pillar => pillar.Percentage != null),
                Skipped = group.Count(pillar => pillar.Percentage == null),
            })
            .ToListAsync(cancellationToken);

        return new PerfectStoreSummary(
            Audits: scores.Audits,
            Scored: scores.Scored,
            AverageScore: Round(scores.Average),
            Pillars:
            [
                .. pillars
                    .OrderBy(row => row.Pillar)
                    .Select(row => new PillarAverage(
                        row.Pillar.ToString(), Round(row.Average), row.Measured, row.Skipped)),
            ],
            WeightSetVersions: versions);
    }

    /// <summary>
    /// Half-up (away from zero) to two places — the policy <see cref="PerfectStoreScore"/> already
    /// applies to every score this averages.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Rounded here rather than left to the caller, because an unrounded average is not
    /// reproducible.</b> Postgres computes <c>avg(numeric)</c> at its own scale and returns a
    /// different tail from the same mean taken in C# — the two agreed to sixteen digits and disagreed
    /// after, which is enough to make an equality assertion fail and not nearly enough to matter to
    /// anybody reading a percentage. Rounding at the boundary makes the number the same wherever it
    /// was computed.
    /// </para>
    /// <para>
    /// Two places, half-up, because that is <c>BR-PRD-9</c>'s policy and the scores being averaged
    /// are already rounded to it. A mean carried to twenty places out of inputs carried to two is
    /// precision this module never had.
    /// </para>
    /// </remarks>
    private static decimal? Round(decimal? value) =>
        value is { } number ? Math.Round(number, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>
    /// No audits, and therefore no score, no pillars and no weight sets to disagree about.
    /// </summary>
    /// <remarks>
    /// A summary rather than a null, because "nobody has audited these shops" is an answer a
    /// dashboard has to render either way — and a nullable return would make every caller write the
    /// empty state twice.
    /// </remarks>
    private static PerfectStoreSummary Empty => new(0, 0, null, [], []);

    private IQueryable<Audit> Query() => db.Audits
        .Include(audit => audit.Availability)
        .Include(audit => audit.Facings)
        .Include(audit => audit.Prices)
        .Include(audit => audit.Answers)
        .Include(audit => audit.Photos)
        .Include(audit => audit.ScoredPillars)

        // Five collection includes on one query would otherwise multiply into a cartesian product of
        // the five sections — thirty availability lines, twenty prices and a dozen answers returning
        // thousands of rows for a few hundred facts. Split queries cost round trips and save that,
        // and the case for them got stronger with every section this slice added.
        .AsSplitQuery();
}
