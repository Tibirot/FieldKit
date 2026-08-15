using FieldKit.Modules.Visit.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Visit;

/// <summary>Reading visits back (<c>VIS-10</c>) — W12 slice 1.</summary>
/// <remarks>
/// <para>
/// <b>The counting happens in Postgres, not in memory.</b> One grouped aggregate returns at most
/// three rows whatever the window holds; materialising a month of visits to count them would move
/// the cost onto the web server and grow it with the tenant. That is the whole reason the contract
/// asks for counts rather than rows — a caller reducing a list would have paid it too.
/// </para>
/// <para>
/// <c>AsNoTracking</c> for the reason <c>OrderQueryService</c> gives: every caller here is a reader,
/// and the change tracker is bookkeeping for changes nobody will make.
/// </para>
/// </remarks>
internal sealed class VisitQueryService(VisitDbContext db) : IVisitQuery
{
    public async Task<VisitOutcomeCounts> CountByOutcomeAsync(
        IReadOnlyCollection<Guid> outletIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        // Nothing in scope is not the same as no filter, and the query below would read the second
        // if this did not answer first: `Contains` over an empty set is `false` in Postgres, so it
        // would in fact return zeroes — but relying on that is relying on a provider's translation
        // of an edge case to enforce a scoping rule. Said here, where it is a decision.
        if (outletIds.Count == 0) return new VisitOutcomeCounts(0, 0, 0);

        /*
         * The window, as instants.
         *
         * `CheckedInAtUtc` is a `DateTimeOffset` and the contract promises inclusive UTC days, so the
         * comparison is half-open over instants — `>= from 00:00` and `< the day after to`. Written
         * this way rather than as `.Date >= from` because a function on the column cannot use the
         * index, and this is the query a dashboard runs on every load.
         */
        var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var counted = await db.Visits
            .AsNoTracking()
            .Where(visit => outletIds.Contains(visit.OutletId))
            .Where(visit => visit.CheckedInAtUtc >= start && visit.CheckedInAtUtc < end)
            .GroupBy(visit => visit.Outcome)
            .Select(group => new { Outcome = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        int Total(VisitOutcome? outcome) =>
            counted.SingleOrDefault(row => row.Outcome == outcome)?.Count ?? 0;

        // A null outcome is an open visit and nothing else: `Visit.CheckOut` sets both together, and
        // the aggregate has no path that writes one without the other.
        return new VisitOutcomeCounts(
            Productive: Total(VisitOutcome.Productive),
            NonProductive: Total(VisitOutcome.NonProductive),
            Open: Total(null));
    }
}
