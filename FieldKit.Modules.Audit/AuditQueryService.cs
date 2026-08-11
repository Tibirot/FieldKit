using FieldKit.Modules.Audit.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Audit;

/// <summary>Answers <see cref="IAuditQuery"/> from the audit schema (<c>AUD-09</c>).</summary>
/// <remarks>
/// Both reads <c>Include</c> all three sections, because an audit without its lines answers nothing a
/// reader asked — and the alternative is one query per section per audit. It reads only Audit's own
/// schema (AT-1); resolving a product id to a name is the caller's job, through Products' contracts.
/// </remarks>
internal sealed class AuditQueryService(AuditDbContext db) : IAuditQuery
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

        return audit?.Describe();
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

        return [.. audits.Select(audit => audit.Describe())];
    }

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
