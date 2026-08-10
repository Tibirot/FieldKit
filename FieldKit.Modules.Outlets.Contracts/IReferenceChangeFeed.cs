using FieldKit.SharedKernel;

namespace FieldKit.Modules.Outlets.Contracts;

/// <summary>One outlet as a device holds it — the shape that crosses the wire on a pull.</summary>
/// <remarks>
/// <para>
/// Deliberately not <c>OutletSummary</c>. That one labels an outlet on a screen; this one is a
/// device's copy of a row and carries the <see cref="RowVersion"/> the client stores as its
/// watermark. Sharing a record between "what a page shows" and "what a phone keeps" would tie the
/// wire format to a UI change.
/// </para>
/// <para>
/// <b><see cref="Code"/> is the tenant's own identifier</b> — what the shop is called in their ERP —
/// and it was missing from this record until the W7+W8 demo went looking for it. A name is not
/// unique: a chain has many shops called "Mega Image", and a device that holds only the name can
/// show a rep a list it cannot tell apart, print a name on a document the back office cannot match
/// to a row, or ask "which of these three is it?" with no way to answer. The back office has always
/// had the code (<c>OutletSummary.Code</c>); the field app is the half that has to say a shop's name
/// out loud to a person, so if either copy needed it, it was this one.
/// </para>
/// </remarks>
public sealed record OutletSnapshot(
    Guid Id,
    string Code,
    string Name,
    Guid ChannelId,
    string? Segment,
    string Status,
    double? Latitude,
    double? Longitude,
    long RowVersion);

/// <summary>
/// One page of changes for a device: what to upsert, what to drop, and how far it now is.
/// </summary>
/// <param name="Cursor">
/// The highest row version represented. The device stores this **after** applying everything in the
/// page, so an interrupted pull resumes from the last cursor it committed rather than losing work.
/// </param>
public sealed record ReferenceChangePage(
    IReadOnlyList<OutletSnapshot> Upserts,
    IReadOnlyList<ReferenceTombstone> Tombstones,
    long Cursor);

/// <summary>
/// The outlets a device should hold, as a delta (<c>OFF-03</c>, sync engine §3).
/// </summary>
/// <remarks>
/// <para>
/// Named in the module registry since W1 and deliberately not built until now. The plan's words
/// were "a primitive designed against a protocol that does not exist yet is a guess" — the protocol
/// is <c>/sync/pull</c>, and Sync is the only caller this is shaped for.
/// </para>
/// <para>
/// <b>Two arguments, because ordering and membership are different questions.</b> The cursor orders
/// *content* changes: anything edited since the device last looked has a higher row version
/// (ADR-0013). Scope decides *membership*: which outlets this rep covers at all. An outlet can
/// change without entering scope, and — the case that makes this awkward — it can enter scope
/// without changing, carrying a row version far below the device's cursor. A pure delta would never
/// send it.
/// </para>
/// <para>
/// So there are two methods, one per question. <see cref="GetChangesAsync"/> orders content for
/// outlets the device already holds; <see cref="GetBaselineAsync"/> hands over outlets it has never
/// been told about, whatever their row version. Sync decides which ids fall in which set by diffing
/// the device's stored scope against the rep's current one — this module does not know what a
/// territory is and is not asked to.
/// </para>
/// </remarks>
public interface IReferenceChangeFeed
{
    /// <summary>
    /// Outlets in <paramref name="outletIds"/> whose row version is above <paramref name="cursor"/>,
    /// plus tombstones for any of them deleted since.
    /// </summary>
    /// <param name="outletIds">
    /// The device's current scope, resolved by the caller. Passed in rather than resolved here
    /// because Outlets does not know what a territory is — Organization does (<c>IRepScope</c>) —
    /// and a module that had to ask would be reaching across a boundary to answer its own question.
    /// </param>
    /// <param name="limit">
    /// A page size. A device rebuilding from zero would otherwise ask for a tenant's whole outlet
    /// base in one response, over a connection that is bad by assumption.
    /// </param>
    Task<ReferenceChangePage> GetChangesAsync(
        long cursor,
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every named outlet as it stands, ignoring any cursor — the first thing a device is told about
    /// rows that have just entered its scope.
    /// </summary>
    /// <remarks>
    /// No cursor parameter, deliberately. These ids are new *to this device*, so "what changed
    /// since" is not a question that can be asked about them: the answer would exclude an outlet
    /// last edited before the device existed, which is most of them.
    /// </remarks>
    Task<IReadOnlyList<OutletSnapshot>> GetBaselineAsync(
        IReadOnlyCollection<Guid> outletIds,
        int limit,
        CancellationToken cancellationToken = default);
}
