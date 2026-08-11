using FieldKit.Modules.Configuration.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Configuration;

/// <summary>Answers <see cref="IScoreWeights"/> from the stored weight sets (<c>CFG-05</c>).</summary>
/// <remarks>
/// One query, filtered to published sets in the database rather than after loading — a draft is not
/// an answer this contract has, and fetching one to discard it would read a row nobody may use.
/// It reads only Configuration's own schema (AT-1).
/// </remarks>
internal sealed class ScoreWeightCatalog(ConfigurationDbContext db) : IScoreWeights
{
    public async Task<ScoreWeightSetDescriptor?> ByVersionAsync(
        int version, CancellationToken cancellationToken = default)
    {
        var set = await db.ScoreWeightSets
            .Include(candidate => candidate.Weights)
            .SingleOrDefaultAsync(
                candidate => candidate.Version == version && candidate.PublishedAtUtc != null,
                cancellationToken);

        return set is null
            ? null
            : new ScoreWeightSetDescriptor(
                set.Version,
                set.PublishedAtUtc!.Value,
                [.. set.Weights
                    .OrderBy(weight => weight.Pillar)
                    .Select(weight => new WeightedPillar(weight.Pillar, weight.Percentage))]);
    }
}
