namespace FieldKit.Modules.Audit.Contracts;

/// <summary>One product's availability, as stored (<c>AUD-01</c>).</summary>
public sealed record AvailabilityLine(Guid ProductId, AvailabilityStatus Status);

/// <summary>One product's facings, as stored (<c>AUD-02</c>).</summary>
public sealed record FacingsLine(Guid ProductId, int Facings);

/// <summary>One product's price check, as stored (<c>AUD-03</c>).</summary>
public sealed record PriceLine(
    Guid ProductId, long ObservedMinorUnits, long? ExpectedMinorUnits, string Currency);

/// <summary>One survey answer, as stored, with the question as it was asked (<c>AUD-04</c>).</summary>
public sealed record AnswerLine(int Order, string QuestionKey, string QuestionText, string Value);

/// <summary>
/// Whether the bytes behind a photo reference have actually arrived (<c>OFF-08</c>, <c>B5</c>).
/// </summary>
/// <remarks>
/// Three states rather than a bool, because "not here" is two different facts to whoever is looking
/// at a shop: a photograph still on a rep's phone is ordinary and needs no one's attention, and one
/// that stopped coming a fortnight ago is a gap in the evidence somebody may have to act on. A bool
/// makes those indistinguishable, which is the thing this slice exists to fix.
/// </remarks>
public enum PhotoEvidenceState
{
    /// <summary>Uploading, or waiting for signal. The normal state of a fresh audit's photographs.</summary>
    Expected,

    /// <summary>Confirmed in storage.</summary>
    Arrived,

    /// <summary>Old enough that it is not coming — see <see cref="PhotoLine.ExpectedWithin"/>.</summary>
    Missing,
}

/// <summary>One photo reference, as stored (<c>AUD-05</c>).</summary>
/// <param name="ObjectKey">
/// Where the image is in object storage — or will be. The upload is separate from this record and
/// may not have finished, or happened at all (<c>B5</c>); a reader should show a gap rather than an
/// error.
/// </param>
/// <param name="UploadedAtUtc">
/// When the device confirmed the object was in storage, or null while it is still expected — W11
/// slice 13a.
/// </param>
/// <param name="State">
/// <paramref name="UploadedAtUtc"/> read against the audit's age. Derived here rather than stored, so
/// there is no flag to set with a job and no second rule to un-set it when a rep finds signal on
/// Monday for Friday's photograph.
/// </param>
public sealed record PhotoLine(
    AuditSection Section,
    string ObjectKey,
    DateTimeOffset? UploadedAtUtc,
    PhotoEvidenceState State)
{
    /// <summary>
    /// How long a photograph may be merely late before it reads as missing.
    /// </summary>
    /// <remarks>
    /// A working week. A rep can be out of signal for a long weekend and a device that has given up
    /// after eight attempts still gets every reconnect after that; past a week, an upload that was
    /// going to happen has had many chances. Erring long is the cheaper mistake — calling a
    /// photograph lost while it is still on a phone sends somebody chasing a rep for nothing.
    /// </remarks>
    public static readonly TimeSpan ExpectedWithin = TimeSpan.FromDays(7);

    /// <summary>The state of a reference, given when the audit was captured and what time it is now.</summary>
    public static PhotoEvidenceState StateOf(
        DateTimeOffset? uploadedAtUtc, DateTimeOffset capturedAtUtc, DateTimeOffset now) =>
        uploadedAtUtc is not null ? PhotoEvidenceState.Arrived
        : now - capturedAtUtc > ExpectedWithin ? PhotoEvidenceState.Missing
        : PhotoEvidenceState.Expected;
}

/// <summary>One pillar's contribution to the score, as stored (<c>AUD-06</c>, <c>AUD-09</c>).</summary>
/// <param name="Pillar">
/// <c>Availability</c>, <c>ShareOfShelf</c> or <c>PriceCompliance</c> — the names of Configuration's
/// <c>ScorePillar</c>, as a <b>string</b> rather than the enum itself.
/// <para>
/// The call <c>CapturedStep.Type</c> already made, for the same reason: it keeps this assembly from
/// referencing another module's contracts to describe its own record. A consumer of
/// <see cref="IAuditQuery"/> would otherwise inherit a dependency on Configuration to read a
/// breakdown, and the cost of the string is one comparison at the two places that care.
/// </para>
/// </param>
/// <param name="Percentage">
/// <c>0</c>–<c>100</c>, or null when the pillar was <b>skipped</b> — renormalised away rather than
/// scored zero (W10 slice 0). The distinction is the whole scoring rule, so it survives to the
/// reader rather than being flattened here.
/// </param>
public sealed record ScoredPillarLine(string Pillar, decimal? Percentage, decimal Weight);

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
    IReadOnlyList<PriceLine> Prices,
    Guid? SurveyFormId,
    IReadOnlyList<AnswerLine> Answers,
    IReadOnlyList<PhotoLine> Photos,
    decimal? Score,
    IReadOnlyList<ScoredPillarLine> ScoredPillars);

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
