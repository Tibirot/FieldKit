using FieldKit.Modules.Audit;
using FieldKit.Modules.Audit.Contracts;

namespace FieldKit.Server.Tests;

/// <summary>
/// What a stored audit must be true of, as a rule rather than as an endpoint (<c>AUD-01</c>,
/// <c>AUD-02</c>, <c>AUD-03</c>) — W10 slice 3a.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate refuses only what could not have been observed: a negative count, one product
/// measured twice in one section, prices in two currencies, and an audit that measured nothing. That
/// list is short on purpose — almost everything else here is a fact a rep saw, and a server
/// second-guessing observations teaches reps to enter whatever gets accepted.
/// </para>
/// <para>
/// <see cref="AuditIngestTests"/> covers what the visit adds: <c>BR-AUD-6</c>'s sealing, the replay
/// window, and the one-audit-per-visit rule.
/// </para>
/// </remarks>
public class AuditRecordTests
{
    private static readonly Guid Visit = Guid.CreateVersion7();
    private static readonly Guid Outlet = Guid.CreateVersion7();
    private static readonly DateTimeOffset Captured = new(2026, 4, 6, 9, 30, 0, TimeSpan.Zero);

    private static CapturedAudit Audit(
        IReadOnlyList<CapturedAvailability>? availability = null,
        IReadOnlyList<CapturedFacings>? facings = null,
        IReadOnlyList<CapturedPrice>? prices = null,
        int? categoryFacings = 40,
        int weightSetVersion = 3) =>
        new(Guid.CreateVersion7(), Visit, Captured, weightSetVersion, categoryFacings,
            availability ?? [], facings ?? [], prices ?? []);

    private static (Modules.Audit.Audit? Audit, AuditRefusal Refusal) Record(CapturedAudit captured) =>
        Modules.Audit.Audit.Record(captured, Outlet, "rep-1");

    [Fact]
    public void An_audit_records_what_was_measured_and_where()
    {
        var product = Guid.CreateVersion7();

        var (audit, refusal) = Record(Audit(
            availability: [new CapturedAvailability(product, AvailabilityStatus.OutOfStock)],
            facings: [new CapturedFacings(product, 6)],
            prices: [new CapturedPrice(product, 1099, 999, "RON")]));

        Assert.Equal(AuditRefusal.None, refusal);
        Assert.NotNull(audit);

        // The outlet and the rep come from the visit rather than the payload — a device that could
        // name its own outlet could file an audit against a shop it never entered.
        Assert.Equal(Outlet, audit.OutletId);
        Assert.Equal("rep-1", audit.UserId);

        // The device's clock, not the server's. An audit worked yesterday is a record of yesterday.
        Assert.Equal(Captured, audit.CapturedAtUtc);

        // The one fact that cannot be recovered later (BR-AUD-8).
        Assert.Equal(3, audit.WeightSetVersion);
        Assert.Equal(40, audit.CategoryFacings);
    }

    [Fact]
    public void The_audit_id_is_the_devices_own()
    {
        // Minted on the phone, so a replayed push maps to this audit rather than a second one — the
        // same call `CapturedVisit.VisitId` makes, and what makes the replay identifiable if it ever
        // slipped past the ledger.
        var captured = Audit(availability: [new CapturedAvailability(Guid.CreateVersion7(), AvailabilityStatus.Present)]);

        var (audit, _) = Record(captured);

        Assert.Equal(captured.AuditId, audit!.Id);
    }

    [Fact]
    public void An_audit_that_measured_nothing_is_refused()
    {
        // An audit step opened and closed without measuring anything is a step the rep did not do.
        // Storing it would put a scoreless audit into every trend line.
        var (audit, refusal) = Record(Audit());

        Assert.Equal(AuditRefusal.Empty, refusal);
        Assert.Null(audit);
    }

    [Fact]
    public void Any_one_section_is_enough_for_an_audit_to_exist()
    {
        /*
         * The other half of the emptiness rule, and the one that matters more.
         *
         * A rep who counted facings but could not read a price tag has done real work, and an audit
         * refused for being partial would lose it entirely. `BR-AUD-2`'s skipped pillar is the same
         * instinct one level up: measure what you can, and let the score renormalise.
         */
        var (audit, refusal) = Record(Audit(facings: [new CapturedFacings(Guid.CreateVersion7(), 3)]));

        Assert.Equal(AuditRefusal.None, refusal);
        Assert.NotNull(audit);
    }

    [Fact]
    public void A_category_total_nobody_counted_is_a_real_answer()
    {
        /*
         * Null, not zero. Without a captured total the share-of-shelf pillar is *skipped* and the
         * score renormalises over the pillars that were measured (W10 slice 0) — scoring the gap
         * zero would treat "unknown" as "bad", which is precisely the faking BR-AUD-2 refuses.
         *
         * Zero would also make the ratio a division by zero dressed up as a measurement.
         */
        var (audit, refusal) = Record(Audit(
            facings: [new CapturedFacings(Guid.CreateVersion7(), 3)], categoryFacings: null));

        Assert.Equal(AuditRefusal.None, refusal);
        Assert.Null(audit!.CategoryFacings);
    }

    [Fact]
    public void Zero_facings_is_a_count_and_a_negative_one_is_not()
    {
        // Zero is the shelf being bare, which is exactly what an availability audit exists to find.
        var (present, none) = Record(Audit(facings: [new CapturedFacings(Guid.CreateVersion7(), 0)]));

        Assert.Equal(AuditRefusal.None, none);
        Assert.Equal(0, present!.Facings.Single().Facings);

        var (_, negative) = Record(Audit(facings: [new CapturedFacings(Guid.CreateVersion7(), -1)]));

        Assert.Equal(AuditRefusal.NegativeCount, negative);
    }

    [Fact]
    public void A_negative_category_total_is_refused_too()
    {
        var (_, refusal) = Record(Audit(
            facings: [new CapturedFacings(Guid.CreateVersion7(), 3)], categoryFacings: -1));

        Assert.Equal(AuditRefusal.NegativeCount, refusal);
    }

    [Fact]
    public void One_product_cannot_be_measured_twice_in_the_same_section()
    {
        var product = Guid.CreateVersion7();

        var (_, refusal) = Record(Audit(availability: [
            new CapturedAvailability(product, AvailabilityStatus.Present),
            new CapturedAvailability(product, AvailabilityStatus.Absent),
        ]));

        Assert.Equal(AuditRefusal.DuplicateProduct, refusal);
    }

    [Fact]
    public void The_same_product_in_three_sections_is_three_measurements_of_it()
    {
        // The rule is per section, and this is why. Availability, facings and price are three
        // different questions about one SKU, and refusing the overlap would make it impossible to
        // audit a product properly.
        var product = Guid.CreateVersion7();

        var (audit, refusal) = Record(Audit(
            availability: [new CapturedAvailability(product, AvailabilityStatus.Present)],
            facings: [new CapturedFacings(product, 4)],
            prices: [new CapturedPrice(product, 1099, 1099, "RON")]));

        Assert.Equal(AuditRefusal.None, refusal);
        Assert.NotNull(audit);
    }

    [Fact]
    public void Prices_are_all_in_one_currency()
    {
        /*
         * Two currencies in one audit means the device resolved expected prices from two different
         * lists — a bug on the phone, not a shop with two tills. Left alone it would produce a
         * compliance delta between amounts that are not comparable: arithmetic that succeeds and
         * means nothing.
         */
        var (_, refusal) = Record(Audit(prices: [
            new CapturedPrice(Guid.CreateVersion7(), 1099, 999, "RON"),
            new CapturedPrice(Guid.CreateVersion7(), 250, 250, "EUR"),
        ]));

        Assert.Equal(AuditRefusal.CurrencyMismatch, refusal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("RONX")]
    [InlineData("RO")]
    public void A_currency_that_is_not_a_currency_is_refused(string currency)
    {
        var (_, refusal) = Record(Audit(
            prices: [new CapturedPrice(Guid.CreateVersion7(), 1099, null, currency)]));

        Assert.Equal(AuditRefusal.CurrencyMismatch, refusal);
    }

    [Fact]
    public void A_currency_is_stored_upper_case_and_trimmed()
    {
        // So that "ron" and "RON" are not two currencies to the rule above, and so a reader never
        // has to case-fold to compare. The same normalisation the price list already applies.
        var (audit, refusal) = Record(Audit(
            prices: [new CapturedPrice(Guid.CreateVersion7(), 1099, null, " ron ")]));

        Assert.Equal(AuditRefusal.None, refusal);
        Assert.Equal("RON", audit!.Prices.Single().Currency);
    }

    [Fact]
    public void A_price_with_nothing_expected_is_stored_and_has_no_delta()
    {
        // An unpriced product is not a compliance failure. Scoring it as one would punish a rep for
        // a gap in somebody else's price list.
        var (audit, _) = Record(Audit(
            prices: [new CapturedPrice(Guid.CreateVersion7(), 1099, null, "RON")]));

        var price = audit!.Prices.Single();

        Assert.Null(price.ExpectedMinorUnits);
        Assert.Null(price.DeltaMinorUnits);
    }

    [Fact]
    public void The_delta_is_observed_minus_expected_and_signed()
    {
        // Positive means the shop is charging over. Derived rather than stored, so it cannot
        // disagree with the two numbers it comes from.
        var (over, _) = Record(Audit(prices: [new CapturedPrice(Guid.CreateVersion7(), 1099, 999, "RON")]));
        var (under, _) = Record(Audit(prices: [new CapturedPrice(Guid.CreateVersion7(), 899, 999, "RON")]));

        Assert.Equal(100, over!.Prices.Single().DeltaMinorUnits);
        Assert.Equal(-100, under!.Prices.Single().DeltaMinorUnits);
    }

    [Fact]
    public void There_is_no_way_to_change_a_stored_audit()
    {
        /*
         * `BR-AUD-6` as a property of the type rather than as a rule somebody remembers to check.
         *
         * An audit arrives complete and append-only; a module with no mutating method is a module
         * that cannot be argued into having one. Asserted by reflection because the thing being
         * checked is the *absence* of code, which no ordinary test can observe.
         */
        var mutators = typeof(Modules.Audit.Audit)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(method => !method.IsSpecialName)
            .Where(method => method.DeclaringType == typeof(Modules.Audit.Audit))
            .Where(method => method.Name is not nameof(Modules.Audit.Audit.Describe))
            .ToList();

        Assert.Empty(mutators);

        // And nothing about it is settable from outside — the collections included.
        var setters = typeof(Modules.Audit.Audit).GetProperties()
            .Where(property => property.SetMethod is { IsPublic: true })
            .Select(property => property.Name)

            // The four `IAuditable`/`ITenantOwned` fields the interceptor writes. They are the
            // framework's, not the audit's, and they are stamped on the way in.
            .Except([nameof(Modules.Audit.Audit.TenantId), nameof(Modules.Audit.Audit.CreatedAtUtc),
                nameof(Modules.Audit.Audit.CreatedBy), nameof(Modules.Audit.Audit.ModifiedAtUtc),
                nameof(Modules.Audit.Audit.ModifiedBy)])
            .ToList();

        Assert.Empty(setters);
    }

    [Fact]
    public void Describing_an_audit_carries_every_section()
    {
        // What a reader actually gets. Written out because the descriptor is the whole read surface
        // — a section silently dropped here would be a section missing from every report.
        var product = Guid.CreateVersion7();

        var (audit, _) = Record(Audit(
            availability: [new CapturedAvailability(product, AvailabilityStatus.Absent)],
            facings: [new CapturedFacings(product, 2)],
            prices: [new CapturedPrice(product, 1099, 999, "RON")]));

        var described = audit!.Describe();

        Assert.Equal(audit.Id, described.AuditId);
        Assert.Equal(Visit, described.VisitId);
        Assert.Equal(Outlet, described.OutletId);
        Assert.Equal(3, described.WeightSetVersion);
        Assert.Equal(AvailabilityStatus.Absent, described.Availability.Single().Status);
        Assert.Equal(2, described.Facings.Single().Facings);
        Assert.Equal(999, described.Prices.Single().ExpectedMinorUnits);
    }
}
