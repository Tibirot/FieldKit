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
/// <para>
/// <b><see cref="RadiusMetres"/> travels even though it is a constant today</b>
/// (<see cref="IOutletGeofence.DefaultRadiusMetres"/>; per-outlet radii are <c>OUT-08</c>). The
/// device decides whether a rep is inside the fence with no network, and the server stores that
/// verdict unmodified — so the alternative is a <c>150</c> written into the TypeScript, which agrees
/// with this server exactly until <c>OUT-08</c> ships and then disagrees silently, on the one input
/// the parity vectors cannot see. Sending it makes <c>OUT-08</c> a change to <c>OutletGeofences</c>
/// and to nothing else.
/// </para>
/// <para>
/// <b><see cref="CountryCode"/> is here for tax, and for nothing else</b> (<c>PRD-07</c>, W11 slice
/// 7c). A tax rate is a fact about a jurisdiction and a class; W11 slice 7b put the rates on the
/// device and left them unusable, because the device had no way to say which country the shop it is
/// standing in belongs to. It is the *shop's* half of that match — not the rep's, not the tenant's:
/// a tenant selling across a border has reps who cross it.
/// </para>
/// <para>
/// <b>Nullable, and the null is load-bearing.</b> An address is optional (<c>OUT-01</c> — a
/// half-known outlet must still be recordable), so a shop entered without one has no country. That
/// means <i>unknown tax</i>, which is what <c>TaxEngine.Resolve</c> and <c>priceLine</c> already
/// agree it means — not untaxed, and not a default worth guessing. Sending an empty string or a
/// tenant default would turn a missing setup step into a confident wrong number on an invoice.
/// </para>
/// <para>
/// Upper-cased at the source (<c>Outlet.Normalise</c>), because it is compared to
/// <c>TaxRate.CountryCode</c>, which is also upper-cased. The device upper-cases again on lookup —
/// belt and braces on a comparison whose failure mode is silence.
/// </para>
/// <para>
/// <b><see cref="TimeZoneId"/> is here so the device and the server can agree which day it is</b>
/// (<c>BR-PRD-6</c>, regression F6) — W11½ R6. A price list runs by calendar day, and a calendar day
/// starts at a different instant in every place. Until now the device dated its pricing by the
/// *rep's phone* and the server re-priced by the *UTC* day: two different rules, not one rule
/// rounded twice, so a rep in Bucharest before 03:00 was reported as disagreeing with a server that
/// had simply asked a different question.
/// </para>
/// <para>
/// <b>The shop's zone decides, because the shop is the party to the trade that cannot move.</b> A
/// rep may cross zones during a shift; the counter does not.
/// </para>
/// <para>
/// <b>Required, and an IANA name rather than an offset</b> — <c>Europe/Bucharest</c>, not
/// <c>+02:00</c>. <see cref="Outlet.TimeZoneId"/> has been required since W1 and says the same
/// thing: an offset is wrong twice a year, and deriving the zone from the coordinates would make the
/// answer depend on which device asked. Nothing had ever carried it out of this module, which is the
/// whole of the gap.
/// </para>
/// </remarks>
public sealed record OutletSnapshot(
    Guid Id,
    string Code,
    string Name,
    Guid ChannelId,
    string? Segment,
    string Status,
    string? CountryCode,
    double? Latitude,
    double? Longitude,
    int RadiusMetres,
    string TimeZoneId,
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
