using FieldKit.Modules.Outlets.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// Dates instants by the shop they happened at. Internal — consumers bind to
/// <see cref="IOutletCalendar"/> (AT-2).
/// </summary>
internal sealed class OutletCalendar(OutletsDbContext db) : IOutletCalendar
{
    public async Task<IReadOnlyDictionary<Guid, DateOnly>> BusinessDaysAsync(
        IReadOnlyCollection<Guid> outletIds,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        if (outletIds.Count == 0) return new Dictionary<Guid, DateOnly>();

        // No tenant predicate: the global query filter supplies it, as everywhere.
        //
        // Closed outlets are dated like any other, for the reason `OutletClassifier` gives — the
        // caller is usually settling something historical, and a shop that shut last week still had
        // a trading day when the order was taken.
        var zones = await db.Outlets
            .Where(outlet => outletIds.Contains(outlet.Id))
            .Select(outlet => new { outlet.Id, outlet.TimeZoneId })
            .ToListAsync(cancellationToken);

        var days = new Dictionary<Guid, DateOnly>(zones.Count);

        foreach (var outlet in zones)
        {
            // The rule itself lives in SharedKernel beside `Money`, because the device implements it
            // too and `vectors/pricing/business-day.v1.json` holds the pair to one answer. What this
            // class decides is *whose* day it is — the shop's — which is the part only Outlets knows.
            if (BusinessDay.On(outlet.TimeZoneId, at) is { } day) days[outlet.Id] = day;
        }

        return days;
    }
}
