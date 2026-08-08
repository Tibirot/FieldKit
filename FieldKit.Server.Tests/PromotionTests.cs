using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FieldKit.Infrastructure.Outbox;
using FieldKit.Modules.Outlets;
using FieldKit.Modules.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldKit.Server.Tests;

/// <summary>
/// Authoring promotions (<c>PRD-05</c>) — W6 slice 8.
/// </summary>
/// <remarks>
/// Percentage-off and fixed-amount-off only. Volume/tiered and BOGO are the second promotion PR.
/// Nothing here asserts that a discount is *applied* — selection by priority within the window is
/// <c>PRD-06</c>, and where a promotion reaches is the next slice.
/// </remarks>
[Collection(ServerCollection.Name)]
public class PromotionTests(ServerFixture fixture)
{
    private const string Promotions = "/api/products/promotions";

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}"[..20];

    private static DateOnly Opens => new(2026, 1, 1);

    private static async Task<Guid> ProductAsync(HttpClient writer)
    {
        var response = await writer.PostAsJsonAsync(
            "/api/products", new { sku = Unique("SKU"), name = "Cola 500ml" });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<ProductResponse>())!.Id;
    }

    private static async Task<Guid> CategoryAsync(HttpClient writer)
    {
        var response = await writer.PostAsJsonAsync(
            "/api/products/categories", new { name = Unique("Cat") });

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<CategoryResponse>())!.Id;
    }

    private static async Task<HttpResponseMessage> CreateAsync(
        HttpClient writer,
        PromotionType type = PromotionType.PercentOff,
        string value = "15",
        string? currency = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int priority = 0,
        string? name = null) =>
        await writer.PostAsJsonAsync(
            Promotions,
            new CreatePromotionRequest(
                Name: name ?? Unique("Promo"),
                Type: type,
                ValidFrom: from ?? Opens,
                Value: value,
                ValidTo: to,
                Priority: priority,
                Currency: currency));

    private static async Task<PromotionResponse> PromotionAsync(
        HttpClient writer,
        PromotionType type = PromotionType.PercentOff,
        string value = "15",
        string? currency = null,
        DateOnly? from = null,
        DateOnly? to = null,
        int priority = 0)
    {
        var response = await CreateAsync(writer, type, value, currency, from, to, priority);

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<PromotionResponse>())!;
    }

    private static async Task<IReadOnlyList<PromotionTargetResponse>> SetTargetsAsync(
        HttpClient writer,
        Guid promotionId,
        IReadOnlyList<Guid>? products = null,
        IReadOnlyList<Guid>? categories = null)
    {
        var response = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotionId}/targets",
            new SetPromotionTargetsRequest(products ?? [], categories ?? []));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<List<PromotionTargetResponse>>())!;
    }

    [Fact]
    public async Task A_percentage_promotion_keeps_its_percentage_and_has_no_currency()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "12.5");

        Assert.Equal(PromotionType.PercentOff, promotion.Type);
        Assert.Equal("12.50", promotion.Value);
        Assert.Null(promotion.Currency);
    }

    [Fact]
    public async Task A_fixed_amount_promotion_carries_its_own_currency()
    {
        // Its own, not the price list's: a promotion is authored once and may reach outlets priced in
        // more than one currency. Refusing to discount a EUR line by an RON amount is BR-PRD-1
        // holding, and PRD-06 can only make that check because the currency is stored here.
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(
            writer, PromotionType.FixedAmountOff, "2.50", currency: "eur");

        Assert.Equal(PromotionType.FixedAmountOff, promotion.Type);
        Assert.Equal("2.50", promotion.Value);
        Assert.Equal("EUR", promotion.Currency);
    }

    [Fact]
    public async Task The_type_and_value_cross_the_wire_by_name_and_as_a_string()
    {
        // The value is a string for the same reason money is (BR-PRD-8) — "12.5% off" losing its
        // last digit to a float is the same class of bug as a price doing so.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "12.5");

        var body = await (await writer.GetAsync($"{Promotions}/{promotion.Id}")).Content
            .ReadAsStringAsync();

        Assert.Contains("\"type\":\"PercentOff\"", body);
        Assert.Contains("\"value\":\"12.50\"", body);
    }

    [Theory]
    [InlineData(PromotionType.PercentOff, "12.5", null)]
    [InlineData(PromotionType.PercentOff, "100", null)]
    [InlineData(PromotionType.FixedAmountOff, "2.5", "EUR")]
    [InlineData(PromotionType.FixedAmountOff, "3", "EUR")]
    public async Task Creating_and_reading_spell_the_value_the_same_way(
        PromotionType type, string value, string? currency)
    {
        // The bug this pins: `decimal` keeps its scale, so "12.5" parsed at creation renders as
        // "12.5", while the same value read back from numeric(5,2) renders as "12.50". One promotion,
        // two spellings, differing only by which request you made — and a client diffing them sees a
        // change that never happened. Both paths now format the way MoneyJsonConverter does.
        using var writer = fixture.CreateAuthenticatedClient();

        var created = await PromotionAsync(writer, type, value, currency);
        var read = await writer.GetFromJsonAsync<PromotionResponse>($"{Promotions}/{created.Id}");

        Assert.Equal(created.Value, read!.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("100.01")]
    public async Task A_percentage_outside_zero_to_a_hundred_is_refused(string value)
    {
        // Zero is refused with the rest: a 0% promotion is not a rule that does nothing, it is a rule
        // that will win a priority contest against a real discount and then take nothing off.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateAsync(writer, PromotionType.PercentOff, value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("value", problem.Field);
        Assert.Equal("product.promotion.percentOutOfRange", problem.Code);
    }

    [Fact]
    public async Task A_hundred_percent_off_is_allowed()
    {
        // The boundary the rule above stops at. A free case is a real trade deal, not a typo.
        using var writer = fixture.CreateAuthenticatedClient();

        Assert.Equal("100.00", (await PromotionAsync(writer, PromotionType.PercentOff, "100")).Value);
    }

    [Fact]
    public async Task A_fixed_amount_of_zero_or_less_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateAsync(writer, PromotionType.FixedAmountOff, "0", currency: "EUR");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.amountNotPositive",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_fixed_amount_larger_than_any_price_is_allowed()
    {
        // Deliberately not refused. Whether that floors a line at zero or disqualifies the promotion
        // is a resolution question (PRD-06), and it cannot be answered here — the same promotion
        // meets a different price at every outlet it reaches.
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(
            writer, PromotionType.FixedAmountOff, "9999.00", currency: "EUR");

        Assert.Equal("9999.00", promotion.Value);
    }

    [Fact]
    public async Task A_comma_decimal_is_refused_rather_than_read_as_thousands()
    {
        // "12,50" would parse to 1250 if thousands separators were allowed — a hundredfold discount
        // that reads as a plausible one. Same refusal as a price line.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateAsync(writer, PromotionType.PercentOff, "12,50");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.valueNotANumber",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_fixed_amount_without_a_currency_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateAsync(writer, PromotionType.FixedAmountOff, "2.50");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("currency", problem.Field);
        Assert.Equal("product.promotion.currencyInvalid", problem.Code);
    }

    [Fact]
    public async Task A_percentage_with_a_currency_is_refused_rather_than_ignored()
    {
        // Dropping it silently means the author finds out when a report disagrees with what they
        // thought they wrote.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateAsync(writer, PromotionType.PercentOff, "15", currency: "EUR");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.currencyNotApplicable",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_window_that_ends_before_it_starts_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        // Equal dates too: half-open makes that an empty window — a promotion never live.
        var response = await CreateAsync(writer, from: Opens, to: Opens);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("validTo", problem.Field);
        Assert.Equal("product.promotion.windowInverted", problem.Code);
    }

    [Fact]
    public async Task Two_promotions_cannot_share_a_name()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var name = Unique("Promo");

        Assert.Equal(HttpStatusCode.Created, (await CreateAsync(writer, name: name)).StatusCode);

        var again = await CreateAsync(writer, name: name);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal(
            "product.promotion.nameTaken",
            Assert.Single(await Refusals.ProblemsOf(again)).Code);
    }

    [Fact]
    public async Task Everything_wrong_at_once_comes_back_at_once()
    {
        // A form with four bad fields should be fixable in one pass, not four round trips.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await writer.PostAsJsonAsync(
            Promotions,
            new CreatePromotionRequest(
                Name: "  ",
                Type: PromotionType.PercentOff,
                ValidFrom: Opens,
                Value: "150",
                ValidTo: Opens,
                Currency: "EUR"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var codes = (await Refusals.ProblemsOf(response)).Select(p => p.Code).ToList();

        Assert.Contains("product.promotion.nameRequired", codes);
        Assert.Contains("product.promotion.percentOutOfRange", codes);
        Assert.Contains("product.promotion.currencyNotApplicable", codes);
        Assert.Contains("product.promotion.windowInverted", codes);
    }

    [Fact]
    public async Task A_promotion_can_be_revalued_redated_and_reprioritised()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "10", priority: 5);

        var response = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}",
            new UpdatePromotionRequest(
                promotion.Name, Opens, "20", new DateOnly(2026, 4, 1), 50));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<PromotionResponse>())!;

        Assert.Equal("20.00", updated.Value);
        Assert.Equal(new DateOnly(2026, 4, 1), updated.ValidTo);
        Assert.Equal(50, updated.Priority);
        Assert.Equal(PromotionType.PercentOff, updated.Type);
    }

    [Fact]
    public async Task Updating_cannot_change_the_type_or_the_currency()
    {
        // Neither is on UpdatePromotionRequest, so this asserts what a caller *cannot say* rather
        // than a refusal. Re-typing would reinterpret the value — 15 meaning "15% off" becoming 15
        // meaning "€15 off" — and every order already priced against it would be explained by a rule
        // that no longer exists.
        Assert.DoesNotContain(
            typeof(UpdatePromotionRequest).GetProperties(),
            property => property.Name is "Type" or "Currency");

        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(
            writer, PromotionType.FixedAmountOff, "2.50", currency: "EUR");

        var response = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}",
            new UpdatePromotionRequest(promotion.Name, Opens, "3.00"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<PromotionResponse>())!;

        Assert.Equal(PromotionType.FixedAmountOff, updated.Type);
        Assert.Equal("EUR", updated.Currency);
        Assert.Equal("3.00", updated.Value);
    }

    [Fact]
    public async Task Promotions_are_listed_best_priority_first()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var low = await PromotionAsync(writer, priority: 1);
        var high = await PromotionAsync(writer, priority: 9_000);

        var listed = (await writer.GetFromJsonAsync<List<PromotionResponse>>(Promotions))!
            .Where(promotion => promotion.Id == low.Id || promotion.Id == high.Id)
            .ToList();

        // Higher wins (BR-PRD-3, as decided on Promotion.Priority) — a promotion that must beat
        // everything already authored is a bigger number, not a renumbering of everything else.
        Assert.Equal([high.Id, low.Id], listed.Select(promotion => promotion.Id));
    }

    [Fact]
    public async Task A_promotion_targets_products_and_categories_together()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(writer);
        var productId = await ProductAsync(writer);
        var categoryId = await CategoryAsync(writer);

        var targets = await SetTargetsAsync(writer, promotion.Id, [productId], [categoryId]);

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.ProductId == productId && t.CategoryId is null);
        Assert.Contains(targets, t => t.CategoryId == categoryId && t.ProductId is null);
    }

    [Fact]
    public async Task Setting_targets_replaces_the_whole_set()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(writer);
        var first = await ProductAsync(writer);
        var second = await ProductAsync(writer);

        await SetTargetsAsync(writer, promotion.Id, [first]);
        var replaced = await SetTargetsAsync(writer, promotion.Id, [second]);

        Assert.Equal(second, Assert.Single(replaced).ProductId);
    }

    [Fact]
    public async Task Emptying_the_targets_withdraws_the_promotion()
    {
        // The same shape and the same meaning as emptying a price list's assignments. A promotion
        // that targets nothing discounts nothing, which is how it is taken out of play without
        // editing its window or deleting a record other things point at.
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(writer);
        var productId = await ProductAsync(writer);

        Assert.Single(await SetTargetsAsync(writer, promotion.Id, [productId]));
        Assert.Empty(await SetTargetsAsync(writer, promotion.Id));

        // And it stays withdrawn on a re-read, rather than the PUT merely returning an empty body.
        var read = await writer.GetFromJsonAsync<List<PromotionTargetResponse>>(
            $"{Promotions}/{promotion.Id}/targets");

        Assert.Empty(read!);
    }

    [Fact]
    public async Task Withdrawing_and_re_targeting_are_symmetric_with_a_price_lists_scope()
    {
        // The asymmetry this pins used to exist: an empty target set was a 400 while an empty price
        // list scope was meaningful. Two endpoints of the same shape behaving differently is the kind
        // of thing a back-office screen discovers the hard way, so it is asserted rather than only
        // fixed.
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(writer);
        var productId = await ProductAsync(writer);

        await SetTargetsAsync(writer, promotion.Id, [productId]);
        await SetTargetsAsync(writer, promotion.Id);

        Assert.Equal(
            productId,
            Assert.Single(await SetTargetsAsync(writer, promotion.Id, [productId])).ProductId);
    }

    [Fact]
    public async Task A_product_or_category_that_does_not_exist_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(writer);

        var badProduct = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}/targets",
            new SetPromotionTargetsRequest([Guid.NewGuid()], []));

        Assert.Equal(HttpStatusCode.BadRequest, badProduct.StatusCode);
        Assert.Equal(
            "product.promotion.productMissing",
            Assert.Single(await Refusals.ProblemsOf(badProduct)).Code);

        var badCategory = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}/targets",
            new SetPromotionTargetsRequest([], [Guid.NewGuid()]));

        Assert.Equal(HttpStatusCode.BadRequest, badCategory.StatusCode);
        Assert.Equal(
            "product.promotion.categoryMissing",
            Assert.Single(await Refusals.ProblemsOf(badCategory)).Code);
    }

    [Fact]
    public async Task Another_tenants_product_reads_as_missing_rather_than_forbidden()
    {
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);
        var productOfB = await ProductAsync(tenantB);

        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}/targets",
            new SetPromotionTargetsRequest([productOfB], []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.productMissing",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    // ─── Volume/tiered (PRD-05, slice 9) ───────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> SetTiersAsync(
        HttpClient writer, Guid promotionId, params PromotionTierRequest[] tiers) =>
        await writer.PutAsJsonAsync(
            $"{Promotions}/{promotionId}/tiers", new SetPromotionTiersRequest(tiers));

    private static async Task<Guid> TieredAsync(HttpClient writer, int priority = 0) =>
        (await PromotionAsync(writer, PromotionType.VolumeTiered, value: null, priority: priority)).Id;

    [Fact]
    public async Task A_tiered_promotion_carries_no_value_of_its_own()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await PromotionAsync(writer, PromotionType.VolumeTiered, value: null);

        Assert.Equal(PromotionType.VolumeTiered, promotion.Type);

        // Null rather than "0.00": a zero would read as "no discount" instead of "look at the tiers".
        Assert.Null(promotion.Value);
        Assert.Null(promotion.Currency);
    }

    [Fact]
    public async Task A_tiered_promotion_sending_a_value_is_refused()
    {
        // Refused rather than ignored, for the same reason a percentage carrying a currency is: the
        // caller has misunderstood where the discounts live, and dropping it silently means they go
        // on to author tiers they believe are redundant.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateAsync(writer, PromotionType.VolumeTiered, value: "15");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.valueNotApplicable",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Tiers_come_back_in_ascending_threshold_order()
    {
        // The order a tier table is read in, and the order resolution will scan it — asserted
        // against an input deliberately out of order.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotionId = await TieredAsync(writer);

        var response = await SetTiersAsync(
            writer,
            promotionId,
            new PromotionTierRequest(24, "10"),
            new PromotionTierRequest(6, "2.5"),
            new PromotionTierRequest(12, "5"));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var tiers = (await response.Content.ReadFromJsonAsync<List<PromotionTierResponse>>())!;

        Assert.Equal([6, 12, 24], tiers.Select(tier => tier.MinQuantity));
        Assert.Equal(["2.50", "5.00", "10.00"], tiers.Select(tier => tier.Value));
        Assert.All(tiers, tier => Assert.Null(tier.Currency));
    }

    [Fact]
    public async Task Amount_tiers_keep_their_currency()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var promotionId = await TieredAsync(writer);

        await SetTiersAsync(
            writer,
            promotionId,
            new PromotionTierRequest(6, "1.50", "eur"),
            new PromotionTierRequest(12, "4.00", "EUR"));

        var tiers = await writer.GetFromJsonAsync<List<PromotionTierResponse>>(
            $"{Promotions}/{promotionId}/tiers");

        Assert.All(tiers!, tier => Assert.Equal("EUR", tier.Currency));
        Assert.Equal(["1.50", "4.00"], tiers!.Select(tier => tier.Value));
    }

    [Fact]
    public async Task Setting_tiers_replaces_the_whole_set()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var promotionId = await TieredAsync(writer);

        await SetTiersAsync(writer, promotionId, new PromotionTierRequest(6, "5"));

        var response = await SetTiersAsync(writer, promotionId, new PromotionTierRequest(12, "8"));
        var tiers = (await response.Content.ReadFromJsonAsync<List<PromotionTierResponse>>())!;

        Assert.Equal(12, Assert.Single(tiers).MinQuantity);
    }

    [Fact]
    public async Task Emptying_the_tiers_withdraws_the_promotion()
    {
        // The same meaning as an empty target set, and as a price list with no assignments. Three
        // endpoints of the same shape, one answer to what empty means.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotionId = await TieredAsync(writer);

        await SetTiersAsync(writer, promotionId, new PromotionTierRequest(6, "5"));

        var response = await SetTiersAsync(writer, promotionId);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var read = await writer.GetFromJsonAsync<List<PromotionTierResponse>>(
            $"{Promotions}/{promotionId}/tiers");

        Assert.Empty(read!);
    }

    [Fact]
    public async Task A_tier_starting_below_two_is_refused()
    {
        // "Buy one or more" is every line that matched at all — a flat discount wearing a tier's
        // clothes, silently shadowing the PercentOff type it duplicates.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotionId = await TieredAsync(writer);

        var response = await SetTiersAsync(writer, promotionId, new PromotionTierRequest(1, "5"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("tiers[0].minQuantity", problem.Field);
        Assert.Equal("product.promotion.tierQuantityTooSmall", problem.Code);
    }

    [Fact]
    public async Task Two_tiers_at_the_same_threshold_are_refused()
    {
        // "The discount at 12" would be a question with two answers, and resolution would have to
        // break a tie that means nothing.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotionId = await TieredAsync(writer);

        var response = await SetTiersAsync(
            writer,
            promotionId,
            new PromotionTierRequest(12, "5"),
            new PromotionTierRequest(12, "8"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.tierQuantityDuplicated",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Mixing_percentage_and_amount_tiers_is_refused()
    {
        // Well-defined but almost certainly a mistake: tiers are picked by quantity, never compared,
        // so nothing breaks — but "5% off at 6, three euros off at 12" is a set nobody can
        // sanity-check at a glance.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotionId = await TieredAsync(writer);

        var response = await SetTiersAsync(
            writer,
            promotionId,
            new PromotionTierRequest(6, "5"),
            new PromotionTierRequest(12, "3.00", "EUR"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.tierKindsMixed",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Amount_tiers_in_two_currencies_are_refused()
    {
        // BR-PRD-1: a set that discounts by EUR at one threshold and RON at another cannot be
        // compared or summed, and resolution would have to pick a currency nobody declared.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotionId = await TieredAsync(writer);

        var response = await SetTiersAsync(
            writer,
            promotionId,
            new PromotionTierRequest(6, "1.50", "EUR"),
            new PromotionTierRequest(12, "20.00", "RON"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.tierCurrenciesMixed",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_bad_tier_names_its_own_index()
    {
        // A form showing four tiers cannot work out from "is above 0 and at most 100" which row to
        // highlight. Same reasoning as contacts[1].email in Outlets.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotionId = await TieredAsync(writer);

        var response = await SetTiersAsync(
            writer,
            promotionId,
            new PromotionTierRequest(6, "5"),
            new PromotionTierRequest(12, "150"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("tiers[1].value", problem.Field);
        Assert.Equal("product.promotion.percentOutOfRange", problem.Code);
    }

    [Fact]
    public async Task A_tier_obeys_the_same_value_rules_as_a_flat_discount()
    {
        // The shared checker, reached through the tier path: comma decimals and out-of-range
        // percentages are refused here exactly as they are on the promotion itself.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotionId = await TieredAsync(writer);

        var comma = await SetTiersAsync(writer, promotionId, new PromotionTierRequest(6, "12,50"));
        Assert.Equal(
            "product.promotion.valueNotANumber",
            Assert.Single(await Refusals.ProblemsOf(comma)).Code);

        var zero = await SetTiersAsync(writer, promotionId, new PromotionTierRequest(6, "0"));
        Assert.Equal(
            "product.promotion.percentOutOfRange",
            Assert.Single(await Refusals.ProblemsOf(zero)).Code);

        var negative = await SetTiersAsync(
            writer, promotionId, new PromotionTierRequest(6, "-1.00", "EUR"));
        Assert.Equal(
            "product.promotion.amountNotPositive",
            Assert.Single(await Refusals.ProblemsOf(negative)).Code);
    }

    [Fact]
    public async Task A_flat_promotion_cannot_have_tiers()
    {
        // It would then hold two discounts with no rule saying which applies.
        using var writer = fixture.CreateAuthenticatedClient();
        var flat = await PromotionAsync(writer, PromotionType.PercentOff, "15");

        var response = await SetTiersAsync(writer, flat.Id, new PromotionTierRequest(6, "5"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.tiersNotApplicable",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task The_database_refuses_a_tiered_promotion_carrying_a_value()
    {
        // The WHEN this slice added to the promotion constraint. Nothing reachable over HTTP can
        // write such a row, which is why it is worth proving at the table.
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO products.promotion
                ("Id", "Name", "type", "percent_off", "amount_off", "currency",
                 "ValidFrom", "ValidTo", "Priority", "TenantId", "CreatedAtUtc")
            VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid().ToString()}, 'VolumeTiered',
                    {10m}, NULL, NULL, {Opens}, NULL, 0, {Guid.NewGuid()}, now())
            """));

        Assert.NotNull(refused);
        Assert.Contains("ck_promotion_value_matches_type", refused.ToString());
    }

    [Fact]
    public async Task The_database_refuses_a_tier_whose_value_and_currency_disagree()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        foreach (var (percent, amount, currency) in new (object?, object?, object?)[]
                 {
                     (5m, 3m, null),      // both kinds
                     (null, null, "EUR"), // neither
                     (5m, null, "EUR"),   // a percentage with a currency
                     (null, 3m, null),    // money with no units
                 })
        {
            var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO products.promotion_tier
                    ("Id", "PromotionId", "min_quantity", "percent_off", "amount_off", "currency",
                     "TenantId", "CreatedAtUtc")
                VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid()}, 6, {percent}, {amount}, {currency},
                        {Guid.NewGuid()}, now())
                """));

            Assert.NotNull(refused);
            Assert.Contains("ck_promotion_tier_value", refused.ToString());
        }
    }

    [Fact]
    public async Task Reading_tiers_and_setting_them_are_different_capabilities()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);
        var promotionId = await TieredAsync(writer);

        Assert.Equal(
            HttpStatusCode.OK,
            (await viewer.GetAsync($"{Promotions}/{promotionId}/tiers")).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await SetTiersAsync(viewer, promotionId, new PromotionTierRequest(6, "5"))).StatusCode);
    }

    // ─── BOGO / bundle (PRD-05, slice 10) ──────────────────────────────────────────────────────

    private static async Task<HttpResponseMessage> CreateBundleAsync(
        HttpClient writer,
        BundleRequest? bundle,
        PromotionType type = PromotionType.BuyXGetY,
        string? value = null) =>
        await writer.PostAsJsonAsync(
            Promotions,
            new CreatePromotionRequest(
                Name: Unique("Promo"),
                Type: type,
                ValidFrom: Opens,
                Value: value,
                Bundle: bundle));

    private static async Task<PromotionResponse> BogoAsync(
        HttpClient writer, BundleRequest? bundle = null)
    {
        var response = await CreateBundleAsync(
            writer, bundle ?? new BundleRequest(2, 1, "100"));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<PromotionResponse>())!;
    }

    [Fact]
    public async Task Buy_two_get_one_free_is_a_hundred_percent_off_the_given_unit()
    {
        // 100 is not a special case in the storage, only in what a shopper calls it. That is what
        // lets "get one free" and "get one half price" be the same offer with a different number,
        // rather than two types.
        using var writer = fixture.CreateAuthenticatedClient();

        var promotion = await BogoAsync(writer, new BundleRequest(2, 1, "100"));

        Assert.Equal(PromotionType.BuyXGetY, promotion.Type);
        Assert.NotNull(promotion.Bundle);
        Assert.Equal(2, promotion.Bundle.BuyQuantity);
        Assert.Equal(1, promotion.Bundle.GetQuantity);
        Assert.Equal("100.00", promotion.Bundle.GetPercentOff);

        // Null means "the same product that was bought" — there is no id to write down when the
        // promotion targets a whole category.
        Assert.Null(promotion.Bundle.GetProductId);

        // It gives units; it does not reduce a price.
        Assert.Null(promotion.Value);
        Assert.Null(promotion.Currency);
    }

    [Fact]
    public async Task A_bundle_can_give_a_different_product_at_a_partial_discount()
    {
        // The same mechanism as BOGO with the id filled in — a cross-sell bundle rather than a
        // second type.
        using var writer = fixture.CreateAuthenticatedClient();
        var gift = await ProductAsync(writer);

        var promotion = await BogoAsync(writer, new BundleRequest(3, 1, "50", gift));

        Assert.Equal(gift, promotion.Bundle!.GetProductId);
        Assert.Equal("50.00", promotion.Bundle.GetPercentOff);
    }

    [Fact]
    public async Task The_bundle_is_one_nested_object_rather_than_four_loose_fields()
    {
        // Four properties that are only ever all-set or all-null belong together in the shape a
        // caller reads: `bundle == null` instead of four checks that must agree.
        using var writer = fixture.CreateAuthenticatedClient();
        var bogo = await BogoAsync(writer);
        var flat = await PromotionAsync(writer, PromotionType.PercentOff, "15");

        var body = await (await writer.GetAsync($"{Promotions}/{bogo.Id}")).Content
            .ReadAsStringAsync();

        Assert.Contains("\"type\":\"BuyXGetY\"", body);
        Assert.Contains("\"bundle\":{", body);
        Assert.Contains("\"getPercentOff\":\"100.00\"", body);

        var flatBody = await (await writer.GetAsync($"{Promotions}/{flat.Id}")).Content
            .ReadAsStringAsync();

        Assert.Contains("\"bundle\":null", flatBody);
    }

    [Fact]
    public async Task A_bundle_promotion_without_a_bundle_is_refused()
    {
        // Required, unlike tiers and targets, which may be empty to mean "reaches nobody". An empty
        // set is still a coherent promotion — one that discounts nothing. "Buy ? get ?" is not a rule
        // at all.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateBundleAsync(writer, bundle: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("bundle", problem.Field);
        Assert.Equal("product.promotion.bundleRequired", problem.Code);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 0)]
    [InlineData(-1, 1)]
    public async Task A_bundle_with_nothing_on_one_side_is_refused(int buy, int get)
    {
        // "Buy none get one" gives the product away to anyone who orders anything; "buy two get none"
        // does nothing while still winning a priority contest against a rule that would have.
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateBundleAsync(writer, new BundleRequest(buy, get, "100"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.bundleQuantityTooSmall",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task The_given_discount_obeys_the_same_percentage_rules_as_everything_else()
    {
        // Through the shared checker, so the refusal names bundle.getPercentOff rather than value.
        using var writer = fixture.CreateAuthenticatedClient();

        var tooMuch = await CreateBundleAsync(writer, new BundleRequest(2, 1, "150"));
        var problem = Assert.Single(await Refusals.ProblemsOf(tooMuch));
        Assert.Equal("bundle.getPercentOff", problem.Field);
        Assert.Equal("product.promotion.percentOutOfRange", problem.Code);

        var zero = await CreateBundleAsync(writer, new BundleRequest(2, 1, "0"));
        Assert.Equal(
            "product.promotion.percentOutOfRange",
            Assert.Single(await Refusals.ProblemsOf(zero)).Code);

        var comma = await CreateBundleAsync(writer, new BundleRequest(2, 1, "12,5"));
        Assert.Equal(
            "product.promotion.valueNotANumber",
            Assert.Single(await Refusals.ProblemsOf(comma)).Code);
    }

    [Fact]
    public async Task A_given_product_that_does_not_exist_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateBundleAsync(
            writer, new BundleRequest(2, 1, "100", Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(response));
        Assert.Equal("bundle.getProductId", problem.Field);
        Assert.Equal("product.promotion.bundleProductMissing", problem.Code);
    }

    [Fact]
    public async Task Another_tenants_product_cannot_be_given_away()
    {
        using var tenantB = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);
        var productOfB = await ProductAsync(tenantB);

        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateBundleAsync(
            writer, new BundleRequest(2, 1, "100", productOfB));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.bundleProductMissing",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_bundle_promotion_carrying_a_value_is_refused()
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateBundleAsync(
            writer, new BundleRequest(2, 1, "100"), value: "15");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.valueNotApplicable",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Theory]
    [InlineData(PromotionType.PercentOff, "15")]
    [InlineData(PromotionType.VolumeTiered, null)]
    public async Task Only_a_bundle_promotion_gives_units_away(PromotionType type, string? value)
    {
        using var writer = fixture.CreateAuthenticatedClient();

        var response = await CreateBundleAsync(
            writer, new BundleRequest(2, 1, "100"), type, value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.bundleNotApplicable",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_bundle_can_be_restated()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await BogoAsync(writer, new BundleRequest(2, 1, "100"));

        var response = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}",
            new UpdatePromotionRequest(
                promotion.Name, Opens, Bundle: new BundleRequest(3, 2, "50")));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var updated = (await response.Content.ReadFromJsonAsync<PromotionResponse>())!;

        Assert.Equal(3, updated.Bundle!.BuyQuantity);
        Assert.Equal(2, updated.Bundle.GetQuantity);
        Assert.Equal("50.00", updated.Bundle.GetPercentOff);
    }

    [Fact]
    public async Task Updating_a_bundle_promotion_without_one_is_refused()
    {
        // A PUT replaces, so an omitted bundle is not "leave it alone" — and letting it through would
        // leave the promotion's quantities describing a rule the author thought they had replaced.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await BogoAsync(writer);

        var response = await writer.PutAsJsonAsync(
            $"{Promotions}/{promotion.Id}", new UpdatePromotionRequest(promotion.Name, Opens));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.bundleRequired",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task A_product_being_given_away_cannot_be_deleted()
    {
        // Restrict on the FK, like every other product reference in this module: the promotion
        // promises to give this product away, and letting it vanish would leave a rule that cannot
        // run.
        //
        // Asserted with raw SQL rather than through DELETE /api/products/{id}, which does not exist.
        // Going through HTTP would have made this pass on the 405 — a green test proving nothing,
        // which is worse than no test because it looks like coverage.
        using var writer = fixture.CreateAuthenticatedClient();
        var gift = await ProductAsync(writer);

        await BogoAsync(writer, new BundleRequest(2, 1, "100", gift));

        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
            $"""DELETE FROM products.product WHERE "Id" = {gift}"""));

        Assert.NotNull(refused);
        Assert.Contains("promotion", refused.ToString());
    }

    [Fact]
    public async Task The_database_refuses_a_type_it_has_never_heard_of()
    {
        // The ELSE FALSE this slice closed. While the four types were arriving one slice at a time
        // the clause was ELSE TRUE — deliberate room, so each new type was a new WHEN rather than an
        // ALTER against stored rows, at the cost of letting any unrecognised type string through.
        // B1 names exactly four and all four are now constrained, so the room is gone and so is the
        // hole. This is the assertion that says so.
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO products.promotion
                ("Id", "Name", "type", "percent_off", "amount_off", "currency",
                 "buy_quantity", "get_quantity", "get_percent_off", "get_product_id",
                 "ValidFrom", "ValidTo", "Priority", "TenantId", "CreatedAtUtc")
            VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid().ToString()}, 'PercentOf',
                    {10m}, NULL, NULL, NULL, NULL, NULL, NULL,
                    {Opens}, NULL, 0, {Guid.NewGuid()}, now())
            """));

        Assert.NotNull(refused);
        Assert.Contains("ck_promotion_value_matches_type", refused.ToString());
    }

    [Fact]
    public async Task The_database_refuses_a_bundle_on_the_wrong_type_and_a_half_stated_one()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        foreach (var (type, percent, buy, get, getPercent) in
                 new (string, object?, object?, object?, object?)[]
                 {
                     ("BuyXGetY", null, 2, 1, null),      // no discount stated for the given units
                     ("BuyXGetY", null, 2, null, 100m),   // nothing said about how many are given
                     ("BuyXGetY", 10m, 2, 1, 100m),       // reducing a price *and* giving units
                     ("PercentOff", 10m, 2, 1, 100m),     // a flat promotion carrying a bundle
                     ("VolumeTiered", null, 2, 1, 100m),  // a tiered promotion carrying a bundle
                 })
        {
            var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO products.promotion
                    ("Id", "Name", "type", "percent_off", "amount_off", "currency",
                     "buy_quantity", "get_quantity", "get_percent_off", "get_product_id",
                     "ValidFrom", "ValidTo", "Priority", "TenantId", "CreatedAtUtc")
                VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid().ToString()}, {type},
                        {percent}, NULL, NULL, {buy}, {get}, {getPercent}, NULL,
                        {Opens}, NULL, 0, {Guid.NewGuid()}, now())
                """));

            Assert.NotNull(refused);
            Assert.Contains("ck_promotion_value_matches_type", refused.ToString());
        }
    }

    // ─── Scope and PromotionActivated (PRD-05, slice 11) ───────────────────────────────────────

    private static async Task<Guid> ChannelAsync(HttpClient admin)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/outlets/channels", new { name = Unique("Channel") });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ChannelResponse>())!.Id;
    }

    private static async Task<Guid> OutletAsync(HttpClient admin, Guid channelId)
    {
        var response = await admin.PostAsJsonAsync(
            "/api/outlets",
            new CreateOutletRequest(
                Unique("OUT"), "Corner Shop", channelId, "Europe/Bucharest"));

        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<OutletResponse>())!.Id;
    }

    private static async Task<HttpResponseMessage> AssignAsync(
        HttpClient writer,
        Guid promotionId,
        IReadOnlyList<Guid>? channels = null,
        IReadOnlyList<Guid>? outlets = null) =>
        await writer.PutAsJsonAsync(
            $"{Promotions}/{promotionId}/assignments",
            new SetPromotionScopeRequest(channels ?? [], outlets ?? []));

    /// <summary>The PromotionActivated events in the outbox for one promotion, oldest first.</summary>
    private async Task<IReadOnlyList<PromotionActivated>> ActivatedAsync(Guid promotionId)
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        // Filtered by type in SQL and matched on the payload in memory: the content column is jsonb,
        // and a Contains against it translates to `jsonb ~~ jsonb`, which Postgres has no operator
        // for.
        var payloads = await db.Set<OutboxMessage>()
            .Where(message => message.Type.Contains(nameof(PromotionActivated)))
            .OrderBy(message => message.OccurredOnUtc)
            .Select(message => message.Content)
            .ToListAsync();

        return
        [
            .. payloads
                .Select(json => JsonSerializer.Deserialize<PromotionActivated>(
                    json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(activated => activated.PromotionId == promotionId),
        ];
    }

    [Fact]
    public async Task A_promotion_can_reach_a_channel_and_particular_outlets()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var outletId = await OutletAsync(admin, channelId);
        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "15");

        var response = await AssignAsync(writer, promotion.Id, [channelId], [outletId]);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        var assignments =
            (await response.Content.ReadFromJsonAsync<List<PromotionAssignmentResponse>>())!;

        Assert.Equal(2, assignments.Count);
        Assert.Contains(assignments, a => a.ChannelId == channelId && a.OutletId is null);
        Assert.Contains(assignments, a => a.OutletId == outletId && a.ChannelId is null);
    }

    [Fact]
    public async Task Assigning_a_promotion_announces_it_through_the_outbox()
    {
        // Read from the outbox rather than asserted at the call site, because the property that
        // matters is that it was written in the same transaction as the assignment rows (ADR-0006),
        // not that a method was called.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var promotion = await PromotionAsync(
            writer, PromotionType.PercentOff, "15", from: Opens, priority: 42);

        await AssignAsync(writer, promotion.Id, [channelId]);

        var activated = Assert.Single(await ActivatedAsync(promotion.Id));

        Assert.Equal(PromotionType.PercentOff, activated.Type);
        Assert.Equal(Opens, activated.ValidFrom);
        Assert.Null(activated.ValidTo);
        Assert.Equal(42, activated.Priority);
        Assert.Equal(1, activated.ChannelCount);
        Assert.Equal(0, activated.OutletCount);
    }

    [Fact]
    public async Task Withdrawing_a_promotion_is_announced_too()
    {
        // "This promotion now reaches nobody" is a change a consumer needs as much as any other — a
        // device that never hears it keeps offering a deal that has been pulled.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var channelId = await ChannelAsync(admin);
        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "15");

        await AssignAsync(writer, promotion.Id, [channelId]);
        await AssignAsync(writer, promotion.Id);

        var activated = await ActivatedAsync(promotion.Id);

        Assert.Equal(2, activated.Count);
        Assert.Equal(0, activated[^1].ChannelCount);
        Assert.Equal(0, activated[^1].OutletCount);
    }

    [Fact]
    public async Task Assigning_replaces_the_whole_scope_without_duplicating_it()
    {
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();

        var first = await ChannelAsync(admin);
        var second = await ChannelAsync(admin);
        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "15");

        await AssignAsync(writer, promotion.Id, [first]);

        var repeated = await AssignAsync(writer, promotion.Id, [first]);
        Assert.Single((await repeated.Content.ReadFromJsonAsync<List<PromotionAssignmentResponse>>())!);

        var replaced = await AssignAsync(writer, promotion.Id, [second]);
        var assignments =
            (await replaced.Content.ReadFromJsonAsync<List<PromotionAssignmentResponse>>())!;

        Assert.Equal(second, Assert.Single(assignments).ChannelId);
    }

    [Fact]
    public async Task A_channel_or_outlet_that_does_not_exist_is_refused()
    {
        // Products cannot see either table (AT-1), so both go through Outlets contracts. Without the
        // checks the scope would save cleanly and reach nobody.
        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "15");

        var badChannel = await AssignAsync(writer, promotion.Id, [Guid.NewGuid()]);
        Assert.Equal(HttpStatusCode.BadRequest, badChannel.StatusCode);
        var problem = Assert.Single(await Refusals.ProblemsOf(badChannel));
        Assert.Equal("channelIds", problem.Field);
        Assert.Equal("product.promotion.channelMissing", problem.Code);

        var badOutlet = await AssignAsync(writer, promotion.Id, outlets: [Guid.NewGuid()]);
        Assert.Equal(HttpStatusCode.BadRequest, badOutlet.StatusCode);
        Assert.Equal(
            "product.promotion.outletMissing",
            Assert.Single(await Refusals.ProblemsOf(badOutlet)).Code);
    }

    [Fact]
    public async Task Another_tenants_outlet_reads_as_missing_rather_than_forbidden()
    {
        using var tenantBAdmin = fixture.CreateAuthenticatedClient(fixture.TenantBAccessToken);
        var channelOfB = await ChannelAsync(tenantBAdmin);
        var outletOfB = await OutletAsync(tenantBAdmin, channelOfB);

        using var writer = fixture.CreateAuthenticatedClient();
        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "15");

        var response = await AssignAsync(writer, promotion.Id, outlets: [outletOfB]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "product.promotion.outletMissing",
            Assert.Single(await Refusals.ProblemsOf(response)).Code);
    }

    [Fact]
    public async Task Every_type_can_be_given_a_scope()
    {
        // Reach is a property of a promotion, not of its type — a tiered deal and a BOGO are pointed
        // at outlets exactly as a flat percentage is.
        using var admin = fixture.CreateAuthenticatedClient(fixture.AdminAccessToken);
        using var writer = fixture.CreateAuthenticatedClient();
        var channelId = await ChannelAsync(admin);

        var tiered = await TieredAsync(writer);
        var bogo = await BogoAsync(writer);

        foreach (var promotionId in new[] { tiered, bogo.Id })
        {
            var response = await AssignAsync(writer, promotionId, [channelId]);

            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

            Assert.Single(await ActivatedAsync(promotionId));
        }
    }

    [Fact]
    public async Task The_database_refuses_a_scope_of_both_or_of_neither()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        foreach (var (channel, outlet) in new (object?, object?)[]
                 {
                     (Guid.NewGuid(), Guid.NewGuid()), // both
                     (null, null),                     // neither
                 })
        {
            var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO products.promotion_assignment
                    ("Id", "PromotionId", "channel_id", "outlet_id", "TenantId", "CreatedAtUtc")
                VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid()}, {channel}, {outlet},
                        {Guid.NewGuid()}, now())
                """));

            Assert.NotNull(refused);
            Assert.Contains("ck_promotion_assignment_one_scope", refused.ToString());
        }
    }

    [Fact]
    public async Task Reading_a_scope_and_setting_it_are_different_capabilities()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);
        var promotion = await PromotionAsync(writer, PromotionType.PercentOff, "15");

        Assert.Equal(
            HttpStatusCode.OK,
            (await viewer.GetAsync($"{Promotions}/{promotion.Id}/assignments")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await AssignAsync(viewer, promotion.Id)).StatusCode);
    }

    [Fact]
    public async Task A_promotion_that_does_not_exist_is_not_found()
    {
        using var writer = fixture.CreateAuthenticatedClient();
        var absent = Guid.NewGuid();

        Assert.Equal(
            HttpStatusCode.NotFound, (await writer.GetAsync($"{Promotions}/{absent}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await writer.GetAsync($"{Promotions}/{absent}/targets")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await writer.GetAsync($"{Promotions}/{absent}/assignments")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await AssignAsync(writer, absent)).StatusCode);

        var write = await writer.PutAsJsonAsync(
            $"{Promotions}/{absent}/targets", new SetPromotionTargetsRequest([], []));

        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    [Fact]
    public async Task Reading_a_promotion_and_authoring_one_are_different_capabilities()
    {
        using var viewer = fixture.CreateAuthenticatedClient(fixture.ReadOnlyAccessToken);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Promotions)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CreateAsync(viewer)).StatusCode);
    }

    [Fact]
    public async Task The_database_refuses_a_value_that_does_not_match_its_type()
    {
        // The check constraint, proven at the table. The endpoint can only ever build matching rows,
        // which is exactly why this is worth asserting: it is the guard for whatever writes without
        // going through the endpoint. Raw SQL because that is the only way to attempt the bad row.
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        foreach (var (type, percent, amount, currency) in new (string, object?, object?, object?)[]
                 {
                     ("PercentOff", null, 5m, "EUR"),      // a percentage carrying money
                     ("PercentOff", 10m, null, "EUR"),     // a percentage with a currency
                     ("FixedAmountOff", null, 5m, null),   // money with no units
                     ("FixedAmountOff", 10m, null, "EUR"), // a fixed amount carrying a percentage
                 })
        {
            var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO products.promotion
                    ("Id", "Name", "type", "percent_off", "amount_off", "currency",
                     "ValidFrom", "ValidTo", "Priority", "TenantId", "CreatedAtUtc")
                VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid().ToString()}, {type},
                        {percent}, {amount}, {currency},
                        {Opens}, NULL, 0, {Guid.NewGuid()}, now())
                """));

            Assert.NotNull(refused);
            Assert.Contains("ck_promotion_value_matches_type", refused.ToString());
        }
    }

    [Fact]
    public async Task The_database_refuses_a_target_of_both_or_of_neither()
    {
        using var scope = fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();

        foreach (var (product, category) in new (object?, object?)[]
                 {
                     (Guid.NewGuid(), Guid.NewGuid()), // both
                     (null, null),                     // neither
                 })
        {
            var refused = await Record.ExceptionAsync(() => db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO products.promotion_target
                    ("Id", "PromotionId", "product_id", "category_id", "TenantId", "CreatedAtUtc")
                VALUES ({Guid.CreateVersion7()}, {Guid.NewGuid()}, {product}, {category},
                        {Guid.NewGuid()}, now())
                """));

            Assert.NotNull(refused);
            Assert.Contains("ck_promotion_target_one_subject", refused.ToString());
        }
    }
}
