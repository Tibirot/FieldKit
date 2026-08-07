using FieldKit.Modules.Outlets.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Outlets;

/// <summary>
/// Classifies outlets for other modules. Internal — consumers bind to
/// <see cref="IOutletClassification"/> (AT-2).
/// </summary>
internal sealed class OutletClassifier(OutletsDbContext db) : IOutletClassification
{
    public async Task<IReadOnlyList<Contracts.OutletClassification>> ClassifyManyAsync(
        IReadOnlyCollection<Guid> outletIds, CancellationToken cancellationToken = default)
    {
        if (outletIds.Count == 0) return [];

        // No tenant predicate: the global query filter supplies it. Writing one by hand would be the
        // beginning of a codebase where some queries have it and some do not.
        //
        // Closed outlets are classified like any other. A closed shop still has a channel, and the
        // callers asking are deciding what *would* apply to it — an assortment report, a historical
        // order's price. Filtering here would make "no classification" mean two different things.
        // The country comes from the address, which is optional — so it is null for a shop entered
        // without one, and the consumer decides what that means. Tax refuses to guess a rate from a
        // guessed jurisdiction, which is the only safe reading.
        return await db.Outlets
            .Where(outlet => outletIds.Contains(outlet.Id))
            .Select(outlet => new Contracts.OutletClassification(
                outlet.Id, outlet.ChannelId, outlet.Address == null ? null : outlet.Address.CountryCode))
            .ToListAsync(cancellationToken);
    }

    public Task<bool> ChannelExistsAsync(Guid channelId, CancellationToken cancellationToken = default) =>
        db.Channels.AnyAsync(channel => channel.Id == channelId, cancellationToken);
}
