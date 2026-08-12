using System.Text.Json.Serialization;
using FieldKit.Modules.Outlets.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FieldKit.Modules.Journey;

/// <summary>Where an outlet's frequency came from — the rung of the ladder that answered.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<FrequencySource>))]
public enum FrequencySource
{
    /// <summary>A rule naming this outlet.</summary>
    Outlet,

    /// <summary>The default for the segment the outlet is in.</summary>
    Segment,
}

/// <summary>What an outlet is due, and which rule said so.</summary>
/// <remarks>
/// The source is returned rather than kept private because "why is this shop planned four times a
/// month?" is the question an admin actually asks, and answering it from the outside would mean
/// re-running the resolution by hand against a screen. It is also what lets the back office show an
/// override as an override rather than as a number that happens to differ.
/// </remarks>
public sealed record ResolvedFrequency(Guid OutletId, CallFrequency Frequency, FrequencySource Source);

/// <summary>
/// Resolves each outlet's effective call frequency: its own rule, else its segment's (<c>JRN-01</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, and staying internal for now.</b> Its only caller is generation (<c>JRN-03</c>),
/// which lives in this module — so there is nothing to expose and no contract to guess at. The same
/// call the module registry has been making since W1.
/// </para>
/// <para>
/// <b>Bulk, because generation resolves a rep's whole territory at once.</b> A per-outlet signature
/// would turn one plan into a query per shop, and the shape of the method is what decides whether
/// that is even possible — the lesson <c>ITerritoryDirectory</c> wrote down.
/// </para>
/// <para>
/// <b>An outlet with no rule and no segment rule is absent from the result</b>, not present with a
/// zero. "Nobody has said how often to visit this shop" is an ordinary state in a half-configured
/// tenant, and it is not the same as "visit it never" — which is why <see cref="CallFrequency"/>
/// refuses to represent zero at all. Generation decides what to do about an outlet it was given no
/// frequency for; it is a gap in configuration, and the honest place to surface it is a screen that
/// can name the shops, not a silent default here.
/// </para>
/// </remarks>
internal sealed class FrequencyResolver(JourneyDbContext db, IOutletClassification classification)
{
    public async Task<IReadOnlyList<ResolvedFrequency>> ForOutletsAsync(
        IReadOnlyCollection<Guid> outletIds, CancellationToken cancellationToken = default)
    {
        if (outletIds.Count == 0) return [];

        var wanted = outletIds.Distinct().ToArray();

        var overrides = await db.OutletFrequencies
            .Where(rule => wanted.Contains(rule.OutletId))
            .ToDictionaryAsync(rule => rule.OutletId, cancellationToken);

        // Only the outlets still unanswered need classifying. A tenant that overrides everything
        // asks Outlets nothing, which is the shape worth having on the path generation runs.
        var unresolved = wanted.Where(id => !overrides.ContainsKey(id)).ToArray();

        var segments = unresolved.Length == 0
            ? []
            : await classification.ClassifyManyAsync(unresolved, cancellationToken);

        // Case-insensitive, deliberately: the segment is free text on the outlet and free text on
        // the rule, so "A" and "a" are the same grade to everyone except a string comparer. The rule
        // keeps the tenant's own casing (SegmentFrequency.Normalise) — matching is the lenient half,
        // storage is the faithful one.
        var defaults = await db.SegmentFrequencies.ToDictionaryAsync(
            rule => rule.Segment, cancellationToken);
        var bySegment = defaults.ToDictionary(
            entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

        var resolved = new List<ResolvedFrequency>(wanted.Length);

        foreach (var outletId in wanted)
        {
            if (overrides.TryGetValue(outletId, out var own))
            {
                resolved.Add(new ResolvedFrequency(outletId, own.Frequency, FrequencySource.Outlet));
                continue;
            }

            var segment = segments.FirstOrDefault(row => row.OutletId == outletId)?.Segment;

            if (segment is not null && bySegment.TryGetValue(segment.Trim(), out var byGrade))
            {
                resolved.Add(new ResolvedFrequency(outletId, byGrade.Frequency, FrequencySource.Segment));
            }
        }

        return resolved;
    }
}
