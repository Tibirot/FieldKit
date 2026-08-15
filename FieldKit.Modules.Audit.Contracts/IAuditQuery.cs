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
/// How one pillar did across a population of audits (<c>AUD-06</c>, <c>AUD-09</c>) — W12 slice 2b.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Skipped"/> is reported, not smoothed away, and it is the number that stops this
/// record lying.</b> A skipped pillar is renormalised out of its audit's score rather than counted as
/// zero (<c>BR-AUD-2</c>, W10 slice 0), so the average below is over the audits that <i>measured</i>
/// it. Without the count beside it, "share of shelf: 96%" from two audits out of forty reads as a
/// triumph instead of as a pillar nobody could count.
/// </para>
/// <para>
/// <b>A mean of percentages, not a percentage of totals.</b> Each audit's pillar figure is already
/// normalised against what that shop was asked to stock, so re-deriving one from raw counts would
/// weight a hypermarket's MSL above a kiosk's. The question a supervisor asks — "how are my shops
/// doing on availability" — is about shops, and every shop counts once.
/// </para>
/// </remarks>
/// <param name="Pillar">
/// <c>Availability</c>, <c>ShareOfShelf</c> or <c>PriceCompliance</c> — a string for the reason
/// <see cref="ScoredPillarLine.Pillar"/> gives.
/// </param>
/// <param name="Average"><c>0</c>–<c>100</c> over the audits that measured it, or null if none did.</param>
/// <param name="Measured">How many audits scored this pillar.</param>
/// <param name="Skipped">How many renormalised it away — <c>BR-AUD-2</c>, and never a zero.</param>
public sealed record PillarAverage(string Pillar, decimal? Average, int Measured, int Skipped);

/// <summary>
/// Perfect-store performance across a set of shops and a window (<c>AUD-09</c>) — W12 slice 2b.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="WeightSetVersions"/> is here because an average of scores from different weight sets
/// is an average of two different rulers.</b> <c>BR-AUD-8</c> stores the version each audit was
/// scored against precisely because a re-weighting cannot be undone afterwards; the honest thing for
/// an aggregate is therefore to say which versions it mixed rather than to hide the mixing behind one
/// number. <see cref="Comparable"/> is the question a caller actually has.
/// </para>
/// <para>
/// It does <b>not</b> refuse to average across versions, and that is a decision rather than an
/// omission. A supervisor whose weights changed mid-month still needs to see the month; what they
/// must not do is read a five-point movement across that boundary as a change in their shops. The
/// contract's job is to make the boundary visible, not to withhold the number.
/// </para>
/// <para>
/// <b><see cref="AverageScore"/> is null rather than zero when nothing was scored</b>, for the reason
/// <c>Audit.Score</c> is: a zero is a claim about a shop, and "nobody has audited these yet" is not
/// one. The same distinction <c>VisitOutcomeCounts.StrikeRate</c> makes.
/// </para>
/// </remarks>
/// <param name="Audits">Audits captured at these shops in the window, scored or not.</param>
/// <param name="Scored">
/// How many carried a score. An audit with nothing measured — or with every measured pillar weighted
/// zero — has none, and averaging it in as zero would be the claim above.
/// </param>
/// <param name="AverageScore">
/// The mean of the scores that exist, <c>0</c>–<c>100</c>, or null. <b>Rounded half-up to two
/// places</b> — <c>BR-PRD-9</c>'s policy, which every score being averaged already carries, and
/// which is also what makes the figure the same whether the mean was taken in Postgres or in memory.
/// </param>
/// <param name="Pillars">One entry per pillar that any audit in the window carried.</param>
/// <param name="WeightSetVersions">Every weight-set version present, ascending.</param>
public sealed record PerfectStoreSummary(
    int Audits,
    int Scored,
    decimal? AverageScore,
    IReadOnlyList<PillarAverage> Pillars,
    IReadOnlyList<int> WeightSetVersions)
{
    /// <summary>
    /// Whether <see cref="AverageScore"/> compares like with like — one weight set, or none.
    /// </summary>
    /// <remarks>
    /// True for an empty window as well as for a single-version one. An average that does not exist
    /// cannot be misleading, and a caller that has to special-case "no data" before asking this
    /// would have two branches where one will do.
    /// </remarks>
    public bool Comparable => WeightSetVersions.Count <= 1;
}

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

    /// <summary>
    /// Perfect store across these shops over a closed date range — the KPI, not the audits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the "own question" <see cref="ForOutletAsync"/>'s note promised.</b> That read is
    /// bounded at a hundred audits because it answers "how has this shop been trending"; a month of a
    /// supervisor's territory is a different question, and reducing a list of records to a mean in
    /// the caller would both ship every line over the wire and put the skipped-versus-zero rule
    /// (<c>BR-AUD-2</c>) in a module that does not own it.
    /// </para>
    /// <para>
    /// <b>Dated by capture, not by arrival.</b> An audit worked yesterday and pushed today is a
    /// record of yesterday — the rule <c>Audit.CapturedAtUtc</c> already states, and the same choice
    /// <c>IVisitQuery</c> makes in dating a visit by check-in. Both ends inclusive, in UTC.
    /// </para>
    /// <para>
    /// An empty <paramref name="outletIds"/> answers an empty summary rather than the tenant's, for
    /// the reason its two siblings give.
    /// </para>
    /// </remarks>
    Task<PerfectStoreSummary> SummariseAsync(
        IReadOnlyCollection<Guid> outletIds,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);
}
