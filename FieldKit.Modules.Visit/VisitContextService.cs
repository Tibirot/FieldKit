using FieldKit.Modules.Visit.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Visit;

/// <summary>Answers <see cref="IVisitContext"/> from the visit schema (<c>VIS-01</c>).</summary>
/// <remarks>
/// <para>
/// One projected query, and no <c>Include</c>: the contract carries five facts, and loading a
/// visit's steps to answer "is it sealed" would page in a whole visit per audit ingested.
/// </para>
/// <para>
/// It reads only Visit's own schema, and the tenant filter is the context's — a visit in another
/// tenant is not found rather than refused, which is the same answer a visit that never existed
/// gives.
/// </para>
/// </remarks>
internal sealed class VisitContextService(VisitDbContext db) : IVisitContext
{
    public Task<VisitFacts?> FindAsync(Guid visitId, CancellationToken cancellationToken = default) =>
        db.Visits
            .Where(visit => visit.Id == visitId)
            .Select(visit => new VisitFacts(
                visit.Id,
                visit.OutletId,
                visit.UserId,
                visit.Status == VisitStatus.CheckedOut,
                visit.CheckedOutAtUtc))
            .SingleOrDefaultAsync(cancellationToken);
}
