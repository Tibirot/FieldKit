using System.Text.Json.Serialization;
using FieldKit.BuildingBlocks;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration.Contracts;
using FieldKit.SharedKernel;

namespace FieldKit.Modules.Audit;

/// <summary>One MSL product, as the rep found it (<c>AUD-01</c>, <c>BR-AUD-1</c>).</summary>
public sealed class AvailabilityEntry : ITenantOwned
{
    public Guid Id { get; private set; }

    public Guid AuditId { get; private set; }

    /// <summary>Products' id, carried bare — the catalogue is another module's schema (AT-1).</summary>
    public Guid ProductId { get; private set; }

    public AvailabilityStatus Status { get; private set; }

    public TenantId TenantId { get; set; }

    private AvailabilityEntry() { } // EF

    internal static AvailabilityEntry Create(Guid auditId, Guid productId, AvailabilityStatus status) =>
        new() { Id = Guid.CreateVersion7(), AuditId = auditId, ProductId = productId, Status = status };
}

/// <summary>Facings counted for one product — the share-of-shelf numerator (<c>AUD-02</c>).</summary>
public sealed class FacingsEntry : ITenantOwned
{
    public Guid Id { get; private set; }

    public Guid AuditId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>How many faces of this product were on the shelf. Zero is a real count.</summary>
    public int Facings { get; private set; }

    public TenantId TenantId { get; set; }

    private FacingsEntry() { } // EF

    internal static FacingsEntry Create(Guid auditId, Guid productId, int facings) =>
        new() { Id = Guid.CreateVersion7(), AuditId = auditId, ProductId = productId, Facings = facings };
}

/// <summary>
/// A shelf price the rep read, against the one they were told to expect (<c>AUD-03</c>,
/// <c>BR-AUD-3</c>).
/// </summary>
public sealed class PriceEntry : ITenantOwned
{
    /// <summary>The column width for a currency code.</summary>
    public const int CurrencyLength = 3;

    public Guid Id { get; private set; }

    public Guid AuditId { get; private set; }

    public Guid ProductId { get; private set; }

    /// <summary>
    /// What was on the shelf edge, in minor units.
    /// </summary>
    /// <remarks>
    /// Integers, the discipline pricing already uses (<c>BR-PRD-8</c>): a compliance delta computed
    /// from <c>double</c> is exactly where the phone's answer and the server's start to differ, and
    /// <c>BR-AUD-5</c> has them agree.
    /// </remarks>
    public long ObservedMinorUnits { get; private set; }

    /// <summary>
    /// What the device resolved as expected, as it resolved it. Null when it could resolve none.
    /// </summary>
    /// <remarks>
    /// <b>Stored, not re-resolved.</b> Asking Pricing what this outlet's price is today would
    /// re-judge a completed audit against a list republished since — marking a rep non-compliant
    /// against a number nobody ever showed them. The same call the geofence assessment makes.
    /// </remarks>
    public long? ExpectedMinorUnits { get; private set; }

    public string Currency { get; private set; } = null!;

    /// <summary>
    /// How far the shelf was from the expectation, or null when there was no expectation.
    /// </summary>
    /// <remarks>
    /// Derived, not stored — it is exactly observed minus expected, and a stored copy is a second
    /// answer that can disagree with the first. Positive means the shop is charging over.
    /// <b>Nothing here decides whether that is a compliance failure</b>: <c>BR-AUD-3</c>'s tolerance
    /// is tenant configuration whose default is an open question in the spec, and the score reads
    /// this in W10 slice 4.
    /// </remarks>
    public long? DeltaMinorUnits =>
        ExpectedMinorUnits is { } expected ? ObservedMinorUnits - expected : null;

    public TenantId TenantId { get; set; }

    private PriceEntry() { } // EF

    internal static PriceEntry Create(
        Guid auditId, Guid productId, long observed, long? expected, string currency) => new()
    {
        Id = Guid.CreateVersion7(),
        AuditId = auditId,
        ProductId = productId,
        ObservedMinorUnits = observed,
        ExpectedMinorUnits = expected,
        Currency = currency.Trim().ToUpperInvariant(),
    };
}

/// <summary>One survey answer, with the question as it was asked (<c>AUD-04</c>).</summary>
public sealed class SurveyAnswerEntry : ITenantOwned
{
    /// <summary>The column width for a question key — <c>SurveyQuestion.MaximumKeyLength</c>.</summary>
    /// <remarks>
    /// The number rather than a reference to Configuration's constant: this module may not see
    /// Configuration's implementation (AT-1), and the contracts assembly does not export column
    /// widths. A widening there is a migration here, which is the honest cost of the boundary.
    /// </remarks>
    public const int MaximumKeyLength = 60;

    /// <summary>The column width for the question text, as <c>SurveyQuestion</c> stores it.</summary>
    public const int MaximumTextLength = 300;

    /// <summary>
    /// The column width for an answer.
    /// </summary>
    /// <remarks>
    /// Generous, because a multi-choice answer is its options joined and a text question is whatever
    /// the rep typed standing in a shop. Bounded all the same: an unbounded column is a column
    /// somebody eventually pastes a document into.
    /// </remarks>
    public const int MaximumValueLength = 2000;

    public Guid Id { get; private set; }

    public Guid AuditId { get; private set; }

    /// <summary>Where it sat in the form. Contiguous from 1, assigned rather than accepted.</summary>
    public int Order { get; private set; }

    /// <summary>What the answer is filed under — see <c>SurveyQuestion.Key</c> for why not an id.</summary>
    public string QuestionKey { get; private set; } = null!;

    /// <summary>
    /// The question as it was asked, copied rather than referenced.
    /// </summary>
    /// <remarks>
    /// A form can be re-worded or a question dropped between the rep answering and the push arriving,
    /// and a key alone would then be an answer nobody can read. The same copy a visit makes of its
    /// workflow step (<c>BR-VIS-6</c>).
    /// </remarks>
    public string QuestionText { get; private set; } = null!;

    /// <summary>What the rep answered, as text whatever the question's type was.</summary>
    public string Value { get; private set; } = null!;

    public TenantId TenantId { get; set; }

    private SurveyAnswerEntry() { } // EF

    internal static SurveyAnswerEntry Create(
        Guid auditId, int order, string key, string text, string value) => new()
    {
        Id = Guid.CreateVersion7(),
        AuditId = auditId,
        Order = order,
        QuestionKey = key.Trim(),
        QuestionText = text.Trim(),
        Value = value.Trim(),
    };
}

/// <summary>A photo the rep took, as a reference to an object that may not exist yet (<c>AUD-05</c>).</summary>
public sealed class PhotoEntry : ITenantOwned
{
    /// <summary>The column width for an object key.</summary>
    public const int MaximumObjectKeyLength = 512;

    public Guid Id { get; private set; }

    public Guid AuditId { get; private set; }

    public AuditSection Section { get; private set; }

    /// <summary>
    /// Where the image is in object storage — or will be.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is behind this yet.</b> The upload path is W11 (<c>OFF-08</c>), so every reference
    /// stored today points at an object that does not exist. That is worth saying out loud, because
    /// a dangling reference looks like a bug to whoever finds it first — and because it will keep
    /// looking like one afterwards, by design: the JSON push and the image upload are independent and
    /// the push regularly wins (<c>B5</c>).
    /// </remarks>
    public string ObjectKey { get; private set; } = null!;

    /// <summary>
    /// When the object was confirmed to be in storage, or null while it is still expected
    /// (<c>OFF-08</c>, <c>B5</c>) — W11 slice 13a.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null is the ordinary state, not an error.</b> The JSON push and the upload are independent
    /// transports and the push usually wins, so a freshly filed audit has references to objects that
    /// are still on a phone. What this makes possible is telling that apart from a photograph that is
    /// never coming — which nothing could do while the two looked identical.
    /// </para>
    /// <para>
    /// <b>Nothing here decides when "still expected" becomes "missing".</b> That is a reader's
    /// question and it is answered on read, against the audit's own age, rather than stored: a stored
    /// flag needs a job to set it and a second rule to un-set it when a late confirmation arrives, and
    /// a rep who finds signal on Monday for Friday's photograph is exactly the case that must work.
    /// </para>
    /// </remarks>
    public DateTimeOffset? UploadedAtUtc { get; private set; }

    public TenantId TenantId { get; set; }

    private PhotoEntry() { } // EF

    /// <summary>
    /// Records that the bytes arrived, the first time it is told.
    /// </summary>
    /// <returns>
    /// <c>true</c> if this call is what changed it. A repeat answers <c>false</c> and leaves the
    /// original timestamp: the device retries a confirmation whose answer it lost, and the time that
    /// matters is when the photograph landed rather than when the retry did.
    /// </returns>
    internal bool Confirm(DateTimeOffset now)
    {
        if (UploadedAtUtc is not null) return false;

        UploadedAtUtc = now;
        return true;
    }

    internal static PhotoEntry Create(Guid auditId, AuditSection section, string objectKey) => new()
    {
        Id = Guid.CreateVersion7(),
        AuditId = auditId,
        Section = section,
        ObjectKey = objectKey.Trim(),
    };
}

/// <summary>
/// One pillar's contribution to a stored score (<c>AUD-06</c>, <c>AUD-09</c>).
/// </summary>
/// <remarks>
/// The breakdown is stored beside the total rather than recomputed for display, and for the reason
/// the total is: it is the working the server did at ingest, against weights that cannot move. A
/// screen deriving it later would be a second arithmetic that could disagree with the first.
/// </remarks>
public sealed class ScoredPillar : ITenantOwned
{
    public Guid Id { get; private set; }

    public Guid AuditId { get; private set; }

    public ScorePillar Pillar { get; private set; }

    /// <summary>
    /// <c>0</c>–<c>100</c>, or null when the pillar was <b>skipped</b>.
    /// </summary>
    /// <remarks>
    /// Null is not zero. A skipped pillar was renormalised away (W10 slice 0); a zero one was
    /// measured and found empty. Storing them the same way would lose the distinction the whole
    /// scoring rule turns on.
    /// </remarks>
    public decimal? Percentage { get; private set; }

    /// <summary>What the weight set said this pillar was worth, whether or not it was measured.</summary>
    public decimal Weight { get; private set; }

    public TenantId TenantId { get; set; }

    private ScoredPillar() { } // EF

    internal static ScoredPillar Create(Guid auditId, ScorePillar pillar, decimal? percentage, decimal weight) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            AuditId = auditId,
            Pillar = pillar,
            Percentage = percentage,
            Weight = weight,
        };
}

/// <summary>Why an audit was refused. <see cref="None"/> means it was not.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AuditRefusal>))]
public enum AuditRefusal
{
    None,

    /// <summary>Nothing was measured — no availability, no facings, no prices.</summary>
    Empty,

    /// <summary>A facings or category-facings count below zero.</summary>
    NegativeCount,

    /// <summary>The same product measured twice in one section.</summary>
    DuplicateProduct,

    /// <summary>Prices in more than one currency, or a code that is not three letters.</summary>
    CurrencyMismatch,

    /// <summary>Two answers under one question key, or answers with no form named (<c>AUD-04</c>).</summary>
    MalformedAnswers,

    /// <summary>A photo with no object key, or two references to one object (<c>AUD-05</c>).</summary>
    MalformedPhotos,
}

/// <summary>
/// One store audit: what a rep measured at a shelf during a visit (<c>AUD-01</c>, <c>AUD-02</c>,
/// <c>AUD-03</c>, <c>BR-AUD-6</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Append-only, and created sealed.</b> <c>BR-AUD-6</c> — an audit belongs to a visit and is
/// sealed with it. There is no edit path here at all, not even a private one: the audit is worked at
/// a shelf and arrives complete, and a module with no way to change a stored audit is a module that
/// cannot be argued into having one. That is also what makes it safe to push through Sync without a
/// conflict story (<c>B7</c>).
/// </para>
/// <para>
/// <b>One audit per visit.</b> A second would leave "this shop's availability last Tuesday" with two
/// answers and no rule for choosing, and the capture screen offers one audit step. The uniqueness is
/// in the schema as well as here, because it is the invariant every reader depends on.
/// </para>
/// <para>
/// <b>It stores measurements, and computes nothing.</b> No score, no compliance flag, no
/// share-of-shelf percentage — those are W10 slice 4's, and they are derived from these numbers plus
/// the weight set named by <see cref="WeightSetVersion"/>. Storing a computed score here would be a
/// second answer that could disagree with the recomputation <c>BR-AUD-8</c> promises.
/// </para>
/// <para>
/// <b>Nothing in this module resolves the MSL or the price list.</b> Both were resolved on the
/// device, from data it had pulled, at the moment the rep was looking at the shelf. Re-resolving
/// either here would describe the audit under configuration that may have been republished since —
/// inventing checks the rep was never asked to make.
/// </para>
/// </remarks>
public sealed class Audit : AggregateRoot, ITenantOwned, IAuditable
{
    private readonly List<AvailabilityEntry> _availability = [];
    private readonly List<FacingsEntry> _facings = [];
    private readonly List<PriceEntry> _prices = [];
    private readonly List<SurveyAnswerEntry> _answers = [];
    private readonly List<PhotoEntry> _photos = [];
    private readonly List<ScoredPillar> _scoredPillars = [];

    /// <summary>Minted on the device, so a replayed push maps to this audit rather than a second one.</summary>
    public Guid Id { get; private set; }

    /// <summary>The visit this belongs to (<c>BR-AUD-6</c>). A bare id — Visit's schema (AT-1).</summary>
    public Guid VisitId { get; private set; }

    /// <summary>Copied from the visit so a reader does not need Visit to answer "which shop".</summary>
    public Guid OutletId { get; private set; }

    /// <summary>The rep — the Keycloak subject, as the visit has it.</summary>
    public string UserId { get; private set; } = null!;

    /// <summary>
    /// When the rep took the measurements — the device's clock, not this server's.
    /// </summary>
    /// <remarks>
    /// An audit worked yesterday and pushed today is a record of yesterday. <c>CreatedAtUtc</c> is
    /// still stamped by the interceptor and is when this server first stored it, so the gap between
    /// the two is how long the work sat on a phone — the same pairing <c>Visit</c> settled on.
    /// </remarks>
    public DateTimeOffset CapturedAtUtc { get; private set; }

    /// <summary>
    /// The weighting version this audit was scored against (<c>BR-AUD-8</c>).
    /// </summary>
    /// <remarks>
    /// Recorded at capture because it is the one fact that cannot be recovered afterwards: a
    /// re-weighting between the audit and its push would leave the server unable to say which numbers
    /// the rep was shown. This is the column W10 slice 0 exists to have created before the first row.
    /// </remarks>
    public int WeightSetVersion { get; private set; }

    /// <summary>
    /// Total category facings — the share-of-shelf denominator (<c>BR-AUD-2</c>).
    /// </summary>
    /// <remarks>
    /// <b>Nullable, and null is a real answer.</b> Without a captured total the pillar is skipped and
    /// the score renormalises over the pillars that were measured (W10 slice 0) — it is not scored
    /// zero, which would treat "unknown" as "bad" and is precisely the faking <c>BR-AUD-2</c>
    /// refuses. Defaulting it to 0 would also make the ratio a division by zero dressed as a
    /// measurement.
    /// </remarks>
    public int? CategoryFacings { get; private set; }

    /// <summary>
    /// Which questionnaire the rep worked, or null when the audit had no survey step (<c>AUD-04</c>).
    /// </summary>
    /// <remarks>
    /// A bare id — the form lives in Configuration's schema (AT-1) — and the one thing this module
    /// <i>does</i> ask Configuration about on the way in, because an answer set naming no form is
    /// uninterpretable. What it does not ask is whether the answers satisfy the form: see
    /// <c>IAuditIngest</c> on why <c>BR-AUD-7</c> is a device rule.
    /// </remarks>
    public Guid? SurveyFormId { get; private set; }

    public IReadOnlyList<AvailabilityEntry> Availability => _availability;
    public IReadOnlyList<FacingsEntry> Facings => _facings;
    public IReadOnlyList<PriceEntry> Prices => _prices;
    public IReadOnlyList<SurveyAnswerEntry> Answers => _answers;
    public IReadOnlyList<PhotoEntry> Photos => _photos;

    /// <summary>
    /// The perfect-store score, as this server computed it (<c>AUD-06</c>, <c>BR-AUD-8</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Stored, and W10 slice 4's comment said it would not be.</b> That comment objected to
    /// storing the <i>device's</i> score, which would have been a second answer competing with the
    /// server's recomputation. What is stored is the recomputation itself: this server's own
    /// arithmetic over sealed entries and a frozen weight set, both of which are on the row beside
    /// it. Anyone can reproduce it; nothing can move underneath it.
    /// </para>
    /// <para>
    /// <b>Computed once, at ingest, rather than on every read.</b> <c>BR-AUD-6</c> makes an audit a
    /// sealed record, and a score derived on read would silently change the day the scorer is
    /// corrected — re-scoring history without anyone deciding to. Storing it makes a re-score a
    /// deliberate act with a migration behind it, which is what re-scoring a sealed record should be.
    /// </para>
    /// <para>
    /// <b>Null when the audit could not be scored</b> — nothing measured, or every measured pillar
    /// weighted zero. A zero would be a claim about a shop nobody looked at.
    /// </para>
    /// </remarks>
    public decimal? Score { get; private set; }

    /// <summary>The pillar breakdown behind <see cref="Score"/>, including the skipped ones.</summary>
    public IReadOnlyList<ScoredPillar> ScoredPillars => _scoredPillars;

    public TenantId TenantId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public string? ModifiedBy { get; set; }

    private Audit() { } // EF

    /// <summary>
    /// Records an audit that already happened (<c>OFF-04</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Takes the visit's outlet and rep rather than reading them, so this cannot quietly disagree
    /// with what <c>IVisitContext</c> told the caller — and so the aggregate stays testable without a
    /// database. Whether the visit exists and is open is <see cref="AuditIngestService"/>'s to
    /// establish; what is enforced here is what a stored audit must be true of regardless.
    /// </para>
    /// <para>
    /// There is no counterpart that creates an audit "in progress". A rep working a shelf holds the
    /// audit on their phone until it is done; a half-finished audit on this server would be a row
    /// every reader has to learn to ignore.
    /// </para>
    /// <para>
    /// <b>Scoring happens here, in the same step as storing</b> (W10 slice 6). Not because it is
    /// convenient, but because it is the only moment the weights are unambiguous: the version the
    /// audit names is resolved by the caller, and from here on the score, the entries and the version
    /// are one row that either exists or does not. A two-step "store, then score" would admit a row
    /// with entries and no score, which every reader would then have to handle.
    /// </para>
    /// </remarks>
    /// <param name="weights">
    /// The <b>published</b> weighting the audit was scored against (<c>BR-AUD-8</c>), resolved from
    /// <c>CapturedAudit.WeightSetVersion</c> by the caller. Taken as a parameter rather than looked
    /// up here for the reason the outlet is: the aggregate stays testable without a database, and it
    /// cannot quietly disagree with what the caller resolved.
    /// </param>
    public static (Audit? Audit, AuditRefusal Refusal) Record(
        CapturedAudit captured, Guid outletId, string userId, IReadOnlyList<PillarWeight> weights)
    {
        if (Check(captured) is var refusal && refusal is not AuditRefusal.None)
        {
            return (null, refusal);
        }

        var audit = new Audit
        {
            Id = captured.AuditId,
            VisitId = captured.VisitId,
            OutletId = outletId,
            UserId = userId,
            CapturedAtUtc = captured.CapturedAtUtc,
            WeightSetVersion = captured.WeightSetVersion,
            CategoryFacings = captured.CategoryFacings,
            SurveyFormId = captured.SurveyFormId,
        };

        audit._availability.AddRange(captured.Availability.Select(
            entry => AvailabilityEntry.Create(audit.Id, entry.ProductId, entry.Status)));

        audit._facings.AddRange(captured.Facings.Select(
            entry => FacingsEntry.Create(audit.Id, entry.ProductId, entry.Facings)));

        audit._prices.AddRange(captured.Prices.Select(entry => PriceEntry.Create(
            audit.Id, entry.ProductId, entry.ObservedMinorUnits, entry.ExpectedMinorUnits,
            entry.Currency)));

        // Numbered from 1 in the order they arrived, which is the order the rep was asked them — the
        // same call `SurveyForm` makes about its questions, for the same reason: a caller sending its
        // own numbers could send a gap or a tie, and every reader would have to decide what that
        // means.
        var order = 1;

        foreach (var answer in AnswersOf(captured))
        {
            audit._answers.Add(SurveyAnswerEntry.Create(
                audit.Id, order++, answer.QuestionKey, answer.QuestionText, answer.Value));
        }

        audit._photos.AddRange(PhotosOf(captured).Select(
            photo => PhotoEntry.Create(audit.Id, photo.Section, photo.ObjectKey)));

        /*
         * The score, from the entries just stored and the weighting just resolved (AUD-06,
         * BR-AUD-8).
         *
         * `PerfectStoreScore` is handed the descriptors rather than the entities, so the scorer sees
         * exactly what `IAuditQuery` will hand a reader — one shape, one arithmetic, and no way for
         * "what was scored" to drift from "what is shown".
         */
        // The audit's own capture time, because the score does not read photo evidence — every
        // reference here was created a line ago and is `Expected` under any clock. Passing a real
        // "now" would suggest this path cares about the difference, and it does not.
        var described = audit.Describe(audit.CapturedAtUtc);

        var scored = PerfectStoreScore.Compute(new ScoreInputs(
            described.Availability,
            described.Facings,
            described.CategoryFacings,
            described.Prices,
            weights));

        audit.Score = scored.Score;

        audit._scoredPillars.AddRange(scored.Pillars.Select(
            pillar => ScoredPillar.Create(audit.Id, pillar.Pillar, pillar.Percentage, pillar.Weight)));

        return (audit, AuditRefusal.None);
    }

    /// <summary>
    /// Whether these measurements are ones this module will store.
    /// </summary>
    /// <remarks>
    /// Deliberately short. Almost everything about an audit is a fact the rep observed, and a server
    /// second-guessing observations is how a rep learns to enter whatever gets accepted. What is
    /// refused here is only what could not have been observed: a negative count, one product measured
    /// twice, prices in two currencies, and an audit that measured nothing at all.
    /// </remarks>
    private static AuditRefusal Check(CapturedAudit captured)
    {
        /*
         * An audit step the rep opened and closed without recording anything is a step they did not
         * do. Storing it would put a scoreless audit into every trend line.
         *
         * Answers and photos count. An audit that is only a questionnaire, or only a photograph of a
         * display, is real work — `AUD-05` calls photo evidence a section of its own, and refusing an
         * audit for having no *numbers* would throw away the one thing the rep could record in a shop
         * that would not let them count the shelf.
         */
        if (captured.Availability.Count == 0
            && captured.Facings.Count == 0
            && captured.Prices.Count == 0
            && AnswersOf(captured).Count == 0
            && PhotosOf(captured).Count == 0)
        {
            return AuditRefusal.Empty;
        }

        if (captured.CategoryFacings is < 0 || captured.Facings.Any(entry => entry.Facings < 0))
        {
            return AuditRefusal.NegativeCount;
        }

        // Per section rather than across the audit: the same product legitimately appears in
        // availability, in facings and in a price check — those are three different measurements of
        // it. Twice in one section is a shelf counted twice.
        if (HasDuplicate(captured.Availability.Select(entry => entry.ProductId))
            || HasDuplicate(captured.Facings.Select(entry => entry.ProductId))
            || HasDuplicate(captured.Prices.Select(entry => entry.ProductId)))
        {
            return AuditRefusal.DuplicateProduct;
        }

        /*
         * One currency for the whole audit, and it must look like a currency.
         *
         * A shelf is priced in one currency; two in one audit means the device resolved expected
         * prices from two different lists, which is a bug on the phone rather than a shop with two
         * tills. Left alone it would produce a compliance delta between amounts that are not
         * comparable — arithmetic that succeeds and means nothing.
         */
        var currencies = captured.Prices
            .Select(entry => entry.Currency?.Trim().ToUpperInvariant() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (currencies.Any(code => code.Length != PriceEntry.CurrencyLength) || currencies.Count > 1)
        {
            return AuditRefusal.CurrencyMismatch;
        }

        return AnswerProblem(captured) is var answers && answers is not AuditRefusal.None
            ? answers
            : PhotoProblem(captured);
    }

    /// <summary>
    /// What is refused about a set of answers — which is deliberately not much (<c>AUD-04</c>).
    /// </summary>
    /// <remarks>
    /// <b>Nothing here checks the answers against the form.</b> Not that every mandatory question was
    /// answered (<c>BR-AUD-7</c> is a device rule — see <c>IAuditIngest</c>), and not that each key
    /// belongs to the questionnaire. Both would test the rep's work against the form as it reads
    /// <i>today</i>, and a form re-worded after they answered would then refuse an audit for a
    /// question that no longer exists. The answers carry their own question text precisely so that
    /// they never need the form to be readable.
    /// </remarks>
    private static AuditRefusal AnswerProblem(CapturedAudit captured)
    {
        var answers = AnswersOf(captured);

        if (answers.Count == 0) return AuditRefusal.None;

        // Answers with no form named. Not fatal to the *answers* — they carry their own text — but a
        // reader has no way to tell what was being asked overall, and `AUD-09` would have a set of
        // responses belonging to no questionnaire. A device that answered a form knows which one.
        if (captured.SurveyFormId is null) return AuditRefusal.MalformedAnswers;

        if (answers.Any(answer =>
                string.IsNullOrWhiteSpace(answer.QuestionKey)
                || string.IsNullOrWhiteSpace(answer.QuestionText)))
        {
            return AuditRefusal.MalformedAnswers;
        }

        // One answer per question. Two under one key is two answers filed under one name, which is
        // the failure the key exists to prevent — and the reason `SurveyForm` refuses duplicate keys
        // at the other end.
        var keys = answers.Select(answer => answer.QuestionKey.Trim()).ToList();

        return keys.Distinct(StringComparer.Ordinal).Count() == keys.Count
            ? AuditRefusal.None
            : AuditRefusal.MalformedAnswers;
    }

    /// <summary>What is refused about a set of photo references (<c>AUD-05</c>).</summary>
    /// <remarks>
    /// Only what makes the reference useless: no key to fetch by, or two references to one object.
    /// Whether the object <i>exists</i> is not checked and cannot be — the upload is separate from
    /// this push and usually later (<c>B5</c>).
    /// </remarks>
    private static AuditRefusal PhotoProblem(CapturedAudit captured)
    {
        var photos = PhotosOf(captured);

        if (photos.Any(photo => string.IsNullOrWhiteSpace(photo.ObjectKey)))
        {
            return AuditRefusal.MalformedPhotos;
        }

        var keys = photos.Select(photo => photo.ObjectKey.Trim()).ToList();

        // Two references to one object is one photo counted twice — the same image would appear
        // under two sections with no way to say which the rep meant.
        return keys.Distinct(StringComparer.Ordinal).Count() == keys.Count
            ? AuditRefusal.None
            : AuditRefusal.MalformedPhotos;
    }

    /// <summary>The answers, treating null as none — the wire omits the property when there are none.</summary>
    private static IReadOnlyList<CapturedAnswer> AnswersOf(CapturedAudit captured) =>
        captured.Answers ?? [];

    private static IReadOnlyList<CapturedPhoto> PhotosOf(CapturedAudit captured) =>
        captured.Photos ?? [];

    private static bool HasDuplicate(IEnumerable<Guid> productIds)
    {
        var all = productIds.ToList();

        return all.Distinct().Count() != all.Count;
    }

    /// <summary>
    /// This audit as a reader sees it, at <paramref name="now"/>.
    /// </summary>
    /// <param name="now">
    /// Only photo evidence depends on it: whether a reference that has not been confirmed is still
    /// expected or has stopped coming is a question about elapsed time, and answering it on read is
    /// what keeps a late confirmation from needing a second rule to undo a stored flag (W11 13a).
    /// </param>
    public AuditRecord Describe(DateTimeOffset now) => new(
        Id,
        VisitId,
        OutletId,
        UserId,
        CapturedAtUtc,
        WeightSetVersion,
        CategoryFacings,
        [.. _availability.Select(entry => new AvailabilityLine(entry.ProductId, entry.Status))],
        [.. _facings.Select(entry => new FacingsLine(entry.ProductId, entry.Facings))],
        [.. _prices.Select(entry => new PriceLine(
            entry.ProductId, entry.ObservedMinorUnits, entry.ExpectedMinorUnits, entry.Currency))],
        SurveyFormId,
        [.. _answers
            .OrderBy(entry => entry.Order)
            .Select(entry => new AnswerLine(
                entry.Order, entry.QuestionKey, entry.QuestionText, entry.Value))],
        [.. _photos.Select(entry => new PhotoLine(
            entry.Section,
            entry.ObjectKey,
            entry.UploadedAtUtc,
            PhotoLine.StateOf(entry.UploadedAtUtc, CapturedAtUtc, now)))],
        Score,
        [.. _scoredPillars
            .OrderBy(entry => entry.Pillar)
            .Select(entry => new ScoredPillarLine(
                entry.Pillar.ToString(), entry.Percentage, entry.Weight))]);
}
