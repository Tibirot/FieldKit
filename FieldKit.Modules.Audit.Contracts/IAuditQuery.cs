namespace FieldKit.Modules.Audit.Contracts;

/// <summary>One product's availability, as stored (<c>AUD-01</c>).</summary>
public sealed record AvailabilityLine(Guid ProductId, AvailabilityStatus Status);

/// <summary>One product's facings, as stored (<c>AUD-02</c>).</summary>
public sealed record FacingsLine(Guid ProductId, int Facings);

/// <summary>One product's price check, as stored (<c>AUD-03</c>).</summary>
public sealed record PriceLine(
    Guid ProductId, long ObservedMinorUnits, long? ExpectedMinorUnits, string Currency);

/// <summary>
/// An audit as a reader sees it (<c>AUD-09</c>).
/// </summary>
/// <remarks>
/// The whole audit rather than a summary, because the readers this exists for — the supervisor
/// reviewing one shop and, later, the pillar breakdown — both want the lines. A summary contract
/// would only push the second query somewhere else.
/// </remarks>
/// <param name="CategoryFacings">
/// The share-of-shelf denominator, or null when the rep could not count it. A null here is what makes
/// the pillar <i>skipped</i> rather than zero (<c>BR-AUD-2</c>, W10 slice 0), so it is surfaced
/// rather than defaulted.
/// </param>
public sealed record AuditRecord(
    Guid AuditId,
    Guid VisitId,
    Guid OutletId,
    string UserId,
    DateTimeOffset CapturedAtUtc,
    int WeightSetVersion,
    int? CategoryFacings,
    IReadOnlyList<AvailabilityLine> Availability,
    IReadOnlyList<FacingsLine> Facings,
    IReadOnlyList<PriceLine> Prices);

/// <summary>
/// Audits for an outlet or a visit (<c>AUD-09</c>).
/// </summary>
/// <remarks>
/// Read-only by design. Everything that <i>creates</i> an audit goes through
/// <see cref="IAuditIngest"/>, and separating the two is what stops a reporting consumer acquiring a
/// write path it never asked for — the same split <c>IVisitWorkflow</c> and <c>IVisitWorkflowFeed</c>
/// make.
/// </remarks>
public interface IAuditQuery
{
    /// <summary>The audit worked during this visit, or null if none was.</summary>
    Task<AuditRecord?> ForVisitAsync(Guid visitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// This outlet's audits, newest first.
    /// </summary>
    /// <remarks>
    /// Bounded by <paramref name="limit"/> rather than paged. The question a reader asks here is
    /// "how has this shop been trending" — a handful of recent audits — and a cursor would be
    /// machinery for a screen that does not scroll. `AUD-09`'s trend views aggregate rather than
    /// list, and will ask their own question when they exist.
    /// </remarks>
    Task<IReadOnlyList<AuditRecord>> ForOutletAsync(
        Guid outletId, int limit, CancellationToken cancellationToken = default);
}
