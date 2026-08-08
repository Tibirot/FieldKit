using FieldKit.Modules.Outlets.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// Answers <see cref="IOutletGeofence"/> from the outlet's own coordinates (<c>OUT-08</c>).
/// </summary>
/// <remarks>
/// The radius is the same for every outlet until <c>OUT-08</c> is built. It is attached here rather
/// than left to the caller so that the day it becomes per-outlet or per-channel, this query changes
/// and nothing else does.
/// </remarks>
internal sealed class OutletGeofences(OutletsDbContext db) : IOutletGeofence
{
    public async Task<OutletGeofence?> ForOutletAsync(
        Guid outletId, CancellationToken cancellationToken = default)
    {
        // Closed outlets answer like any other. Whether a rep should be checking in at a shut shop
        // is a decision for Visit — the same reasoning IOutletClassification carries, and the same
        // reason "no such outlet" and "closed" must not collapse into one answer here.
        return await db.Outlets
            .Where(outlet => outlet.Id == outletId)
            .Select(outlet => new OutletGeofence(
                outlet.Id, outlet.Latitude, outlet.Longitude, IOutletGeofence.DefaultRadiusMetres))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
