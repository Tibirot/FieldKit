using FieldKit.SharedKernel;

namespace FieldKit.Modules.Journey.Contracts;

/// <summary>
/// One call on a rep's round, as the device holds it (<c>JRN-05</c>, sync engine §3).
/// </summary>
/// <remarks>
/// <para>
/// Everything the *Today's Journey* screen draws and check-in needs, and nothing else. The plan it
/// belongs to is not here: a device that has the calls has the round, and the plan row carries a
/// generation timestamp and a status that mean something to a supervisor reviewing a draft — which
/// is not a thing a phone ever sees.
/// </para>
/// <para>
/// <see cref="Status"/> and <see cref="Source"/> cross as strings rather than as Journey's enums,
/// so this assembly describes its own record without a consumer having to bind to an ordinal that
/// changes when a value is inserted.
/// </para>
/// </remarks>
public sealed record PlannedVisitSnapshot(
    Guid Id,
    Guid OutletId,
    DateOnly Date,
    string Status,
    string Source,
    string? NotVisitedReason,
    long RowVersion);

/// <summary>One page of a rep's round: what to upsert, what to drop, and how far it now is.</summary>
public sealed record JourneyChangePage(
    IReadOnlyList<PlannedVisitSnapshot> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// The calls a device should hold, as a delta (<c>OFF-03</c>, W8 slice 8a).
/// </summary>
/// <remarks>
/// <para>
/// <b>One method, where outlets needed two — and the difference is the whole point of this slice.</b>
/// An outlet's membership is a server-side fact that moves independently of the row: a shop can enter
/// a rep's territory without being edited, carrying a row version far below the device's cursor, so
/// <c>IReferenceChangeFeed</c> needs a baseline call to hand over rows a delta would never mention.
/// </para>
/// <para>
/// A planned call cannot do that. It is <i>born</i> belonging to one rep — the plan names the user —
/// and it never changes hands. So membership only ever changes by the row being created, and
/// creation stamps a row version above every cursor by construction. A cursor is sufficient, and a
/// baseline method would be an unreachable branch dressed as symmetry.
/// </para>
/// <para>
/// <b>Published plans only.</b> A draft is a supervisor's experiment that the next generation run
/// replaces wholesale, so sending one would put calls on a rep's phone that are about to stop
/// existing — the same rule <see cref="IJourneyQuery"/> enforces, for the same reason, and enforced
/// here rather than left to Sync because "which plans count" is Journey's business.
/// </para>
/// <para>
/// <b>No date window.</b> The feed sends every call on the rep's published plans and lets the device
/// decide what is too old to show. A server-side window would make the passage of midnight a
/// membership change with no row version behind it — exactly the problem the outlet baseline exists
/// to work around — for a rule a phone can evaluate perfectly well against a date it already holds.
/// </para>
/// </remarks>
public interface IJourneyChangeFeed
{
    /// <summary>
    /// Calls on <paramref name="userId"/>'s published plans whose row version is above
    /// <paramref name="cursor"/>, plus tombstones for any deleted since.
    /// </summary>
    /// <param name="limit">
    /// A page size. A rep re-binding mid-cycle would otherwise ask for every call on every plan they
    /// have ever been given, in one response, over a connection that is bad by assumption.
    /// </param>
    Task<JourneyChangePage> GetChangesAsync(
        long cursor, string userId, int limit, CancellationToken cancellationToken = default);
}
