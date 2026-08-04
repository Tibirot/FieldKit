using FieldKit.Modules.Outlets.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// Resolves outlets for other modules. Internal — consumers bind to <see cref="IOutletCatalog"/> (AT-2).
/// </summary>
internal sealed class OutletCatalog(OutletsDbContext db) : IOutletCatalog
{
    public async Task<IReadOnlyList<OutletSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> outletIds, CancellationToken cancellationToken = default)
    {
        if (outletIds.Count == 0) return [];

        // No tenant predicate: the global query filter supplies it. Writing one by hand would be the
        // beginning of a codebase where some queries have it and some do not.
        return await db.Outlets
            .Where(outlet => outletIds.Contains(outlet.Id))
            .Select(outlet => new OutletSummary(
                outlet.Id, outlet.Code, outlet.Name, outlet.Status != OutletStatus.Closed))
            .ToListAsync(cancellationToken);
    }
}
