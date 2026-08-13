using FieldKit.Modules.Audit;
using FieldKit.Modules.Audit.Contracts;
using FieldKit.Modules.Configuration.Contracts;

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

    private static readonly Guid Form = Guid.CreateVersion7();

    private static CapturedAudit Audit(
        IReadOnlyList<CapturedAvailability>? availability = null,
        IReadOnlyList<CapturedFacings>? facings = null,
        IReadOnlyList<CapturedPrice>? prices = null,
        int? categoryFacings = 40,
        int weightSetVersion = 3,
        Guid? surveyFormId = null,
        IReadOnlyList<CapturedAnswer>? answers = null,
        IReadOnlyList<CapturedPhoto>? photos = null) =>
        new(Guid.CreateVersion7(), Visit, Captured, weightSetVersion, categoryFacings,
            availability ?? [], facings ?? [], prices ?? [], surveyFormId, answers, photos);

    private static CapturedAnswer Answer(string key, string value = "Yes") =>
        new(key, $"Question {key}?", value);

    /// <summary>
    /// The weighting these cases score against. Incidental to almost all of them.
    /// </summary>
    /// <remarks>
    /// This file is about what a stored audit must be <i>true of</i>; the arithmetic is
    /// <see cref="PerfectStoreScoreTests"/>'s and the resolution of a version is
    /// <see cref="AuditIngestTests"/>'s. What the two scoring cases below assert is only that
    /// <c>Record</c> scores at all, and from the entries it just stored.
    /// </remarks>
    private static PillarWeight[] Balanced() =>
    [
        new(ScorePillar.Availability, 50m),
        new(ScorePillar.ShareOfShelf, 30m),
        new(ScorePillar.PriceCompliance, 20m),
    ];

    private static (Modules.Audit.Audit? Audit, AuditRefusal Refusal) Record(
        CapturedAudit captured, IReadOnlyList<PillarWeight>? weights = null) =>
        Modules.Audit.Audit.Record(captured, Outlet, "rep-1", weights ?? Balanced());

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
    public void An_audit_is_scored_from_the_entries_it_just_stored()
    {
        /*
         * W10 slice 6, and the reason scoring is in the same step as storing: it is the only moment
         * the weights are unambiguous. From here on the score, the entries and the version are one
         * row that either exists or does not.
         *
         * Availability 100 (weight 50), share of shelf skipped, price 0 (weight 20) →
         * (100 × 50 + 0 × 20) ÷ 70 = 71.428… → 71.43.
         */
        var product = Guid.CreateVersion7();

        var (audit, _) = Record(Audit(
            availability: [new CapturedAvailability(product, AvailabilityStatus.Present)],
            prices: [new CapturedPrice(product, 1200, 1000, "RON")],
            categoryFacings: null));

        Assert.Equal(71.43m, audit!.Score);

        Assert.Equal(100m, audit.ScoredPillars.Single(p => p.Pillar == ScorePillar.Availability).Percentage);
        Assert.Null(audit.ScoredPillars.Single(p => p.Pillar == ScorePillar.ShareOfShelf).Percentage);
        Assert.Equal(0m, audit.ScoredPillars.Single(p => p.Pillar == ScorePillar.PriceCompliance).Percentage);

        // The weights are stored beside the percentages, so the arithmetic can be checked by hand
        // from the row alone — which is what "the server recomputes with those weights" has to mean.
        Assert.Equal(30m, audit.ScoredPillars.Single(p => p.Pillar == ScorePillar.ShareOfShelf).Weight);
    }

    [Fact]
    public void A_weighting_that_scores_nothing_leaves_the_score_null_and_the_breakdown_intact()
    {
        // Null is not zero, all the way out to the row. The breakdown still records what was measured
        // and what each pillar was worth, so a reader can see *why* there is no score.
        var (audit, _) = Record(
            Audit(availability: [new CapturedAvailability(Guid.CreateVersion7(), AvailabilityStatus.Present)],
                categoryFacings: null),
            [new PillarWeight(ScorePillar.Availability, 0m), new PillarWeight(ScorePillar.ShareOfShelf, 100m)]);

        Assert.Null(audit!.Score);
        Assert.Equal(100m, audit.ScoredPillars.Single(p => p.Pillar == ScorePillar.Availability).Percentage);
        Assert.Equal(0m, audit.ScoredPillars.Single(p => p.Pillar == ScorePillar.Availability).Weight);
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
    public void Survey_answers_are_numbered_in_the_order_they_arrived_and_keep_the_question()
    {
        /*
         * The text is carried, not looked up. A form can be re-worded — or the question dropped —
         * between the rep answering and the push arriving, and a key alone would then be an answer
         * nobody can read. The same copy a visit makes of its workflow step (BR-VIS-6).
         */
        var (audit, refusal) = Record(Audit(
            surveyFormId: Form,
            answers: [Answer("chiller_lit"), Answer("facings_ok", "No"), Answer("notes", "Shelf was wet")]));

        Assert.Equal(AuditRefusal.None, refusal);
        Assert.Equal(Form, audit!.SurveyFormId);
        Assert.Equal([1, 2, 3], audit.Answers.OrderBy(a => a.Order).Select(a => a.Order));
        Assert.Equal(
            ["chiller_lit", "facings_ok", "notes"],
            audit.Answers.OrderBy(a => a.Order).Select(a => a.QuestionKey));
        Assert.Equal("Question notes?", audit.Answers.Single(a => a.QuestionKey == "notes").QuestionText);
        Assert.Equal("Shelf was wet", audit.Answers.Single(a => a.QuestionKey == "notes").Value);
    }

    [Fact]
    public void An_audit_that_is_only_a_questionnaire_is_a_real_audit()
    {
        // A shop that would not let the rep count the shelf still lets them answer questions. An
        // emptiness rule that ignored answers would throw that away.
        var (audit, refusal) = Record(Audit(surveyFormId: Form, answers: [Answer("chiller_lit")]));

        Assert.Equal(AuditRefusal.None, refusal);
        Assert.NotNull(audit);
    }

    [Fact]
    public void An_audit_that_is_only_a_photograph_is_a_real_audit()
    {
        // AUD-05 calls photo evidence a section of its own. A display worth photographing is worth
        // recording even when nothing was counted.
        var (audit, refusal) = Record(Audit(
            photos: [new CapturedPhoto(AuditSection.General, "tenant-a/audits/x/1.jpg")]));

        Assert.Equal(AuditRefusal.None, refusal);
        Assert.NotNull(audit);
    }

    [Fact]
    public void Two_answers_under_one_question_key_are_refused()
    {
        // The failure the key exists to prevent, from the answering end — SurveyForm refuses
        // duplicate keys at the authoring end for the same reason.
        var (_, refusal) = Record(Audit(
            surveyFormId: Form, answers: [Answer("chiller_lit"), Answer("chiller_lit", "No")]));

        Assert.Equal(AuditRefusal.MalformedAnswers, refusal);
    }

    [Fact]
    public void Answers_that_name_no_questionnaire_are_refused()
    {
        /*
         * The answers would still be readable — they carry their own text — but a reader could not
         * say what was being asked overall, and AUD-09 would hold a set of responses belonging to no
         * form. A device that answered a form knows which one.
         */
        var (_, refusal) = Record(Audit(answers: [Answer("chiller_lit")]));

        Assert.Equal(AuditRefusal.MalformedAnswers, refusal);
    }

    [Fact]
    public void An_answer_with_no_question_behind_it_is_refused()
    {
        var (_, blankKey) = Record(Audit(
            surveyFormId: Form, answers: [new CapturedAnswer("  ", "Is the chiller lit?", "Yes")]));

        var (_, blankText) = Record(Audit(
            surveyFormId: Form, answers: [new CapturedAnswer("chiller_lit", " ", "Yes")]));

        Assert.Equal(AuditRefusal.MalformedAnswers, blankKey);
        Assert.Equal(AuditRefusal.MalformedAnswers, blankText);
    }

    [Fact]
    public void An_empty_answer_is_a_real_answer()
    {
        /*
         * The other half of the rule above, and the one worth stating. A rep who left an optional
         * text question blank has answered it — "nothing to add" is a finding. Only the *question*
         * has to be present; the value need not be.
         *
         * BR-AUD-7's "mandatory questions must be answered" is enforced on the device, where the rep
         * is looking at the form. See IAuditIngest for why re-checking it here would refuse audits
         * for questions that did not exist when they were worked.
         */
        var (audit, refusal) = Record(Audit(
            surveyFormId: Form, answers: [new CapturedAnswer("notes", "Anything to add?", "")]));

        Assert.Equal(AuditRefusal.None, refusal);
        Assert.Equal(string.Empty, audit!.Answers.Single().Value);
    }

    [Fact]
    public void A_form_named_with_no_answers_is_fine()
    {
        // A rep opened the questionnaire and answered nothing optional. Refusing that would be the
        // server insisting the rep must have had something to say.
        var (_, refusal) = Record(Audit(
            facings: [new CapturedFacings(Guid.CreateVersion7(), 3)], surveyFormId: Form));

        Assert.Equal(AuditRefusal.None, refusal);
    }

    [Fact]
    public void A_photo_is_stored_as_a_reference_and_nothing_checks_the_object()
    {
        /*
         * B5: photos are uploaded separately, on reconnect, and the JSON push regularly wins the
         * race. Refusing an audit until its images landed would hold a rep's whole day hostage to a
         * slow upload — and the upload path itself does not exist until W11, so *every* key stored
         * today points at nothing.
         */
        var (audit, refusal) = Record(Audit(photos: [
            new CapturedPhoto(AuditSection.ShareOfShelf, "tenant-a/audits/x/shelf.jpg"),
            new CapturedPhoto(AuditSection.PriceCompliance, "tenant-a/audits/x/tag.jpg"),
        ]));

        Assert.Equal(AuditRefusal.None, refusal);
        Assert.Equal(2, audit!.Photos.Count);
        Assert.Equal(
            AuditSection.ShareOfShelf,
            audit.Photos.Single(p => p.ObjectKey.EndsWith("shelf.jpg")).Section);
    }

    [Fact]
    public void A_photo_with_no_object_key_is_refused()
    {
        // A reference with nothing to fetch by is not a reference. Unlike a missing *object*, which
        // is the ordinary case, this cannot become valid later.
        var (_, refusal) = Record(Audit(photos: [new CapturedPhoto(AuditSection.General, "   ")]));

        Assert.Equal(AuditRefusal.MalformedPhotos, refusal);
    }

    [Fact]
    public void One_object_cannot_be_referenced_twice_in_an_audit()
    {
        // The same image under two sections is one photo counted twice, with no way to say which the
        // rep meant.
        var (_, refusal) = Record(Audit(photos: [
            new CapturedPhoto(AuditSection.ShareOfShelf, "tenant-a/audits/x/1.jpg"),
            new CapturedPhoto(AuditSection.General, "tenant-a/audits/x/1.jpg"),
        ]));

        Assert.Equal(AuditRefusal.MalformedPhotos, refusal);
    }

    [Fact]
    public void The_sections_a_photo_can_belong_to_are_not_the_scored_pillars()
    {
        /*
         * `AuditSection` reads like `ScorePillar` for its first three members and then stops:
         * Survey and General are things a rep points a camera at and nothing weighs. Sharing one
         * enum would make adding a scored pillar silently change where photos can be filed.
         *
         * Asserted rather than commented, because the two lists agreeing today is exactly what would
         * tempt somebody to merge them.
         */
        Assert.Contains(AuditSection.Survey, Enum.GetValues<AuditSection>());
        Assert.Contains(AuditSection.General, Enum.GetValues<AuditSection>());
        Assert.Equal(5, Enum.GetValues<AuditSection>().Length);
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
            prices: [new CapturedPrice(product, 1099, 999, "RON")],
            surveyFormId: Form,
            answers: [Answer("chiller_lit")],
            photos: [new CapturedPhoto(AuditSection.General, "tenant-a/audits/x/1.jpg")]));

        var described = audit!.Describe(Captured);

        Assert.Equal(audit.Id, described.AuditId);
        Assert.Equal(Visit, described.VisitId);
        Assert.Equal(Outlet, described.OutletId);
        Assert.Equal(3, described.WeightSetVersion);
        Assert.Equal(AvailabilityStatus.Absent, described.Availability.Single().Status);
        Assert.Equal(2, described.Facings.Single().Facings);
        Assert.Equal(999, described.Prices.Single().ExpectedMinorUnits);
        Assert.Equal(Form, described.SurveyFormId);
        Assert.Equal("Question chiller_lit?", described.Answers.Single().QuestionText);
        Assert.Equal(AuditSection.General, described.Photos.Single().Section);
    }
}
