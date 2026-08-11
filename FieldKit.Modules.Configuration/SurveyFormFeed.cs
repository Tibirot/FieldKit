using System.Globalization;
using FieldKit.Infrastructure;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Configuration;

/// <summary>
/// Survey forms, as a delta (<c>OFF-03</c>, <c>AUD-04</c>) — W10 slice 7.
/// </summary>
/// <remarks>
/// The same two-source merge as every other feed — live rows above the cursor, tombstones above the
/// cursor — with no scope predicate at all. The tenant filter is the DbContext's, as everywhere.
/// </remarks>
internal sealed class SurveyFormFeed(ConfigurationDbContext db) : ISurveyFormFeed
{
    public async Task<SurveyFormChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default)
    {
        // `Include` rather than a projection, for the reason the workflow feed gives: EF cannot
        // translate the ordered sub-select into the record's constructor, and a form is small.
        var changed = await db.SurveyForms
            .Include(form => form.Questions)
            .Where(form => form.RowVersion > cursor)
            .OrderBy(form => form.RowVersion)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var upserts = changed.Select(Describe).ToList();

        var tombstones = await db.Set<Tombstone>()
            .Where(tombstone => tombstone.RowVersion > cursor
                && tombstone.EntityType == nameof(SurveyForm))
            .OrderBy(tombstone => tombstone.RowVersion)
            .Take(limit)
            .Select(tombstone => new ReferenceTombstone(tombstone.EntityId, tombstone.RowVersion))
            .ToListAsync(cancellationToken);

        // The highest version *in this page*, never the table's maximum — a truncated page must
        // resume rather than skip everything between the last row sent and the high-water mark.
        var highest = cursor;
        if (upserts.Count > 0) highest = Math.Max(highest, upserts[^1].RowVersion);
        if (tombstones.Count > 0) highest = Math.Max(highest, tombstones[^1].RowVersion);

        return new SurveyFormChangePage(upserts, tombstones, highest);
    }

    private static SurveyFormSnapshot Describe(SurveyForm form) => new(
        form.Id,
        form.Name,
        [.. form.Questions
            .OrderBy(question => question.Order)
            .Select(question => new SurveyQuestionSnapshot(
                question.Order,
                question.Key,
                question.Text,
                question.Type.ToString(),
                question.Mandatory,
                question.Options))],
        form.RowVersion);
}

/// <summary>
/// Published perfect-store weightings, as a delta (<c>OFF-03</c>, <c>BR-AUD-8</c>) — W10 slice 7.
/// </summary>
/// <remarks>
/// <b>The one feed with a predicate that is not about scope.</b> Every other filters by cursor and
/// perhaps by whose row it is; this one also filters to <i>published</i>, because a draft is a thing
/// an administrator is still editing and a device that scored against one would have its audit
/// refused on push. It is a rule about state, not about audience.
/// </remarks>
internal sealed class ScoreWeightFeed(ConfigurationDbContext db) : IScoreWeightFeed
{
    public async Task<ScoreWeightChangePage> GetChangesAsync(
        long cursor, int limit, CancellationToken cancellationToken = default)
    {
        var changed = await db.ScoreWeightSets
            .Include(set => set.Weights)
            .Where(set => set.RowVersion > cursor && set.PublishedAtUtc != null)
            .OrderBy(set => set.RowVersion)
            .Take(limit)
            .ToListAsync(cancellationToken);

        var upserts = changed.Select(Describe).ToList();

        /*
         * Carried for the shape's sake rather than because anything produces one.
         *
         * Nothing deletes a published weight set — sealed audits point at them forever — so this
         * query should return empty for the life of the product. It is here because a feed whose
         * page type has a tombstone list and never fills it invites the next reader to wonder
         * whether it was forgotten.
         */
        var tombstones = await db.Set<Tombstone>()
            .Where(tombstone => tombstone.RowVersion > cursor
                && tombstone.EntityType == nameof(ScoreWeightSet))
            .OrderBy(tombstone => tombstone.RowVersion)
            .Take(limit)
            .Select(tombstone => new ReferenceTombstone(tombstone.EntityId, tombstone.RowVersion))
            .ToListAsync(cancellationToken);

        /*
         * The cursor advances past *skipped* drafts, and it has to.
         *
         * A draft has a row version like anything else, and it is above the cursor. If the watermark
         * only ever moved to the highest row actually sent, a tenant with a draft sitting at the top
         * of the table would have every device re-query that draft on every pull forever — and the
         * page would arrive empty each time, so nothing would look wrong.
         *
         * So the watermark is the highest version *considered*, which is the highest weight-set row
         * at or below the page's reach. Publishing that draft bumps its row version again (publish
         * is a write), so it lands on the next pull rather than being skipped.
         */
        var highest = await db.ScoreWeightSets
            .Where(set => set.RowVersion > cursor)
            .OrderBy(set => set.RowVersion)
            .Take(limit)
            .MaxAsync(set => (long?)set.RowVersion, cancellationToken) ?? cursor;

        highest = Math.Max(highest, cursor);
        if (tombstones.Count > 0) highest = Math.Max(highest, tombstones[^1].RowVersion);

        return new ScoreWeightChangePage(upserts, tombstones, highest);
    }

    private static ScoreWeightSetSnapshot Describe(ScoreWeightSet set) => new(
        set.Id,
        set.Version,
        set.PublishedAtUtc!.Value,
        [.. set.Weights
            .OrderBy(weight => weight.Pillar)
            .Select(weight => new ScoreWeightSnapshot(
                weight.Pillar.ToString(),

                // Invariant culture and a fixed shape: a device parsing "33,34" would be a decimal
                // separator away from a score that disagrees with the server's.
                weight.Percentage.ToString("0.00##", CultureInfo.InvariantCulture)))],
        set.RowVersion);
}
