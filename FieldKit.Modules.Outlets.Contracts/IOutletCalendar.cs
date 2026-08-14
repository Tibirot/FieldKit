namespace FieldKit.Modules.Outlets.Contracts;

/// <summary>
/// Which trading day an instant falls on, at a given shop (<c>BR-PRD-6</c>) — W11½ R6b.
/// </summary>
/// <remarks>
/// <para>
/// <b>A price list runs by calendar day, and a calendar day starts at a different instant in every
/// place.</b> Until this contract existed, the Order module re-priced against the *UTC* date of the
/// capture instant while the device priced against the *rep's phone's* local date — two different
/// rules rather than one rule rounded twice. An order taken in Bucharest before 03:00 was therefore
/// reported as disagreeing with a server that had asked a different question (regression F6).
/// </para>
/// <para>
/// <b>The shop decides, because it is the party to the trade that cannot move.</b> A rep may cross
/// zones during a shift; the counter does not. <c>Outlet.TimeZoneId</c> has carried the answer since
/// W1 and nothing outside this module could read it.
/// </para>
/// <para>
/// <b>Its own contract rather than a wider <c>IOutletCatalog</c>.</b> That interface says a caller
/// needing more "should ask for a contract that says what it needs, not for this one to grow", and
/// <see cref="IOutletClassification"/> is the standing precedent for answering that with a second
/// narrow one. Every existing consumer of <c>OutletSummary</c> would otherwise bind to a field it
/// never asked for.
/// </para>
/// <para>
/// <b>It returns the *day*, not the zone.</b> Handing back an IANA name would put the conversion in
/// every caller — which is the duplication this slice exists to remove, one module further out. The
/// rule lives where the data does, and Order never learns what a time zone is.
/// </para>
/// </remarks>
public interface IOutletCalendar
{
    /// <summary>
    /// The business day <paramref name="at"/> falls on for each outlet, keyed by outlet id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Batch, mirroring <c>IOutletCatalog.FindManyAsync</c> and <c>IOutletClassification.ClassifyManyAsync</c>
    /// for the same reason: a caller settling a batch of pushed orders would otherwise turn one query
    /// into as many as the rep captured.
    /// </para>
    /// <para>
    /// <b>An outlet with no answer is absent from the result rather than present with a guess.</b>
    /// Two things produce that: an id this tenant does not have, and a zone neither runtime
    /// recognises — .NET and V8 do not ship identical zone databases, so a tenant can hold a name one
    /// of them knows. Falling back to UTC would silently reinstate the defect for exactly the shops
    /// nobody had noticed, and it would look like the rule working.
    /// </para>
    /// <para>
    /// The caller decides what an absence means. For <c>Order</c> it means the order is recorded as
    /// *not re-priced* rather than *differs*, which is the same answer it already gives an outlet the
    /// pricing service does not know.
    /// </para>
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, DateOnly>> BusinessDaysAsync(
        IReadOnlyCollection<Guid> outletIds,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);
}
