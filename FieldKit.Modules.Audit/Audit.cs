using FieldKit.BuildingBlocks;
using FieldKit.Modules.Audit.Contracts;
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

/// <summary>Why an audit was refused. <see cref="None"/> means it was not.</summary>
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

    public IReadOnlyList<AvailabilityEntry> Availability => _availability;
    public IReadOnlyList<FacingsEntry> Facings => _facings;
    public IReadOnlyList<PriceEntry> Prices => _prices;

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
    /// </remarks>
    public static (Audit? Audit, AuditRefusal Refusal) Record(
        CapturedAudit captured, Guid outletId, string userId)
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
        };

        audit._availability.AddRange(captured.Availability.Select(
            entry => AvailabilityEntry.Create(audit.Id, entry.ProductId, entry.Status)));

        audit._facings.AddRange(captured.Facings.Select(
            entry => FacingsEntry.Create(audit.Id, entry.ProductId, entry.Facings)));

        audit._prices.AddRange(captured.Prices.Select(entry => PriceEntry.Create(
            audit.Id, entry.ProductId, entry.ObservedMinorUnits, entry.ExpectedMinorUnits,
            entry.Currency)));

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
        if (captured.Availability.Count == 0
            && captured.Facings.Count == 0
            && captured.Prices.Count == 0)
        {
            // An audit step the rep opened and closed without measuring anything is a step they did
            // not do. Storing it would put a scoreless audit into every trend line.
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

        return AuditRefusal.None;
    }

    private static bool HasDuplicate(IEnumerable<Guid> productIds)
    {
        var all = productIds.ToList();

        return all.Distinct().Count() != all.Count;
    }

    /// <summary>This audit as a reader sees it.</summary>
    public AuditRecord Describe() => new(
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
            entry.ProductId, entry.ObservedMinorUnits, entry.ExpectedMinorUnits, entry.Currency))]);
}
